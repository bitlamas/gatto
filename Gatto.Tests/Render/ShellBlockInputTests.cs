using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ShellBlockInputTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private const string Command = "$s = @'\necho a\necho b\necho c\necho d\n'@; $s | ssh host \"bash -s\"";
    private static string Output(int n) => string.Join("\r\n", Enumerable.Range(1, n).Select(i => $"row{i:00}"));

    //sixty rows of prose sit above the block, so a notch the window hands on has a transcript to scroll
    private static (StreamRenderer R, ChromePainter P, VtScreenSurface S, ToolBlockItem B) Committed(int width, int height, string output, string command = Command)
    {
        var s = new VtScreenSurface(width, height);
        var gate = new object();
        var model = new TranscriptModel("coder");
        model.Append(new AssistantBlockItem(Enumerable.Range(0, 60).Select(i => $"prose {i}").ToArray(), "coder"));
        var p = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
        p.AltScreen.Enter();
        r.BeginTurn();
        var call = new ToolCall("c1", "shell", JsonSerializer.Serialize(new { command }));
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult(output, Gloss: "exit 0"));
        r.EndTurn();
        p.Repaint();
        return (r, p, s, model.Items.OfType<ToolBlockItem>().Single());
    }

    private static int RowOf(VtScreenSurface s, string needle)
    {
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(needle, StringComparison.Ordinal));
        Assert.True(y >= 0, $"no row holds {needle}");
        return y;
    }

    private static void Press(ChromePainter p, int x, int y)
    {
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    private static void Wheel(ChromePainter p, int x, int y, int delta)
    {
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Wheel, MouseButton.None, delta, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    private static void Focus(ChromePainter p, ToolBlockItem b)
    {
        p.Focus.Focus(p.Model.Items.ToList().IndexOf(b), p.Width, p.ViewportRows(), reveal: false);
        p.Repaint();
    }

    [Fact]
    public void The_links_reach_their_states()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        var y = RowOf(s, ShellBlockRender.ExpandWords);
        Press(p, s.Viewport[y].IndexOf(ShellBlockRender.ExpandWords, StringComparison.Ordinal) + 2, y);
        Assert.False(b.Collapsed);
        y = RowOf(s, "show all");
        Press(p, s.Viewport[y].IndexOf("show all", StringComparison.Ordinal) + 2, y);
        Assert.Equal(ShellView.All, b.View);
        y = RowOf(s, "show less");
        Press(p, s.Viewport[y].IndexOf("show less", StringComparison.Ordinal) + 2, y);
        Assert.Equal(ShellView.Window, b.View);
        y = RowOf(s, ShellBlockRender.CollapseWords);
        Press(p, s.Viewport[y].IndexOf(ShellBlockRender.CollapseWords, StringComparison.Ordinal) + 2, y);
        Assert.True(b.Collapsed);
    }

    [Fact]
    public void A_notch_over_the_output_window_moves_it_and_leaves_the_transcript()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        b.OutputTop = 10;
        Focus(p, b);
        var y = RowOf(s, "row11");
        Wheel(p, 10, y, 120);
        Assert.Equal(7, b.OutputTop);
        Assert.True(p.Scroll.Following);
    }

    [Fact]
    public void A_notch_over_a_block_without_focus_scrolls_the_transcript()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        b.OutputTop = 10;
        p.Repaint();
        Wheel(p, 10, RowOf(s, "row11"), 120);
        Assert.Equal(10, b.OutputTop);
        Assert.False(p.Scroll.Following);
    }

    [Fact]
    public void A_notch_over_the_command_of_a_block_without_focus_scrolls_the_transcript()
    {
        var (_, p, s, b) = Committed(120, 40, Output(3));
        Wheel(p, 10, RowOf(s, "echo d"), 120);
        Assert.Equal(0, b.CommandFromTail);
        Assert.False(p.Scroll.Following);
    }

    [Fact]
    public void A_block_opened_by_its_link_takes_the_wheel()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        var y = RowOf(s, ShellBlockRender.ExpandWords);
        Press(p, s.Viewport[y].IndexOf(ShellBlockRender.ExpandWords, StringComparison.Ordinal) + 2, y);
        Wheel(p, 10, RowOf(s, "row03"), -120);
        Assert.Equal(3, b.OutputTop);
        Assert.True(p.Scroll.Following);
    }

    [Fact]
    public void A_block_loses_the_wheel_when_the_focus_moves_away()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        Focus(p, b);
        p.Focus.Clear();
        p.Repaint();
        Wheel(p, 10, RowOf(s, "row03"), -120);
        Assert.Equal(0, b.OutputTop);
    }

    private static bool Accented(VtScreenSurface s) =>
        s.Viewport[RowOf(s, "shell")].StartsWith($"{GlyphSet.Unicode.Box.Vertical}", StringComparison.Ordinal);

    [Fact]
    public void A_notch_that_scrolls_the_transcript_away_from_the_focused_block_clears_its_focus()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        Focus(p, b);
        Assert.True(Accented(s), "precondition: the focused block shows the accent bar");
        Wheel(p, 10, RowOf(s, "prose 50"), 120);
        Assert.False(p.Scroll.Following);
        Assert.Null(p.Focus.Focused);
        Assert.False(Accented(s));
    }

    [Fact]
    public void A_block_that_lost_the_focus_to_the_wheel_does_not_catch_it_on_the_way_back()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        var y = RowOf(s, ShellBlockRender.ExpandWords);
        Press(p, s.Viewport[y].IndexOf(ShellBlockRender.ExpandWords, StringComparison.Ordinal) + 2, y);
        Wheel(p, 10, RowOf(s, "prose 50"), 120);
        Wheel(p, 10, RowOf(s, "row01"), -120);
        Assert.Equal(0, b.OutputTop);
    }

    [Fact]
    public void A_notch_inside_the_focused_window_keeps_the_focus()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        b.OutputTop = 10;
        Focus(p, b);
        Wheel(p, 10, RowOf(s, "row11"), 120);
        Assert.Equal(7, b.OutputTop);
        Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
        Assert.True(Accented(s));
    }

    [Fact]
    public void A_notch_the_focused_window_hands_on_at_its_top_keeps_the_focus()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        Focus(p, b);
        Wheel(p, 10, RowOf(s, "row01"), 120);
        Assert.False(p.Scroll.Following);
        Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
    }

    [Fact]
    public void A_notch_over_the_focused_blocks_header_scrolls_the_transcript_and_keeps_the_focus()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        Focus(p, b);
        Wheel(p, 10, RowOf(s, "shell"), 120);
        Assert.False(p.Scroll.Following);
        Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
    }

    [Fact]
    public void A_notch_over_the_composer_that_scrolls_the_transcript_clears_the_focus()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        Focus(p, b);
        var composer = p.ChromeRowInfo.First(r => r.Region == ChromeRegion.Composer).Visible.TrimEnd();
        var y = s.Viewport.ToList().FindLastIndex(v => v.TrimEnd() == composer);
        Assert.True(composer.Length > 0 && y >= 0, "precondition: the composer row is on screen");
        Wheel(p, 10, y, 120);
        Assert.False(p.Scroll.Following);
        Assert.Null(p.Focus.Focused);
    }

    [Fact]
    public void A_notch_past_the_window_top_scrolls_the_transcript()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        Focus(p, b);
        var y = RowOf(s, "row01");
        Wheel(p, 10, y, 120);
        Assert.Equal(0, b.OutputTop);
        Assert.False(p.Scroll.Following);
    }

    //at its last line the window passes the notch on, so the transcript moves
    [Fact]
    public void A_notch_down_past_the_window_end_scrolls_the_transcript()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        b.OutputTop = 15;
        Focus(p, b);
        p.Scroll.ScrollBy(1, p.Width, p.ViewportRows());
        p.Repaint();
        Assert.False(p.Scroll.Following);
        //row17 is the last window row still on screen (the hint and its gap cover the rest)
        Wheel(p, 10, RowOf(s, "row17"), -120);
        Assert.Equal(15, b.OutputTop);
        Assert.True(p.Scroll.Following);
    }

    [Fact]
    public void Both_close_routes_reset_both_windows_for_the_next_open()
    {
        foreach (var byHeader in new[] { true, false })
        {
            var (_, p, s, b) = Committed(120, 40, Output(20));
            b.Collapsed = false;
            Focus(p, b);
            Wheel(p, 10, RowOf(s, "row03"), -120);
            Wheel(p, 10, RowOf(s, "echo d"), 120);
            Assert.True(b.OutputTop > 0 && b.CommandFromTail > 0, $"the windows did not scroll: {b.OutputTop} {b.CommandFromTail}");
            if (byHeader) Press(p, 4, RowOf(s, "shell"));
            else { var y = RowOf(s, ShellBlockRender.CollapseWords); Press(p, s.Viewport[y].IndexOf(ShellBlockRender.CollapseWords, StringComparison.Ordinal) + 2, y); }
            Assert.True(b.Collapsed);
            var y2 = RowOf(s, ShellBlockRender.ExpandWords);
            Press(p, s.Viewport[y2].IndexOf(ShellBlockRender.ExpandWords, StringComparison.Ordinal) + 2, y2);
            Assert.False(b.Collapsed);
            Assert.Equal((0, 0), (b.OutputTop, b.CommandFromTail));
            Assert.Contains(s.Viewport, v => v.Contains("row01", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_notch_over_the_command_window_moves_it_toward_the_first_rows()
    {
        var (_, p, s, b) = Committed(120, 40, Output(3));
        Focus(p, b);
        var y = RowOf(s, "echo d");
        Wheel(p, 10, y, 120);
        Assert.True(b.CommandFromTail > 0);
        Assert.True(p.Scroll.Following);   //the window absorbed the notch, so the transcript did not move
        //at the first row the window cannot use the next notch, so the transcript takes it
        Wheel(p, 10, RowOf(s, "$s = @'"), 120);
        Assert.False(p.Scroll.Following);
    }

    [Fact]
    public void A_press_on_an_output_row_starts_a_selection()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        p.Repaint();
        var y = RowOf(s, "row02");
        p.Mouse.Handle(new MouseEvent(10, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.False(b.Collapsed);
        Assert.NotNull(p.Selection.Current);
    }

    [Fact]
    public void Copy_of_output_rows_from_column_zero_holds_the_text_only()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        p.Repaint();
        var y1 = RowOf(s, "row02");
        var y2 = RowOf(s, "row03");
        p.Mouse.Handle(new MouseEvent(0, y1, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y2, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y2, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal("row02\nrow03", p.Selection.CopyText(p.Width, T, null, GlyphSet.Unicode)?.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Ctrl_r_cycles_closed_window_all_and_skips_all_when_nothing_is_hidden()
    {
        var (_, big, _, b) = Committed(120, 40, Output(20));
        var i = big.Model.Items.ToList().IndexOf(b);
        big.Focus.Focus(i, 120, 40);
        big.Focus.ToggleFocused(120, 40); Assert.False(b.Collapsed); Assert.Equal(ShellView.Window, b.View);
        b.OutputTop = 4; b.CommandFromTail = 2;   //the user scrolled both windows before the closing press
        big.Focus.ToggleFocused(120, 40); Assert.Equal(ShellView.All, b.View);
        big.Focus.ToggleFocused(120, 40); Assert.True(b.Collapsed);
        //closing resets both positions, so the next open starts at the top of the window
        big.Focus.ToggleFocused(120, 40); Assert.False(b.Collapsed);
        Assert.Equal(ShellView.Window, b.View); Assert.Equal(0, b.OutputTop); Assert.Equal(0, b.CommandFromTail);

        //a one-row command with two output lines hides nothing, so the second press closes the block
        var (_, small, _, sb) = Committed(120, 40, Output(2), "git status");
        Assert.False(sb.ShellLayout(120, T, GlyphSet.Unicode).Hides);
        var j = small.Model.Items.ToList().IndexOf(sb);
        small.Focus.Focus(j, 120, 40);
        small.Focus.ToggleFocused(120, 40); Assert.False(sb.Collapsed);
        small.Focus.ToggleFocused(120, 40); Assert.True(sb.Collapsed);
    }

    [Fact]
    public void A_resize_after_scrolling_keeps_both_windows_in_range()
    {
        var (_, p, s, b) = Committed(120, 40, Output(20));
        b.Collapsed = false;
        b.OutputTop = 15;
        b.CommandFromTail = 3;
        foreach (var w in new[] { 80, 47, 30, 120 })
        {
            s.Resize(w, 40);
            p.Repaint();
            var l = b.ShellLayout(w, T, GlyphSet.Unicode);
            Assert.InRange(l.CommandShown, 1, ShellBlockRender.CommandWindow);
            Assert.Equal(ShellBlockRender.OutputWindow, l.OutputShown);
            Assert.All(l.Rows, r => Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(r.Text)) <= w));
        }
    }
}
