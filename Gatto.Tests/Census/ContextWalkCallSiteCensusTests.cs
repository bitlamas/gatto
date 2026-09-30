namespace Gatto.Tests.Census;

//context files are read at launch and again only by /new, /role and /compact. a new call site would force a mid-session re-prefill, and only this count sees it.
public class ContextWalkCallSiteCensusTests
{
    private const string Call = "ContextFiles.Collect(";

    private static IReadOnlyList<(string File, int Count)> Sites(Func<string, string>? mutate = null)
    {
        var found = new List<(string, int)>();
        foreach (var path in SourceTree.ProductionFiles())
        {
            var text = File.ReadAllText(path);
            //put the call in before CodeOnly runs, so the test proves the whole census through the strip rather than a substring count
            if (mutate is not null) text = mutate(text);
            var code = SourceTree.CodeOnly(text);
            var n = Occurrences(code, Call);
            if (n > 0) found.Add((Path.GetFileName(path), n));
        }
        return found;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void THE_WALK_HAS_EXACTLY_FOUR_PRODUCTION_CALL_SITES()
    {
        var sites = Sites();
        var total = sites.Sum(s => s.Count);

        //the three calls in GattoApp serve launch, the recompose and /role. the doctor check runs the same call rather than a second copy of the logic
        Assert.Equal(4, total);
        Assert.Equal(
            new[] { ("Doctor.cs", 1), ("GattoApp.cs", 3) },
            sites.OrderBy(s => s.File, StringComparer.Ordinal).ToArray());
    }

    //a count is evidence only after the pattern moves when the source does. add a fifth call to the text before CodeOnly runs, so it takes the real path.
    [Fact]
    public void THE_CENSUS_CAN_SEE_A_FIFTH_CALL_SITE()
    {
        var planted = Sites(text => text.Contains("class GattoApp", StringComparison.Ordinal)
            ? text + "\nclass PlantedFifthSite { void M(string cwd) { var _ = "
                   + Call + "cwd, false, null); } }\n"
            : text);

        Assert.Equal(5, planted.Sum(s => s.Count));
    }
}
