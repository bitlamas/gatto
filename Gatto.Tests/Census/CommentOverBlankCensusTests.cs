namespace Gatto.Tests.Census;

//a comment over a blank line heads a group of members, or it has drifted from the code it names. the count is pinned per project, so a new one is a decision
public class CommentOverBlankCensusTests
{
    //measured at the tree after every drifted comment moved onto its subject or went. what is left heads a group
    private static readonly Dictionary<string, int> Pinned = new()
    {
        ["Gatto"] = 25,
        ["Gatto.Terminal"] = 3,
        ["Gatto.Tests"] = 273,
        ["Gatto.Terminal.Tests"] = 7,
    };

    //the 1-based lines of the comment-only lines whose next line is blank. a comment after code, a doc comment and a // inside a literal do not count
    internal static List<int> OverBlank(string source)
    {
        var raw = source.Split('\n');
        var masked = SourceTree.WithoutComments(source).Split('\n');
        Assert.True(raw.Length == masked.Length, "the comment mask changed the number of lines");

        var found = new List<int>();
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            var text = raw[i].TrimStart();
            if (masked[i].Trim().Length > 0 || !text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("///", StringComparison.Ordinal)) continue;
            if (raw[i + 1].Trim().Length == 0) found.Add(i + 1);
        }
        return found;
    }

    [Fact]
    public void THE_COMMENTS_OVER_A_BLANK_LINE_ARE_THE_PINNED_COUNT_IN_EVERY_PROJECT()
    {
        var files = SourceTree.ProductionFiles().Concat(SourceTree.TestFiles()).ToList();
        //a reader that found no files would match a pin of zero
        Assert.True(files.Count > 200, $"the census read only {files.Count} source files, so the reader is broken");

        var root = SourceTree.RepoRoot();
        var counts = new Dictionary<string, int>();
        var where = new List<string>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var project = rel[..rel.IndexOf('/')];
            var lines = OverBlank(SourceTree.Read(file));
            counts[project] = counts.GetValueOrDefault(project) + lines.Count;
            where.AddRange(lines.Select(n => $"{rel}:{n}"));
        }

        Assert.Equal(Pinned.Keys.Order(StringComparer.Ordinal), counts.Keys.Order(StringComparer.Ordinal));
        foreach (var (project, pin) in Pinned)
            Assert.True(counts[project] == pin,
                $"{project} holds {counts[project]} comments over a blank line, pinned at {pin}. move a drifted one onto the code it names, "
                + "or change the pin for a heading over a group:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", where.Where(w => w.StartsWith(project + "/", StringComparison.Ordinal))));
    }

    [Fact]
    public void THE_CENSUS_COUNTS_A_COMMENT_OVER_A_BLANK_AND_NOTHING_ELSE()
    {
        var planted = string.Join('\n',
        [
            "//over a blank",
            "",
            "class A",
            "{",
            "    //over code",
            "    int x = 1; //after code, over a blank",
            "",
            "    /// a doc comment over a blank",
            "",
            "    string s = \"\"\"",
            "        // inside a raw string",
            "",
            "        \"\"\";",
            "    //indented, over a line of spaces",
            "    ",
            "    int y;",
            "}",
            "//the last line",
        ]);

        Assert.Equal([1, 14], OverBlank(planted));
    }
}
