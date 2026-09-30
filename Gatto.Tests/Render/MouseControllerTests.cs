using System.Collections.Concurrent;
using System.Threading;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class MouseControllerTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private sealed class Harness
    {
        public required VtScreenSurface S;
        public required ViewportCompositor Comp;
        public required ScrollController Scroll;
        public required FocusController Focus;
        public required SelectionController Selection;
        public required TranscriptModel Model;
        public required MouseController Mc;
        public required int W;
        public required int VpRows;   //the count of transcript rows is surface height minus chrome.
        public long Clock;            //the test moves this clock to drive triple-click timing
        public List<string> QueueList = new();   //a gesture removes entries from this list through DispatchOldest
        //every gesture the controller fires is recorded here, begin, extend or clear
        public List<(int Line, int Col, ComposerGesture Kind)> Gestures = new();

        public void Paint(int bottomOffset = 0, bool following = true) => Comp.Paint(bottomOffset, following);
        public void DispatchOldest() { if (QueueList.Count > 0) QueueList.RemoveAt(0); }
        public void Resize(int width) { S.Resize(width, S.Height); W = width; }
        public int QueueRowY(int regionRow) =>
            Enumerable.Range(0, S.Height).First(y => Comp.HitTest(y) is ChromeTarget { Region: ChromeRegion.Queue } ct && ct.RegionRow == regionRow);
        public void Wheel(int delta) => Mc.Handle(new MouseEvent(0, 0, MouseKind.Wheel, MouseButton.None, delta, 0), W, VpRows);
        public void ClickAt(int x, int y) => Mc.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0), W, VpRows);
        public void PressAt(int x, int y, int clicks = 1) => Mc.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0) { Clicks = clicks }, W, VpRows);
        public void MoveAt(int x, int y) => Mc.Handle(new MouseEvent(x, y, MouseKind.Move, MouseButton.None, 0, 0), W, VpRows);
        public void ReleaseAt(int x, int y) => Mc.Handle(new MouseEvent(x, y, MouseKind.Release, MouseButton.Left, 0, 0), W, VpRows);
        public void ClickRow(int y) => ClickAt(4, y);      //clicks the text of a row, past the two-cell gutter.
        public void ClickGutter(int y) => ClickAt(0, y);
        public int RowWhere(System.Func<MouseTarget, bool> pred) =>
            Enumerable.Range(0, S.Height).First(y => pred(Comp.HitTest(y)));
        public int RowOfCell(int item, int rel) =>
            Enumerable.Range(0, S.Height).First(y => Comp.CellAt(0, y) is { } c && c.ItemIndex == item && c.Rel == rel);
        public int ChromeRowY(int chromeRow) =>
            Enumerable.Range(0, S.Height).First(y => Comp.HitTest(y) is ChromeTarget ct && ct.Row == chromeRow);
        //the Tool region only, so the row is one on the generic chrome-drag path
        public int ChromeRowIndex(int regionRow) =>
            Enumerable.Range(0, S.Height).Select(y => Comp.HitTest(y)).OfType<ChromeTarget>()
                .First(ct => ct.Region == ChromeRegion.Tool && ct.RegionRow == regionRow).Row;
    }

    //three composer rows with the real tagging, so the harness doesn't depend on the wrap-width math
    private static Harness HarnessWithWrappedComposer()
    {
        var rows = new List<ChromeRow>
        {
            new("❯ one two three", "❯ one two three", false, 0, ChromeRegion.Composer, 0),
            new("  four five six", "  four five six", true, 2, ChromeRegion.Composer, 1),
            new("  seven eight",   "  seven eight",   true, 2, ChromeRegion.Composer, 2),
        };
        var s = new VtScreenSurface(40, 10);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var gate = new object();
        var scroll = new ScrollController(model, index);
        var comp = new ViewportCompositor(s, index, gate, glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => new ChromeBlock(rows, 0, 2) };
        var focus = new FocusController(model, index, scroll, comp);
        var selection = new SelectionController(model);
        var h2 = new Harness
        {
            //seven is the ten-row surface minus the three chrome rows
            S = s, Comp = comp, Scroll = scroll, Focus = focus, Selection = selection, Model = model,
            Mc = null!, W = 40, VpRows = 7,
        };
        h2.Mc = new MouseController(comp, scroll, focus, model, wheelLines: 3, selection, T, () => h2.Clock);
        return h2;
    }

    //real transcript rows so a clamp differs from a no-op, with the chrome tagged Tool to stay on the generic drag path
    private static Harness HarnessWithTranscriptAndWrappedComposer()
    {
        var rows = new List<ChromeRow>
        {
            new("❯ one two three", "❯ one two three", false, 0, ChromeRegion.Tool, 0),
            new("  four five six", "  four five six", true, 2, ChromeRegion.Tool, 1),
            new("  seven eight",   "  seven eight",   true, 2, ChromeRegion.Tool, 2),
        };
        var s = new VtScreenSurface(40, 12);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var gate = new object();
        var scroll = new ScrollController(model, index);
        var comp = new ViewportCompositor(s, index, gate, glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => new ChromeBlock(rows, 0, 2) };
        var focus = new FocusController(model, index, scroll, comp);
        foreach (var it in Many(3)) model.Append(it);
        var selection = new SelectionController(model);
        var h2 = new Harness
        {
            S = s, Comp = comp, Scroll = scroll, Focus = focus, Selection = selection, Model = model,
            Mc = null!, W = 40, VpRows = 9,   //nine is the twelve-row surface minus the three chrome rows
        };
        h2.Mc = new MouseController(comp, scroll, focus, model, wheelLines: 3, selection, T, () => h2.Clock,
            onComposerSelect: (line, col, kind) => h2.Gestures.Add((line, col, kind)));
        return h2;
    }

    //a hardcoded row is a transcript row once the transcript is seeded, so aim by region tag
    private static Harness HarnessWithRegionTaggedChromeAndSeededTranscript()
    {
        var rows = new List<ChromeRow>
        {
            new("prompt row", "prompt row", false, 0, ChromeRegion.Prompt, 0),
            new("tool row", "tool row", false, 0, ChromeRegion.Tool, 0),
        };
        var s = new VtScreenSurface(40, 12);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var gate = new object();
        var scroll = new ScrollController(model, index);
        var comp = new ViewportCompositor(s, index, gate, glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => new ChromeBlock(rows, 0, 2) };
        var focus = new FocusController(model, index, scroll, comp);
        foreach (var it in Many(30)) model.Append(it);
        var selection = new SelectionController(model);
        return new Harness
        {
            S = s, Comp = comp, Scroll = scroll, Focus = focus, Selection = selection, Model = model,
            Mc = null!, W = 40, VpRows = 10,   //ten is the twelve-row surface minus the two chrome rows
        };
    }

    private static Harness Build(int w, int h, int wheelLines, params TranscriptItem[] items)
    {
        var s = new VtScreenSurface(w, h);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var gate = new object();
        var scroll = new ScrollController(model, index);
        var comp = new ViewportCompositor(s, index, gate, glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => ChromeBlock.FromText(new[] { "> " }, 0, 2) };
        var focus = new FocusController(model, index, scroll, comp);
        foreach (var it in items) model.Append(it);
        var selection = new SelectionController(model);
        var h2 = new Harness { S = s, Comp = comp, Scroll = scroll, Focus = focus, Selection = selection, Model = model, Mc = null!, W = w, VpRows = h - 1 };
        h2.Mc = new MouseController(comp, scroll, focus, model, wheelLines, selection, T, () => h2.Clock,
            onComposerSelect: (line, col, kind) => h2.Gestures.Add((line, col, kind)));
        return h2;
    }

    private static AssistantBlockItem Prose(string t) => new(new[] { t }, "generalist") { LeadingBlank = false };
    private static string Visible(string row) => Gatto.Terminal.TermText.StripAnsiForWidth(row);
    private static ReasoningItem CollapsedReasoning() =>
        new(new[] { "thinking" }) { Streaming = false, Collapsed = true, Elapsed = TimeSpan.FromSeconds(3), LeadingBlank = true };
    private static ToolBlockItem ExpandedTool() =>
        new("read_file", "f.txt", "✓ ok", true, "generalist") { FullResult = "a\nb\nc", Collapsed = false, LeadingBlank = true };

    private static TranscriptItem[] Many(int n) =>
        Enumerable.Range(0, n).Select(i => (TranscriptItem)Prose($"line {i}")).ToArray();

    [Fact]
    public void Sub_notch_wheel_accumulates_and_does_not_detach_until_a_full_line()
    {
        var h = Build(40, 6, wheelLines: 1, Many(30));   //wheelLines one, so a single 120-delta notch scrolls one line
        h.Paint();
        Assert.True(h.Scroll.Following);
        h.Wheel(40); h.Wheel(40);                        //80 accumulated is under 120, so no line scrolls.
        Assert.True(h.Scroll.Following);                 //a partial line must not detach the follow.
        h.Wheel(40);                                      //a full 120 scrolls one line and detaches.
        Assert.False(h.Scroll.Following);
    }

    [Fact]
    public void A_128_delta_notch_scrolls_wheel_lines()   //a real mouse sends 128 delta per notch.
    {
        var h = Build(40, 6, wheelLines: 3, Many(30));   //384 delta holds three full lines with 24 remaining.
        h.Paint();
        h.Wheel(128);
        Assert.False(h.Scroll.Following);
        Assert.Equal(3, h.Scroll.BottomOffset(40, 5));   //three lines exactly, the 24 leftover delta does not add a fourth
    }

    //paint first, HitTest answers no target until the first paint

    [Fact]
    public void Wheel_OverThePromptRow_ConsumedByTheComposer_TranscriptUnchanged()
    {
        var h = HarnessWithRegionTaggedChromeAndSeededTranscript();
        var received = -1;
        h.Mc = new MouseController(h.Comp, h.Scroll, h.Focus, h.Model, wheelLines: 3, h.Selection, T,
            onComposerScroll: n => { received = n; return true; });
        h.Paint();
        var promptY = Enumerable.Range(0, 12).First(y => h.Comp.HitTest(y) is ChromeTarget { Region: ChromeRegion.Prompt });
        h.Mc.Handle(new MouseEvent(4, promptY, MouseKind.Wheel, MouseButton.None, 120, 0), h.W, h.VpRows);
        Assert.True(h.Scroll.Following);   //the transcript stays put.
        Assert.Equal(3, received);         //the callback gets the line count, so a 120 delta becomes three
    }

    [Fact]
    public void Wheel_OverANonPromptChromeRow_StillScrollsTheTranscript()
    {
        //every chrome row other than prompt, rule and composer scrolls the transcript
        var h = HarnessWithRegionTaggedChromeAndSeededTranscript();
        h.Mc = new MouseController(h.Comp, h.Scroll, h.Focus, h.Model, wheelLines: 3, h.Selection, T,
            onComposerScroll: _ => true);   //this callback consumes everything, so only a prompt row may reach it
        h.Paint();
        var toolRowY = Enumerable.Range(0, 12).First(y => h.Comp.HitTest(y) is ChromeTarget { Region: ChromeRegion.Tool });
        h.Mc.Handle(new MouseEvent(4, toolRowY, MouseKind.Wheel, MouseButton.None, 120, 0), h.W, h.VpRows);
        Assert.False(h.Scroll.Following);   //the transcript moves.
    }

    [Fact]
    public void Wheel_OverThePromptRow_CallbackReturnsFalse_FallsThroughToTheTranscript()
    {
        var h = HarnessWithRegionTaggedChromeAndSeededTranscript();
        h.Mc = new MouseController(h.Comp, h.Scroll, h.Focus, h.Model, wheelLines: 3, h.Selection, T,
            onComposerScroll: _ => false);
        h.Paint();
        var promptY = Enumerable.Range(0, 12).First(y => h.Comp.HitTest(y) is ChromeTarget { Region: ChromeRegion.Prompt });
        h.Mc.Handle(new MouseEvent(4, promptY, MouseKind.Wheel, MouseButton.None, 120, 0), h.W, h.VpRows);
        Assert.False(h.Scroll.Following);   //with no panel to consume the notch, the transcript is the fallback.
    }

    [Fact]
    public void Click_a_collapsed_reasoning_summary_expands_and_focuses_it()   //a click must match what Ctrl+R does
    {
        var h = Build(40, 16, 3, Prose("hi"), CollapsedReasoning(), ExpandedTool());
        h.Paint();
        h.ClickRow(h.RowWhere(t => t is ItemTarget { ItemIndex: 1, HeaderRow: true }));
        Assert.False(h.Model.Items[1].Collapsed);
        Assert.Equal(1, h.Focus.Focused);
    }

    [Fact]
    public void Click_an_expanded_item_header_collapses_and_focuses_it()
    {
        var h = Build(40, 16, 3, Prose("hi"), CollapsedReasoning(), ExpandedTool());
        h.Paint();
        var header = Enumerable.Range(0, 16).First(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 2, HeaderRow: true });
        h.ClickRow(header);
        Assert.True(h.Model.Items[2].Collapsed);
        Assert.Equal(2, h.Focus.Focused);
    }

    [Fact]
    public void Click_the_thinking_blocks_affordance_row_expands_it()
    {
        //a click on the affordance row expands the block and must not start a selection drag
        var thinking = new ReasoningItem(Enumerable.Range(0, 12).Select(i => $"thought {i}").ToArray())
            { Streaming = true, Collapsed = true, LeadingBlank = true };
        var h = Build(40, 20, 3, Prose("hi"), thinking);
        h.Paint();

        var affordance = Enumerable.Range(0, 20).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1 });
        Assert.Contains("⋯", Visible(h.S.Viewport[affordance]));   //the marker glyph proves the clicked row is the affordance
        h.ClickRow(affordance);   //the text column, past the two-cell gutter

        Assert.False(h.Model.Items[1].Collapsed);
    }

    [Fact]
    public void A_thinking_block_whose_tail_is_scrolled_off_does_not_offer_its_affordance()
    {
        //a row at the frame's bottom edge has not reached the end of the item
        var thinking = new ReasoningItem(Enumerable.Range(0, 12).Select(i => $"thought {i}").ToArray())
            { Streaming = true, Collapsed = true, LeadingBlank = true };
        //make the transcript long enough to scroll, otherwise the offset clamps to 0 and the fixture tests the unscrolled case.
        var h = Build(40, 12, 3, Many(15).Concat(new TranscriptItem[] { thinking, Prose("tail") }).ToArray());   //the tail item takes the scrolled rows, the hint and its gap cover the affordance only
        h.Paint(bottomOffset: 1, following: false);

        var thinkingIndex = h.Model.Items.Count - 2;
        var lastVisible = Enumerable.Range(0, 12)
            .Last(y => h.Comp.HitTest(y) is ItemTarget it && it.ItemIndex == thinkingIndex);
        //no marker and no header flag on this row, otherwise the test passes for the wrong reason
        var text = Visible(h.S.Viewport[lastVisible]);
        Assert.DoesNotContain("⋯", text);
        Assert.Contains("thought", text);
        Assert.False(((ItemTarget)h.Comp.HitTest(lastVisible)).HeaderRow);

        h.ClickRow(lastVisible);

        Assert.True(h.Model.Items[thinkingIndex].Collapsed);   //the click was a selection, so the block stays capped.
    }

    [Fact]
    public void Click_a_thinking_blocks_PREVIEW_TEXT_does_not_toggle_it()
    {
        //the preview rows are real text a user might select, so the affordance rule must stay narrow
        var thinking = new ReasoningItem(Enumerable.Range(0, 12).Select(i => $"thought {i}").ToArray())
            { Streaming = true, Collapsed = true, LeadingBlank = true };
        var h = Build(40, 20, 3, Prose("hi"), thinking);
        h.Paint();

        //pick a middle row, which is neither the header nor the affordance.
        var rows = Enumerable.Range(0, 20).Where(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1 }).ToList();
        Assert.True(rows.Count >= 3, "need a middle row to click");
        h.ClickRow(rows[1]);

        Assert.True(h.Model.Items[1].Collapsed);   //the click was a selection, so the block stays capped
    }

    [Fact]
    public void A_press_on_the_body_of_an_open_tool_block_gives_it_the_focus_and_closes_nothing()   //so the wheel reaches its window again
    {
        var h = Build(40, 16, 3, Prose("hi"), CollapsedReasoning(), ExpandedTool());
        h.Paint();
        var body = Enumerable.Range(0, 16).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 2, HeaderRow: false });
        h.ClickRow(body);                            //the text column, past the gutter
        Assert.False(h.Model.Items[2].Collapsed);
        Assert.Equal(2, h.Focus.Focused);
    }

    [Fact]
    public void A_press_on_the_body_of_an_open_write_block_gives_no_focus()
    {
        var write = new ToolBlockItem("write_file", "a.md", "✓ ok", true, "generalist") { FullResult = "wrote it\nsecond", Collapsed = false, LeadingBlank = true };
        var h = Build(40, 16, 3, Prose("hi"), write);
        h.Paint();
        var body = Enumerable.Range(0, 16).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1, HeaderRow: false });
        h.ClickRow(body);
        Assert.False(h.Model.Items[1].Collapsed);
        Assert.Null(h.Focus.Focused);
    }

    [Fact]
    public void A_press_on_the_rail_of_an_open_tool_block_starts_a_selection_and_closes_nothing()   //the copy from column 0 wins over the close
    {
        var h = Build(40, 16, 3, Prose("hi"), CollapsedReasoning(), ExpandedTool());
        h.Paint();
        var body = Enumerable.Range(0, 16).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 2, HeaderRow: false });
        h.PressAt(0, body);
        h.MoveAt(10, body);
        h.ReleaseAt(10, body);
        Assert.False(h.Model.Items[2].Collapsed);
        Assert.NotNull(h.Selection.Current);
    }

    [Fact]
    public void A_press_on_the_rail_of_an_open_thought_block_closes_it()
    {
        var thought = new ReasoningItem(new[] { "one", "two", "three" }) { Streaming = false, Collapsed = false, Elapsed = TimeSpan.FromSeconds(3), LeadingBlank = true };
        var h = Build(40, 16, 3, Prose("hi"), thought);
        h.Paint();
        var body = Enumerable.Range(0, 16).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1, HeaderRow: false });
        h.ClickGutter(body);
        Assert.True(h.Model.Items[1].Collapsed);
    }

    [Fact]
    public void A_press_on_the_rail_of_an_open_write_block_closes_nothing()   //a write opens to its window and takes the window's rail rule
    {
        var write = new ToolBlockItem("write_file", "a.md", "✓ ok", true, "generalist") { FullResult = "wrote it", WriteContent = "line one\nline two", Collapsed = false, LeadingBlank = true };
        var h = Build(40, 16, 3, Prose("hi"), write);
        h.Paint();
        var body = Enumerable.Range(0, 16).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1, HeaderRow: false });
        Assert.Contains("line two", Visible(h.S.Viewport[body]));
        h.ClickGutter(body);
        Assert.False(h.Model.Items[1].Collapsed);
    }

    [Fact]
    public void Click_the_gutter_of_a_collapsed_thinking_preview_expands_it()
    {
        //a middle preview row is neither the header nor the affordance, so only the gutter rule can toggle the block from it
        var thinking = new ReasoningItem(Enumerable.Range(0, 12).Select(i => $"thought {i}").ToArray())
            { Streaming = true, Collapsed = true, LeadingBlank = true };
        var h = Build(40, 20, 3, Prose("hi"), thinking);
        h.Paint();

        var rows = Enumerable.Range(0, 20).Where(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1 }).ToList();
        var middle = rows[1];
        var target = (ItemTarget)h.Comp.HitTest(middle);
        Assert.False(target.HeaderRow);
        Assert.False(target.LastRow);
        Assert.Contains("thought 1", Visible(h.S.Viewport[middle]));
        h.ClickGutter(middle);

        Assert.False(h.Model.Items[1].Collapsed);
        Assert.Equal(1, h.Focus.Focused);
    }

    [Fact]
    public void Click_the_gutter_of_a_collapsed_tool_blocks_gloss_row_does_not_toggle_it()
    {
        //the rows under a collapsed tool header are content to select, so the collapsed gutter rule reaches thinking blocks only
        var tool = new ToolBlockItem("read_file", "f.txt", "✓ ok", true, "generalist") { FullResult = "a\nb\nc", Collapsed = true, LeadingBlank = true };
        var h = Build(40, 16, 3, Prose("hi"), tool);
        h.Paint();

        var gloss = Enumerable.Range(0, 16).Last(y => h.Comp.HitTest(y) is ItemTarget { ItemIndex: 1, HeaderRow: false });
        Assert.Contains("✓ ok", Visible(h.S.Viewport[gloss]));
        h.ClickGutter(gloss);

        Assert.True(h.Model.Items[1].Collapsed);
    }

    [Fact]
    public void Click_plain_text_changes_nothing()
    {
        var h = Build(40, 16, 3, Prose("hi"), CollapsedReasoning(), ExpandedTool());
        h.Paint();
        h.ClickRow(h.RowWhere(t => t is ItemTarget { ItemIndex: 0 }));
        Assert.Null(h.Focus.Focused);                                    //prose is not collapsible, so focus stays empty.
    }

    [Fact]
    public void Click_a_leading_blank_gap_changes_nothing()
    {
        var h = Build(40, 16, 3, Prose("hi"), CollapsedReasoning(), ExpandedTool());
        h.Paint();
        var summary = h.RowWhere(t => t is ItemTarget { ItemIndex: 1, HeaderRow: true });
        Assert.IsType<NoTarget>(h.Comp.HitTest(summary - 1));   //the row above the summary is the blank gap.
        h.ClickRow(summary - 1);
        Assert.Null(h.Focus.Focused);
    }

    [Fact]
    public void Click_the_jump_hint_reattaches_to_the_bottom()
    {
        var h = Build(40, 8, 3, Many(30));
        h.Paint(bottomOffset: 5, following: false);   //a detached paint shows the hint.
        h.ClickRow(h.RowWhere(t => t is JumpHintTarget));
        Assert.True(h.Scroll.Following);
    }

    [Fact]
    public void ChromePainter_exposes_a_MouseController()
    {
        var painter = new ChromePainter(new VtScreenSurface(40, 10), T, new object());
        Assert.NotNull(painter.Mouse);
        Assert.NotNull(painter.Selection);
    }

    [Fact]
    public void Plain_drag_selects_text_inclusive()
    {
        var h = Build(40, 6, 3, Prose("hello world"));   //the row starts with a two-cell marker, so the text begins at column two
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.PressAt(2, y);
        h.MoveAt(6, y);           //drag to the last selected cell, inclusive.
        h.ReleaseAt(6, y);
        Assert.NotNull(h.Selection.Current);
        Assert.Equal("hello", h.Selection.CopyText(h.W, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void A_bare_click_selects_nothing()
    {
        var h = Build(40, 6, 3, Prose("hello world"));
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.PressAt(4, y);
        h.ReleaseAt(4, y);        //without a move the span is zero-width, and it normalizes to null.
        Assert.Null(h.Selection.Current);
    }

    [Fact]
    public void Shift_press_starts_no_selection()   //a shift press leaves the selection to the terminal itself.
    {
        var h = Build(40, 6, 3, Prose("hello world"));
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.Mc.Handle(new MouseEvent(4, y, MouseKind.Press, MouseButton.Left, 0, ConsoleModifiers.Shift), h.W, h.VpRows);
        Assert.Null(h.Selection.Current);
    }

    [Fact]
    public void Double_click_selects_the_word()
    {
        var h = Build(40, 6, 3, Prose("hello world"));   //two marker cells, so column four is inside hello
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.PressAt(4, y, clicks: 2);
        Assert.Equal("hello", h.Selection.CopyText(h.W, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void Triple_click_selects_the_whole_visible_row()
    {
        var h = Build(40, 6, 3, Prose("hello world"));
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.Clock = 1000;
        h.PressAt(4, y, clicks: 2);
        h.Clock = 1100;               //the next press is 100 milliseconds later, inside the triple-click window.
        h.PressAt(4, y, clicks: 1);   //this press completes the triple-click.
        Assert.Equal("● hello world", h.Selection.CopyText(h.W, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void Triple_click_selects_the_whole_logical_line_across_wraps()
    {
        //one long logical line wraps to at least three rows at width twenty.
        var h = Build(20, 10, 3, Prose("one two three four five six seven eight nine ten eleven"));
        h.Paint();
        var rowsCount = h.Model.Items[0].Render(h.W, T, glyphs: GlyphSet.Unicode).Count;
        Assert.True(rowsCount >= 3, "precondition: wrapped to ≥3 rows");
        var midY = h.RowOfCell(0, 1);   //pick the middle visual row of the logical line.
        h.Clock = 1000;
        h.PressAt(4, midY, clicks: 2);
        h.Clock = 1100;
        h.PressAt(4, midY, clicks: 1);   //triple-click the middle row.
        var (lo, hi) = h.Selection.Current!.Value.Normalized();
        Assert.Equal(0, lo.ItemIndex);
        Assert.Equal(0, lo.Rel);                 //row zero is the first wrap row of the logical line
        Assert.Equal(rowsCount - 1, hi.Rel);     //the span ends at the last wrap row, past the middle row that was clicked
    }

    //the fixture holds two logical lines, each wrapping to two rows.
    private static Harness TwoLogicalLines() =>
        Build(20, 12, 3, new AssistantBlockItem(
            new[] { "aaa bbb ccc ddd eee fff", "ggg hhh iii jjj kkk lll" }, "generalist") { LeadingBlank = false });

    [Fact]
    public void Drag_after_triple_click_grows_by_whole_logical_lines()
    {
        var h = TwoLogicalLines();
        h.Paint();
        var flags = h.Model.Items[0].RowWraps(h.W, T, glyphs: GlyphSet.Unicode).Select(x => x.Continuation).ToList();
        int lineBstart = -1; for (var i = 1; i < flags.Count; i++) if (!flags[i]) { lineBstart = i; break; }
        Assert.True(lineBstart > 0, "precondition: a second logical line exists");
        var lineBend = lineBstart; while (lineBend + 1 < flags.Count && flags[lineBend + 1]) lineBend++;

        var yA = h.RowOfCell(0, 0);
        h.Clock = 1000; h.PressAt(4, yA, clicks: 2);
        h.Clock = 1100; h.PressAt(4, yA, clicks: 1);          //the triple-click selects all of line A.
        h.MoveAt(4, h.RowOfCell(0, lineBstart));              //dragging to the first row of line B, so a raw extend would stop here

        var (lo, hi) = h.Selection.Current!.Value.Normalized();
        Assert.Equal(0, lo.Rel);
        Assert.Equal(lineBend, hi.Rel);   //all of line B is pulled in, where a raw extend would stop at the first row.
    }

    [Fact]
    public void Drag_after_triple_click_does_not_resurrect_a_cleared_selection()
    {
        var h = TwoLogicalLines();
        h.Paint();
        var yA = h.RowOfCell(0, 0);
        h.Clock = 1000; h.PressAt(4, yA, clicks: 2);
        h.Clock = 1100; h.PressAt(4, yA, clicks: 1);
        Assert.NotNull(h.Selection.Current);

        h.Model.Items[0].Collapsed = !h.Model.Items[0].Collapsed;   //changing the anchored item mid-drag invalidates it.
        h.Selection.Clear();                                        //the clear is what the paint performs after the invalidation.
        h.MoveAt(4, h.RowOfCell(0, 2));                             //a move must never re-create a cleared span.
        Assert.Null(h.Selection.Current);                          //the selection stays cleared until the next press.
    }

    [Fact]
    public void A_press_that_begins_no_drag_leaves_a_later_Move_inert()
    {
        var h = TwoLogicalLines();
        h.Paint();
        var yA = h.RowOfCell(0, 0);
        h.Clock = 1000; h.PressAt(4, yA, clicks: 2);
        h.Clock = 1100; h.PressAt(4, yA, clicks: 1);   //a line span from a triple-click survives the release.
        h.ReleaseAt(4, yA);
        var before = h.Selection.Current;
        Assert.NotNull(before);

        //a press on dead space must leave the gesture dead, so the next move can't grow the span off a stale anchor
        var deadY = Enumerable.Range(0, h.S.Height).First(y => h.Comp.CellAt(4, y) is null);
        h.PressAt(4, deadY);
        h.MoveAt(4, h.RowOfCell(0, 2));

        Assert.Equal(before, h.Selection.Current);
    }

    [Fact]
    public void Drag_to_the_top_edge_auto_scrolls_toward_older()
    {
        var h = Build(40, 8, 3, Many(40));   //the transcript is long enough to scroll.
        h.Paint();
        Assert.True(h.Scroll.Following);
        var pressRow = Enumerable.Range(0, h.VpRows).Last(yy => h.Comp.CellAt(4, yy) is not null);
        h.PressAt(4, pressRow);              //the drag starts on the bottom-most visible content row.
        h.MoveAt(4, 0);                       //dragging to the top edge scrolls toward older content.
        Assert.False(h.Scroll.Following);     //scrolling up detaches the follow.
    }

    [Fact]
    public void A_click_on_an_expanded_tool_body_starts_a_drag_not_a_toggle()   //the split between header and body decides the click.
    {
        var h = Build(40, 10, 3, ExpandedTool());
        h.Paint();
        var bodyRow = h.RowWhere(t => t is ItemTarget { HeaderRow: false });
        var before = h.Model.Items[0].Collapsed;
        h.PressAt(6, bodyRow);                //the press is past the gutter, on text.
        Assert.Equal(before, h.Model.Items[0].Collapsed);   //a body press must not toggle the block.
        h.MoveAt(10, bodyRow);
        Assert.NotNull(h.Selection.Current);  //the press starts a drag instead.
    }

    //a composer press with no captured layout must bail, a copy-only fallback would make it selectable but not editable
    [Fact]
    public void A_composer_press_bails_with_no_selection_when_no_ComposerLayout_is_captured()
    {
        var h = HarnessWithWrappedComposer();          //the rows are tagged as composer, but no layout is wired.
        h.Paint();
        var midY = h.ChromeRowY(1);                    //pick a middle wrap row.

        h.Clock = 1000; h.PressAt(4, midY, clicks: 2);
        h.Clock = 1100; h.PressAt(4, midY, clicks: 1);   //a double click followed by a single click must leave no line span

        Assert.Null(h.Selection.ChromeCurrent);   //no selection here, a fallback to another path would be wrong
    }

    //a composer press resolves against the captured ComposerLayout snapshot and fires the composer callback

    private sealed class ComposerHarness
    {
        public required VtScreenSurface S;
        public required ViewportCompositor Comp;
        public required SelectionController Selection;
        public required ComposerLayout Layout;
        public required MouseController Mc;
        public required int W;
        public required int VpRows;
        public long Clock;
        public List<(int Line, int Col, ComposerGesture Kind)> Gestures = new();

        public void Paint() => Comp.Paint(0, true);
        public int RowY(int regionRow) =>
            Enumerable.Range(0, S.Height).First(y =>
                Comp.HitTest(y) is ChromeTarget { Region: ChromeRegion.Composer } ct && ct.RegionRow == regionRow);
        public void PressAt(int x, int y, int clicks = 1) =>
            Mc.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0) { Clicks = clicks }, W, VpRows);
        public void MoveAt(int x, int y) => Mc.Handle(new MouseEvent(x, y, MouseKind.Move, MouseButton.None, 0, 0), W, VpRows);
    }

    //rows come from the layout's own segments, so no wrap is typed by hand
    private static ComposerHarness BuildComposer(IReadOnlyList<string> lines, int width, int height)
    {
        var layout = new ComposerLayout(lines, width);
        var rows = new List<ChromeRow>();
        var regionRow = 0;
        for (var li = 0; li < lines.Count; li++)
        {
            var segs = layout.SegsPerLine[li];
            for (var si = 0; si < segs.Count; si++)
            {
                var text = (li == 0 && si == 0 ? "❯ " : "  ") + segs[si].Text;
                rows.Add(new ChromeRow(text, text, si > 0, si > 0 ? 2 : 0, ChromeRegion.Composer, regionRow));
                regionRow++;
            }
        }

        var s = new VtScreenSurface(width, height);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var gate = new object();
        var scroll = new ScrollController(model, index);
        var comp = new ViewportCompositor(s, index, gate, glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => new ChromeBlock(rows, 0, 2) };
        var focus = new FocusController(model, index, scroll, comp);
        var selection = new SelectionController(model);
        var h = new ComposerHarness
        {
            S = s, Comp = comp, Selection = selection, Layout = layout, Mc = null!,
            W = width, VpRows = height - rows.Count,
        };
        h.Mc = new MouseController(comp, scroll, focus, model, wheelLines: 3, selection, T, () => h.Clock,
            onComposerSelect: (line, col, kind) => h.Gestures.Add((line, col, kind)),
            composerLayout: () => h.Layout);
        return h;
    }

    [Fact]
    public void Press_on_a_composer_cell_fires_Begin_via_the_layout_and_skips_BeginChromeDrag()
    {
        var lines = new[] { "hello world foo bar baz qux quux corge grault" };
        var h = BuildComposer(lines, width: 20, height: 10);   //width twenty gives a budget of seventeen cells, so the line wraps into several rows
        h.Paint();
        var y = h.RowY(0);

        h.PressAt(6, y);   //press a content cell on the head row.

        var expected = h.Layout.ComposerCellToPosition(0, 6);
        Assert.Equal(new[] { (expected.Line, expected.Col, ComposerGesture.Begin) }, h.Gestures);
        Assert.Null(h.Selection.ChromeCurrent);
    }

    [Fact]
    public void Move_after_a_composer_press_fires_Extend_via_the_same_layout()
    {
        var lines = new[] { "hello world foo bar baz qux quux corge grault" };
        var h = BuildComposer(lines, width: 20, height: 10);
        h.Paint();
        var y = h.RowY(0);

        h.PressAt(4, y);
        h.MoveAt(8, y);

        var expectedExtend = h.Layout.ComposerCellToPosition(0, 8);
        Assert.Equal(2, h.Gestures.Count);
        Assert.Equal(ComposerGesture.Begin, h.Gestures[0].Kind);
        Assert.Equal((expectedExtend.Line, expectedExtend.Col, ComposerGesture.Extend), h.Gestures[1]);
    }

    [Fact]
    public void Double_click_on_a_composer_word_sends_Begin_at_word_start_and_Extend_at_word_end()
    {
        var h = BuildComposer(new[] { "hello world" }, width: 40, height: 10);   //this width fits the line on one row.
        h.Paint();
        var y = h.RowY(0);

        h.PressAt(9, y, clicks: 2);   //press inside the word world.

        Assert.Equal(2, h.Gestures.Count);
        Assert.Equal((0, 6, ComposerGesture.Begin), h.Gestures[0]);    //the begin gesture sits at character six, where the word starts.
        Assert.Equal((0, 11, ComposerGesture.Extend), h.Gestures[1]);  //the extend runs to character eleven, the end of the line.
        Assert.Null(h.Selection.ChromeCurrent);
    }

    [Fact]
    public void Triple_click_on_a_composer_row_selects_the_whole_logical_line()
    {
        var lines = new[] { "short line one", "a longer second logical line here" };
        var h = BuildComposer(lines, width: 40, height: 10);   //at this width each line fits one row.
        h.Paint();
        var y = h.RowY(1);   //take the row of the second logical line.

        h.Clock = 1000; h.PressAt(4, y, clicks: 2);   //the double-click first fires its own word gestures.
        h.Gestures.Clear();                            //clear so only the triple-click's own gestures remain.
        h.Clock = 1100; h.PressAt(4, y, clicks: 1);   //this press completes the triple-click.

        Assert.Equal(2, h.Gestures.Count);
        Assert.Equal((1, 0, ComposerGesture.Begin), h.Gestures[0]);
        Assert.Equal((1, lines[1].Length, ComposerGesture.Extend), h.Gestures[1]);
        Assert.Null(h.Selection.ChromeCurrent);
    }

    //the real painter's composer RegionRow must match the flat wrap-row index the layout expects, an off-by-one turns this red
    [Fact]
    public void ChromePainter_Composer_RegionRow_is_the_flat_WrapSeg_index_ComposerLayout_expects()
    {
        var lines = new List<string> { "one two three four five six seven eight nine ten eleven twelve" };
        const int width = 20, height = 10;
        var s = new VtScreenSurface(width, height);
        var status = new StatusInfo(@"C:\Users\user\projects\gatto", "model", "generalist", new CtxState(), @"C:\Users\user");
        var painter = new ChromePainter(s, T, new object())
        {
            Frame = new InputFrame(s, T, "generalist", status, glyphs: GlyphSet.Unicode),
        };
        painter.State.Composer = new EditorView(lines, 0, 0);

        var composerRows = painter.ComposeChromeBlock(width, height).Rows.Where(r => r.Region == ChromeRegion.Composer).ToList();
        Assert.True(composerRows.Count >= 3, "precondition: the line wraps to several composer rows");

        var layout = new ComposerLayout(lines, width);
        for (var r = 0; r < composerRows.Count; r++)
        {
            var regionRow = composerRows[r].RegionRow;
            Assert.Equal(r, regionRow);   //region rows start at zero and follow physical row order.

            //round-trip the region row through the layout's two maps, which must return the same region row.
            var (line, col) = layout.ComposerCellToPosition(regionRow, 2);   //cell two is the first content cell.
            var (backRegionRow, _) = layout.PositionToCell(line, col);
            Assert.Equal(regionRow, backRegionRow);
        }
    }

    [Fact]
    public void A_transcript_drag_moving_into_chrome_clamps_to_the_last_transcript_row()
    {
        var h = HarnessWithTranscriptAndWrappedComposer();
        h.Paint();
        //find the boundary row independently of the fixture, as the last row the cell probe resolves.
        var boundaryY = Enumerable.Range(0, h.S.Height).Last(y => h.Comp.CellAt(4, y) is not null);
        var expected = h.Comp.CellAt(4, boundaryY)!.Value;

        h.PressAt(4, h.RowOfCell(0, 0));
        h.MoveAt(4, h.S.Height - 1);                   //drag past the boundary, into the last chrome row.

        var (lo, hi) = h.Selection.Current!.Value.Normalized();
        Assert.Equal(expected.ItemIndex, hi.ItemIndex);   //assert the drag ends on the exact boundary row.
        Assert.Equal(expected.Rel, hi.Rel);               //the weaker claim, that no chrome span started, is not enough.
        Assert.Null(h.Selection.ChromeCurrent);
    }

    [Fact]
    public void A_chrome_drag_never_scrolls_the_viewport()
    {
        var h = Build(40, 8, 3, Many(40));
        h.Paint();
        Assert.True(h.Scroll.Following);
        h.PressAt(4, h.S.Height - 1);                  //press in the chrome block.
        h.MoveAt(4, 0);                                 //move to the top edge as a drag does.
        Assert.True(h.Scroll.Following);                //a chrome drag must never auto-scroll the view.
    }

    //the mirror direction needs its own guard, a missing one would extend a transcript span from a chrome drag
    [Fact]
    public void A_chrome_drag_moving_into_the_transcript_clamps_to_the_first_chrome_row()
    {
        var h = HarnessWithTranscriptAndWrappedComposer();
        h.Paint();

        h.PressAt(4, h.ChromeRowY(2));                  //anchor on the last composer wrap row.
                                                         //the anchor sits away from the clamp target, so a no-op cannot pass.
        h.MoveAt(4, h.RowOfCell(0, 0));                 //drag up into the transcript.

        var (lo, hi) = h.Selection.ChromeCurrent!.Value.Normalized();
        Assert.Equal(h.ChromeRowIndex(0), lo.Row);      //the span is clamped to the first chrome row.
        Assert.Null(h.Selection.Current);
    }

    //click counts stay per-domain, a shared counter would read the first chrome click as a triple
    [Fact]
    public void Double_click_transcript_then_first_chrome_click_at_0_0_is_not_misread_as_a_triple()
    {
        var h = HarnessWithTranscriptAndWrappedComposer();
        h.Paint();
        var y = h.RowOfCell(0, 0);

        h.Clock = 1000; h.PressAt(4, y, clicks: 2);                        //double-click a word in the transcript.
        h.Clock = 1100; h.PressAt(0, h.ChromeRowY(0), clicks: 1);          //this is the session's first chrome click, at the default cell.

        var (lo, hi) = h.Selection.ChromeCurrent!.Value.Normalized();
        Assert.Equal(lo, hi);   //zero-width, so the press wasn't read as a triple click
    }
    private sealed class KeyFeed : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }
    //a non-composer drag enqueues one clear gesture at press and never on a move or a composer press

    [Fact]
    public void A_transcript_drag_begin_enqueues_a_composer_Clear_gesture()
    {
        var h = Build(40, 6, 3, Prose("hello world"));
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.PressAt(2, y);
        Assert.Contains(h.Gestures, g => g.Kind == ComposerGesture.Clear);
    }

    [Fact]
    public void A_transcript_drag_Move_does_not_re_fire_the_Clear_gesture()
    {
        //a drag across several cells must enqueue exactly one clear
        var h = Build(40, 6, 3, Prose("hello world"));
        h.Paint();
        var y = h.RowOfCell(0, 0);
        h.PressAt(2, y);
        h.MoveAt(4, y); h.MoveAt(6, y); h.MoveAt(8, y);
        Assert.Single(h.Gestures, g => g.Kind == ComposerGesture.Clear);
    }

    [Fact]
    public void A_non_composer_chrome_drag_begin_enqueues_a_composer_Clear_gesture()
    {
        //the fixture's rows are tagged Tool, the non-composer branch this gesture exercises
        var h = HarnessWithTranscriptAndWrappedComposer();
        h.Paint();
        h.PressAt(4, h.ChromeRowY(0));
        Assert.Contains(h.Gestures, g => g.Kind == ComposerGesture.Clear);
    }

    [Fact]
    public void A_composer_region_press_does_not_also_enqueue_a_Clear_gesture()
    {
        //a composer press sets the selection, so it must not also clear what it just set
        var h = BuildComposer(new[] { "hello world" }, width: 40, height: 10);
        h.Paint();
        var y = h.RowY(0);
        h.PressAt(4, y);
        Assert.DoesNotContain(h.Gestures, g => g.Kind == ComposerGesture.Clear);
    }

    //the callback goes into a real InputPump, so the test proves the gesture reaches the channel the editor drains
    [Fact]
    public void A_transcript_drag_begin_Clear_gesture_reaches_a_real_InputPump_composer_channel()
    {
        var h = Build(40, 6, 3, Prose("hello world"));
        var pump = new InputPump(new KeyInputSource(new KeyFeed()));
        var mc = new MouseController(h.Comp, h.Scroll, h.Focus, h.Model, 3, h.Selection, T,
            onComposerSelect: (line, col, kind) => pump.EnqueueComposerGesture(new ComposerInput.Select(line, col, kind)));
        h.Paint();
        var y = h.RowOfCell(0, 0);

        mc.Handle(new MouseEvent(2, y, MouseKind.Press, MouseButton.Left, 0, 0), h.W, h.VpRows);

        var ev = Assert.IsType<ComposerInput.Select>(pump.Composer.Read());
        Assert.Equal(ComposerGesture.Clear, ev.Kind);
    }
}
