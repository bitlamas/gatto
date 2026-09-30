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

public sealed class ToolBlockWindowInputTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static string Lines(int from, int n) => string.Join("\r\n", Enumerable.Range(from, n).Select(i => $"text of line {i}"));

    private static string Files(params (string Path, int Matches)[] files) =>
        string.Join("\r\n", files.SelectMany(f => Enumerable.Range(1, f.Matches).Select(i => $"{f.Path}:{i * 10}: {f.Path}-m{i}")));

    //sixty rows of prose sit above the block, so a notch the window hands on has a transcript to scroll
    private static (ChromePainter P, VtScreenSurface S, ToolBlockItem B) Committed(ToolCall call, ToolResult result, int wheelLines = 3)
    {
        var s = new VtScreenSurface(120, 40);
        var gate = new object();
        var model = new TranscriptModel("coder");
        model.Append(new AssistantBlockItem(Enumerable.Range(0, 60).Select(i => $"prose {i}").ToArray(), "coder"));
        var p = new ChromePainter(s, T, gate, model, wheelLines)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
        p.AltScreen.Enter();
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, result);
        r.EndTurn();
        p.Repaint();
        return (p, s, model.Items.OfType<ToolBlockItem>().Single());
    }

    private static (ChromePainter P, VtScreenSurface S, ToolBlockItem B) Read(int n, int wheelLines = 3) =>
        Committed(new ToolCall("c1", "read_file", JsonSerializer.Serialize(new { path = "a.cs" })), new ToolResult(Lines(1, n), Gloss: $"{n} lines"), wheelLines);

    private static (ChromePainter P, VtScreenSurface S, ToolBlockItem B) Grep(string text, int wheelLines = 3) =>
        Committed(new ToolCall("c1", "grep", JsonSerializer.Serialize(new { pattern = "m" })), new ToolResult(text, Gloss: "matches"), wheelLines);

    private static int RowOf(VtScreenSurface s, string needle)
    {
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(needle, StringComparison.Ordinal));
        Assert.True(y >= 0, $"no row holds {needle}:\n{string.Join("\n", s.Viewport.Select(v => v.TrimEnd()))}");
        return y;
    }

    private static void Press(ChromePainter p, int x, int y)
    {
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    private static void PressWords(ChromePainter p, VtScreenSurface s, string words)
    {
        var y = RowOf(s, words);
        Press(p, s.Viewport[y].IndexOf(words, StringComparison.Ordinal) + 2, y);
    }

    private static void Wheel(ChromePainter p, int y, int delta)
    {
        p.Mouse.Handle(new MouseEvent(10, y, MouseKind.Wheel, MouseButton.None, delta, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    private static void Focus(ChromePainter p, ToolBlockItem b)
    {
        p.Focus.Focus(p.Model.Items.ToList().IndexOf(b), p.Width, p.ViewportRows(), reveal: false);
        p.Repaint();
    }

    private static void Open(ChromePainter p, ToolBlockItem b, int top = 0)
    {
        b.Collapsed = false;
        b.OutputTop = top;
        p.Repaint();
    }

    private static string? Drag(ChromePainter p, int x1, int y1, int x2, int y2)
    {
        p.Mouse.Handle(new MouseEvent(x1, y1, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x2, y2, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x2, y2, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        return p.Selection.CopyText(p.Width, T, null, GlyphSet.Unicode)?.Replace("\r\n", "\n");
    }

    //the footer and the link
    [Fact]
    public void A_press_on_show_all_opens_every_row_and_show_less_returns_to_the_window_at_its_top()
    {
        var (p, s, b) = Grep(Files(("a.cs", 20)));
        PressWords(p, s, ShellBlockRender.ExpandWords);
        Assert.False(b.Collapsed);
        b.OutputTop = 6;
        p.Repaint();
        PressWords(p, s, "show all");
        Assert.Equal(ShellView.All, b.View);
        Assert.Contains(s.Viewport, v => v.Contains("a.cs-m20", StringComparison.Ordinal));
        PressWords(p, s, "show less");
        Assert.Equal((ShellView.Window, 0), (b.View, b.OutputTop));
    }

    [Fact]
    public void A_press_one_cell_outside_the_action_changes_nothing()
    {
        var (p, s, b) = Read(30);
        Open(p, b);
        var y = RowOf(s, "show all");
        var x = s.Viewport[y].IndexOf("show all", StringComparison.Ordinal);
        Press(p, x + "show all".Length, y);
        Press(p, x - 1, y);
        Assert.Equal(ShellView.Window, b.View);
        Assert.False(b.Collapsed);
    }

    //the wheel
    [Fact]
    public void A_notch_over_the_focused_window_moves_it_by_the_wheel_setting_and_leaves_the_transcript()
    {
        var (p, s, b) = Read(30);
        Open(p, b, 6);
        Focus(p, b);
        var gloss = RowOf(s, ShellBlockRender.CollapseWords);
        Wheel(p, RowOf(s, "text of line 8"), 120);
        Assert.Equal(3, b.OutputTop);
        Assert.Equal(gloss, RowOf(s, ShellBlockRender.CollapseWords));
    }

    [Fact]
    public void A_notch_at_the_windows_end_scrolls_the_transcript()
    {
        var (p, s, b) = Read(30);
        Open(p, b);
        Focus(p, b);
        var gloss = RowOf(s, ShellBlockRender.CollapseWords);
        Wheel(p, RowOf(s, "text of line 2"), 120);
        Assert.Equal(0, b.OutputTop);
        Assert.Equal(gloss + 3, RowOf(s, ShellBlockRender.CollapseWords));
    }

    [Fact]
    public void A_notch_over_a_block_without_the_focus_scrolls_the_transcript()
    {
        var (p, s, b) = Read(30);
        Open(p, b, 6);
        var gloss = RowOf(s, ShellBlockRender.CollapseWords);
        Wheel(p, RowOf(s, "text of line 8"), 120);
        Assert.Equal(6, b.OutputTop);
        Assert.Equal(gloss + 3, RowOf(s, ShellBlockRender.CollapseWords));
    }

    [Fact]
    public void A_notch_over_the_sticky_heading_moves_the_window()
    {
        var (p, s, b) = Grep(Files(("a.cs", 12)));
        Open(p, b, 4);
        Focus(p, b);
        var sticky = RowOf(s, "a.cs-m4") - 1;
        Assert.EndsWith("a.cs", s.Viewport[sticky].TrimEnd(), StringComparison.Ordinal);
        Wheel(p, sticky, -120);
        Assert.Equal(7, b.OutputTop);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(3, 8)]
    public void A_notch_across_a_heading_sends_nothing_to_the_transcript(int wheelLines, int after)
    {
        var (p, s, b) = Grep(Files(("a.cs", 3), ("b.cs", 8)), wheelLines);
        Open(p, b, 4);
        Focus(p, b);
        var gloss = RowOf(s, ShellBlockRender.CollapseWords);
        Wheel(p, RowOf(s, "b.cs-m1"), 120 * -1);
        Assert.Equal(after, b.OutputTop);
        Assert.Equal(gloss, RowOf(s, ShellBlockRender.CollapseWords));
    }

    //a press on the body gives the focus back, so the wheel reaches the window after it scrolled the transcript
    private static void WheelBack(ChromePainter p, VtScreenSurface s, ToolBlockItem b, string rowText, string pressText)
    {
        Open(p, b, 6);
        Focus(p, b);
        Wheel(p, RowOf(s, rowText), -120);
        Assert.Equal(9, b.OutputTop);
        Wheel(p, RowOf(s, "prose 5"), 120);
        Assert.Null(p.Focus.Focused);
        Press(p, 12, RowOf(s, pressText));
        Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
        Wheel(p, RowOf(s, pressText), -120);
        Assert.Equal(12, b.OutputTop);
        Assert.False(b.Collapsed);
    }

    [Fact]
    public void A_press_on_a_windowed_block_body_brings_the_wheel_back()
    {
        var (p, s, b) = Read(30);
        WheelBack(p, s, b, "text of line 8", "text of line 10");   //the window's first row, the jump hint covers the rows under it
    }

    [Fact]
    public void A_press_on_a_shell_block_body_brings_the_wheel_back()
    {
        var output = string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"row{i:00}"));
        var (p, s, b) = Committed(new ToolCall("c1", "shell", JsonSerializer.Serialize(new { command = "ls" })), new ToolResult(output, Gloss: "exit 0"));
        WheelBack(p, s, b, "row08", "row10");
    }

    [Fact]
    public void A_press_on_the_rail_of_a_body_row_gives_the_focus_and_still_copies()
    {
        var (p, s, b) = Read(30);
        Open(p, b);
        Assert.Equal("text of line 2\ntext of line 3", Drag(p, 0, RowOf(s, "text of line 2"), 119, RowOf(s, "text of line 3")));
        Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
        Assert.False(b.Collapsed);
    }

    //the keys and the header
    [Fact]
    public void Ctrl_R_cycles_closed_window_show_all_closed()
    {
        var (p, _, b) = Read(30);
        Focus(p, b);
        p.Focus.ToggleFocused(120, 40); Assert.Equal((false, ShellView.Window), (b.Collapsed, b.View));
        p.Focus.ToggleFocused(120, 40); Assert.Equal((false, ShellView.All), (b.Collapsed, b.View));
        p.Focus.ToggleFocused(120, 40); Assert.True(b.Collapsed);
        p.Focus.ToggleFocused(120, 40); Assert.Equal((false, ShellView.Window), (b.Collapsed, b.View));
    }

    [Fact]
    public void Ctrl_R_skips_show_all_when_the_window_hides_nothing()
    {
        var (p, _, b) = Read(3);
        Focus(p, b);
        p.Focus.ToggleFocused(120, 40); Assert.False(b.Collapsed);
        p.Focus.ToggleFocused(120, 40); Assert.True(b.Collapsed);
        Assert.Equal(ShellView.Window, b.View);
    }

    [Fact]
    public void Show_all_over_a_row_that_fits_draws_no_footer_and_Ctrl_R_closes_it()
    {
        var (p, s, b) = Read(1);
        Focus(p, b);
        Open(p, b);
        b.View = ShellView.All;
        p.Repaint();
        Assert.DoesNotContain(s.Viewport, v => v.Contains("show less", StringComparison.Ordinal));
        var y = RowOf(s, "text of line 1") + 1;
        for (var x = 0; x < 20; x++) Press(p, x, y);
        Assert.Equal((false, ShellView.All), (b.Collapsed, b.View));
        p.Focus.ToggleFocused(120, 40);
        Assert.True(b.Collapsed);
    }

    [Fact]
    public void A_block_of_one_row_cut_at_the_edge_offers_show_all_on_screen_and_to_Ctrl_R()
    {
        var (p, s, b) = Committed(new ToolCall("c1", "read_file", JsonSerializer.Serialize(new { path = "a.cs" })),
            new ToolResult("start " + string.Concat(Enumerable.Repeat("word ", 40)) + "tail", Gloss: "1 line"));
        Focus(p, b);
        p.Focus.ToggleFocused(120, 40);
        p.Repaint();
        Assert.Equal((false, ShellView.Window), (b.Collapsed, b.View));
        Assert.DoesNotContain(s.Viewport, v => v.Contains(" tail", StringComparison.Ordinal));
        RowOf(s, "show all");
        p.Focus.ToggleFocused(120, 40);
        p.Repaint();
        Assert.Equal((false, ShellView.All), (b.Collapsed, b.View));
        RowOf(s, " tail");
        RowOf(s, "show less");
        p.Focus.ToggleFocused(120, 40);
        Assert.True(b.Collapsed);
    }

    [Fact]
    public void A_header_press_toggles_closed_and_window()
    {
        var (p, s, b) = Read(30);
        Press(p, 2, RowOf(s, "read_file"));
        Assert.Equal((false, ShellView.Window), (b.Collapsed, b.View));
        Press(p, 2, RowOf(s, "read_file"));
        Assert.True(b.Collapsed);
    }

    [Fact]
    public void A_closed_block_opens_at_the_top_of_its_window()
    {
        var (p, s, b) = Read(30);
        Open(p, b, 6);
        b.View = ShellView.All;
        Press(p, 2, RowOf(s, "read_file"));
        Assert.True(b.Collapsed);
        Press(p, 2, RowOf(s, "read_file"));
        Assert.Equal((false, ShellView.Window, 0), (b.Collapsed, b.View, b.OutputTop));
    }

    //the copy
    [Fact]
    public void A_copy_of_two_body_rows_from_column_0_holds_the_text_only()
    {
        var (p, s, b) = Read(30);
        Open(p, b);
        Assert.Equal("text of line 2\ntext of line 3", Drag(p, 0, RowOf(s, "text of line 2"), 119, RowOf(s, "text of line 3")));
        Assert.False(b.Collapsed);
    }

    [Fact]
    public void A_copy_across_the_sticky_heading_holds_the_path_once()
    {
        var (p, s, b) = Grep(Files(("a.cs", 12)));
        Open(p, b, 4);
        var m4 = RowOf(s, "a.cs-m4");
        Assert.Equal("a.cs\na.cs-m4\na.cs-m5", Drag(p, 0, m4 - 1, 119, m4 + 1));
    }

    [Fact]
    public void A_copy_across_the_empty_last_row_adds_no_line()
    {
        var (p, s, b) = Grep(Files(("a.cs", 4), ("b.cs", 3)));
        Open(p, b, ToolWindow.MaxTop(ToolBody.Grep(GrepRows.Parse(b.FullResult)!)));
        var last = RowOf(s, "b.cs-m3");
        Assert.Equal("b.cs-m2\nb.cs-m3", Drag(p, 0, last - 1, 119, last + 1));
    }

    //a notch the window cannot use leaves the item as it was, so a live selection on it stands
    [Theory]
    [InlineData("grep")]
    [InlineData("shell")]
    public void A_notch_that_moves_nothing_keeps_the_items_revision_and_its_selection(string tool)
    {
        var (p, s, b) = tool == "grep"
            ? Grep(Files(("a.cs", 3)))
            : Committed(new ToolCall("c1", "shell", JsonSerializer.Serialize(new { command = "ls" })), new ToolResult("row01\r\nrow02\r\nrow03", Gloss: "exit 0"));
        Open(p, b);
        b.ResetView();
        p.Repaint();
        Focus(p, b);
        var text = tool == "grep" ? "a.cs-m2" : "row02";
        var y = RowOf(s, text);
        Assert.NotNull(Drag(p, 0, y, 119, y));
        var rev = b.Rev;
        Wheel(p, y, -120);
        Assert.Equal(rev, b.Rev);
        Assert.NotNull(p.Selection.Current);
    }

    [Fact]
    public void A_press_in_column_0_of_a_body_row_starts_a_selection_and_closes_nothing()
    {
        var (p, s, b) = Read(30);
        Open(p, b);
        var y = RowOf(s, "text of line 2");
        Press(p, 0, y);
        Press(p, 1, y);
        Assert.False(b.Collapsed);
        Assert.Equal("text of line 2", Drag(p, 0, y, 119, y));
    }
}
