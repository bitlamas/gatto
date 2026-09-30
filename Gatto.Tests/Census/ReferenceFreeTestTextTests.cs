using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//test text publishes with the tree, so no test string or file name cites a ruling, task or finding number, a spec section or a ledger row
public class ReferenceFreeTestTextTests
{
    //the shapes of a ruling, decision, item, task, finding and spec section number. ledger counts as a word of its own, since a file suffix or a key is the product's
    private static readonly Regex Reference = new(
        @"(?<![A-Za-z0-9\\])(?:[Rr]\d{2,3}|D\d{1,2}|U-0\d{3}|T\d{1,2}[a-z]?|(?:CW|SB|CG)-?\d{1,2})(?![0-9:])|\u00a7\s?\d|(?<![A-Za-z0-9_\-])[Ll]edger(?![A-Za-z0-9_.\-])",
        RegexOptions.Compiled);

    //an interpolation hole is code, so a number format inside one is not text
    private static readonly Regex Hole = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    //these files plant references on purpose, as the fixtures of a census
    private static readonly string[] Planters =
    [
        "Gatto.Tests/Census/ReferenceFreeTestTextTests.cs",
        "Gatto.Tests/Census/OneLineCommentCensusTests.cs",
        "Gatto.Tests/Census/ReferenceFreeIdentifierTests.cs",
    ];

    private static string Tests => Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests");

    private static IEnumerable<string> TestFiles(string pattern) =>
        Directory.EnumerateFiles(Tests, pattern, SearchOption.AllDirectories)
            .Where(f => !SourceTree.HasFolderBelow(Tests, f, "obj", "bin"))
            .OrderBy(f => f, StringComparer.Ordinal);

    //the literals of a source that cite a reference, holes blanked in an interpolated one
    internal static List<string> Citing(string source) =>
        [.. SourceTree.StringLiterals(source)
            .Where(l => Reference.IsMatch(l.TakeWhile(c => c is '$' or '@').Contains('$') ? Hole.Replace(l, " ") : l))];

    internal static bool NameCites(string fileName) => Reference.IsMatch(fileName);

    private static string Trim(string s) =>
        (s.Length <= 100 ? s : s[..100] + "...").Replace("\n", "\\n");

    [Fact]
    public void NO_STRING_UNDER_THE_TESTS_CITES_A_REFERENCE()
    {
        var found = new List<string>();
        var files = 0;
        var literals = 0;
        foreach (var file in TestFiles("*.cs"))
        {
            var rel = Path.GetRelativePath(SourceTree.RepoRoot(), file).Replace('\\', '/');
            files++;
            var source = SourceTree.Read(file);
            literals += SourceTree.StringLiterals(source).Count;
            if (Planters.Contains(rel, StringComparer.Ordinal)) continue;
            found.AddRange(Citing(source).Select(l => $"{rel}: {Trim(l)}"));
        }

        //a reader that found nothing would pass every file
        Assert.True(files > 300, $"the census read only {files} test files, so the reader is broken");
        Assert.True(literals > 20000, $"the census read only {literals} literals, so the literal reader is broken");
        Assert.True(found.Count == 0,
            $"{found.Count} test literals cite a number or a ledger row a public reader cannot look up. write the constraint it stands for:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found));
    }

    [Fact]
    public void NO_FILE_NAME_UNDER_THE_TESTS_CITES_A_REFERENCE()
    {
        var all = TestFiles("*").ToList();
        var found = all.Where(f => NameCites(Path.GetFileName(f)))
            .Select(f => Path.GetRelativePath(SourceTree.RepoRoot(), f).Replace('\\', '/')).ToList();

        Assert.True(all.Count > 300, $"the census read only {all.Count} test file names, so the walk is broken");
        Assert.True(found.Count == 0,
            "these test file names cite a number a public reader cannot look up. name what the file tests:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found));
    }

    //one planted literal for each spelling the rule refuses
    [Theory]
    [InlineData("\"as R67 rules\"")]
    [InlineData("\"R186's converse\"")]
    [InlineData("\"schema text must follow D3\"")]
    [InlineData("\"the spec's D14\"")]
    [InlineData("\"closes U-0393\"")]
    [InlineData("\"T5, wizard rebuild\"")]
    [InlineData("\"the verdict rows of T19\"")]
    [InlineData("\"T13a moved the writes\"")]
    [InlineData("\"the deviation ledger\"")]
    [InlineData("\"(Ledger 241)\"")]
    [InlineData("\"gatto-r132-walk-\"")]
    [InlineData("$\"{n} lines the rule R48 names\"")]
    [InlineData("@\"R109 in a verbatim string\"")]
    [InlineData("$@\"{n} and R60\"")]
    [InlineData("\"\"\"\n        R55 in a raw string\n        \"\"\"")]
    [InlineData("\"the two visual languages (CW-29)\"")]
    [InlineData("\"the composer took the reply, SB-4\"")]
    [InlineData("\"CG-14 found it\"")]
    [InlineData("\"\u00a75.7's whole posture\"")]
    [InlineData("\"one of \u00a74's failure arms\"")]
    [InlineData("\"\u00a7 6.8 exists to prevent it\"")]
    public void A_LITERAL_THAT_CITES_A_REFERENCE_IS_REFUSED(string literal) =>
        Assert.Single(Citing("class A\n{\n    int n = 1;\n    string s = " + literal + ";\n}\n"));

    //a format in a hole, the product's ledger file and key, a tool call id, a time of day and a quant all pass
    [Theory]
    [InlineData("$\"m-{i:D5}-of-00003.gguf\"")]
    [InlineData("$\"o/m{i:D3}\"")]
    [InlineData("\".ledger.jsonl\"")]
    [InlineData("\"citation_ledger\"")]
    [InlineData("\"gatto-ledger-test-\"")]
    [InlineData("\"t1\"")]
    [InlineData("\"2026-07-01T00:00:00Z\"")]
    [InlineData("\"T00:00:00Z\"")]
    [InlineData("\"Q4_K_M\"")]
    [InlineData("\"  0%\\r 10%\\r100%\"")]
    [InlineData("\"the R2 register, a U-turn, the D-pad\"")]
    [InlineData("\"UTF-8 and F32\"")]
    [InlineData("\"an A770, the A1 slot, Q4 and F16 weights\"")]
    [InlineData("\"SHA-256 and a CW rotation\"")]
    public void A_LITERAL_WITHOUT_A_REFERENCE_PASSES(string literal) =>
        Assert.Empty(Citing("class A\n{\n    int i = 1;\n    string s = " + literal + ";\n}\n"));

    //a key name is code and a reference in a comment is the comment census's, so only literals are read
    [Fact]
    public void CODE_AND_COMMENTS_ARE_NOT_READ()
    {
        var source = "class A\n{\n    //as R67 rules\n    var k = System.ConsoleKey.D1;\n    var t = T5;\n}\n";
        Assert.Empty(Citing(source));
    }

    [Theory]
    [InlineData("R67VocabularySweepTests.cs", true)]
    [InlineData("R92DigitTests.cs", true)]
    [InlineData("U-0455-walk.txt", true)]
    [InlineData("D3StyleTests.cs", true)]
    [InlineData("T5WalkTests.cs", true)]
    [InlineData("T13aWalkTests.cs", true)]
    [InlineData("CW8AskCursorTests.cs", true)]
    [InlineData("SB-4-probe.txt", true)]
    [InlineData("CitationLedgerTests.cs", false)]
    [InlineData("QuantTokenTests.cs", false)]
    [InlineData("s1.ledger.jsonl", false)]
    public void A_FILE_NAME_IS_READ_BY_THE_SAME_RULE(string name, bool cites) =>
        Assert.Equal(cites, NameCites(name));
}
