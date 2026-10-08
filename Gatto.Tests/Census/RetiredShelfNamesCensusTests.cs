using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//the curated view, the sort axes, the publisher control and default_publisher left the product whole, so no source file names them again
public class RetiredShelfNamesCensusTests
{
    private static readonly Regex Retired = new(
        @"\b(HubSearchView|HubSearchRequest|HubSearchOutcome|ShelfRow|HubLookup|SearchOrder|DefaultView|default_publisher|DefaultPublisher|PreferredPublisher|ApprovedPublishers|CuratedPublisher|HasPublisherSlot|TierFill)\b");

    [Fact]
    public void NO_PRODUCTION_FILE_NAMES_A_RETIRED_SHELF_NAME()
    {
        var hits = SourceTree.ProductionFiles()
            .SelectMany(f => SourceTree.Read(f).Split('\n').Select((line, i) => (f, i, line)))
            .Where(x => Retired.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(SourceTree.RepoRoot(), x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, "retired names are back:\n" + string.Join("\n", hits));
    }

    //the matcher is fired at a planted line first, so a pattern that matches nothing cannot pass the census above
    [Fact]
    public void THE_MATCHER_FINDS_A_PLANTED_NAME()
    {
        Assert.Matches(Retired, "var v = HubSearchView.Curated;");
        Assert.Matches(Retired, "\"default_publisher\": \"unsloth\"");
        Assert.DoesNotMatch(Retired, "var r = ShelfRows.Of(x);");
    }
}
