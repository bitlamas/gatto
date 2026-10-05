using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a composer taller than the room the chrome may use shows a window of its rows, the caret always inside it
public class ComposerWindowTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (ChromePainter Painter, RecordingSurface Surface) Painter(int width, int height)
    {
        var surface = new RecordingSurface { Width = width, Height = height };
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, new object())
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        return (painter, surface);
    }

    private static List<string> Lines(int n) => [.. Enumerable.Range(0, n).Select(i => $"line {i}")];

    private static List<ChromeRow> ComposerRows(ChromeBlock block) =>
        [.. block.Rows.Where(r => r.Region == ChromeRegion.Composer)];

    //the composer rows are lines, one row each at this width, so a row's text less the prompt and the join mark names its line
    private static string LineOf(ChromeRow row) => Bare(row.Visible);

    private static string Bare(string visible) => visible.Trim().TrimStart('❯').TrimEnd('\\').Trim();

    [Fact]
    public void A_COMPOSER_TALLER_THAN_THE_ROOM_KEEPS_THE_TOP_RULE_AND_THE_TRANSCRIPT_ROWS()
    {
        var (p, _) = Painter(80, 30);
        var lines = Lines(200);
        p.State.Composer = new EditorView(lines, 199, lines[199].Length);

        var block = p.ComposeChromeBlock(80, 30);

        //the transcript keeps 3 rows, so the whole block fits in 27
        Assert.True(block.Rows.Count <= 27, $"{block.Rows.Count} chrome rows at height 30");
        Assert.Contains(block.Rows, r => r.Region == ChromeRegion.Rule && r.Visible.Contains("gatto", StringComparison.Ordinal));
        var shown = ComposerRows(block);
        Assert.Equal("line 199", LineOf(shown[^1]));
        Assert.DoesNotContain(shown, r => LineOf(r) == "line 0");
        //the caret sits on the last line's row
        Assert.Equal("line 199", LineOf(block.Rows[block.CaretRow]));
    }

    //however long the paste, the composer holds a third of the window's rows and the transcript keeps the rest
    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    public void A_200_LINE_PASTE_HOLDS_A_THIRD_OF_THE_WINDOW(int height)
    {
        var (p, _) = Painter(80, height);
        var lines = Lines(200);
        p.State.Composer = new EditorView(lines, 199, lines[199].Length);

        var block = p.ComposeChromeBlock(80, height);

        Assert.Equal(height / 3, ComposerRows(block).Count);
        Assert.Equal("line 199", LineOf(block.Rows[block.CaretRow]));
        Assert.True(height - block.Rows.Count >= height - height / 3 - (block.Rows.Count - ComposerRows(block).Count), $"{block.Rows.Count} chrome rows at {height}");
    }

    [Fact]
    public void THE_CARET_MOVING_UP_PAST_THE_WINDOW_SCROLLS_IT_BY_ONE_ROW()
    {
        var (p, _) = Painter(80, 30);
        var lines = Lines(200);
        p.State.Composer = new EditorView(lines, 199, 0);
        var first = ComposerRows(p.ComposeChromeBlock(80, 30));
        //row 0 is the hidden-rows mark, so the first line shown is on row 1
        var top = int.Parse(LineOf(first[1])["line ".Length..]);

        //typing inside the window leaves it where it is
        p.State.Composer = new EditorView(lines, top + 1, 0);
        Assert.Equal($"line {top}", LineOf(ComposerRows(p.ComposeChromeBlock(80, 30))[1]));

        //one line above the window brings the window up by one row, the caret on its first line under the mark
        p.State.Composer = new EditorView(lines, top - 1, 0);
        var block = p.ComposeChromeBlock(80, 30);
        var moved = ComposerRows(block);
        Assert.Equal($"line {top - 1}", LineOf(moved[1]));
        Assert.Equal(first.Count, moved.Count);
        Assert.Equal($"line {top - 1}", LineOf(block.Rows[block.CaretRow]));
    }

    [Fact]
    public void A_RESIZE_THAT_SHRINKS_THE_ROOM_CLAMPS_THE_WINDOW_AND_KEEPS_THE_CARET_IN_IT()
    {
        var (p, s) = Painter(80, 40);
        var lines = Lines(200);
        p.State.Composer = new EditorView(lines, 120, 0);
        p.ComposeChromeBlock(80, 40);

        s.Height = 15;
        var block = p.ComposeChromeBlock(80, 15);

        Assert.True(block.Rows.Count <= 12, $"{block.Rows.Count} chrome rows at height 15");
        Assert.Contains(ComposerRows(block), r => LineOf(r) == "line 120");
        Assert.Equal("line 120", LineOf(block.Rows[block.CaretRow]));
    }

    //a mouse gesture maps a composer row through its RegionRow into the full layout, so the first windowed row must map to its own line
    [Fact]
    public void THE_FIRST_WINDOWED_ROW_MAPS_TO_ITS_OWN_LINE_FOR_THE_MOUSE()
    {
        var (p, _) = Painter(80, 30);
        var lines = Lines(200);
        p.State.Composer = new EditorView(lines, 199, 0);
        //row 0 is the hidden-rows mark, so the first windowed line is on row 1
        var firstRow = ComposerRows(p.ComposeChromeBlock(80, 30))[1];
        var top = int.Parse(LineOf(firstRow)["line ".Length..]);

        var layout = new ComposerLayout(lines, 80);
        Assert.Equal((top, 0), layout.ComposerCellToPosition(firstRow.RegionRow, 2));
    }

    [Fact]
    public void A_WINDOWED_FRAME_EMITS_ONLY_ITS_ROWS_AND_COUNTS_THE_CARET_FROM_THEM()
    {
        var frame = new InputFrame(new RecordingSurface { Width = 80 }, T, "coder",
            new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode);
        var lines = Lines(10);

        var layout = frame.Compose(new EditorView(lines, 3, 0), editorWindow: (2, 3));

        Assert.Equal(3, layout.EditorRowTags.Count);
        Assert.Equal(2, layout.EditorWindowTop);
        Assert.Equal("line 2", Bare(layout.VisibleRows[1]));   //row 0 is the top rule
        Assert.Equal(1 + (3 - 2), layout.CursorRowOffset);
    }

    //the highlight is decided per row before the cut, so a windowed row is the same string as that row unwindowed
    [Fact]
    public void A_SELECTION_ACROSS_THE_WINDOW_EDGE_PAINTS_EACH_SHOWN_ROW_AS_THE_FULL_FRAME_DOES()
    {
        var frame = new InputFrame(new RecordingSurface { Width = 80 }, T, "coder",
            new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode);
        var lines = Lines(10);
        var view = new EditorView(lines, 5, 3, SelStart: (1, 2), SelEnd: (5, 3));

        var full = frame.Compose(view);
        var windowed = frame.Compose(view, editorWindow: (3, 4));

        for (var i = 0; i < 4; i++)
            Assert.Equal(full.Rows[1 + 3 + i], windowed.Rows[1 + i]);
    }
}
