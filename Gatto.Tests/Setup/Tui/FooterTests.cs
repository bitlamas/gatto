using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the footer is one row, always, and the expectations are read out of the golden corpus
public class FooterTests
{
    private static readonly FooterKey[] Shelf =
    [
        new("Tab", "area"), new("↑↓", "move"), new("Enter", "next"),
        new("m", "local"), new("Esc", "leave"),
    ];
    private static readonly FooterKey[] Two = [new("Enter", "choose"), new("Esc", "leave")];

    private const string Marks = "✓ GPU · ⚠ RAM · ✗ too big";
    private const string Sentence = "fewer params = faster";

    [Fact]
    public void A_row_with_no_legend_is_just_the_keys()
    {
        var row = Footer.Compose(100, Two);
        Assert.Equal("  Enter choose" + new string(' ', Footer.KeyGap) + "Esc leave", row);
    }

    [Fact]
    public void The_legend_rides_right_aligned_behind_an_ascii_pipe()
    {
        //the legend is right-aligned and keeps the margin, so it sits flush to the frame's right edge
        var row = Footer.Compose(100, Shelf, new Legend(LegendKind.Marks, Marks));
        Assert.EndsWith("| " + Marks, row, StringComparison.Ordinal);
        Assert.Equal(Margins.Inside(100), Gatto.Terminal.UnicodeWidth.Of(row));
    }

    //a legend is drawn whole or not at all, and 60 is the width where the hints are gone and it still does not fit
    [Fact]
    public void A_SENTENCE_legend_that_does_not_fit_DROPS_rather_than_ellipsing()
    {
        var row = Footer.Compose(60, Shelf, new Legend(LegendKind.Sentence, Sentence));
        Assert.DoesNotContain("…", row, StringComparison.Ordinal);
        Assert.DoesNotContain("|", row, StringComparison.Ordinal);
        Assert.Equal(Footer.Compose(60, Shelf), row);
    }

    [Fact]
    public void A_MARK_legend_drops_entirely_below_the_floor()
    {
        //ten cells of glyphs and an ellipsis explain nothing, so it goes
        var row = Footer.Compose(70, Shelf, new Legend(LegendKind.Marks, Marks));
        Assert.Equal(Footer.Compose(70, Shelf), row);
    }

    //the in-session keys row, the widest real row at 76 cells on the three-space joiner, since Esc back to session is the ruled copy
    private static readonly FooterKey[] InSession =
    [
        new("Tab", "area"), new("↑↓", "move"), new("Enter", "next"),
        new("m", "local"), new("Esc", "back"),
    ];

    //the sweep drives the widest real rows from the floor of 78 and asserts that the row fits
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_keys_NEVER_wrap_and_the_row_is_always_one(bool inSession)
    {
        var keys = inSession ? InSession : Shelf;
        //both rows sweep from the 78-column floor, the in-session row is 76 cells and fits it without an exception
        const int floor = 78;
        for (var w = floor; w <= 120; w++)
        {
            foreach (var leg in new Legend?[]
                { null, new Legend(LegendKind.Marks, Marks), new Legend(LegendKind.Sentence, Sentence) })
            {
                var row = Footer.Compose(w, keys, leg);
                Assert.DoesNotContain('\n', row);
                Assert.StartsWith("  Tab area", row, StringComparison.Ordinal);
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= w,
                    $"w={w} inSession={inSession}: row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells and WRAPS");
            }
        }
    }

    //the trim buys four cells at 100, exactly what the in-session legend was short by, so it renders here
    [Fact]
    public void AND_THE_TRIM_LETS_THE_IN_SESSION_LEGEND_RENDER_AT_100()
    {
        var row = Footer.Compose(100, InSession, new Legend(LegendKind.Sentence, "fewer params = faster"));

        Assert.Contains("fewer params = faster", row, StringComparison.Ordinal);
        Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= 100,
            $"the row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells at 100");
    }

    [Fact]
    public void THE_IN_SESSION_ROW_FITS_THE_78_COLUMN_FLOOR()
    {
        var row = Footer.Compose(78, InSession);
        Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= 78,
            $"in-session keys row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells at a 78-column floor");
    }

    //derive the keys from Shelf.Keys() so the row is genuinely too wide at 78 (one that fits would prove the sweep nothing)
    [Fact]
    public void The_sweep_would_CATCH_a_keys_row_too_wide_for_the_floor()
    {
        //every region a shelf can offer, so Shelf.Keys returns every key one can draw
        var every = Gatto.Cli.Setup.Tui.Shelf.Keys(
            new FocusRing([Region.Families, Region.Publisher, Region.List, Region.Search]),
            ShelfSource.Hub, searchKey: true, lift: true, back: true);

        Assert.True(Gatto.Terminal.UnicodeWidth.Of(Footer.Compose(78, every)) > 78,
            "the fixture is not actually too wide, so the planted positive proves nothing");
    }

    //the ellipsis only shows up where the legend can't fit and can still keep one mark, so the sweep covers a range
    [Fact]
    public void A_MARK_LEGEND_IS_WHOLE_OR_ABSENT_AT_EVERY_WIDTH()
    {
        var truncated = new List<string>();
        var partial = new List<string>();
        for (var w = 60; w <= 140; w++)
        {
            var row = Footer.Compose(w, Shelf, new Legend(LegendKind.Marks, Marks));
            if (row.Contains('…')) truncated.Add($"{w}: {row.TrimEnd()}");
            if (row.Contains('|') && !row.Contains(Marks, StringComparison.Ordinal))
                partial.Add($"{w}: {row.TrimEnd()}");
        }

        Assert.True(truncated.Count == 0,
            "the legend is ellipsed at " + truncated.Count + " widths:\n"
            + string.Join("\n", truncated));
        Assert.True(partial.Count == 0,
            "a legend is drawn without being whole at " + partial.Count + " widths:\n"
            + string.Join("\n", partial));
    }

    //the other half, so dropping the legend at every width can't satisfy the rule above
    [Fact]
    public void A_MARK_LEGEND_THAT_FITS_IS_STILL_DRAWN()
    {
        var row = Footer.Compose(120, Shelf, new Legend(LegendKind.Marks, Marks));

        Assert.EndsWith("| " + Marks, row, StringComparison.Ordinal);
    }

    //a user who armed a destructive chord should be reading the cost
    [Fact]
    public void The_armed_row_replaces_the_keys_row()
    {
        var armed = Footer.Armed("Esc again: stops the fetch and deletes the 7.9 GB already here");
        Assert.Equal("  Esc again: stops the fetch and deletes the 7.9 GB already here", armed);
        Assert.DoesNotContain("Tab", armed, StringComparison.Ordinal);
    }
}
