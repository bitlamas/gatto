using Gatto.Cli.Setup;

namespace Gatto.Tests.Census;

//an unmasked census cannot lose a span, but it can count a token inside prose. count each bound token raw and masked, and a change means it needs SourceMask.
public class CensusTokenCensusTests
{
    //list each token by hand. a token is a string inside a method, and code that extracts it would be a second parser that can be wrong.
    public static TheoryData<string, string, string> Bounds()
    {
        var data = new TheoryData<string, string, string>();
        void Add(string census, string file, params string[] tokens)
        {
            foreach (var t in tokens) data.Add(census, file, t);
        }

        Add("ContextWalkCallSiteCensusTests", "Gatto/Cli/GattoApp.cs", "class GattoApp");
        Add("LaunchRefusalCensusTests", "Gatto/Cli/GattoApp.cs", "ServeNotice.Refusing(", "return 1;");
        Add("PlainFaceCensusTests", "Gatto/Cli/GattoApp.cs", "new Setup.Tui.TuiWizardSurface(");
        Add("ServerBinaryCensusTests", "Gatto/Cli/GattoApp.cs", "new ServeManager(", "config.LlamaServer");
        Add("FixtureScreenKeyCensusTests", "Gatto/Cli/Setup/SetupFlow.cs", "public const string ");
        Add("AnswerableScreenCensusTests", "Gatto/Cli/Setup/SetupFlow.cs", "return _awaiting switch");
        return data;
    }

    //these censuses must never mask, since masking deletes the prose they bound on. exclude one token at a time, so a census can bound in code and in a literal.
    private static readonly (string Census, string Token, string Why)[] ProseSubject =
    [
        ("AnswerableScreenCensusTests", "has no handler for the answer",
         "the bound IS the throw message, so every occurrence is a string literal"),
        ("DocSummaryCensusTests", "<summary>",
         "the subject IS the doc comment, so masking deletes what it counts"),
    ];

    //the tree keeps no doc summary once its comments are one line, so the summary row counts this sample
    private const string DocSample = "/// <summary>what the member is for</summary>\npublic void M() { }\n";

    private static string Source(string file) =>
        SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), file.Replace('/', Path.DirectorySeparatorChar)));

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    //a bound token must count the same raw and masked. a changed count means the census reads its token out of a comment or a string, and it must then use the mask
    [Theory]
    [MemberData(nameof(Bounds))]
    public void A_CENSUS_BOUND_MEANS_THE_SAME_MASKED_AND_UNMASKED(string census, string file, string token)
    {
        var src = Source(file);

        var raw = Count(src, token);
        var masked = Count(SourceMask.Mask(src), token);

        Assert.True(raw > 0, $"{census}'s bound `{token}` is not in {file} at all, so it reads nothing");
        Assert.True(raw == masked,
            $"{census} counts `{token}` {raw} times in {file} and {masked} times with comments and "
            + "string literals blanked, so it is reading its answer out of prose. Either the token "
            + "moved into a literal or the census needs SourceMask.");
    }

    //put a token inside a literal in the fixture string so the two counts differ. without it, the rows above could be a comparison that agrees with itself.
    [Fact]
    public void AND_THE_DIFF_FIRES_ON_A_TOKEN_THAT_LIVES_IN_A_LITERAL()
    {
        const string fixture = "class C\n"
            + "{\n"
            + "    // a comment naming Resolve(home)\n"
            + "    const string Help = \"Resolve(home) is what it does\";\n"
            + "}\n";

        var raw = Count(fixture, "Resolve(home)");
        var masked = Count(SourceMask.Mask(fixture), "Resolve(home)");

        Assert.Equal(2, raw);
        Assert.Equal(0, masked);
    }

    //each excluded token must count fewer times masked than raw, so the exclusion stays necessary. a token that counts the same belongs in the theory instead.
    [Fact]
    public void THE_EXCLUDED_CENSUSES_REALLY_DO_BOUND_ON_PROSE()
    {
        var flow = Source("Gatto/Cli/Setup/SetupFlow.cs");

        foreach (var (census, token, why) in ProseSubject)
        {
            var src = census == "DocSummaryCensusTests" ? DocSample : flow;
            var raw = Count(src, token);
            var masked = Count(SourceMask.Mask(src), token);

            Assert.True(raw > 0, $"{census}'s bound `{token}` is not there at all");
            Assert.True(masked < raw,
                $"{census} is excluded because {why}, but `{token}` now counts {raw} raw and "
                + $"{masked} masked — it is code, so the exclusion has become a gap");
        }
    }
}
