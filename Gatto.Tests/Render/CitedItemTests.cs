using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Render;

//the row the gate draws under a reply, at fixed widths: the links that fit, a count of the rest with the letter's words, and every link when open
public sealed class CitedItemTests
{
    private static readonly Theme T = new(new TermCaps(false, false));

    private static List<string> Rows(CitedItem item, int width) => [.. item.Render(width, T, GlyphSet.Unicode).Select(TermText.StripAnsiForWidth)];

    private static readonly string[] Four = ["https://one.example/a", "https://two.example/b", "https://three.example/c", "https://four.example/d"];

    [Fact]
    public void LINKS_THAT_FIT_ARE_ONE_ROW_WITHOUT_THEIR_SCHEME_AND_NOTHING_TO_OPEN()
    {
        var item = new CitedItem(["https://one.example/a", "http://two.example/b"]) { Collapsed = true };

        Assert.Equal(["♯ cited, not seen this session: one.example/a · two.example/b"], Rows(item, 80));
        Assert.False(item.Opens(80));
    }

    [Fact]
    public void LINKS_PAST_THE_ROW_FOLD_UNDER_A_COUNT_IN_THE_LETTERS_WORDS()
    {
        var item = new CitedItem(Four) { Collapsed = true };

        Assert.Equal(
            ["♯ cited, not seen this session: one.example/a", "  ⎿ … 3 more · click to expand"],
            Rows(item, 50));
        Assert.True(item.Opens(50));
    }

    [Fact]
    public void AN_OPEN_ROW_NAMES_EVERY_LINK_UNDER_THE_HANG_AND_STAYS_A_STOP()
    {
        var item = new CitedItem(Four) { Collapsed = false };

        var rows = Rows(item, 50);

        Assert.Equal("  ⎿ click to collapse", rows[^1]);
        Assert.Equal(
            "cited, not seen this session: one.example/a · two.example/b · three.example/c · four.example/d",
            string.Join(" ", rows.Take(rows.Count - 1).Select(r => r[2..])));
        Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= 50, r));
        Assert.True(item.Opens(50));
    }

    private static readonly string Long = "https://a.example/" + new string('x', 80);

    //a link is never cut: a first link wider than the row wraps whole under the hang, and the links after it are counted
    [Fact]
    public void A_FIRST_LINK_TOO_LONG_FOR_THE_ROW_WRAPS_WHOLE_UNDER_THE_HANG()
    {
        var item = new CitedItem([Long, "https://b.example/y"]) { Collapsed = true };

        var rows = Rows(item, 60);

        Assert.Equal("  ⎿ … 1 more · click to expand", rows[^1]);
        Assert.Contains(Long[8..], string.Concat(rows.Take(rows.Count - 1).Select((r, i) => i == 0 ? r[(r.IndexOf(": ", StringComparison.Ordinal) + 2)..] : r[2..])));
        Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= 60, r));
        Assert.True(item.Opens(60));
    }

    //one long link has nothing after it, so the row wraps it whole and has nothing to open
    [Fact]
    public void ONE_LONG_LINK_WRAPS_WHOLE_WITH_NOTHING_TO_OPEN()
    {
        var item = new CitedItem([Long]) { Collapsed = true };

        var rows = Rows(item, 60);

        Assert.False(item.Opens(60));
        Assert.DoesNotContain(rows, r => r.Contains("click to", StringComparison.Ordinal));
        Assert.Contains(Long[8..], string.Concat(rows.Select((r, i) => i == 0 ? r[(r.IndexOf(": ", StringComparison.Ordinal) + 2)..] : r[2..])));
    }

    //a resize refolds the row from the width it is given
    [Fact]
    public void A_WIDER_WINDOW_FITS_THE_LINKS_AND_DROPS_THE_COUNT()
    {
        var item = new CitedItem(Four) { Collapsed = true };

        Assert.Equal(2, Rows(item, 50).Count);
        Assert.Single(Rows(item, 140));
    }
}
