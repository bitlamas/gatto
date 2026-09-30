using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

//the selection highlight is painted while live, cleared on a reflow or an item mutation, and kept through a same-width repaint
public sealed class CompositorSelectionTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private sealed class RawSurface : ITermSurface
    {
        public string Raw = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public RawSurface(int w, int h) { Width = w; Height = h; }
        public void Write(string s) => Raw = s;   //each paint writes one full frame, so the capture replaces the previous one.
    }

    private static (RawSurface, ViewportCompositor, TranscriptModel, SelectionController) Build(int w, int h)
    {
        var s = new RawSurface(w, h);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode)
        { ComposeChrome = (_, _) => ChromeBlock.FromText(new[] { "> " }, 0, 2) };
        model.Append(new AssistantBlockItem(new[] { "hello world" }, "generalist") { LeadingBlank = false });
        var sel = new SelectionController(model);
        comp.Selection = sel;
        return (s, comp, model, sel);
    }

    private static void SelectHello(SelectionController sel)   //select cells two to six of the first item, the word hello.
    {
        sel.BeginDrag(new SelCell(0, 0, 2), DragKind.Char, 40);
        sel.ExtendTo(new SelCell(0, 0, 6), 40);
    }

    //build a chrome-only compositor on a raw surface, so the background escape survives capture.
    private static (RawSurface, ViewportCompositor) CompositorWithChrome(ChromeBlock block)
    {
        var s = new RawSurface(40, block.Rows.Count + 2);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => block };
        comp.Selection = new SelectionController(model);
        return (s, comp);
    }

    //the test block never overflows the tiny surface, so its own rows are what the frame paints
    private static IReadOnlyList<ChromeRow> ChromeRowsOf(ChromeBlock block) => block.Rows;

    //split the painted frame by row, so a test can assert one row's escapes instead of the whole frame
    private static List<string> RowsText(string raw, int height)
    {
        var rows = new List<string>(height);
        for (var r = 0; r < height; r++)
        {
            var cup = Ansi.Cup(r + 1, 1);
            var cupIdx = raw.IndexOf(cup, System.StringComparison.Ordinal);
            var clearIdx = raw.IndexOf(Ansi.ClearLine, cupIdx + cup.Length, System.StringComparison.Ordinal);
            var contentStart = clearIdx + Ansi.ClearLine.Length;
            var nextCup = r + 1 < height ? Ansi.Cup(r + 2, 1) : null;
            var end = nextCup is null ? raw.Length : raw.IndexOf(nextCup, contentStart, System.StringComparison.Ordinal);
            if (end < 0) end = raw.Length;
            rows.Add(raw[contentStart..end]);
        }
        return rows;
    }

    [Fact]
    public void A_live_selection_paints_the_highlight_bg()
    {
        var (s, comp, _, sel) = Build(40, 6);
        SelectHello(sel);
        comp.Paint(0, true);
        Assert.Contains(T.SelectionBgOn, s.Raw);
    }

    [Fact]
    public void No_selection_paints_no_highlight()
    {
        var (s, comp, _, _) = Build(40, 6);
        comp.Paint(0, true);
        Assert.DoesNotContain(T.SelectionBgOn, s.Raw);
    }

    [Fact]
    public void A_same_width_repaint_keeps_the_highlight()   //a highlight must survive the frequent repaints that the purr triggers.
    {
        var (s, comp, _, sel) = Build(40, 6);
        SelectHello(sel);
        comp.Paint(0, true);
        comp.Paint(0, true);   //the second repaint at the same width simulates a purr tick.
        Assert.Contains(T.SelectionBgOn, s.Raw);
        Assert.NotNull(sel.Current);
    }

    [Fact]
    public void A_reflow_clears_the_selection_at_the_next_paint()
    {
        var (s, comp, _, sel) = Build(40, 6);
        SelectHello(sel);
        comp.Paint(0, true);
        Assert.Contains(T.SelectionBgOn, s.Raw);
        s.Width = 30;                       //the resize breaks the anchor width the span recorded.
        comp.Paint(0, true);
        Assert.DoesNotContain(T.SelectionBgOn, s.Raw);
        Assert.Null(sel.Current);
    }

    [Fact]
    public void An_anchored_item_mutation_clears_the_highlight()
    {
        var (s, comp, model, sel) = Build(40, 6);
        SelectHello(sel);
        comp.Paint(0, true);
        Assert.Contains(T.SelectionBgOn, s.Raw);
        model.Items[0].Collapsed = !model.Items[0].Collapsed;   //changing collapse raises the revision of the anchored item.
        comp.Paint(0, true);
        Assert.DoesNotContain(T.SelectionBgOn, s.Raw);
        Assert.Null(sel.Current);
    }

    [Fact]
    public void A_chrome_span_paints_the_selection_background_on_its_rows_only()
    {
        var block = ChromeBlock.FromText(new[] { "purr", "❯ hello" }, 1, 2);
        var (s, comp) = CompositorWithChrome(block);
        comp.Selection!.BeginChromeDrag(new ChromeCell(1, 2), DragKind.Char, s.Width, ChromeRowsOf(block));
        comp.Selection.ExtendChromeTo(new ChromeCell(1, 6), s.Width, ChromeRowsOf(block));
        comp.Paint(0, true);

        var painted = RowsText(s.Raw, s.Height);
        Assert.Contains("[48;", painted[^1]);      //the composer row paints a background.
        Assert.DoesNotContain("[48;", painted[^2]); //the purr row paints no background.
    }

    [Fact]
    public void A_chrome_reflow_clears_the_chrome_span_at_the_next_paint()
    {
        var block = ChromeBlock.FromText(new[] { "purr", "❯ hello" }, 1, 2);
        var (s, comp) = CompositorWithChrome(block);
        comp.Selection!.BeginChromeDrag(new ChromeCell(1, 2), DragKind.Char, s.Width, ChromeRowsOf(block));
        comp.Selection.ExtendChromeTo(new ChromeCell(1, 6), s.Width, ChromeRowsOf(block));
        comp.Paint(0, true);
        Assert.Contains("[48;", RowsText(s.Raw, s.Height)[^1]);
        s.Width = 30;                       //the resize breaks the anchor width the span recorded.
        comp.Paint(0, true);
        Assert.DoesNotContain("[48;", RowsText(s.Raw, s.Height)[^1]);
        Assert.Null(comp.Selection.ChromeCurrent);   //the span must be cleared, and unpainting it alone leaves a stale selection
        Assert.False(comp.Selection.HasSelection);
    }

    [Fact]
    public void A_touched_chrome_row_mutation_clears_the_chrome_span()
    {
        var rows = new List<ChromeRow> { ChromeRow.Plain("purr"), ChromeRow.Plain("❯ hello") };
        var s = new RawSurface(40, rows.Count + 2);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode)
        { ComposeChrome = (_, _) => new ChromeBlock(rows, 1, 2) };
        comp.Selection = new SelectionController(model);
        comp.Selection.BeginChromeDrag(new ChromeCell(1, 2), DragKind.Char, s.Width, rows);
        comp.Selection.ExtendChromeTo(new ChromeCell(1, 6), s.Width, rows);
        comp.Paint(0, true);
        Assert.Contains("[48;", RowsText(s.Raw, s.Height)[^1]);
        rows[1] = ChromeRow.Plain("❯ hellp");   //replace the text of a touched row between two paints.
        comp.Paint(0, true);
        Assert.DoesNotContain("[48;", RowsText(s.Raw, s.Height)[^1]);
        Assert.Null(comp.Selection.ChromeCurrent);   //the span must be cleared, and unpainting it alone leaves a stale selection
        Assert.False(comp.Selection.HasSelection);
    }

    [Fact]
    public void Stop_drops_the_selection_structurally()
    {
        var (_, comp, _, sel) = Build(40, 6);
        SelectHello(sel);
        comp.Stop();
        Assert.Null(comp.Selection);
    }
}
