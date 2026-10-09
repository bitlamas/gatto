using Gatto.Cli.Setup;
using Gatto.Core.Home;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the live probes read a fetched model's repo and projector off its real profile, and a projector beside a file by its name
public class LocalAddedFactsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-laf-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private string Copy(string folder, string name)
    {
        var dir = Path.Combine(_home, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.Copy(Fixture("tiny.gguf"), path);
        return path;
    }

    [Fact]
    public void A_FETCHED_MODEL_WITH_A_PROJECTOR_READS_BACK_ITS_REPO_AND_ITS_PROJECTOR()
    {
        GattoHome.EnsureInitialized(_home);
        var gguf = Copy("w", "tiny.gguf");
        var projector = Copy("w", "mmproj-F32.gguf");

        Assert.Null(WriteSetApply.Apply(_home, new WriteSet
        {
            CreateModel = new WriteSet.Model(gguf, Port: 1235, Context: 8192,
                Source: new Gatto.Roles.ModelSource("unsloth/tiny-GGUF", "tiny.gguf")),
            Projector = projector,
        }, out var id));

        Assert.Equal(new AddedFacts("unsloth/tiny-GGUF", HasProjector: true),
            ((ISetupProbes)new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode)).Added(id!));
    }

    [Fact]
    public void A_MODEL_ADOPTED_OFF_DISK_HAS_NO_REPO_AND_NO_PROJECTOR()
    {
        GattoHome.EnsureInitialized(_home);
        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(Copy("w", "tiny.gguf"), Port: 1235, Context: 8192) }, out var id));

        var probes = (ISetupProbes)new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode);
        Assert.Equal(new AddedFacts(null, HasProjector: false), probes.Added(id!));
        Assert.Null(probes.Added("no-such-model"));
    }

    [Fact]
    public void A_PROJECTOR_BESIDE_A_FILE_IS_FOUND_BY_ITS_NAME()
    {
        var with = Copy("with", "tiny.gguf");
        Copy("with", "mmproj-F32.gguf");
        var without = Copy("without", "tiny.gguf");
        Copy("without", "other-Q4_K_M.gguf");

        var probes = (ISetupProbes)new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode);

        Assert.True(probes.ProjectorBeside(with));
        Assert.False(probes.ProjectorBeside(without));
    }
}
