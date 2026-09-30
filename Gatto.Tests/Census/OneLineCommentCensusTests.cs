using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a comment is one // line that follows the comment convention, and every file is held to it with no pin
public class OneLineCommentCensusTests
{
    //a comment-only line breaks the rule when it is a doc comment line or a block-comment line. any two comment lines in a row break it
    internal static int RuleBreakingLines(string source)
    {
        var raw = source.Split('\n');
        var masked = SourceTree.WithoutComments(source).Split('\n');
        Assert.True(raw.Length == masked.Length, "the comment mask changed the number of lines");

        var count = 0;
        var run = 0;
        var special = false;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && raw[i].Trim().Length > 0 && masked[i].Trim().Length == 0)
            {
                run++;
                var text = raw[i].TrimStart();
                special |= text.StartsWith("///", StringComparison.Ordinal) || !text.StartsWith("//", StringComparison.Ordinal);
                continue;
            }
            count += run >= 2 ? run : run == 1 && special ? 1 : 0;
            run = 0;
            special = false;
            if (i < raw.Length && BesideCode(raw[i], masked[i])) count++;
        }
        return count;
    }

    //a line of code that also held a block comment, or a doc comment, since only one trailing // comment may share a line with code
    private static bool BesideCode(string raw, string masked)
    {
        if (raw == masked || masked.Trim().Length == 0) return false;
        if (!raw.StartsWith(masked, StringComparison.Ordinal)) return true;
        var tail = raw[masked.Length..];
        return !tail.StartsWith("//", StringComparison.Ordinal) || tail.StartsWith("///", StringComparison.Ordinal);
    }

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(SourceTree.RepoRoot(), "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(SourceTree.RepoRoot(), p).Replace('\\', '/'))
            .Where(p => SourceTree.ProductProjects.Append("Gatto.Tests").Any(d => p.StartsWith(d + "/", StringComparison.Ordinal))
                && !p.Contains("/bin/", StringComparison.Ordinal)
                && !p.Contains("/obj/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public void NO_FILE_HOLDS_A_RULE_BREAKING_COMMENT_LINE()
    {
        var over = new List<string>();
        var seen = 0;
        foreach (var rel in Sources())
        {
            seen++;
            var n = RuleBreakingLines(SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), rel)));
            if (n > 0) over.Add($"{n} {rel}");
        }

        //a reader that found no files would pass every file
        Assert.True(seen > 200, $"the census read only {seen} source files, so the reader is broken");
        Assert.True(over.Count == 0,
            "a comment is one // line. these files hold stacked, doc or block comment lines:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", over)
            + Environment.NewLine + "rewrite each as one line.");
    }

    [Fact]
    public void THE_CENSUS_COUNTS_EVERY_SHAPE_THAT_BREAKS_THE_RULE()
    {
        var planted = string.Join('\n',
        [
            "// two plain",
            "// lines",
            "class A",
            "{",
            "    /// <summary>one doc line</summary>",
            "    int x = 1; // a trailing comment",
            "",
            "    /* one block line */",
            "    int y;",
            "    /*",
            "     a block",
            "    */",
            "}",
        ]);

        Assert.Equal(2 + 1 + 1 + 3, RuleBreakingLines(planted));
    }

    //a block comment that shares its line with code, and a doc comment after code, break the rule as much as one alone on its line
    [Fact]
    public void THE_CENSUS_COUNTS_A_BLOCK_OR_DOC_COMMENT_BESIDE_CODE()
    {
        var planted = string.Join('\n',
        [
            "class A",
            "{",
            "    int a; /* after code */",
            "    /* before code */ int b;",
            "    int c; /* between */ int d;",
            "    int e; /// a doc comment after code",
            "    int f; /*",
            "      a block that opens after code",
            "    */ int g;",
            "    int h; // an ordinary trailing comment",
            "    string s = \"/* not a comment */\";",
            "}",
        ]);

        Assert.Equal(7, RuleBreakingLines(planted));
    }

    //a census that also counted these would fail every file for comments the rule allows
    [Fact]
    public void THE_CENSUS_LEAVES_ONE_LINE_COMMENTS_AND_STRINGS_ALONE()
    {
        var planted = string.Join('\n',
        [
            "//one line above a member",
            "class A",
            "{",
            "    int x = 1; //a trailing comment is allowed",
            "",
            "    //a single line",
            "",
            "    //another single line after a blank line",
            "    string s = \"\"\"",
            "        // not a comment",
            "        // still not a comment",
            "        \"\"\";",
            "    string u = \"// not a comment either\";",
            "}",
        ]);

        Assert.Equal(0, RuleBreakingLines(planted));
    }

    private static readonly string[] Marks = ["\u26D4", "\u26A0"];

    //the lines where a warning or forbidding mark sits inside a comment. the mask keeps string literals, so a footer that prints a mark never counts.
    internal static List<int> MarkedCommentLines(string source)
    {
        var raw = source.Split('\n');
        var masked = SourceTree.WithoutComments(source).Split('\n');
        Assert.True(raw.Length == masked.Length, "the comment mask changed the number of lines");

        static int Count(string line) => Marks.Sum(m => line.Split(m).Length - 1);
        return [.. Enumerable.Range(0, raw.Length).Where(i => Count(raw[i]) > Count(masked[i])).Select(i => i + 1)];
    }

    //a comment states the fact a mark stood for in words
    [Fact]
    public void NO_COMMENT_HOLDS_A_WARNING_OR_FORBIDDING_MARK()
    {
        var found = new List<string>();
        var swept = 0;
        foreach (var rel in Sources())
        {
            swept++;
            found.AddRange(MarkedCommentLines(SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), rel))).Select(n => $"{rel}:{n}"));
        }

        Assert.True(swept > 200, $"the census read only {swept} files, so the reader is broken");
        Assert.True(found.Count == 0,
            "a comment holds a warning or forbidding mark. write the fact the mark stood for, or drop the mark:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found));
    }

    [Fact]
    public void THE_MARK_CENSUS_FIRES_IN_A_COMMENT_AND_NOT_IN_A_STRING()
    {
        var planted = string.Join('\n',
        [
            "//\u26D4 a forbidding mark above a member",
            "class A",
            "{",
            "    int x = 1; //\u26A0 a warning at the end of a line",
            "    const string Footer = \"\u2713 GPU \u00B7 \u26A0 RAM \u00B7 \u2717 too big\";",
            "    string s = \"// \u26D4 not a comment\";",
            "}",
        ]);

        Assert.Equal([1, 4], MarkedCommentLines(planted));
    }

    //the marks and the em dash stand in this file as escapes, since a tool that decodes them while writing the file leaves a census that passes
    [Fact]
    public void THIS_FILE_HOLDS_ITS_MARKS_AS_ESCAPES()
    {
        var path = Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests", "Census", "OneLineCommentCensusTests.cs");
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.All(b => b < 128), $"{path} holds a byte above 127, so an escape was decoded into its character");
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        //each needle is built from two pieces, or this line would hold the escape it looks for
        foreach (var code in new[] { "26D4", "26A0", "2014" })
            Assert.Contains("\\" + "u" + code, text, StringComparison.Ordinal);
    }

    //one line comment as the reader sees it, the text after its slashes and whether code stands before it on the line
    internal readonly record struct LineComment(int Line, string Text, bool Alone);

    //the line comments of a source, the part of each raw line the mask removed, so a comment after code counts
    internal static List<LineComment> LineComments(string source)
    {
        var raw = source.Split('\n');
        var masked = SourceTree.WithoutComments(source).Split('\n');
        Assert.True(raw.Length == masked.Length, "the comment mask changed the number of lines");

        var found = new List<LineComment>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == masked[i] || !raw[i].StartsWith(masked[i], StringComparison.Ordinal)) continue;
            var tail = raw[i][masked[i].Length..];
            if (!tail.StartsWith("//", StringComparison.Ordinal) || tail.StartsWith("///", StringComparison.Ordinal)) continue;
            found.Add(new LineComment(i + 1, tail[2..], masked[i].Trim().Length == 0));
        }
        return found;
    }

    //two or more capitals, then capitals, digits, dots or hyphens, and no letter straight after
    private static readonly Regex Acronym = new(@"^[A-Z]{2,}[A-Z0-9.\-]*(?![A-Za-z])", RegexOptions.Compiled);

    private static readonly Regex SentenceEnd = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly Regex YearShape = new(@"\b20\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex Reference = new(@"\bU-\d{3,4}\b|\bR\d{2,3}\b|\bD\d{1,2}\b", RegexOptions.Compiled);

    private static string[] Sentences(string text) => SentenceEnd.Split(text.Trim());

    private static bool OpensWell(string s) => s.Length > 0 && (char.IsLower(s[0]) || char.IsDigit(s[0]) || Acronym.IsMatch(s));

    //every rule a machine can decide, each with the name a failure prints
    internal static readonly (string Name, Func<string, bool> Breaks)[] Rules =
    [
        ("an empty comment", t => t.Trim().Length == 0),
        ("a space after the slashes", t => t.Length > 0 && char.IsWhiteSpace(t[0]) && t.Trim().Length > 0),
        ("over 240 characters", t => t.Length > 240),
        ("a sentence over 30 words", t => Sentences(t).Any(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 30)),
        ("an opener that is not lowercase, a digit or an acronym", t => t.Trim().Length > 0 && !char.IsWhiteSpace(t[0]) && !OpensWell(t)),
        ("a later sentence that opens with a capital that is no acronym", t => Sentences(t).Skip(1).Any(s => s.Length > 0 && char.IsUpper(s[0]) && !Acronym.IsMatch(s))),
        ("a semicolon", t => t.Contains(';')),
        ("an em dash", t => t.Contains('\u2014')),
        ("a backtick", t => t.Contains('`')),
        ("a year shape", t => YearShape.IsMatch(t)),
        ("an item, ruling or decision number", t => Reference.IsMatch(t)),
    ];

    //every rule each comment of a source breaks, as file, line, rule and the comment's first 80 characters
    internal static List<string> Breaks(string rel, string source) =>
        [.. from c in LineComments(source)
            from r in Rules
            where r.Breaks(c.Text)
            select $"{rel}:{c.Line}: {r.Name}: //{(c.Text.Length > 80 ? c.Text[..80] + "..." : c.Text)}"];

    [Fact]
    public void NO_COMMENT_BREAKS_A_RULE_OF_THE_CONVENTION()
    {
        var found = new List<string>();
        var comments = 0;
        foreach (var rel in Sources())
        {
            var source = SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), rel));
            comments += LineComments(source).Count;
            found.AddRange(Breaks(rel, source));
        }

        Assert.True(comments > 10000, $"the reader found only {comments} comments, so it is broken");
        Assert.True(found.Count == 0, "these comments break the convention:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found));
    }

    //a comment on its own line and one after code count, and no // inside any kind of literal does
    [Fact]
    public void THE_READER_FINDS_EVERY_COMMENT_AND_NO_LITERAL()
    {
        var planted = string.Join('\n',
        [
            "//on its own line",
            "class A",
            "{",
            "    int x = 1; //after code",
            "    string a = \"// in a string\";",
            "    string b = @\"// in a verbatim \"\" string\";",
            "    string c = $\"// in an {x} interpolated string\";",
            "    string d = \"\"\"",
            "        // in a raw string",
            "        of two lines",
            "        \"\"\";",
            "    char q = '\"';",
            "    string e = \"after the quote // still a string\";",
            "}",
        ]);

        Assert.Equal([new LineComment(1, "on its own line", true), new LineComment(4, "after code", false)], LineComments(planted));
    }

    //the rules a single comment breaks, read through the whole reader
    private static List<string> RulesBrokenBy(string comment) =>
        [.. Breaks("planted.cs", "class A\n{\n    int x = 1; " + comment + "\n}\n").Select(b => b.Split(": ")[1])];

    [Theory]
    [InlineData("//", "an empty comment")]
    [InlineData("//   ", "an empty comment")]
    [InlineData("// a space first", "a space after the slashes")]
    [InlineData("//a sentence with a semicolon; like this", "a semicolon")]
    [InlineData("//an em dash \u2014 like this", "an em dash")]
    [InlineData("//a `name` in backticks", "a backtick")]
    [InlineData("//The capital", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//--continue opens the folder", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//`quoted` first", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//\"a quote\" first", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//(an aside) first", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//U+2028 as an opener", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//one sentence. Then a capital", "a later sentence that opens with a capital that is no acronym")]
    [InlineData("//ruled on 2026-09-29", "a year shape")]
    [InlineData("//since 2026 it reads the folder", "a year shape")]
    [InlineData("//the default port 2048 is taken", "a year shape")]
    [InlineData("//keeps U+2028 in the text", "a year shape")]
    [InlineData("//closes U-0393", "an item, ruling or decision number")]
    [InlineData("//as R186 says", "an item, ruling or decision number")]
    [InlineData("//the spec's D14 decides it", "an item, ruling or decision number")]
    public void A_COMMENT_THAT_BREAKS_A_RULE_IS_REFUSED_BY_THAT_RULE(string comment, string rule) =>
        Assert.Contains(rule, RulesBrokenBy(comment));

    [Theory]
    [InlineData("//a lowercase opener")]
    [InlineData("//3 rows fit")]
    [InlineData("//CRLF to LF first")]
    [InlineData("//UTF-8 bytes, read as they are")]
    [InlineData("//one sentence. then another in lowercase. CRLF after a stop is an acronym")]
    [InlineData("//holds 20480 tokens")]
    [InlineData("//at 1920 columns")]
    [InlineData("//tensors in F32 and F16, RGB colours")]
    [InlineData("//the R2 register and a U-turn")]
    [InlineData("//the D-pad keys")]
    public void A_COMMENT_THAT_KEEPS_EVERY_RULE_PASSES(string comment) =>
        Assert.Empty(RulesBrokenBy(comment));

    //a key name inside a literal is data, and only a comment is read
    [Fact]
    public void A_NUMBER_IN_A_STRING_IS_NOT_A_COMMENT()
    {
        var source = "class A\n{\n    string k = \"D1\";\n    string u = \"U-0393 and R186\";\n}\n";
        Assert.Empty(Breaks("planted.cs", source));
    }

    [Theory]
    [InlineData(240, false)]
    [InlineData(241, true)]
    public void THE_LENGTH_LIMIT_IS_240_CHARACTERS(int length, bool refused)
    {
        var comment = "//" + new string('a', length);
        Assert.Equal(refused, RulesBrokenBy(comment).Contains("over 240 characters"));
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public void THE_SENTENCE_LIMIT_IS_30_WORDS(int words, bool refused)
    {
        var comment = "//" + string.Join(' ', Enumerable.Repeat("word", words));
        Assert.Equal(refused, RulesBrokenBy(comment).Contains("a sentence over 30 words"));
    }

    //the comments this census exists for, as they stood before their fix, each refused by the rule it broke
    [Theory]
    [InlineData("//--continue with an id resumes in the folder the session was saved in, so the tools and the saved file read that folder", "an opener that is not lowercase, a digit or an acronym")]
    [InlineData("//a transient `/slots` failure nulls the server count. without the floor at the seen maximum the row dips to the lower estimate for one poll cycle.", "a backtick")]
    [InlineData("//a non-empty string is not enough. the embedded schema must parse and declare the draft 2020-12 dialect.", "a year shape")]
    [InlineData("//drops C0 controls, DEL and C1, while U+2028, U+2029 and bidi overrides survive", "a year shape")]
    [InlineData("//a gutter click toggles an open block or a collapsed thinking block. the rows under a collapsed tool header are content, so a gutter press there selects. an open shell block paints its own rail in these columns and D14 promises a copy from column zero, so a press there selects instead", "over 240 characters")]
    [InlineData("//a gutter click toggles an open block or a collapsed thinking block. the rows under a collapsed tool header are content, so a gutter press there selects. an open shell block paints its own rail in these columns and D14 promises a copy from column zero, so a press there selects instead", "an item, ruling or decision number")]
    public void THE_COMMENTS_OF_RECORD_ARE_REFUSED(string comment, string rule) =>
        Assert.Contains(rule, RulesBrokenBy(comment));

    //two sentences of 20 words each stay under the limit, which counts a sentence, not the comment
    [Fact]
    public void TWO_SENTENCES_OF_20_WORDS_PASS()
    {
        var sentence = string.Join(' ', Enumerable.Repeat("word", 20));
        Assert.DoesNotContain("a sentence over 30 words", RulesBrokenBy("//" + sentence + ". " + sentence));
    }
}
