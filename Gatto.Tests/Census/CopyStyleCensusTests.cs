using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//the command layer's two copy rules pinned per file, since a census cannot tell a body line from a json key or an exception message
public class CopyStyleCensusTests
{
    //the surface list, so a file that joins the command layer is an edit here rather than a silent gap
    private static readonly (string Path, int EmDash, int Capital)[] Surfaces =
    [
        ("Gatto/Cli/ArgRouter.cs", 0, 0),
        ("Gatto/Cli/AuditionConsole.cs", 0, 0),
        ("Gatto/Cli/ConnectContext.cs", 0, 0),
        ("Gatto/Cli/ContextWarning.cs", 0, 0),
        ("Gatto/Cli/Doctor.cs", 0, 0),
        ("Gatto/Cli/FatalPause.cs", 0, 0),
        ("Gatto/Cli/GattoApp.cs", 0, 2),
        ("Gatto/Cli/QuantEdit.cs", 0, 0),
        ("Gatto/Cli/SelfInstall.cs", 0, 0),
        ("Gatto/Cli/Uninstall.cs", 0, 3),
        ("Gatto/Cli/UninstallConsent.cs", 2, 10),
        ("Gatto/Cli/UnmanagedSession.cs", 0, 0),
        ("Gatto/Cli/UpdateCheck.cs", 0, 0),
        ("Gatto/Cli/UpdateCommand.cs", 0, 0),
        ("Gatto/Cli/UpdateDownload.cs", 0, 0),
        ("Gatto/Cli/UpdateSeam.cs", 2, 2),
        ("Gatto/Roles/Audition/AuditionReport.cs", 0, 0),
        ("Gatto/Roles/Audition/AuditionRunner.cs", 0, 1),
        ("Gatto/Roles/Audition/TaskWords.cs", 0, 0),
    ];

    private static readonly Regex EmDash = new("—", RegexOptions.Compiled);

    //a literal that opens with a space continues a sentence and keeps its capital, and a space inside is the test for prose
    private static readonly Regex CapitalOpener = new(@"^[A-Z]", RegexOptions.Compiled);

    //a segment glued behind a literal that ends a sentence continues it and keeps its capital, a capital after a variable is still a site
    private static readonly Regex ContinuesAfterFullStop = new(@"[.!?]\s*""\s*\+\s*$", RegexOptions.Compiled);

    private sealed record Site(int Line, string Text);

    //count on the code view, so a literal nested in an interpolation hole is not counted twice. report the raw line, the reader wants the sentence
    private static List<Site> EmDashSites(string source)
    {
        var raw = source.Split('\n');
        var code = SourceTree.WithoutComments(source).Split('\n');
        Assert.True(code.Length == raw.Length,
            $"the code view has {code.Length} lines and the source has {raw.Length}, so every reported "
            + "line number in this census would be wrong. fix the mask before trusting a count.");

        var sites = new List<Site>();
        for (var i = 0; i < code.Length; i++)
            for (var n = EmDash.Matches(code[i]).Count; n > 0; n--)
            {
                var text = raw[i].Trim();
                sites.Add(new Site(i + 1, text[..Math.Min(text.Length, 70)]));
            }
        return sites;
    }

    //every string literal that opens with a capital and holds a space, at the line its opening quote sits on.
    private static List<Site> CapitalOpeners(string source)
    {
        var sites = new List<Site>();
        foreach (var lit in SourceTree.StringLiterals(source))
        {
            var opener = lit.TrimStart('$', '@');
            var fence = 0;
            while (fence < opener.Length && opener[fence] == '"') fence++;
            if (fence == 0) continue;

            var text = fence >= 3 ? RawBody(opener, fence) : Between(opener, fence);
            if (text.Length == 0 || !text.Contains(' ') || !CapitalOpener.IsMatch(text)) continue;

            var at = source.IndexOf(lit, StringComparison.Ordinal);
            if (at >= 0 && ContinuesAfterFullStop.IsMatch(source[..at])) continue;

            sites.Add(new Site(at < 0 ? 0 : source[..at].Count(c => c == '\n') + 1,
                text.Length > 70 ? text[..70] : text));
        }
        return sites;
    }

    private static string Between(string literal, int fence)
    {
        var end = literal.LastIndexOf('"');
        return end <= fence ? "" : literal[fence..end].Trim();
    }

    //a raw string's body is the lines between the fences, dedented by the closing fence's column.
    private static string RawBody(string literal, int fence)
    {
        var open = literal.IndexOf('\n');
        var close = literal.LastIndexOf(new string('"', fence), StringComparison.Ordinal);
        if (open < 0 || close < open) return "";
        var lines = literal[(open + 1)..close].Split('\n');
        var indent = lines[^1].Length - lines[^1].TrimStart().Length;
        return string.Join(" ", lines.Select(l => l.Length >= indent ? l[indent..] : l.Trim())
            .Where(l => l.Length > 0));
    }

    //check the pattern against a known match before reading the tree, an absence proves nothing until it does
    [Fact]
    public void THE_EM_DASH_MATCHER_FIRES_ON_A_PLANTED_STRING_AND_NOT_ON_A_COMMENT()
    {
        var planted = string.Join('\n',
        [
            "class A",
            "{",
            "    // a comment with an em dash — invisible to this census on purpose",
            "    string Bad = \"server reachable — run gatto doctor\";",
            "    string Fine = \"server reachable · run gatto doctor\";",
            "}",
        ]);

        var sites = EmDashSites(planted);
        Assert.Single(sites);
        Assert.Contains("server reachable", sites[0].Text, StringComparison.Ordinal);

        Assert.Empty(EmDashSites("class A { string Fine = \"plain, with a comma\"; }"));
        //a literal holding two em dashes counts two, so a single match cannot pass this count
        Assert.Equal(2, EmDashSites("class A { string Two = \"a — b — c\"; }").Count);
    }

    [Fact]
    public void THE_CAPITAL_MATCHER_FIRES_ON_A_PLANTED_BODY_LINE_AND_NOT_ON_A_KEY()
    {
        var planted = string.Join('\n',
        [
            "class A",
            "{",
            "    string Body = \"The program file removes itself as this exits.\";",
            "    string Key = \"default_model\";",
            "    string Lower = \"run gatto doctor when this is done\";",
            "    string Join = \"couldn't compare versions (a vs b). \"",
            "        + \"Nothing was changed.\";",
            "    string Mid = \"a state · \" + \"Capital wrong\";",
            "}",
        ]);

        var hits = CapitalOpeners(planted);

        //only two of the five sample literals are sites, a key, a lowercase line and a continuation after a full stop are not
        Assert.Equal(2, hits.Count);
        Assert.StartsWith("The program file", hits[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Capital wrong", hits[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NO_EM_DASH_IN_PRINTED_OUTPUT_ON_THE_SECTION_EIGHT_SURFACES()
    {
        var over = Report(Surfaces.Select(s => s.Path),
            p => EmDashSites(SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), p))),
            Surfaces.ToDictionary(s => s.Path, s => s.EmDash));

        Assert.True(over.Count == 0,
            "one separator, `·`, and the em dash never appears in printed output. these files are over "
            + "their pin:\n" + string.Join("\n", over)
            + "\nuse `·` on a fact row, or a comma, a colon or a full stop inside a sentence. a pin "
            + "moves down in the commit that sweeps its file, and never up.");
    }

    [Fact]
    public void NO_BODY_LINE_OPENS_WITH_A_CAPITAL_ON_THE_SECTION_EIGHT_SURFACES()
    {
        var over = Report(Surfaces.Select(s => s.Path),
            p => CapitalOpeners(SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), p))),
            Surfaces.ToDictionary(s => s.Path, s => s.Capital));

        Assert.True(over.Count == 0,
            "every body line opens lowercase, so one command reads as one voice:\n" + string.Join("\n", over)
            + "\na proper noun, a button caption or a shell command earns an exception here, and that "
            + "is the only reason a pin sits above zero. lower it in the commit that sweeps its file.");
    }

    //a missing file is a census that reads less than it claims, so it fails here and not as a clean zero.
    private static List<string> Report(IEnumerable<string> paths,
        Func<string, List<Site>> sites, Dictionary<string, int> pins)
    {
        var over = new List<string>();
        foreach (var path in paths)
        {
            var file = Path.Combine(SourceTree.RepoRoot(), path);
            Assert.True(File.Exists(file), $"{path} is listed and missing, so this census reads less than it claims.");

            var found = sites(path);
            if (found.Count <= pins[path]) continue;
            over.Add($"  {path}: {found.Count}, pinned at {pins[path]}");
            //one row per literal, with how many separators it holds, so a double is not read as two sites
            foreach (var g in found.GroupBy(s => (s.Line, s.Text)).OrderBy(g => g.Key.Line))
                over.Add($"      {g.Key.Line}{(g.Count() > 1 ? " x" + g.Count() : "")}: {g.Key.Text}");
        }
        return over;
    }
}
