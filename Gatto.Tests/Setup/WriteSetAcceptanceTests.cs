using System.Text.Json.Nodes;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Home;

namespace Gatto.Tests.Setup;

//assert on what reached disk, the flow's write set can be filled in and never written. a null home lets SetupRunner return 0 at any pause
public class WriteSetAcceptanceTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-writeset-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    //the four walks

    //the walk that covers the full model path, and writes the most
    [Fact]
    public void THE_LLAMA_WALK_LEAVES_A_COMPLETE_SETUP_ON_DISK()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes
        {
            Llama = null,                       //absent, so the walk goes through steering
            Found = [Model()],                  //this walk is the only path that writes llama_server
            Consent = null,                     //unanswered, so the update question really is asked
        };
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"), "0",
            SetupFlow.Yes, SetupFlow.UpdateYes, SetupFlow.OpenRepl, SetupFlow.Yes, SetupFlow.Yes]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("done", face.Keys);

        //the model on disk, with its sibling files beside it
        var models = Gatto.Roles.Model.ListIds(Path.Combine(_home, "models"));
        var modelId = Assert.Single(models);
        Assert.True(File.Exists(Path.Combine(_home, "models", modelId, "profile.json")));
        Assert.True(File.Exists(Path.Combine(_home, "models", modelId, "tuning.txt")));

        Assert.Equal(@"C:\llama\llama-server.exe", Str("llama_server"));

        //read the id back from the models dir, a name guessed from the file would be wrong
        Assert.Equal(modelId, DefaultModelOnDisk());

        TheConsentAnswerReachedDisk();
    }

    //the capability comes from the face, so this walk covers the wiring seam too. drop the m answer and 0 answers the Hub shelf instead
    [Fact]
    public void THE_SOURCE_SWITCHING_WALK_LEAVES_A_COMPLETE_SETUP_ON_DISK()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Llama = null, Found = [Model()], Consent = null };
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"),
            ShelfControls.SourceAnswer(), "0",
            SetupFlow.Yes, SetupFlow.UpdateYes, SetupFlow.OpenRepl, SetupFlow.Yes, SetupFlow.Yes])
        { CanSwitchSource = true };

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("done", face.Keys);

        //the same disk assertions as the llama walk, the landing changed and the destination did not
        var modelId = Assert.Single(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));
        Assert.Equal(@"C:\llama\llama-server.exe", Str("llama_server"));
        Assert.Equal(modelId, DefaultModelOnDisk());
        TheConsentAnswerReachedDisk();

        //the walk must cross the Hub before the local shelf, or the flag could be ignored and still pass
        Assert.Equal(SetupFlow.SearchKey, face.Keys.First(k => k is SetupFlow.SearchKey or SetupFlow.DiscoveredKey));
    }

    //connects to a server somebody else runs, so no model and an endpoint instead
    [Fact]
    public void THE_CONNECT_WALK_LEAVES_A_COMPLETE_SETUP_ON_DISK()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes
        {
            Connect = new ConnectProbe("http://127.0.0.1:1234", ["their-model"], 8192),
            Consent = null,
        };
        //positional, so the connect flow's four questions (fork, confirm, prove, update) are answered in this order
        var face = new Surface([.. WalkOpening.Answers, SetupFlow.ForkConnect, SetupFlow.Yes,
            SetupFlow.OpenRepl, SetupFlow.UpdateYes, SetupFlow.Finish]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("done", face.Keys);
        Assert.Empty(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));

        //assert the invariant, the config can never name an endpoint that is not in the map
        var endpoint = Str("default_endpoint");
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        Assert.NotNull(Config()["endpoints"]?[endpoint!]);

        Assert.Equal("their-model", DefaultModelOnDisk());
        TheConsentAnswerReachedDisk();
    }

    //reuse arms no write pause, so the Done pause must write default_model unconditionally
    [Fact]
    public void THE_REUSE_WALK_LANDS_ITS_DEFAULT_MODEL()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Found = [Model()], Existing = "already-set-up", Consent = null };
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(Exe), "0", SetupFlow.UpdateYes, SetupFlow.OpenRepl,
            SetupFlow.Yes, SetupFlow.Yes]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("done", face.Keys);
        Assert.Equal("already-set-up", DefaultModelOnDisk());

        //reuse means no second model for the same weights
        Assert.Empty(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));
        TheConsentAnswerReachedDisk();
    }

    //use the existing model ends where reuse does, so it gets its own walk
    [Fact]
    public void THE_COLLISION_USE_EXISTING_WALK_LANDS_ITS_DEFAULT_MODEL()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Found = [Model()], Colliding = "same-name-different-file", Consent = null };
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(Exe), "0", SetupFlow.UseExistingModel,
            SetupFlow.UpdateYes, SetupFlow.OpenRepl, SetupFlow.Yes, SetupFlow.Yes]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("done", face.Keys);
        Assert.Equal("same-name-different-file", DefaultModelOnDisk());
        Assert.Empty(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));
        TheConsentAnswerReachedDisk();
    }

    //the runner's own guards

    //leaving at the update question still completes the setup, the default model is on disk and the consent stays unanswered
    [Fact]
    public void ESC_AT_THE_UPDATE_QUESTION_LEAVES_A_COMPLETE_SETUP()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Llama = null, Found = [Model()], Consent = null };

        //the script stops at the update question, null from the surface is Esc
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"), "0",
            SetupFlow.Yes]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        var modelId = Assert.Single(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));
        Assert.Equal(modelId, DefaultModelOnDisk());      //the setup is complete even though the run left early
        Assert.Null(Flag("update_check"));               //the unanswered question must not be written to disk

        //read the leaving screen by key, a filter by screen kind can quietly match nothing
        var leaving = string.Join(" ", face.Seen
            .Where(s => s is WizardScreen.Info { Key: "left" } or WizardScreen.Terminal { Key: "left" })
            .SelectMany(s => s switch
            {
                WizardScreen.Info i => i.Rows.Select(r => r.Text),
                //the projection adds NextStep too, reading only the rows would drop the sentence under test
                WizardScreen.Terminal t => t.Rows.Select(r => r.Text).Append(t.NextStep?.Text ?? ""),
                _ => [],
            }));
        Assert.NotEqual("", leaving);   //the filter found a screen at all
        //the leaving sentence names the model, so assert its words here
        Assert.Contains("gatto is set up and pointed at", leaving, StringComparison.Ordinal);
        Assert.DoesNotContain("not pointed at a model yet", leaving, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has been written", leaving, StringComparison.Ordinal);
    }

    //reuse plus Esc at the update question has no flush behind it, so the default must still be written
    [Fact]
    public void ESC_AT_THE_UPDATE_QUESTION_ON_A_REUSE_WALK_STILL_LANDS_THE_DEFAULT()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Found = [Model()], Existing = "already-set-up", Consent = null };

        //the script stops at the update question, so the surface hands back Esc
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(Exe), "0"]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Equal("already-set-up", DefaultModelOnDisk());
        Assert.Null(Flag("update_check"));
    }

    //read the disk when the completion screen renders, the final state can be right and the words false
    [Fact]
    public void THE_DEFAULT_IS_ON_DISK_BEFORE_THE_COMPLETION_SCREEN_CLAIMS_IT()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Llama = null, Found = [Model()], Consent = null };
        //the completion screen is done.summary, the check's result kept the prove key
        var face = new WatchingSurface(Path.Combine(_home, "gatto.json"), "done.summary",
            [.. WalkOpening.Answers, ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"), "0",
            SetupFlow.Yes, SetupFlow.UpdateYes, SetupFlow.OpenRepl]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.NotNull(face.Snapshot);
        var atCompletion = JsonNode.Parse(face.Snapshot!)!["defaults"]?["local"]?["model"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(atCompletion),
            "the completion screen says the configuration has been saved, it must be true when read");
        Assert.Equal(DefaultModelOnDisk(), atCompletion);
    }

    //a screen only the watch advances takes no scripted answer, poll and report Landed or fail loudly
    private static string? WatchedThrough(WizardScreen.Choice c, Func<bool>? watch)
    {
        if (!c.OnlyTheWatchAdvances || watch is null) return null;
        Assert.True(SpinWait.SpinUntil(watch, TimeSpan.FromSeconds(30)),
            $"'{c.Key}' watches and nothing ever landed, so this walk cannot proceed");
        return SetupFlow.Landed;
    }

    //snapshots the config the instant the named screen renders, the reading twin of BreakingSurface
    private sealed class WatchingSurface(string configPath, string watchFor, params string?[] answers)
        : IWizardSurface
    {
        private int _i;
        public string? Snapshot { get; private set; }
        public List<WizardScreen> Seen { get; } = [];

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null)
        {
            Record(c, c.Key);
            return WatchedThrough(c, watch) ?? Next();
        }
        public string? Ask(WizardScreen.Ask a) { Record(a, a.Key); return Next(); }
        public void Show(WizardScreen.Info i) => Record(i, i.Key);
        public void End(WizardScreen.Terminal t) => Record(t, t.Key);

        private void Record(WizardScreen s, string key)
        {
            Seen.Add(s);
            if (key == watchFor && Snapshot is null && File.Exists(configPath))
                Snapshot = File.ReadAllText(configPath);
        }

        private string? Next() => _i < answers.Length ? answers[_i++] : null;
    }

    //count the offers and the prove-it calls, one resume seam serves all three pauses
    [Fact]
    public void THE_OFFER_IS_MADE_ONCE_AND_PROVE_IT_RUNS_ONCE()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Llama = null, Found = [Model()], Consent = null };
        var face = new Surface([.. WalkOpening.Answers, ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"), "0",
            SetupFlow.Yes, SetupFlow.UpdateYes, SetupFlow.OpenRepl, SetupFlow.Yes]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("done", face.Keys);
        Assert.Equal(1, face.Keys.Count(k => k == "model.audition.offer"));
        Assert.Equal(1, probes.ProveCalls);
    }

    //a pause that cannot write names the step it failed at and stops, at every pause
    [Theory]
    //breaking at the audition offer makes the Done pause the failing write
    [InlineData("model.audition.offer", "couldn't set the default model")]
    //breaking at the update question makes the consent flush the failing write
    [InlineData("update", "couldn't save the update-check choice")]
    public void A_PAUSE_THAT_CANNOT_WRITE_NAMES_THE_STEP(string breakAt, string expected)
    {
        GattoHome.EnsureInitialized(_home);
        var probes = new Probes { Llama = null, Found = [Model()], Consent = null };
        var face = new BreakingSurface(Path.Combine(_home, "gatto.json"), breakAt,
            [.. WalkOpening.Answers, ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"), "0",
            SetupFlow.Yes, SetupFlow.UpdateYes, SetupFlow.OpenRepl]);

        try
        {
            var exit = SetupRunner.Run(new SetupFlow(probes), face, _home);

            Assert.Equal(1, exit);
            var failed = string.Join(" ", face.Seen.OfType<WizardScreen.Info>()
                .Where(i => i.Key == "write.failed").SelectMany(i => i.Rows.Select(r => r.Text)));
            Assert.Contains(expected, failed, StringComparison.Ordinal);

            //the Done pause runs before the completion screen, so a failure can never follow the congratulations
            if (breakAt == "model.audition.offer") Assert.DoesNotContain("done.summary", face.Keys);
        }
        finally
        {
            var cfg = Path.Combine(_home, "gatto.json");
            if (File.Exists(cfg)) File.SetAttributes(cfg, FileAttributes.Normal);
        }
    }

    //makes the config unwritable when the named screen shows, so a later pause fails, and the test's finally restores it
    private sealed class BreakingSurface(string configPath, string breakAt, params string?[] answers)
        : IWizardSurface
    {
        private int _i;
        public List<WizardScreen> Seen { get; } = [];

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null)
        {
            Record(c, c.Key);
            return WatchedThrough(c, watch) ?? Next();
        }
        public string? Ask(WizardScreen.Ask a) { Record(a, a.Key); return Next(); }
        public void Show(WizardScreen.Info i) => Record(i, i.Key);
        public void End(WizardScreen.Terminal t) => Record(t, t.Key);

        private void Record(WizardScreen s, string key)
        {
            Seen.Add(s);
            if (key == breakAt && File.Exists(configPath))
                File.SetAttributes(configPath, FileAttributes.ReadOnly);
        }

        private string? Next() => _i < answers.Length ? answers[_i++] : null;

        public IEnumerable<string> Keys => Seen.Select(ScreenKey.Of);
    }

    //every walk asserts the update answer reached disk
    private void TheConsentAnswerReachedDisk() => Assert.True(Flag("update_check"));

    //reading the disk

    private JsonNode Config() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json")))!;

    private string? Str(string key) => Config()[key]?.GetValue<string>();

    //the default endpoint's entry, where every writer puts the model now
    private string? DefaultModelOnDisk() => Config()["defaults"]?[Str("default_endpoint")!]?["model"]?.GetValue<string>();

    private bool? Flag(string key) => Config()[key]?.GetValue<bool>();

    //fixtures

    private FoundModel Model(string name = "tiny.gguf")
    {
        var dst = Path.Combine(_home, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), dst, overwrite: true);
        return new FoundModel(dst, 4_000_000_000, null);
    }

    private sealed class Surface(params string?[] answers) : IWizardSurface
    {
        private int _i;
        public List<WizardScreen> Seen { get; } = [];
        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null)
        {
            Seen.Add(c);
            return WatchedThrough(c, watch) ?? Next();
        }
        public string? Ask(WizardScreen.Ask a) { Seen.Add(a); return Next(); }
        public void Show(WizardScreen.Info i) => Seen.Add(i);
        public void End(WizardScreen.Terminal t) => Seen.Add(t);
        private string? Next() => _i < answers.Length ? answers[_i++] : null;

        //set by the walk that binds m, so every other walk in this file keeps describing the numbered product
        public bool CanSwitchSource { get; init; }

        public IEnumerable<string> Keys => Seen.Select(ScreenKey.Of);
    }

    //the engine path the steer screen is pointed at, typed through ShelfControls.TypedAnswer (a bare key at these screens throws)
    private const string Exe = @"C:\llama\llama-server.exe";

    private sealed class Probes : ISetupProbes
    {
        public string? Llama { get; init; } = @"C:\llama\llama-server.exe";
        public IReadOnlyList<FoundModel> Found { get; init; } = [];
        public ConnectProbe? Connect { get; init; }
        public string? Existing { get; init; }
        public string? Colliding { get; init; }
        public bool? Consent { get; init; }

        public HardwareSnapshot? Hardware() => new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);

        //display only, and null is the honest default (an invented name would put hardware on a screen no probe reported)
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => Llama;
        public bool HasResolvableModel() => false;
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => Connect;
        public string? GattoServingOn(int port) => null;

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new(Found, Roots);
        public HubSearchOutcome Search(HubSearchRequest request) => new([], null);
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => new TypedIdOutcome.Unreachable(false);
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => new("z.zip", null, "a graphics card", "Vulkan");
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, p);
        public int ContextFor(string p) => 8192;
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModelFor(string ggufPath) => Existing;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
            Colliding is { Length: > 0 }
                ? (Gatto.Roles.IdClash.DifferentModel, Colliding)
                : (Gatto.Roles.IdClash.Free, null);
        //a fixed fake path, so the screen renders at a chosen state instead of reporting whatever exe runs the tests
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public bool? UpdateConsent() => Consent;
        public string? ExistingEndpointFor(string baseUrl) => null;
        public string ConfigPath() => Path.Combine("home", "gatto.json");
        //counted rather than flagged (only a count tells running once from being suppressed, and a bad resume route runs prove-it twice)
        public int ProveCalls { get; private set; }

        public ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null)
        {
            ProveCalls++;
            return new ProveOutcome(true, "hi", TimeSpan.FromSeconds(2));
        }
    }
}
