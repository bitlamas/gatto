namespace Gatto.Tests.Census;

//no screen may promise that gatto asks about the graphics card, since no code asks. a machine with no usable graphics device is told that fact.
public class AskPromiseCensusTests
{
    //match each retired phrase as a substring. the sentences were built from concatenated literals, so a pattern anchored to a whole line misses them.
    private static readonly string[] RetiredPhrases =
    [
        "will ask rather than guess",
        "asked instead of guessed",
        "gatto can ask about the card",
        "will ask before pricing",
    ];

    //the real census and its known match must share this scan, so the test drives the census itself rather than a copy
    private static IReadOnlyList<string> Scan(string label, string source)
    {
        var hits = new List<string>();
        var lines = SourceTree.WithoutComments(source).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            foreach (var phrase in RetiredPhrases)
                if (lines[i].Contains(phrase, StringComparison.Ordinal))
                    hits.Add($"{label}:{i + 1}  {phrase}");
        return hits;
    }

    [Fact]
    public void NO_SCREEN_PROMISES_AN_ASK_THAT_NOTHING_IMPLEMENTS()
    {
        var offenders = new List<string>();

        foreach (var file in SourceTree.ProductionFiles())
        {
            //strip the comments and keep the literals, since a screen prints literals. keep the line numbers, and a phrase split across literals still reads as one run.
            offenders.AddRange(Scan(Path.GetFileName(file), SourceTree.Read(file)));
        }

        Assert.True(offenders.Count == 0,
            "A screen promises an ask that no code implements (the branch that asked about the card is gone):\n  "
            + string.Join("\n  ", offenders));
    }

    //the positive calls the same Scan as the real run, so both check the same pattern. a positive using Contains alone would pass even if the line split broke.
    [Fact]
    public void THE_CENSUS_CATCHES_EVERY_PHRASE_IN_A_LIVE_STRING_LITERAL()
    {
        foreach (var phrase in RetiredPhrases)
        {
            var planted = "namespace X;\nclass Y\n{\n"
                + "    static string Row() => \"the machine screen says " + phrase + " to the user\";\n"
                + "}\n";

            var hits = Scan("planted.cs", planted);
            Assert.Single(hits);
            Assert.Contains(phrase, hits[0], StringComparison.Ordinal);
            Assert.Contains(":4", hits[0], StringComparison.Ordinal);   //the hit must name the line of the literal that holds the phrase
        }
    }

    //a phrase in a comment is prose and not a screen, so the census must ignore it. this test keeps that scope
    [Fact]
    public void A_PHRASE_IN_A_COMMENT_IS_NOT_A_PROMISE()
    {
        foreach (var phrase in RetiredPhrases)
        {
            var planted = "namespace X;\nclass Y\n{\n"
                + "    // the arm this replaced said gatto " + phrase + ", and nothing did\n"
                + "    /// <summary>it " + phrase + "</summary>\n"
                + "    static string Row() => \"an honest sentence\";\n"
                + "}\n";

            Assert.Empty(Scan("planted.cs", planted));
        }

        //ordinary copy that only mentions asking is not a promise either.
        Assert.Empty(Scan("innocent.cs",
            "static string R() => \"gatto asks before anything is downloaded or written\";"));
    }
}
