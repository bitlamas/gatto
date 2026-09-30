using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

//the chrome check, ChromeStillValid, compares visible text and row count, since the chrome repaints constantly and has no revision to bump
public sealed class ChromeSelectionTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static IReadOnlyList<ChromeRow> Rows(params string[] text) =>
        text.Select(ChromeRow.Plain).ToList();

    [Fact]
    public void A_chrome_span_survives_a_repaint_that_changes_an_untouched_row()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("purr 1s", "❯ hello");
        sel.BeginChromeDrag(new ChromeCell(1, 2), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(1, 6), 40, rows);

        var ticked = Rows("purr 2s", "❯ hello");        //the purr row changed and the selected row did not.
        Assert.True(sel.ChromeStillValid(ticked));
    }

    [Fact]
    public void A_chrome_span_dies_when_a_touched_row_changes()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("purr 1s", "❯ hello");
        sel.BeginChromeDrag(new ChromeCell(1, 2), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(1, 6), 40, rows);

        Assert.False(sel.ChromeStillValid(Rows("purr 1s", "❯ hello!")));   //the composer text changed by one typed character.
    }

    [Fact]
    public void A_chrome_span_dies_when_the_row_count_changes_even_if_the_text_matches()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("", "❯ hi");
        //anchor on the blank row, whose text never changes, so the count check is proven on its own when a row is inserted
        sel.BeginChromeDrag(new ChromeCell(0, 0), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(0, 0), 40, rows);

        Assert.False(sel.ChromeStillValid(Rows("", "↳ queued", "❯ hi")));
    }

    [Fact]
    public void The_two_domains_are_mutually_exclusive()
    {
        var model = new TranscriptModel("generalist");
        model.Append(new AssistantBlockItem(new[] { "prose" }, "generalist") { LeadingBlank = false });
        var sel = new SelectionController(model);

        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 40);
        sel.ExtendTo(new SelCell(0, 0, 3), 40);
        Assert.NotNull(sel.Current);

        sel.BeginChromeDrag(new ChromeCell(0, 0), DragKind.Char, 40, Rows("❯ hi"));
        Assert.Null(sel.Current);                 //starting a chrome drag clears the transcript span.
        Assert.NotNull(sel.ChromeCurrent);
        Assert.True(sel.HasSelection);

        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 40);
        Assert.Null(sel.ChromeCurrent);           //starting a transcript drag clears the chrome span.
    }

    //the ChromeRowRange result keeps inclusive endpoints in the flat chrome row space, like transcript rows, and neither drag direction may drop one

    [Fact]
    public void ChromeRowRange_single_row_span_is_inclusive_of_the_head_cell()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("❯ hello world");
        sel.BeginChromeDrag(new ChromeCell(0, 2), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(0, 6), 40, rows);

        Assert.True(sel.ChromeRowRange(0, 13, out var start, out var end));
        Assert.Equal(2, start);
        Assert.Equal(7, end);   //the end is the head column plus one, so the head cell is included.
    }

    [Fact]
    public void ChromeRowRange_multi_row_span_covers_first_middle_last_correctly()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("row zero text", "row one text", "row two text");
        sel.BeginChromeDrag(new ChromeCell(0, 4), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(2, 3), 40, rows);

        Assert.True(sel.ChromeRowRange(0, 13, out var s0, out var e0));
        Assert.Equal(4, s0);
        Assert.Equal(13, e0);   //the first row spans from the anchor column to the row end.

        Assert.True(sel.ChromeRowRange(1, 12, out var s1, out var e1));
        Assert.Equal(0, s1);
        Assert.Equal(12, e1);   //the range covers a middle row in full.

        Assert.True(sel.ChromeRowRange(2, 13, out var s2, out var e2));
        Assert.Equal(0, s2);
        Assert.Equal(4, e2);    //the last row spans from column zero to the head column plus one.
    }

    [Fact]
    public void ChromeRowRange_is_the_same_in_either_drag_direction()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("row zero text", "row one text", "row two text");

        sel.BeginChromeDrag(new ChromeCell(0, 4), DragKind.Char, 40, rows);   //anchor the drag above the head cell.
        sel.ExtendChromeTo(new ChromeCell(2, 3), 40, rows);
        sel.ChromeRowRange(0, 13, out var downS0, out var downE0);
        sel.ChromeRowRange(1, 12, out var downS1, out var downE1);
        sel.ChromeRowRange(2, 13, out var downS2, out var downE2);

        sel.BeginChromeDrag(new ChromeCell(2, 3), DragKind.Char, 40, rows);   //anchor below the head, with the same endpoints.
        sel.ExtendChromeTo(new ChromeCell(0, 4), 40, rows);
        sel.ChromeRowRange(0, 13, out var upS0, out var upE0);
        sel.ChromeRowRange(1, 12, out var upS1, out var upE1);
        sel.ChromeRowRange(2, 13, out var upS2, out var upE2);

        Assert.Equal((downS0, downE0), (upS0, upE0));
        Assert.Equal((downS1, downE1), (upS1, upE1));
        Assert.Equal((downS2, downE2), (upS2, upE2));
    }

    [Fact]
    public void ChromeRowRange_returns_false_for_a_row_outside_the_span()
    {
        var sel = new SelectionController(new TranscriptModel("generalist"));
        var rows = Rows("row zero", "row one", "row two");
        sel.BeginChromeDrag(new ChromeCell(1, 0), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(1, 3), 40, rows);

        Assert.False(sel.ChromeRowRange(0, 8, out _, out _));
        Assert.False(sel.ChromeRowRange(2, 8, out _, out _));
    }

    //copying chrome text reads ChromeRow.Visible directly, so the copy must refuse when the rows changed since the span was taken

    [Fact]
    public void Copying_a_wrapped_composer_line_rejoins_it_with_exactly_one_space()
    {
        var rows = new List<ChromeRow>
        {
            new("❯ the quick brown", "❯ the quick brown", false, 0, ChromeRegion.Composer, 0),
            new("  fox jumps",       "  fox jumps",       true,  2, ChromeRegion.Composer, 1),
        };
        var sel = new SelectionController(new TranscriptModel("generalist"));
        sel.BeginChromeDrag(new ChromeCell(0, 2), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(1, 10), 40, rows);

        var text = sel.CopyText(40, T, rows, glyphs: GlyphSet.Unicode);   //use the single entry point CopyText, so the test drives the real dispatch
        Assert.Equal("the quick brown fox jumps", text);
        Assert.DoesNotContain("  ", text);          //the rejoin must leave no two-space artifact from the continuation indent.
    }

    [Fact]
    public void Copy_refuses_when_the_rows_changed_since_the_span_was_taken()
    {
        var rows = Rows("❯ hello");
        var sel = new SelectionController(new TranscriptModel("generalist"));
        sel.BeginChromeDrag(new ChromeCell(0, 2), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(0, 6), 40, rows);

        //the user typed after the last paint, and the copy arrives before the next one.
        Assert.Null(sel.CopyText(40, T, Rows("❯ hello!"), glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void A_head_inside_the_continuation_gutter_appends_nothing()
    {
        var rows = new List<ChromeRow>
        {
            new("❯ alpha", "❯ alpha", false, 0, ChromeRegion.Composer, 0),
            new("  beta",  "  beta",  true,  2, ChromeRegion.Composer, 1),
        };
        var sel = new SelectionController(new TranscriptModel("generalist"));
        sel.BeginChromeDrag(new ChromeCell(0, 2), DragKind.Char, 40, rows);
        sel.ExtendChromeTo(new ChromeCell(1, 1), 40, rows);      //extend the head into the two-cell indent of the continuation row.

        Assert.Equal("alpha", sel.CopyText(40, T, rows, glyphs: GlyphSet.Unicode));          //a head inside the indent must append no bare trailing space.
    }
}
