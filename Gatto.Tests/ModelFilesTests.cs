using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

//the profile holds keys this class has no opinion about, and every guard checks that a bystander key survived
public class ModelFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-files-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string TwoFiles = """
        {
          "files": [
            { "path": "C:\\w\\m-Q4_K_M.gguf", "quant": "Q4_K_M", "active": true },
            { "path": "C:\\w\\m-Q6_K.gguf", "quant": "Q6_K", "active": false }
          ],
          "port": 1235,
          "context": 8192,
          "sampling": { "temperature": 0.7 },
          "extra_args": ["--flash-attn", "on"],
          "api_key": "s3cr3t-value"
        }
        """;

    private string Write(string json = TwoFiles)
    {
        var dir = Path.Combine(_dir, "m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"), json);
        return _dir;
    }

    private ModelProfile Reload() => Model.Load(_dir, "m").Profile;

    //the quant switch changes only which file is active, and nothing else in the profile may move
    [Fact]
    public void THE_SWITCH_MOVES_THE_ACTIVE_FILE()
    {
        ModelFiles.SetActive(Write(), "m", "Q6_K");

        var p = Reload();
        Assert.Equal(@"C:\w\m-Q6_K.gguf", p.ActivePath);
        Assert.Equal(2, p.Files.Count);   //switching must not remove the other file.
    }

    //the switch must leave the tuned settings intact, an edit that drops them destroys the user's sampling, arguments and keys
    [Fact]
    public void AND_EVERY_OTHER_KEY_SURVIVES_THE_SWITCH()
    {
        ModelFiles.SetActive(Write(), "m", "Q6_K");

        var p = Reload();
        Assert.Equal(0.7, p.Sampling!.Value.GetProperty("temperature").GetDouble());
        Assert.Equal(new[] { "--flash-attn", "on" }, p.ExtraArgs);
        Assert.Equal("s3cr3t-value", p.ApiKey);
        Assert.Equal(8192, p.Context);
        Assert.Equal(1235, p.Port);
    }

    //after a switch exactly one file is active, an uncleared old active makes a profile the loader refuses
    [Fact]
    public void AND_THE_OLD_ACTIVE_IS_CLEARED_NOT_JOINED()
    {
        ModelFiles.SetActive(Write(), "m", "Q6_K");

        Assert.Single(Reload().Files, f => f.Active);
    }

    //switching to the already-active file must succeed, refusing it would turn a confirm screen's yes into an error
    [Fact]
    public void SWITCHING_TO_THE_ACTIVE_FILE_IS_A_NO_OP_NOT_A_REFUSAL()
    {
        ModelFiles.SetActive(Write(), "m", "Q4_K_M");

        Assert.Equal(@"C:\w\m-Q4_K_M.gguf", Reload().ActivePath);
    }

    //an unknown quant must be refused, and the refusal names the quants that exist
    [Fact]
    public void AN_UNKNOWN_QUANT_IS_REFUSED_AND_THE_REFUSAL_LISTS_THE_REAL_ONES()
    {
        var ex = Assert.Throws<GattoConfigException>(() => ModelFiles.SetActive(Write(), "m", "IQ1_S"));

        Assert.Contains("IQ1_S", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Q4_K_M", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Q6_K", ex.Message, StringComparison.Ordinal);
    }

    //a delete works per file, the sibling file and the other settings must survive
    [Fact]
    public void REMOVING_A_FILE_LEAVES_THE_SIBLING_AND_THE_SETTINGS()
    {
        ModelFiles.Remove(Write(), "m", "Q6_K");

        var p = Reload();
        Assert.Equal(@"C:\w\m-Q4_K_M.gguf", Assert.Single(p.Files).Path);
        Assert.Equal("s3cr3t-value", p.ApiKey);
    }

    //the active file cannot be removed and the refusal says to switch first, a profile with no active file does not load
    [Fact]
    public void THE_ACTIVE_FILE_CANNOT_BE_REMOVED_AND_THE_REFUSAL_SAYS_SWITCH_FIRST()
    {
        var ex = Assert.Throws<GattoConfigException>(() => ModelFiles.Remove(Write(), "m", "Q4_K_M"));

        Assert.Contains("switch", ex.Message, StringComparison.OrdinalIgnoreCase);
        //a refused removal must leave both files listed
        Assert.Equal(2, Reload().Files.Count);
    }

    //the last file cannot be removed, deleting it is deleting the model itself
    [Fact]
    public void THE_LAST_FILE_CANNOT_BE_REMOVED_EVEN_WHEN_IT_IS_NOT_ACTIVE()
    {
        const string one = """
            { "files": [{ "path": "C:\\w\\m.gguf", "quant": "Q4_K_M", "active": true }],
              "port": 1, "context": 2 }
            """;

        var ex = Assert.Throws<GattoConfigException>(() => ModelFiles.Remove(Write(one), "m", "Q4_K_M"));

        Assert.Contains("only file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remove the model itself", ex.Message, StringComparison.Ordinal);
    }

    //removal edits only the profile, the gguf on disk stays and deleting it needs its own consent
    [Fact]
    public void REMOVING_A_FILE_DOES_NOT_TOUCH_THE_WEIGHTS_ON_DISK()
    {
        var weights = Path.Combine(_dir, "w");
        Directory.CreateDirectory(weights);
        var gguf = Path.Combine(weights, "m-Q6_K.gguf");
        File.WriteAllText(gguf, "weights");

        var json = TwoFiles.Replace(@"C:\\w\\m-Q6_K.gguf", gguf.Replace(@"\", @"\\"),
            StringComparison.Ordinal);
        ModelFiles.Remove(Write(json), "m", "Q6_K");

        Assert.True(File.Exists(gguf), "removing a file from the profile deleted the user's weights");
    }

    //editing loads the profile through the loader first, a profile it refuses is never edited
    [Fact]
    public void A_PROFILE_THE_LOADER_REFUSES_IS_NOT_EDITED()
    {
        const string twoActive = """
            { "files": [{ "path": "a.gguf", "quant": "Q4_K_M", "active": true },
                        { "path": "b.gguf", "quant": "Q6_K", "active": true }],
              "port": 1, "context": 2 }
            """;

        Assert.Throws<GattoConfigException>(() => ModelFiles.SetActive(Write(twoActive), "m", "Q6_K"));
    }
}
