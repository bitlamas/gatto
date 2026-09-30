using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Home;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the ambient sweep stays at the engine's rule against a full-disk crawl, a typed path is consent to look deeper
public class ScanDepthTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs) { try { Directory.Delete(d, recursive: true); } catch (Exception) { } }
    }

    private string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gatto-depth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    private static void WriteGguf(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.context_length", 4096);
        }));
    }

    private static string[] NamesIn(IReadOnlyList<FoundModel> found) =>
        [.. found.Select(f => Path.GetFileName(f.Path))];

    //three levels below the root is the first depth the ambient sweep cannot reach, a fixture one level shallower passes either way
    [Fact]
    public void THE_TYPED_DEPTH_REACHES_A_FILE_THE_AMBIENT_DEPTH_CANNOT()
    {
        var root = NewTempDir();
        WriteGguf(Path.Combine(root, "a", "b", "c", "buried.gguf"));

        Assert.DoesNotContain("buried.gguf", NamesIn(ModelDiscovery.Scan([root], ModelDiscovery.AmbientDepth)));
        Assert.Contains("buried.gguf", NamesIn(ModelDiscovery.Scan([root], ModelDiscovery.TypedDepth)));
    }

    //the typed depth has a ceiling too, consent to one folder is not consent to a whole drive
    [Fact]
    public void THE_TYPED_DEPTH_IS_A_CEILING_TOO()
    {
        var root = NewTempDir();
        WriteGguf(Path.Combine(root, "a", "b", "c", "d", "e", "deeper.gguf"));

        Assert.DoesNotContain("deeper.gguf", NamesIn(ModelDiscovery.Scan([root], ModelDiscovery.TypedDepth)));
    }

    //both depths reach this layout (the fixture above is chosen because it discriminates)
    [Fact]
    public void LM_STUDIOS_LAYOUT_IS_REACHED_BY_THE_AMBIENT_SWEEP_TOO()
    {
        var root = NewTempDir();
        WriteGguf(Path.Combine(root, "unsloth", "gemma-4-26B-A4B-it", "gemma-4-26B-A4B-it-Q4_K_M.gguf"));

        Assert.Contains("gemma-4-26B-A4B-it-Q4_K_M.gguf",
            NamesIn(ModelDiscovery.Scan([root], ModelDiscovery.AmbientDepth)));
    }

    //one sweep applies two depths, the ambient roots keep the engine's ceiling and the typed root gets the deeper scan
    [Fact]
    public void THE_SWEEP_WALKS_THE_TYPED_ROOT_DEEPER_THAN_THE_AMBIENT_ONES()
    {
        var home = NewTempDir();
        GattoHome.EnsureInitialized(home);
        //point the ambient Downloads root at an empty temp folder, otherwise the sweep reads the real Downloads and passes for the wrong reason
        using var probes = new LiveSetupProbes(home, GlyphSet.Unicode, TextWriter.Null, downloadsDir: NewTempDir());

        //place the same shape under both kinds of root, only the typed one may reach it
        var weights = ModelLocation.SuggestedDir(home, null);
        WriteGguf(Path.Combine(weights, "a", "b", "c", "ambient.gguf"));

        var typed = NewTempDir();
        WriteGguf(Path.Combine(typed, "a", "b", "c", "typed.gguf"));

        var names = NamesIn(probes.Scan(typed).Found);

        Assert.Contains("typed.gguf", names);
        Assert.DoesNotContain("ambient.gguf", names);
    }

    //a shard set lives in one directory, so the two passes group it under the same path
    [Fact]
    public void A_SET_REACHED_BY_BOTH_ROOTS_IS_STILL_ONE_ROW_WITH_EVERY_SHARD_COUNTED()
    {
        var home = NewTempDir();
        GattoHome.EnsureInitialized(home);
        using var probes = new LiveSetupProbes(home, GlyphSet.Unicode, TextWriter.Null, downloadsDir: NewTempDir());

        var weights = ModelLocation.SuggestedDir(home, null);
        var dir = Path.Combine(weights, "big");
        for (var i = 1; i <= 3; i++)
            WriteGguf(Path.Combine(dir, $"big-0000{i}-of-00003.gguf"));

        //the typed root is the weights folder's parent, so both scans reach the same directory.
        var found = probes.Scan(Path.GetDirectoryName(weights)!).Found;

        var m = Assert.Single(found);
        Assert.Equal(3, m.ShardsPresent);
        Assert.True(m.IsComplete);
    }
}
