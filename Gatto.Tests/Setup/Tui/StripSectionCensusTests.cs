using Gatto.Cli.Setup;
using Gatto.Tests.Census;

namespace Gatto.Tests.Setup.Tui;

//every screen declares its strip section in WalkSection.Of or the reason it has none in WalkSection.NotOnTheStrip, a screen in neither fails here
public class StripSectionCensusTests
{
    private static IReadOnlyDictionary<string, int> Emitted() => ScreenKeys.Extract(ScreenKeys.Construction);

    [Fact]
    public void EVERY_EMITTED_SCREEN_DECLARES_A_STRIP_SECTION_OR_A_REASON_IT_HAS_NONE()
    {
        var emitted = Emitted();

        //the census's own oracle, an extraction that matched nothing would report no undeclared screens and pass green
        Assert.True(emitted.Count >= 30,
            $"extraction found only {emitted.Count} screen keys — the construction-site pattern is broken");

        var undeclared = emitted.Keys
            .Where(k => !WalkSection.Of.ContainsKey(k) && !WalkSection.NotOnTheStrip.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(undeclared.Count == 0,
            $"extracted {emitted.Count} screen keys.\n"
            + "These screens declare no strip section. A screen with none renders the strip with no "
            + "cursor, which says the walk has not started — correct for the welcome map and a lie "
            + "everywhere else. Add the section to WalkSection.Of, or the reason to "
            + "WalkSection.NotOnTheStrip:\n  " + string.Join("\n  ", undeclared));
    }

    //a key in both tables would answer from Of while the reason sits there reading as if it applied
    [Fact]
    public void NO_SCREEN_IS_IN_BOTH_TABLES()
    {
        var both = WalkSection.Of.Keys.Where(WalkSection.NotOnTheStrip.ContainsKey)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(both.Count == 0,
            "these screens declare a section AND a reason they have none:\n  " + string.Join("\n  ", both));
    }

    //a row naming a screen nothing constructs is a decision about nothing and makes the tables look complete
    [Fact]
    public void NO_TABLE_ROW_NAMES_A_SCREEN_THAT_NO_LONGER_EXISTS()
    {
        var emitted = Emitted();
        var stale = WalkSection.Of.Keys.Concat(WalkSection.NotOnTheStrip.Keys)
            .Where(k => !emitted.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "these rows name screens nothing constructs — delete the row with the screen:\n  "
            + string.Join("\n  ", stale));
    }

    //a claimed section must be on one of the two roads, or the lookup yields no cursor and the strip goes quiet
    [Fact]
    public void EVERY_CLAIMED_SECTION_IS_ON_A_ROAD()
    {
        var roads = WalkSection.LlamaRoad.Concat(WalkSection.ConnectRoad).ToHashSet(StringComparer.Ordinal);
        var orphans = WalkSection.Of.Values.Distinct(StringComparer.Ordinal)
            .Where(v => !roads.Contains(v))
            .OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.True(orphans.Count == 0,
            "these sections are claimed by a screen but appear on neither road:\n  "
            + string.Join("\n  ", orphans));
    }

    //a blank or one-word reason passes the census above while telling the next reader nothing
    [Fact]
    public void EVERY_SECTIONLESS_SCREEN_CARRIES_A_REASON_WORTH_READING()
    {
        var thin = WalkSection.NotOnTheStrip
            .Where(r => r.Value.Trim().Length < 30)
            .Select(r => $"{r.Key}: \"{r.Value}\"")
            .OrderBy(r => r, StringComparer.Ordinal).ToList();

        Assert.True(thin.Count == 0,
            "these rows say a screen is off the strip without saying why:\n  " + string.Join("\n  ", thin));
    }
}
