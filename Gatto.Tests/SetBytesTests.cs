using System.Text.RegularExpressions;
using Gatto.Core.Acquire;
using Gatto.Tests.Census;

namespace Gatto.Tests;

//a model's size is the sum of its shard set, since the first shard can be metadata-only
public class SetBytesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-setbytes-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    //make a file of exactly the given length, without writing content. the subject is a number, so filling 210 MB of zeroes would test the disk.
    private string File_(string name, long bytes)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        fs.SetLength(bytes);
        return path;
    }

    //the set totals 210 MB while the first shard alone is 10 MB, so the two sizes render differently
    private string ShardSet()
    {
        var one = File_("m-00001-of-00003.gguf", 10_000_000);
        File_("m-00002-of-00003.gguf", 100_000_000);
        File_("m-00003-of-00003.gguf", 100_000_000);
        return one;
    }

    [Fact]
    public void A_SHARD_PATH_PRICES_THE_WHOLE_SET()
    {
        Assert.Equal(210_000_000, ModelDiscovery.SetBytesOrNull(ShardSet()));
    }

    //the first shard alone is a different number, so a helper that returned the file's own length would fail here
    [Fact]
    public void AND_THE_FIRST_SHARD_ALONE_IS_A_DIFFERENT_NUMBER()
    {
        var one = ShardSet();

        Assert.Equal(10_000_000, new FileInfo(one).Length);
        Assert.NotEqual(new FileInfo(one).Length, ModelDiscovery.SetBytesOrNull(one));
    }

    //the props answer names whichever shard the server loaded, so any shard must price the same set
    [Fact]
    public void ANY_SHARD_OF_THE_SET_PRICES_THE_SAME_SET()
    {
        ShardSet();

        Assert.Equal(210_000_000,
            ModelDiscovery.SetBytesOrNull(Path.Combine(_dir, "m-00003-of-00003.gguf")));
    }

    [Fact]
    public void A_LONE_FILE_STILL_READS_ITS_OWN_SIZE()
    {
        Assert.Equal(4_096, ModelDiscovery.SetBytesOrNull(File_("solo.gguf", 4_096)));
    }

    //null is not zero, so a surface drops its size clause instead of printing 0 GB
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AN_EMPTY_PATH_IS_NULL(string? path) =>
        Assert.Null(ModelDiscovery.SetBytesOrNull(path));

    [Fact]
    public void A_PATH_WITH_NOTHING_AT_IT_IS_NULL() =>
        Assert.Null(ModelDiscovery.SetBytesOrNull(Path.Combine(_dir, "not-here.gguf")));

    //the scan starts at 00001 and stops at the first gap, so a missing first shard falls back to the given path
    [Fact]
    public void A_SET_MISSING_ITS_FIRST_SHARD_PRICES_WHAT_IS_THERE()
    {
        var two = File_("h-00002-of-00003.gguf", 50_000_000);

        Assert.Equal(50_000_000, ModelDiscovery.SetBytesOrNull(two));
    }

    //the census matches two spellings of a length read, since matching only the direct form stays blind to the defect
    private static readonly (string Name, Regex Pattern)[] Spellings =
    [
        ("direct", new Regex(@"new FileInfo\([^)]*\)\.Length", RegexOptions.Compiled)),
        ("null-safe", new Regex(@"new FileInfo\([^)]*\) is( not)? \{ Exists: true \}", RegexOptions.Compiled)),
    ];

    //exemptions are listed file by file with a reason each
    private static readonly (string File, string Why)[] Exempt =
    [
        ("Gatto/Core/Acquire/ModelDiscovery.cs", "the summing site itself, and the one home"),
        ("Gatto/Core/Acquire/ModelAdoption.cs", "measures ONE file per copy: the loop is already "
            + "over ShardSiblings and each arrival is verified by its own size"),
        ("Gatto/Core/Acquire/HubFetch.cs", "a .part's own bytes, which is what resume needs"),
        ("Gatto/Cli/Uninstall.cs", "the gatto EXE's size, which is one file by definition"),
        ("Gatto/Core/Loop/Permissions/PermissionGate.cs", "the size of the one file a write would replace, never a model"),
    ];

    //a production file that reads a model path's length directly is the shard defect returning under a new name
    [Fact]
    public void NO_PRODUCTION_FILE_PRICES_A_MODEL_BY_ONE_FILES_LENGTH()
    {
        //one known match per spelling and a known miss for every other spelling. one shared match would certify the pattern against itself
        Assert.Matches(Spellings[0].Pattern, "var n = new FileInfo(path).Length;");
        Assert.Matches(Spellings[1].Pattern,
            "return new FileInfo(path) is { Exists: true } f ? f.Length : null;");
        Assert.DoesNotMatch(Spellings[0].Pattern,
            "return new FileInfo(path) is { Exists: true } f ? f.Length : null;");
        Assert.DoesNotMatch(Spellings[1].Pattern, "var n = new FileInfo(path).Length;");
        foreach (var (_, p) in Spellings)
            Assert.DoesNotMatch(p, "var f = new FileInfo(path); if (f.Exists) { }");

        var allowed = Exempt.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = SourceTree.RepoRoot();
        var offenders = new List<string>();
        foreach (var path in SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (allowed.Contains(rel)) continue;
            var code = SourceTree.CodeOnly(SourceTree.Read(path));
            foreach (var (name, pattern) in Spellings)
                if (pattern.IsMatch(code)) offenders.Add($"{rel}  ({name})");
        }

        Assert.True(offenders.Count == 0,
            "these files price something by ONE file's length, and a model path can be a shard:\n  "
            + string.Join("\n  ", offenders));
    }

    //this floor proves every exempt file really holds the read it is exempt for, or the census would pass by seeing nothing
    [Theory]
    [InlineData("Gatto/Core/Acquire/ModelDiscovery.cs")]
    [InlineData("Gatto/Core/Acquire/ModelAdoption.cs")]
    [InlineData("Gatto/Core/Acquire/HubFetch.cs")]
    [InlineData("Gatto/Cli/Uninstall.cs")]
    public void AND_EVERY_EXEMPT_FILE_REALLY_CARRIES_THE_READ_IT_IS_EXEMPT_FOR(string rel)
    {
        Assert.Contains(rel, Exempt.Select(e => e.File));

        var code = SourceTree.CodeOnly(SourceTree.Read(
            Path.Combine(SourceTree.RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains(Spellings, s => s.Pattern.IsMatch(code));
    }
}
