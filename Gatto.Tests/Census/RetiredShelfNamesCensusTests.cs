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

    //the badge's words left the shelf with the badge, so the shelf's folders never say them again. the audition's own report keeps them outside these folders
    private static readonly (string Words, string[][] Scopes)[] RetiredWords =
    [
        ("tool-calling verified", [["Cli", "Setup", "Shelves"], ["Core", "Acquire"]]),
        ("not measured by gatto yet", [["Cli", "Setup", "Shelves"], ["Core", "Acquire"]]),
        ("not measured yet", [["Cli", "Setup", "Shelves"], ["Core", "Acquire"]]),
        ("None of these has been tool-calling verified yet.", [["Cli", "Setup", "Shelves"], ["Core", "Acquire"]]),
        //no screen may promise words the shelf no longer shows, so this one is retired from every production file
        ("the shelf shows it as tool-calling verified", [[]]),
    ];

    private static IReadOnlyList<string> FilesIn(string[] scope) =>
        scope.Length == 0 ? SourceTree.ProductionFiles() : SourceTree.ProductionFilesUnder(scope);

    [Fact]
    public void NO_SHELF_FILE_SAYS_THE_RETIRED_BADGE_WORDS()
    {
        var hits = RetiredWords
            .SelectMany(w => w.Scopes.SelectMany(FilesIn).Distinct().Select(f => (w.Words, f)))
            .SelectMany(x => SourceTree.Read(x.f).Split('\n').Select((line, i) => (x.Words, x.f, i, line)))
            .Where(x => x.line.Contains(x.Words, StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(SourceTree.RepoRoot(), x.f)}:{x.i + 1}: \"{x.Words}\"")
            .ToList();

        Assert.True(hits.Count == 0, "retired badge words are back:\n" + string.Join("\n", hits));
    }

    //a scope written as one string with slashes enumerates nothing and the census above would pass over no file, so each scope must hold one
    [Fact]
    public void EVERY_SCOPE_OF_THE_RETIRED_WORDS_HOLDS_A_FILE()
    {
        foreach (var scope in RetiredWords.SelectMany(w => w.Scopes))
            Assert.NotEmpty(FilesIn(scope));
    }

    //the matcher is fired at a planted line first, so a pattern that matches nothing cannot pass the census above
    [Fact]
    public void THE_MATCHER_FINDS_A_PLANTED_NAME()
    {
        Assert.Matches(Retired, "var v = HubSearchView.Curated;");
        Assert.Matches(Retired, "\"default_publisher\": \"unsloth\"");
        Assert.DoesNotMatch(Retired, "var r = ShelfRows.Of(x);");
        Assert.Contains(RetiredWords, w => "rows.Add(g.Bad + \" not measured yet\");".Contains(w.Words, StringComparison.Ordinal));
    }
}
