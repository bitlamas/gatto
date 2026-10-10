namespace Gatto.Tests.Census;

//a mark is not a constraint, so no comment holds the warning or the forbidding sign. a screen and its assertions keep them as words the user reads
public class WarningMarkCensusTests
{
    private static readonly char[] Marks = [(char)0x26A0, (char)0x26D4];

    //every .cs file of every project with the tests, and the shipped scripts, since the sync publishes all their comments
    private static List<string> Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Gatto", "Shipped"), "*.csx", SearchOption.AllDirectories))
            .Where(f => !SourceTree.HasFolderBelow(root, f, "obj", "bin", "tmp", ".git"))
            .OrderBy(f => f, StringComparer.Ordinal)];

    internal static IReadOnlyList<string> Marked(string source) =>
        [.. SourceTree.Comments(source).Where(c => c.IndexOfAny(Marks) >= 0)];

    [Fact]
    public void NO_COMMENT_HOLDS_THE_WARNING_OR_THE_FORBIDDING_SIGN()
    {
        var root = SourceTree.RepoRoot();
        var files = Files(root);
        Assert.True(files.Count > 500, $"the census swept {files.Count} files, which is not this tree");

        var hits = files
            .SelectMany(f => Marked(SourceTree.Read(f)).Select(c => $"{Path.GetRelativePath(root, f)}: {c.Trim()}"))
            .ToList();

        Assert.True(hits.Count == 0,
            "write the fact the mark stood for, or nothing:\n  " + string.Join("\n  ", hits));
    }

    [Fact]
    public void THE_CENSUS_SEES_A_MARKED_COMMENT_and_passes_a_MARKED_LITERAL()
    {
        Assert.Single(Marked("var x = 1; //" + Marks[0] + " careful\n"));
        Assert.Single(Marked("//" + Marks[1] + " never\nvar x = 1;\n"));
        Assert.Empty(Marked("var s = \"5 " + Marks[0] + " RAM\";\n"));
    }
}
