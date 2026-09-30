using Xunit;

namespace Gatto.Tests.Census;

//no source file may hold a raw NUL byte, git calls it binary so the diff and the line-ending rule both go. ESC bytes stay out of this scan
public class SourceByteCensusTests
{
    //read the file as bytes, a decoder could swallow the byte this scans for. the test with a known NUL runs through this same method
    private static List<(string Path, int At)> NulsIn(IEnumerable<string> files) =>
        [.. files
            .Select(p => (Path: p, At: Array.IndexOf(File.ReadAllBytes(p), (byte)0)))
            .Where(hit => hit.At >= 0)];

    private static IEnumerable<string> Sources(string root) =>
        SourceTree.ProductProjects.Append("Gatto.Tests")
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root, project), "*.cs*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csx", StringComparison.Ordinal))
            .Where(f => !SourceTree.HasFolderBelow(root, f, "obj", "bin", "tmp"));

    [Fact]
    public void NO_SOURCE_FILE_CARRIES_A_RAW_NUL()
    {
        var files = Sources(SourceTree.RepoRoot()).ToList();
        Assert.True(files.Count > 100, $"the census swept {files.Count} files, which is not this tree");

        var hits = NulsIn(files);

        Assert.True(hits.Count == 0,
            "a raw NUL byte makes the file binary to git and grep, and exempts it from "
            + "`.gitattributes`' line-ending rule. Write the escape `\\0` instead:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, hits.Select(h => $"  {h.Path} at byte {h.At}")));
    }

    //a sweep that finds nothing proves nothing until the scan fires on a known NUL. ordinary source must not trip it, or the rule becomes unmeetable and gets deleted
    [Fact]
    public void THE_SCAN_FIRES_ON_A_PLANTED_NUL_AND_NOT_ON_ORDINARY_SOURCE()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-nul-").FullName;
        try
        {
            //the defect is written exactly as it shipped, a char literal holding an actual zero byte, so the scan fires on the real shape
            var planted = Path.Combine(dir, "Planted.cs");
            File.WriteAllBytes(planted, [.. "var c = '"u8.ToArray(), 0, .. "';\n"u8.ToArray()]);

            //the honest spelling of the same line, plus prose that only names the escape, must not trip the scan
            var innocent = Path.Combine(dir, "Innocent.cs");
            File.WriteAllText(innocent, "var c = '\\0';\n// a NUL is written \\0, never as the byte\n");

            var found = NulsIn([planted, innocent]);

            Assert.Equal([planted], found.Select(h => h.Path));
            Assert.Equal("var c = '".Length, found[0].At);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    //a checkout under a tmp folder must still be swept, and an obj folder inside it must still be skipped
    [Fact]
    public void THE_SWEEP_SKIPS_BUILD_FOLDERS_BELOW_THE_ROOT_AND_NOT_ABOVE_IT()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-sweep-").FullName;
        try
        {
            var root = Path.Combine(dir, "tmp", "checkout");
            foreach (var project in SourceTree.ProductProjects.Append("Gatto.Tests"))
                Directory.CreateDirectory(Path.Combine(root, project));
            Directory.CreateDirectory(Path.Combine(root, "Gatto", "obj"));
            var kept = Path.Combine(root, "Gatto", "Root.cs");
            File.WriteAllText(kept, "class Root { }\n");
            File.WriteAllText(Path.Combine(root, "Gatto", "obj", "Generated.cs"), "class Generated { }\n");

            Assert.Equal([kept], Sources(root));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
