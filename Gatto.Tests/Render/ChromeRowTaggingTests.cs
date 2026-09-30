using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//each row from ComposeChromeBlock must have its region and region row alongside the plain visible text, so the height clamp keeps them aligned
public class ChromeRowTaggingTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static ChromePainter NewPainter(int width, int height)
    {
        var gate = new object();
        var surface = new RecordingSurface { Width = width, Height = height };
        var status = new StatusInfo(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", new CtxState(), @"C:\Users\user");
        return new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
        };
    }

    [Fact]
    public void Queue_rows_carry_their_message_index_and_the_rollup_is_minus_one()
    {
        //the row budget forces the oldest queued messages into one roll-up row above the shown ones, which stay nearest the composer
        var p = NewPainter(width: 40, height: 20);
        p.State.QueueTexts = new[] { "one", "two", "three", "four", "five", "six", "seven" };

        var rows = p.ComposeChromeBlock(40, 20).Rows;
        var queue = rows.Where(r => r.Region == ChromeRegion.Queue).ToList();

        //the queue block opens with a blank separator row, which holds no message, so its region row is -1 like the roll-up
        Assert.Equal(-1, queue[0].RegionRow);
        Assert.Equal("", queue[0].Visible.Trim());
        Assert.Equal(-1, queue[1].RegionRow);                                  //row one is the roll-up.
        Assert.Contains("more", queue[1].Visible, StringComparison.Ordinal);
        Assert.Equal(6, queue[^1].RegionRow);                                  //the newest message is the last row.
        Assert.Contains("seven", queue[^1].Visible, StringComparison.Ordinal);
        //compare with StringComparison.Ordinal, since the default compare matches any string. probe the roll-up row, whose face has real text
        Assert.DoesNotContain("\x1b", queue[1].Visible, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_reports_a_visible_face_free_of_escapes()
    {
        var p = NewPainter(width: 40, height: 20);
        p.State.PurrText = "purring";
        p.State.Notice = "♯ writing";
        foreach (var r in p.ComposeChromeBlock(40, 20).Rows)
            Assert.DoesNotContain("\x1b", r.Visible, StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_stay_aligned_with_the_painted_rows_when_the_height_clamp_drops_categories()
    {
        var p = NewPainter(width: 60, height: 10);      //the height is small enough that the clamp really drops categories.
        p.State.QueueTexts = new[] { "q1", "q2" };
        p.State.PurrText = "purring";
        p.State.Notice = "writing";
        p.State.Composer = new EditorView(new List<string> { "typing" }, 0, 6);

        var block = p.ComposeChromeBlock(60, 10);
        var painted = p.ComposeForTest(60, 10).Rendered;      //take the same compose output the painter paints.

        Assert.Equal(painted.Count, block.Rows.Count);
        for (var i = 0; i < painted.Count; i++)
            Assert.Equal(painted[i], block.Rows[i].Rendered);

        //match these rows on their text (a queued message's first row starts with the composer glyph too)
        Assert.Contains(block.Rows, r => r.Visible.Contains("q2", StringComparison.Ordinal));   //assert the queue is on screen, so the region checks are not vacuous.
        foreach (var r in block.Rows)
        {
            if (r.Visible.Contains("q1", StringComparison.Ordinal) || r.Visible.Contains("q2", StringComparison.Ordinal))
                Assert.Equal(ChromeRegion.Queue, r.Region);
            if (r.Visible.Contains("typing", StringComparison.Ordinal)) Assert.Equal(ChromeRegion.Composer, r.Region);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(48)]
    public void Composer_rows_tag_wrap_continuations_with_a_two_cell_prefix(int width)
    {
        var p = NewPainter(width, height: 20);
        var longLine = string.Join(' ', Enumerable.Repeat("word", 40));
        p.State.Composer = new EditorView(new List<string> { longLine }, 0, longLine.Length);

        var rows = p.ComposeChromeBlock(width, 20).Rows;
        var composer = rows.Where(r => r.Region == ChromeRegion.Composer).ToList();

        Assert.True(composer.Count >= 3, "precondition: the line wrapped");
        Assert.False(composer[0].Continuation);                       //the first composer row is the head row.
        Assert.Equal(0, composer[0].PrefixCells);
        Assert.All(composer.Skip(1), r =>
        {
            Assert.True(r.Continuation);
            Assert.Equal(2, r.PrefixCells);                           //a continuation row indents by two cells.
            Assert.StartsWith("  ", r.Visible);                       //the tagged prefix must match the rendered text.
        });
    }
}
