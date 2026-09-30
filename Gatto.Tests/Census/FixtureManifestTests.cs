using System.Security.Cryptography;
using Xunit;

namespace Gatto.Tests.Census;

//every file under Fixtures is listed in PUBLIC.txt with its hash, so a new or edited fixture fails until someone reads it whole and lists it again
public class FixtureManifestTests
{
    private static string FixturesDir() => Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests", "Fixtures");

    //path from Fixtures with forward slashes, a space, lowercase hex SHA-256
    internal static SortedDictionary<string, string> Tree(string dir) =>
        new(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("PUBLIC.txt", StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
                f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))), StringComparer.Ordinal);

    internal static SortedDictionary<string, string> Manifest(string text) =>
        new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[0], p => p[1]), StringComparer.Ordinal);

    internal static List<string> Differences(SortedDictionary<string, string> tree, SortedDictionary<string, string> manifest)
    {
        var d = new List<string>();
        foreach (var (path, hash) in tree)
            if (!manifest.TryGetValue(path, out var listed)) d.Add($"unlisted: {path}");
            else if (listed != hash) d.Add($"changed: {path}");
        foreach (var path in manifest.Keys.Where(p => !tree.ContainsKey(p))) d.Add($"listed but gone: {path}");
        return d;
    }

    [Fact]
    public void EVERY_FIXTURE_IS_LISTED_WITH_ITS_HASH()
    {
        var dir = FixturesDir();
        var tree = Tree(dir);
        Assert.True(tree.Count > 20, $"the census swept {tree.Count} fixtures, which is not this tree");
        var manifest = Manifest(SourceTree.Read(Path.Combine(dir, "PUBLIC.txt")));

        var differences = Differences(tree, manifest);

        Assert.True(differences.Count == 0,
            "a fixture is listed in Fixtures/PUBLIC.txt with its SHA-256 once it was read whole; "
            + "read the file, then list it again:\n  " + string.Join("\n  ", differences));
    }

    [Fact]
    public void THE_MANIFEST_IS_SORTED_LF_AND_LOWERCASE_HEX()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir(), "PUBLIC.txt"));
        Assert.DoesNotContain((byte)'\r', bytes);
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(lines.OrderBy(l => l, StringComparer.Ordinal), lines);
        Assert.All(lines, l => Assert.Matches("^[^ ]+ [0-9a-f]{64}$", l));
    }

    //the three differences the census reports, each planted into a tree and a manifest that otherwise agree
    [Fact]
    public void THE_CENSUS_SEES_A_NEW_AN_EDITED_AND_A_MISSING_FIXTURE()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-manifest-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
            var manifest = Manifest(string.Join("\n", Tree(dir).Select(kv => $"{kv.Key} {kv.Value}")));
            Assert.Empty(Differences(Tree(dir), manifest));

            File.WriteAllText(Path.Combine(dir, "b.txt"), "changed");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "c");
            File.Delete(Path.Combine(dir, "a.txt"));

            Assert.Equal(["changed: b.txt", "unlisted: c.txt", "listed but gone: a.txt"], Differences(Tree(dir), manifest));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
