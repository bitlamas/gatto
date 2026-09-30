using Gatto.Cli;
using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Tests;

//every refusal must arrive before the consent screen composes, so assert the frame sequence a wording check would miss
public class QuantRefusalOrderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-order-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    //record every screen shown and always answer yes, since a recorder that said no would make no-write true for the wrong reason
    private sealed class Frames
    {
        public List<WizardAsk> Shown { get; } = [];
        public int Probes { get; private set; }

        public bool Ask(WizardAsk ask) { Shown.Add(ask); return true; }
        public bool Serving() { Probes++; return true; }
    }

    private Model TwoFiles(string id = "gemma-4-e4b-it") => Write(id,
        """{"files": [{"path": "a-Q4_K_M.gguf", "quant": "Q4_K_M", "active": true}, """
        + """{"path": "a-Q6_K.gguf", "quant": "Q6_K", "active": false}], "port": 1235, "context": 8192}""");

    private Model OneFile(string id = "solo") => Write(id,
        """{"files": [{"path": "a-Q4_K_M.gguf", "quant": "Q4_K_M", "active": true}], "port": 1235, "context": 8192}""");

    private Model Write(string id, string profile)
    {
        Directory.CreateDirectory(Path.Combine(_dir, id));
        File.WriteAllText(Path.Combine(_dir, id, "profile.json"), profile);
        return Model.Load(_dir, id);
    }

    //test each refusal Remove can make on a serving model, since serving is the state that composes a screen
    [Theory]
    [InlineData("Q9_ZZ", "has no file called")]                 //a quant that is not on the list at all.
    [InlineData("Q4_K_M", "switch to another one first")]       //the quant that is the active file.
    public void A_REFUSED_REMOVE_NEVER_REACHES_A_SCREEN(string name, string because)
    {
        var f = new Frames();

        var said = QuantEdit.Remove(_dir, TwoFiles(), name, f.Serving, f.Ask);

        Assert.Contains(because, said, StringComparison.Ordinal);
        Assert.Empty(f.Shown);
        //a refusal decided from the list alone must ask the machine nothing, so the probe count stays zero.
        Assert.Equal(0, f.Probes);
    }

    //the last-file refusal needs a one-file model to reach it, and it must show no screen either
    [Fact]
    public void AND_THE_LAST_FILE_IS_REFUSED_WITHOUT_A_SCREEN_TOO()
    {
        var f = new Frames();

        var said = QuantEdit.Remove(_dir, OneFile(), "Q4_K_M", f.Serving, f.Ask);

        Assert.Contains("only file", said, StringComparison.Ordinal);
        Assert.Empty(f.Shown);
        Assert.Equal(0, f.Probes);
    }

    //the switch follows the same rule: a refused switch composes no screen and probes nothing.
    [Fact]
    public void A_REFUSED_SWITCH_NEVER_REACHES_A_SCREEN()
    {
        var f = new Frames();

        var said = QuantEdit.Switch(_dir, TwoFiles(), "Q9_ZZ", f.Serving, f.Ask);

        Assert.Contains("has no file called", said, StringComparison.Ordinal);
        Assert.Empty(f.Shown);
        Assert.Equal(0, f.Probes);
    }

    //a recorder that records nothing would satisfy the assertions above, so a legal edit must show exactly one frame here
    [Fact]
    public void AND_A_LEGAL_EDIT_ON_A_SERVING_MODEL_DOES_REACH_ONE()
    {
        var f = new Frames();

        var said = QuantEdit.Remove(_dir, TwoFiles(), "Q6_K", f.Serving, f.Ask);

        var frame = Assert.Single(f.Shown);
        Assert.Equal("Forget Q6_K from gemma-4-e4b-it?", frame.Question);
        Assert.Equal(1, f.Probes);
        Assert.Contains("no longer lists Q6_K", said, StringComparison.Ordinal);
    }

    //the same legal case for the switch, so its empty-screen assertion can fail
    [Fact]
    public void AND_A_LEGAL_SWITCH_ON_A_SERVING_MODEL_DOES_REACH_ONE()
    {
        var f = new Frames();

        var said = QuantEdit.Switch(_dir, TwoFiles(), "Q6_K", f.Serving, f.Ask);

        Assert.Equal("Run gemma-4-e4b-it on Q6_K from now on?", Assert.Single(f.Shown).Question);
        Assert.Contains("will use Q6_K from now on", said, StringComparison.Ordinal);
    }

    //a refused edit must leave the profile byte-identical, since frame assertions only cover what the user saw
    [Fact]
    public void AND_A_REFUSED_EDIT_LEAVES_THE_PROFILE_BYTE_FOR_BYTE()
    {
        var model = TwoFiles();
        var path = Path.Combine(_dir, model.Id, "profile.json");
        var before = File.ReadAllBytes(path);

        QuantEdit.Remove(_dir, model, "Q9_ZZ", new Frames().Serving, new Frames().Ask);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    //switching to the file already in use answers one plain line, since a screen would offer the same file twice
    [Fact]
    public void THE_ALREADY_ACTIVE_SWITCH_ANSWERS_WITHOUT_A_SCREEN_A_PROBE_OR_A_WRITE()
    {
        var f = new Frames();
        var model = TwoFiles();
        var path = Path.Combine(_dir, model.Id, "profile.json");
        var before = File.ReadAllBytes(path);

        var said = QuantEdit.Switch(_dir, model, "Q4_K_M", f.Serving, f.Ask);

        Assert.Equal("Q4_K_M is the file gemma-4-e4b-it is set to use. Nothing to change", said);
        Assert.Empty(f.Shown);
        Assert.Equal(0, f.Probes);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    //a lowercase spelling is the same request, since ModelFiles.Matches folds case and a check that did not would ask about a no-op
    [Fact]
    public void AND_THE_NO_OP_FOLDS_CASE_THE_SAME_WAY_THE_LIBRARY_DOES()
    {
        var f = new Frames();

        var said = QuantEdit.Switch(_dir, TwoFiles(), "q4_k_m", f.Serving, f.Ask);

        //the reply uses the file's stored label, since the user may have typed any case
        Assert.StartsWith("Q4_K_M is the file", said, StringComparison.Ordinal);
        Assert.Empty(f.Shown);
    }

    //without this case the write-absence assertions pass against a store that never writes, so a real switch must move the bytes
    [Fact]
    public void AND_A_REAL_SWITCH_DOES_MOVE_THE_BYTES()
    {
        var model = TwoFiles();
        var path = Path.Combine(_dir, model.Id, "profile.json");
        var before = File.ReadAllBytes(path);

        QuantEdit.Switch(_dir, model, "Q6_K", () => false, ask: null);

        Assert.NotEqual(before, File.ReadAllBytes(path));
    }

    //a failed start must restore both the server and the stored setting, since the server reads the profile's active path
    [Fact]
    public void A_FAILED_RESTART_PUTS_THE_SERVER_AND_THE_SETTING_BACK()
    {
        var f = new Frames();
        var model = TwoFiles();
        var asked = new List<(string? From, string? To)>();

        var said = QuantEdit.Switch(_dir, model, "Q6_K", f.Serving, f.Ask,
            restart: (previous, reloaded) =>
            {
                asked.Add((Active(previous), Active(reloaded)));
                return false;   //false means the new file did not load.
            });

        //prove the restart was really attempted and aimed at the new file.
        Assert.Equal([("Q4_K_M", "Q6_K")], asked);
        Assert.Equal("Q4_K_M", Active(Model.Load(_dir, model.Id)));
        Assert.Contains("stayed on Q4_K_M", said, StringComparison.Ordinal);
    }

    //nothing serving: a plain write, no restart and no screen. the restart it's given must go uncalled, which passing nothing could not prove
    [Fact]
    public void WITH_NOTHING_SERVING_THE_SWITCH_WRITES_AND_TOUCHES_NO_SERVER()
    {
        var model = TwoFiles();
        var asked = 0;
        var restarted = 0;

        var said = QuantEdit.Switch(_dir, model, "Q6_K",
            serving: () => false,
            ask: _ => { asked++; return true; },
            restart: (_, _) => { restarted++; return true; });

        Assert.Equal(0, asked);       //no consent question may be asked when nothing serves.
        Assert.Equal(0, restarted);
        Assert.Equal("Q6_K", Active(Model.Load(_dir, model.Id)));
        Assert.Equal("gemma-4-e4b-it will use Q6_K from now on", said);
    }

    //read the quant off the profile, a remembered copy can go stale
    private static string? Active(Model m) => m.Profile.Files.Single(f => f.Active).Quant;
    //a restart that throws still puts the setting back, since the profile would otherwise name a file that failed to load. the exception still reaches the caller
    [Fact]
    public void A_RESTART_THAT_THROWS_STILL_PUTS_THE_SETTING_BACK()
    {
        var f = new Frames();
        var model = TwoFiles();

        Assert.Throws<InvalidOperationException>(() =>
            QuantEdit.Switch(_dir, model, "Q6_K", f.Serving, f.Ask,
                restart: (_, _) => throw new InvalidOperationException("the binary is not there")));

        Assert.Equal("Q4_K_M", Active(Model.Load(_dir, model.Id)));
    }
}
