using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class HitTestTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static Func<int, int, ChromeBlock> Composer() =>
        (w, h) => ChromeBlock.FromText(new[] { "> " }, caretRow: 0, caretCol: 2);

    //the chrome-only setup has no transcript lines, so a chrome row's physical row comes from the height minus the block's rows
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewportCompositor, VtScreenSurface> ChromeSurfaces = new();

    private static ViewportCompositor CompositorWithChrome(ChromeBlock block)
    {
        var s = new VtScreenSurface(10, block.Rows.Count + 2);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => block };
        ChromeSurfaces.Add(comp, s);
        return comp;
    }

    private static List<int> LastRows(ViewportCompositor comp, int n)
    {
        ChromeSurfaces.TryGetValue(comp, out var s);
        var h = s!.Height;
        return Enumerable.Range(h - n, n).ToList();
    }

    private static void Resize(ViewportCompositor comp, int width)
    {
        ChromeSurfaces.TryGetValue(comp, out var s);
        s!.Resize(width, s.Height);
    }

    //build keeps leading blanks, the tests target the separator rows
    private static (VtScreenSurface, ViewportCompositor) Build(int w, int h, params TranscriptItem[] items)
    {
        var s = new VtScreenSurface(w, h);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        foreach (var it in items) model.Append(it);
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode) { ComposeChrome = Composer() };
        return (s, comp);
    }

    private static TranscriptItem[] Sample() => new TranscriptItem[]
    {
        new AssistantBlockItem(new[] { "hello" }, "generalist") { LeadingBlank = false },              //this fixture entry is item index zero.
        new ReasoningItem(new[] { "thinking" }) { Streaming = false, Collapsed = true,                  //this fixture entry is item index one.
            Elapsed = TimeSpan.FromSeconds(5), LeadingBlank = true },
        new ToolBlockItem("read_file", "f.txt", "✓ ok · 3 lines", true, "generalist")                  //this fixture entry is item index two.
            { FullResult = "line1\nline2\nline3", Collapsed = false, LeadingBlank = true },
    };

    [Fact]
    public void Leading_blank_row_is_NoTarget_and_the_header_below_it_is_the_item()
    {
        var (_, comp) = Build(40, 14, Sample());
        comp.Paint(bottomOffset: 0, following: true);
        var t = Enumerable.Range(0, 14).Select(comp.HitTest).ToList();

        //the reasoning summary paints one header row for item one.
        var summary = t.FindIndex(x => x is ItemTarget { ItemIndex: 1, HeaderRow: true });
        Assert.True(summary > 0, "reasoning summary header row not found");
        //the row above it is the invisible leading-blank gap, so it must hit no target.
        Assert.IsType<NoTarget>(t[summary - 1]);
    }

    [Fact]
    public void Expanded_item_header_is_HeaderRow_body_is_not()
    {
        var (_, comp) = Build(40, 14, Sample());
        comp.Paint(bottomOffset: 0, following: true);
        var t = Enumerable.Range(0, 14).Select(comp.HitTest).ToList();

        var toolRows = Enumerable.Range(0, 14).Where(y => t[y] is ItemTarget { ItemIndex: 2 }).ToList();
        Assert.True(toolRows.Count >= 2, "expected a header + at least one body row for the expanded tool");
        Assert.True(((ItemTarget)t[toolRows[0]]).HeaderRow);          //the first visible tool row is the header.
        Assert.False(((ItemTarget)t[toolRows[^1]]).HeaderRow);        //the tool's body rows are not headers.
    }

    [Fact]
    public void Chrome_row_hit_tests_to_ChromeTarget()   //a chrome row must answer as a chrome target
    {
        var (_, comp) = Build(40, 14, Sample());
        comp.Paint(bottomOffset: 0, following: true);
        var t = Assert.IsType<ChromeTarget>(comp.HitTest(13));   //the physical bottom row is the composer.
        Assert.Equal(0, t.Row);
        Assert.Equal(ChromeRegion.Other, t.Region);               //the fixture builds the block with FromText, which sets no region
    }

    [Fact]
    public void Jump_hint_row_hits_JumpHintTarget()
    {
        //append many items so the transcript overflows, and a detached scroll surfaces the hint.
        var items = Enumerable.Range(0, 30)
            .Select(i => (TranscriptItem)new AssistantBlockItem(new[] { $"line {i}" }, "generalist") { LeadingBlank = false })
            .ToArray();
        var (_, comp) = Build(40, 8, items);
        //the scroll controller is internal, so paint with a detached offset instead. that is exactly the pair the painter passes.
        comp.Paint(bottomOffset: 5, following: false);
        var hint = Enumerable.Range(0, 8).FirstOrDefault(y => comp.HitTest(y) is JumpHintTarget, -1);
        Assert.True(hint >= 0, "jump-hint row not hit");
    }

    [Fact]
    public void A_click_on_a_stale_resized_frame_is_NoTarget()
    {
        var (s, comp) = Build(40, 14, Sample());
        comp.Paint(bottomOffset: 0, following: true);
        var t = Enumerable.Range(0, 14).Select(comp.HitTest).ToList();
        var itemRow = t.FindIndex(x => x is ItemTarget);
        Assert.True(itemRow >= 0);

        s.Resize(40, 20);   //the surface changed before any repaint.
        Assert.IsType<NoTarget>(comp.HitTest(itemRow));   //hit-test must refuse when the layout dimensions no longer match the surface.
    }

    [Fact]
    public void A_chrome_row_hit_tests_to_its_region_and_row()
    {
        var comp = CompositorWithChrome(ChromeBlock.FromText(new[] { "↳ queued", "❯ " }, 1, 2));
        comp.Paint(0, true);
        var y = LastRows(comp, 2)[0];                      //take the physical row of the queued row.

        var t = Assert.IsType<ChromeTarget>(comp.HitTest(y));
        Assert.Equal(ChromeRegion.Other, t.Region);        //rows built by FromText hold no region tag, so Other is the expected region
        Assert.Equal(0, t.Row);
        Assert.Equal(new ChromeCell(0, 4), comp.ChromeCellAt(4, y));
    }

    [Fact]
    public void ChromeCellAt_refuses_a_stale_frame_like_CellAt_does()
    {
        var comp = CompositorWithChrome(ChromeBlock.FromText(new[] { "❯ " }, 0, 2));
        comp.Paint(0, true);
        var y = LastRows(comp, 1)[0];
        Assert.NotNull(comp.ChromeCellAt(2, y));

        Resize(comp, width: 20);                            //resize the surface alone, with no repaint after
        Assert.Null(comp.ChromeCellAt(2, y));
    }
}
