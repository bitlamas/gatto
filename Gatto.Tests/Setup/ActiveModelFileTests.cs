using Gatto.Cli.Setup;
using Gatto.Core.Home;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the model row's path and size must come from one read, so the two lines agree. the test drives the live probes, since a fake returns only what it was told
public class ActiveModelFileTests
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-amf-").FullName;

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    //scaffold a real model the way the wizard's own apply does, and return its id.
    private string Scaffold()
    {
        GattoHome.EnsureInitialized(_home);
        var gguf = Path.Combine(_home, "tiny.gguf");
        File.Copy(Fixture("tiny.gguf"), gguf);
        Assert.Null(WriteSetApply.Apply(
            _home,
            new WriteSet { CreateModel = new WriteSet.Model(gguf, Port: 1235, Context: 8192) },
            out var modelId));
        Assert.NotNull(modelId);
        return modelId!;
    }

    [Fact]
    public void IT_REPORTS_THE_ACTIVE_FILES_PATH_AND_ITS_SIZE_ON_DISK()
    {
        var id = Scaffold();
        var probes = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode);

        var got = probes.ActiveModelFile(id);

        Assert.NotNull(got);
        //assert the size against the file rather than a typed literal. a literal pins the fixture's length in a second place and goes red on unrelated changes
        Assert.Equal(new FileInfo(got!.Value.Path).Length, got.Value.Bytes);
        Assert.True(got.Value.Bytes > 0, "a zero-byte answer is a file nobody measured");

        //the two rows must describe one file, so compare the name with ActiveFileFor's answer rather than a literal
        Assert.Equal(probes.ActiveFileFor(id), Path.GetFileName(got.Value.Path));
    }

    //drive the negative by deleting the file, so a stub returning a size for a path it never opened fails
    [Fact]
    public void A_PROFILE_NAMING_A_FILE_THAT_IS_GONE_SAYS_NOTHING()
    {
        var id = Scaffold();
        var probes = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode);
        var live = probes.ActiveModelFile(id);
        Assert.NotNull(live);           //the probe answered before the delete, so the later null is caused by the delete itself.

        File.Delete(live!.Value.Path);

        Assert.Null(probes.ActiveModelFile(id));
    }

    //an id with no profile gives null like ActiveFileFor does, since every failure there is one answer
    [Fact]
    public void AN_UNKNOWN_MODEL_SAYS_NOTHING()
    {
        GattoHome.EnsureInitialized(_home);

        Assert.Null(new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).ActiveModelFile("no-such-model"));
    }
}
