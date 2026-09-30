using System.Diagnostics;
using System.Text.Json;
using Gatto.Cli;
using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Tests;

//the order of swap, persist and arm is driven over real files. a guard on source text cannot fail when the words stay put and the behaviour moves
public class ModelSwitchDeedTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-deed-").FullName;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(50) };

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_home, true); } catch (Exception) { }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeProc : ServeManager.IServeProcess
    {
        public int Pid { get; init; } = 30660;
        public bool HasExited { get; set; }
        public string ProcessName { get; init; } = "llama-server";
        public void Kill() { }
        public bool WaitForExit(int ms) => true;
    }

    private const string Incoming = "gemma-4-26B-A4B-it";
    //this id must never equal the incoming one. a fixture with one string for both cannot show which model a decision names.
    private const string Serving = "qwen-qwen3.6-35b-a3b";
    //distinct from the two constants above, so a decision naming the wrong one of three is visible.
    private const string SessionModel = "mistral-small-3.2-24b";

    //a json fragment, null leaves the auto_serve key out and a bool writes it
    private static string AutoServeJson(bool? autoServe) =>
        autoServe is null ? "" : $", \"auto_serve\": {(autoServe.Value ? "true" : "false")}";

    //null writes no auto_serve key, and an absent key serves when someone can be asked (a hint otherwise)
    private Model WriteModel(string id, int port, bool? autoServe = true)
    {
        var gguf = Path.Combine(_home, id + "-Q4_K_M.gguf");
        File.WriteAllText(gguf, "");
        var dir = Path.Combine(_home, "models", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{ "files": [{ "path": {{JsonSerializer.Serialize(gguf)}}, "active": true }], "port": {{port}}, "context": 4096{{AutoServeJson(autoServe)}} }""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """{ "endpoints": { "local": { "base_url": "http://127.0.0.1:9" } }, "default_endpoint": "local" }""");
        return Model.Load(Path.Combine(_home, "models"), id);
    }

    //serving answers from a real serve.json, and nothing may be spawned since every arm under test refuses before a start is asked for
    private ServeManager Manager(string? servingModelId)
    {
        File.WriteAllText(Path.Combine(_home, "llama-server.exe"), "");
        if (servingModelId is not null)
            File.WriteAllText(Path.Combine(_home, "serve.json"),
                $$"""{"pid":30660,"model":{{JsonSerializer.Serialize(servingModelId)}},"port":9,"started":"2026-09-02T00:00:00Z"}""");

        var proc = new FakeProc();
        return new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => throw new Xunit.Sdk.XunitException("nothing may be spawned by the deed"),
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromMilliseconds(30));
    }

    //the steps the deed did, in the order it did them. this log is the oracle a source-text guard could only describe.
    private sealed class Log
    {
        public List<string> Steps { get; } = [];
        public bool SwapSucceeds { get; init; } = true;

        //record the swap arguments, since the go-back branch swaps a model for itself and a count alone cannot tell that from an ordinary switch
        public List<(string? From, string To)> Swaps { get; } = [];

        public ModelSwitchDeed.Seams Seams() => new(
            Swap: (served, to) => { Steps.Add("swap"); Swaps.Add((served, to.Id)); return SwapSucceeds; },
            Start: _ => { Steps.Add("start"); return true; });
    }

    private ModelSwitchOutcome Outcome(Model incoming) =>
        new(true, incoming.Id, "sys", 4096, null, incoming, null);

    //a switch must not write a default, since it rewrites what the next bare gatto opens. check gatto.json on disk, a fake with no writer records nothing
    [Fact]
    public void A_SUCCESSFUL_SWITCH_WRITES_NO_DEFAULT_TO_GATTO_JSON()
    {
        var incoming = WriteModel(Incoming, 9);
        var outgoing = WriteModel(Serving, 9);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            _ => "Yes", confirmed: false, _ => { }, log.Seams());

        Assert.Equal(["swap"], log.Steps);
        Assert.Null(settled);
        Assert.DoesNotContain("default_model",
            File.ReadAllText(Path.Combine(_home, "gatto.json")), StringComparison.Ordinal);
    }

    //the caller arms only on a null return, so a failed swap must not return null. arming onto a server that never came up pairs the session with wrong weights
    [Fact]
    public void THE_SWAP_HAPPENS_AND_THEN_THE_CALLER_ARMS()
    {
        var incoming = WriteModel(Incoming, 9);
        var outgoing = WriteModel(Serving, 9);

        var ok = new Log();
        Assert.Null(ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            _ => "Yes", confirmed: false, _ => { }, ok.Seams()));
        Assert.Equal(["swap"], ok.Steps);

        var failed = new Log { SwapSucceeds = false };
        var settled = ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            _ => "Yes", confirmed: false, _ => { }, failed.Seams());

        Assert.Equal(["swap"], failed.Steps);
        Assert.NotNull(settled);
        Assert.False(settled!.Success);
        Assert.Contains("stayed on " + Serving, settled.Message, StringComparison.Ordinal);
    }

    //a failed swap restores the model the server held, which need not be the session's. the message names that model beside the one the session stayed on.
    [Fact]
    public void A_FAILED_SWAP_PUTS_BACK_THE_SERVED_MODEL_NOT_THE_SESSIONS()
    {
        var incoming = WriteModel(Incoming, 9);
        var session = WriteModel(SessionModel, 9);
        WriteModel(Serving, 9);
        var failed = new Log { SwapSucceeds = false };

        var settled = ModelSwitchDeed.Run(Outcome(incoming), session, Manager(Serving),
            _ => "Yes", confirmed: false, _ => { }, failed.Seams());

        Assert.Equal([(Serving, Incoming)], failed.Swaps);
        Assert.NotNull(settled);
        Assert.False(settled!.Success);
        Assert.Contains("stayed on " + SessionModel, settled.Message, StringComparison.Ordinal);
        Assert.Contains(Serving, settled.Message, StringComparison.Ordinal);
    }

    //the restore loads the served model by id, and a model that no longer loads gives a sentence naming it
    [Fact]
    public void THE_RESTORE_LOADS_THE_SERVED_MODEL_OR_SAYS_WHY_IT_CANNOT()
    {
        WriteModel(Serving, 9);
        var modelsDir = Path.Combine(_home, "models");

        var (back, cannot) = GattoApp.RestoreTarget(modelsDir, Serving);
        Assert.Equal(Serving, back?.Id);
        Assert.Null(cannot);

        var (gone, why) = GattoApp.RestoreTarget(modelsDir, SessionModel);   //this model was never written, so the load must fail.
        Assert.Null(gone);
        Assert.Contains("couldn't load " + SessionModel, why, StringComparison.Ordinal);
    }

    //declining the confirm must touch nothing. no swap runs, and the message names the model the session keeps.
    [Fact]
    public void DECLINING_THE_CONFIRM_TOUCHES_NEITHER_THE_SERVER_NOR_THE_CONFIG()
    {
        var incoming = WriteModel(Incoming, 9);
        var outgoing = WriteModel(Serving, 9);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            _ => "No, stay", confirmed: false, _ => { }, log.Seams());

        Assert.Empty(log.Steps);
        Assert.Equal("staying on " + Serving, settled!.Message);
    }

    //the session is not on the served model here, so declining keeps the server on the model it holds. the message names it
    [Fact]
    public void DECLINING_WHEN_THE_SERVER_HOLDS_ANOTHER_MODEL_NAMES_THE_MODEL_IT_KEEPS()
    {
        var incoming = WriteModel(Incoming, 9);
        var outgoing = WriteModel("session-model", 9);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            _ => "No, keep", confirmed: false, _ => { }, log.Seams());

        Assert.Empty(log.Steps);
        Assert.Equal("keeping " + Serving + " running", settled!.Message);
    }

    //the server already holds the requested model, so nothing changes and nothing is asked. the ask throws here, which proves the silence
    [Fact]
    public void SERVING_THE_REQUESTED_MODEL_ALREADY_ASKS_NOTHING_AND_SWAPS_NOTHING()
    {
        var incoming = WriteModel(Incoming, 9);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), null, Manager(Incoming),
            _ => throw new Xunit.Sdk.XunitException("nothing may be asked on this arm"),
            confirmed: false, _ => { }, log.Seams());

        Assert.Empty(log.Steps);
        Assert.Null(settled);
    }

    //with nothing serving, the model is started rather than swapped
    [Fact]
    public void WITH_NOTHING_SERVING_THE_MODEL_IS_STARTED_AND_THEN_PERSISTED()
    {
        var incoming = WriteModel(Incoming, 9);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), null, Manager(null),
            _ => "Yes", confirmed: false, _ => { }, log.Seams());

        Assert.Equal(["start"], log.Steps);
        Assert.Null(settled);
    }

    //a confirmed switch must not ask again, since a second question shows one option twice. the ask throws here, so reaching it is a failure
    [Fact]
    public void A_CONFIRMED_SWITCH_DOES_NOT_ASK_THE_USER_AGAIN()
    {
        var incoming = WriteModel(Incoming, 9);
        var outgoing = WriteModel(Serving, 9);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            _ => throw new Xunit.Sdk.XunitException("the wizard already asked; this must not ask"),
            confirmed: true, _ => { }, log.Seams());

        Assert.Equal(["swap"], log.Steps);
        Assert.Null(settled);
        Assert.Equal([(Serving, Incoming)], log.Swaps);
    }

    //a confirmed switch must still start the server when nothing serves. nothing may be asked on this path, so the throwing fake proves the start happened unasked.
    [Fact]
    public void A_CONFIRMED_SWITCH_STILL_STARTS_THE_SERVER_AND_ASKS_NOTHING()
    {
        var incoming = WriteModel(Incoming, 9, autoServe: null);
        var log = new Log();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), null, Manager(null),
            _ => throw new Xunit.Sdk.XunitException("nothing may be asked on this road"),
            confirmed: true, _ => { }, log.Seams());

        Assert.Equal(["start"], log.Steps);
        Assert.Null(settled);
    }

    //the two declines share one id yet change the machine differently. only this test fails if that distinction is lost
    [Fact]
    public void THE_TWO_DECLINES_SHARE_AN_ANSWER_AND_MOVE_THE_MACHINE_DIFFERENTLY()
    {
        WriteModel(Incoming, 9);
        var previous = WriteModel(Serving, 9);
        //one answer serves both declines: the session ends on the model it was already on.
        var answer = Outcome(previous);

        //the check ran, so the new model holds the server. going back must restart the previous one.
        var afterCheck = new Log();
        Assert.Null(ModelSwitchDeed.Run(answer, previous, Manager(Incoming),
            _ => throw new Xunit.Sdk.XunitException("the wizard already asked"),
            confirmed: true, _ => { }, afterCheck.Seams()));

        //no check ran, so the previous model never stopped serving. nothing may change.
        var noCheck = new Log();
        Assert.Null(ModelSwitchDeed.Run(answer, previous, Manager(Serving),
            _ => throw new Xunit.Sdk.XunitException("the wizard already asked"),
            confirmed: true, _ => { }, noCheck.Seams()));

        //the two logs are what tell the two declines apart, so they must differ
        Assert.NotEqual(afterCheck.Steps, noCheck.Steps);
        Assert.Equal(["swap"], afterCheck.Steps);
        Assert.Empty(noCheck.Steps);
        //the swap pair's first half is the id the server held, so a failed start puts that model back
        Assert.Equal([(Incoming, Serving)], afterCheck.Swaps);
        Assert.Empty(noCheck.Swaps);
    }
    //the unconfirmed path must put the swap confirm up, and the recorded ask is the proof (the /model command has no wizard)
    [Fact]
    public void AN_UNCONFIRMED_SWITCH_PUTS_THE_SWAP_CONFIRM_UP()
    {
        var incoming = WriteModel(Incoming, 9);
        var outgoing = WriteModel(Serving, 9);
        var log = new Log();
        var asked = new List<WizardAsk>();

        var settled = ModelSwitchDeed.Run(Outcome(incoming), outgoing, Manager(Serving),
            a => { asked.Add(a); return "Yes"; },
            confirmed: false, _ => { }, log.Seams());

        var screen = Assert.Single(asked);
        Assert.Contains(Incoming, screen.Question, StringComparison.Ordinal);
        Assert.Contains(screen.Options!, o => o.Label.Contains(Serving, StringComparison.Ordinal));
        Assert.Equal(["swap"], log.Steps);
        Assert.Null(settled);
    }
}
