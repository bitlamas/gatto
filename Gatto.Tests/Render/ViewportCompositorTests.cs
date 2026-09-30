using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ViewportCompositorTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    //the fixture chrome is a spacer plus a composer row, with the caret on the composer's text cell.
    private static Func<int, int, ChromeBlock> Composer(string text, bool spacer = true)
    {
        var composer = "> " + text;
        return (w, h) => spacer
            ? ChromeBlock.FromText(new[] { "", composer }, caretRow: 1, caretCol: 2 + text.Length)
            : ChromeBlock.FromText(new[] { composer }, caretRow: 0, caretCol: 2 + text.Length);
    }

    private static (VtScreenSurface, ViewportCompositor) Build(int w, int h, params TranscriptItem[] items)
    {
        var s = new VtScreenSurface(w, h);
        s.Write(Ansi.AltScreenEnter);   //the compositor runs on the alt screen only.
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        //clear the leading blank so the frame math uses pure content heights, and the blank has its own tests
        foreach (var it in items) { model.Append(it); it.LeadingBlank = false; }
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode);
        return (s, comp);
    }

    //at the very top the first transcript row must stay reachable, the jump hint takes one of the window's rows
    [Fact]
    public void Scrolled_to_the_top_the_first_content_row_is_reachable_under_the_jump_hint()
    {
        //ten content rows show four, six screen rows minus two chrome rows.
        var lines = Enumerable.Range(0, 10).Select(n => $"row {n}").ToArray();
        var (s, comp) = Build(30, 6, new AssistantBlockItem(lines, "generalist"));
        comp.ComposeChrome = Composer("hi");

        //fully scrolled up means six rows below the four-row window.
        comp.Paint(bottomOffset: 6, following: false);

        var body = string.Join("\n", s.Viewport);
        Assert.Contains("row 0", body, StringComparison.Ordinal);   //the oldest row must be on screen, which is the regression under test.
        Assert.Contains("↓", body, StringComparison.Ordinal);       //the hint must still show.
    }

    //the hint and the blank row above it each take a row, so the count is 2 more than the offset
    [Fact]
    public void The_jump_hint_counts_the_rows_it_displaces()
    {
        var lines = Enumerable.Range(0, 10).Select(n => $"row {n}").ToArray();
        var (s, comp) = Build(30, 6, new AssistantBlockItem(lines, "generalist"));
        comp.ComposeChrome = Composer("hi");
        comp.Paint(bottomOffset: 6, following: false);

        var hint = s.Viewport.First(r => r.Contains('↓'));
        Assert.Contains("8 more", TermText.StripAnsiForWidth(hint), StringComparison.Ordinal);
        Assert.Contains("row 1", s.Viewport[1]);   //row 1 is the last one shown, and rows 2 to 9 are the 8
    }

    [Fact]
    public void Composer_sits_on_the_physical_bottom_row_even_with_little_content()
    {
        var (s, comp) = Build(30, 6, new AssistantBlockItem(new[] { "only line" }, "generalist"));
        comp.ComposeChrome = Composer("hi");
        comp.Paint(bottomOffset: 0, following: true);

        Assert.Equal("> hi", s.Viewport[5]);                  //the composer sits on the last physical row.
        Assert.Contains("only line", s.Viewport[3]);          //content is bottom-anchored, just above the spacer and composer.
        Assert.Equal("", s.Viewport[0]);                       //blank rows pad the top
    }

    [Fact]
    public void Full_frame_bottom_anchors_the_transcript_tail_when_content_overflows()
    {
        var items = new TranscriptItem[8];
        for (var i = 0; i < 8; i++) items[i] = new AssistantBlockItem(new[] { $"line {i}" }, "generalist");
        var (s, comp) = Build(30, 6, items);   //eight content rows and two chrome rows leave four transcript rows.
        comp.ComposeChrome = Composer("x");
        comp.Paint(bottomOffset: 0, following: true);

        //the last four rows show, hugging the chrome, and the composer stays on the bottom.
        Assert.Contains("line 4", s.Viewport[0]);
        Assert.Contains("line 7", s.Viewport[3]);
        Assert.Equal("> x", s.Viewport[5]);
    }

    [Fact]
    public void Scrolling_up_shows_earlier_content_and_never_grows_scrollback()
    {
        var items = new TranscriptItem[8];
        for (var i = 0; i < 8; i++) items[i] = new AssistantBlockItem(new[] { $"line {i}" }, "generalist");
        var (s, comp) = Build(30, 6, items);
        comp.ComposeChrome = Composer("x");
        comp.Paint(bottomOffset: 2, following: false);   //scrolling up detaches, and earlier content shows with the jump hint.

        //the hint and the blank row above it take the 2 bottom rows, the top row stays
        Assert.Contains("line 2", s.Viewport[0]);
        Assert.Contains("line 3", s.Viewport[1]);
        Assert.Equal("", s.Viewport[2]);
        Assert.Contains("Ctrl+End", s.Viewport[3]);
        Assert.Empty(s.Scrollback);   //the alt screen owns its scrolling, so nothing may spill into native scrollback.
        Assert.Equal(0, s.Scrolled);
    }

    [Fact]
    public void Tiny_viewport_keeps_the_composer_when_the_chrome_outgrows_the_frame()
    {
        var (s, comp) = Build(30, 1, new AssistantBlockItem(new[] { "content" }, "generalist"));
        comp.ComposeChrome = Composer("x");   //two chrome rows are squeezed into a one-row frame.
        comp.Paint(bottomOffset: 0, following: true);

        Assert.Single(s.Viewport);
        Assert.Equal("> x", s.Viewport[0]);
    }

    [Fact]
    public void Clipped_top_item_paints_its_visible_tail_rows_with_intact_styling()
    {
        var tall = new ReasoningItem(new[] { "r0", "r1", "r2", "r3" });
        var (s, comp) = Build(30, 4, tall);   //two chrome rows leave two transcript rows, so the last two rows show.
        comp.ComposeChrome = Composer("x");
        comp.Paint(bottomOffset: 0, following: true);

        Assert.Contains("r2", s.Viewport[0]);
        Assert.Contains("r3", s.Viewport[1]);
        Assert.Equal("> x", s.Viewport[3]);
    }

    [Fact]
    public void Over_wide_verbatim_row_is_clipped_and_never_spills_the_frame()
    {
        //a command echo renders verbatim by design, so after a shrink a row is wider than the terminal. autowrap under cursor addressing would shift every row below it.
        var wide = new CommandEchoItem(new[] { new string('x', 100) }, After: null);
        var (s, comp) = Build(20, 4, wide);
        comp.ComposeChrome = Composer("c");
        comp.Paint(bottomOffset: 0, following: true);

        Assert.Equal(4, s.Viewport.Count);
        Assert.All(s.Viewport, row => Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(row)) <= 20));
        Assert.Equal("> c", s.Viewport[3]);
    }

    //a styling check paints on RecordingSurface and reads the raw bytes, since VtScreenSurface models cells and drops the escapes
    private static (RecordingSurface, ViewportCompositor) BuildRecording(int w, int h, params TranscriptItem[] items)
    {
        var s = new RecordingSurface { Width = w, Height = h };
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        foreach (var it in items) { model.Append(it); it.LeadingBlank = false; }
        return (s, new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void FocusedItemIndex_marks_the_item_with_an_accent_bar_not_a_reverse_video_wash()
    {
        var (s, comp) = BuildRecording(30, 6,
            new AssistantBlockItem(new[] { "item zero" }, "generalist"),
            new AssistantBlockItem(new[] { "item one" }, "generalist"));

        comp.FocusedItemIndex = null;
        comp.Paint(bottomOffset: 0, following: true);
        Assert.DoesNotContain("\x1b[7m", s.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("│", s.Text, StringComparison.Ordinal);

        //the focus cursor writes the bar as its own cursor-position write, so the row's SGR state is never edited
        s.Clear();
        comp.FocusedItemIndex = 1;
        comp.Paint(bottomOffset: 0, following: true);
        Assert.DoesNotContain("\x1b[7m", s.Text, StringComparison.Ordinal);
        Assert.Contains("│", s.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_focus_bar_lands_only_on_the_focused_items_row()
    {
        var (s, comp) = Build(30, 6,
            new AssistantBlockItem(new[] { "item zero" }, "generalist"),
            new AssistantBlockItem(new[] { "item one" }, "generalist"));

        comp.FocusedItemIndex = 1;
        comp.Paint(bottomOffset: 0, following: true);
        var zero = Enumerable.Range(0, 6).First(i => s.Viewport[i].Contains("zero"));
        var one = Enumerable.Range(0, 6).First(i => s.Viewport[i].Contains("one"));   //the bar overwrites the first cell, yet the row still matches the search text.
        Assert.Equal('│', s.Viewport[one][0]);
        Assert.NotEqual('│', s.Viewport[zero][0]);
    }

    [Fact]
    public void Every_frame_row_closes_any_open_OSC8_hyperlink()
    {
        var (s, comp) = BuildRecording(20, 4,
            new AssistantBlockItem(new[] { "alpha" }, "generalist"),
            new AssistantBlockItem(new[] { "beta" }, "generalist"));
        comp.Paint(bottomOffset: 0, following: true);
        //every row closes any open link scope, or it leaks over the rows below and the terminal underlines the whole frame
        var closes = System.Text.RegularExpressions.Regex.Matches(
            s.Text, System.Text.RegularExpressions.Regex.Escape(Ansi.LinkClose)).Count;
        Assert.Equal(4, closes);
    }

    [Fact]
    public void Stop_makes_further_paints_no_op()
    {
        var (s, comp) = Build(30, 4, new AssistantBlockItem(new[] { "hi" }, "generalist"));
        comp.ComposeChrome = Composer("x");
        comp.Stop();
        comp.Paint(bottomOffset: 0, following: true);

        Assert.All(s.Viewport, row => Assert.Equal("", row));   //a paint after Stop draws nothing, so a late tick can't draw chrome onto the restored main screen
    }
}
