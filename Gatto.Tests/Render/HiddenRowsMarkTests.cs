using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//a window that hides rows says how many on its edge rows, and the caret's row is never the one a mark takes
public class HiddenRowsMarkTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static ChromePainter Painter(int width, int height, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var surface = new RecordingSurface { Width = width, Height = height };
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        return new ChromePainter(surface, T, new object(), glyphs: g)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: g),
            RoleForTint = "coder",
            State = { Composer = new EditorView(new List<string> { "" }, 0, 0) },
        };
    }

    private static List<string> Lines(int n) => [.. Enumerable.Range(0, n).Select(i => $"line {i}")];

    //a row's text without the prompt, the hang and the join mark
    private static string Bare(ChromeRow row) => row.Visible.Trim().TrimStart('❯').TrimEnd('\\').Trim();

    private static List<ChromeRow> Region(ChromeBlock block, ChromeRegion region) => [.. block.Rows.Where(r => r.Region == region)];

    private static ChromeBlock Composer(ChromePainter p, int caretLine)
    {
        var lines = Lines(200);
        p.State.Composer = new EditorView(lines, caretLine, 0);
        return p.ComposeChromeBlock(80, 30);
    }

    [Fact]
    public void A_composer_at_the_end_of_a_paste_marks_the_rows_above_and_nothing_below()
    {
        var block = Composer(Painter(80, 30), 199);
        var rows = Region(block, ChromeRegion.Composer);

        Assert.Equal(10, rows.Count);
        Assert.Equal("↑ 191 more", Bare(rows[0]));
        Assert.Equal("line 199", Bare(rows[^1]));
        Assert.DoesNotContain(rows, r => r.Visible.Contains('↓'));
    }

    [Fact]
    public void A_composer_at_the_top_of_a_paste_marks_the_rows_below_and_nothing_above()
    {
        var block = Composer(Painter(80, 30), 0);
        var rows = Region(block, ChromeRegion.Composer);

        Assert.Equal("line 0", Bare(rows[0]));
        Assert.Equal("↓ 191 more", Bare(rows[^1]));
        Assert.Equal("line 0", Bare(block.Rows[block.CaretRow]));
    }

    //the caret lands on the window's last row, which the lower mark would take, so the window moves one more row down
    [Fact]
    public void The_caret_is_never_on_a_mark_row_the_window_moves_one_more_row_instead()
    {
        var block = Composer(Painter(80, 30), 100);
        var rows = Region(block, ChromeRegion.Composer);

        Assert.Equal(10, rows.Count);
        Assert.Equal("↑ 93 more", Bare(rows[0]));
        Assert.Equal("↓ 99 more", Bare(rows[^1]));
        Assert.Equal("line 100", Bare(rows[^2]));
        Assert.Equal("line 100", Bare(block.Rows[block.CaretRow]));
    }

    [Fact]
    public void A_tall_panel_following_its_tail_marks_the_rows_above()
    {
        var p = Painter(60, 30);
        p.SetPanel([.. Enumerable.Range(0, 200).Select(i => $"  p{i:000}")]);
        var rows = Region(p.ComposeChromeBlock(60, 30), ChromeRegion.Prompt);

        Assert.Equal(15, rows.Count);
        Assert.Equal("↑ 186 more", rows[0].Visible.Trim());
        Assert.Equal("p199", rows[^1].Visible.Trim());
    }

    [Fact]
    public void A_panel_scrolled_up_marks_both_edges()
    {
        var p = Painter(60, 30);
        p.SetPanel([.. Enumerable.Range(0, 200).Select(i => $"  p{i:000}")]);
        p.ScrollPanel(50);
        var rows = Region(p.ComposeChromeBlock(60, 30), ChromeRegion.Prompt);

        Assert.Equal(15, rows.Count);
        Assert.StartsWith("↑ ", rows[0].Visible.Trim());
        Assert.StartsWith("↓ ", rows[^1].Visible.Trim());
        Assert.EndsWith(" more", rows[^1].Visible.Trim());
    }

    //the panel's caret sits on the window's first row, which the upper mark would take, so the window moves one more row up
    [Fact]
    public void A_panel_caret_on_the_row_a_mark_would_take_moves_the_window()
    {
        var p = Painter(60, 30);
        p.SetPanelFactory((_, _) => new PanelContent([.. Enumerable.Range(0, 200).Select(i => $"  p{i:000}")], Caret: (185, 2)));
        var block = p.ComposeChromeBlock(60, 30);
        var rows = Region(block, ChromeRegion.Prompt);

        Assert.Equal("↑ 185 more", rows[0].Visible.Trim());
        Assert.Equal("p185", rows[1].Visible.Trim());
        Assert.Equal("↓ 2 more", rows[^1].Visible.Trim());
        Assert.Equal("p185", block.Rows[block.CaretRow].Visible.Trim());
    }

    [Fact]
    public void Under_the_ascii_set_the_marks_use_the_ascii_arrows()
    {
        var p = Painter(60, 30, GlyphSet.Ascii);
        p.SetPanel([.. Enumerable.Range(0, 200).Select(i => $"  p{i:000}")]);
        p.ScrollPanel(50);
        var rows = Region(p.ComposeChromeBlock(60, 30), ChromeRegion.Prompt);

        Assert.StartsWith("^ ", rows[0].Visible.Trim());
        Assert.StartsWith("v ", rows[^1].Visible.Trim());
    }
}
