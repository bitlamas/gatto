using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class LineIndexTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (TranscriptModel, LineIndex) Build(params TranscriptItem[] items)
    {
        var m = new TranscriptModel("generalist");
        var idx = new LineIndex(m, T, glyphs: GlyphSet.Unicode);
        //clear LeadingBlank after the append, so the leading-blank separator doesn't change the row counts
        foreach (var it in items) { m.Append(it); it.LeadingBlank = false; }
        return (m, idx);
    }

    [Fact]
    public void Count_equals_the_items_own_render_row_count_the_contract()
    {
        //wide glyphs and fences are where a separate row estimate drifts from the render
        var cjk = new AssistantBlockItem(new[] { "日本語のテキストは全角で折り返す" }, "generalist");
        var fenced = new AssistantBlockItem(new[] { "```", "code line", "```" }, "generalist");
        var (_, idx) = Build(cjk, fenced);

        Assert.Equal(cjk.Render(16, T, glyphs: GlyphSet.Unicode).Count, idx.Count(cjk, 16));
        Assert.Equal(fenced.Render(30, T, glyphs: GlyphSet.Unicode).Count, idx.Count(fenced, 30));
    }

    [Fact]
    public void Rebuild_at_a_narrower_width_increases_total_rows()
    {
        var wrapy = new AssistantBlockItem(new[] { "one two three four five six seven eight nine ten" }, "generalist");
        var (_, idx) = Build(wrapy);

        idx.Rebuild(60);
        var wide = idx.TotalRows(60);
        idx.Rebuild(14);
        var narrow = idx.TotalRows(14);

        Assert.True(narrow > wide, $"narrow ({narrow}) should wrap into more rows than wide ({wide})");
    }

    [Fact]
    public void Window_is_bottom_anchored_and_returns_only_intersecting_items()
    {
        //five items, one row each
        var items = new TranscriptItem[5];
        for (var i = 0; i < 5; i++) items[i] = new AssistantBlockItem(new[] { $"line {i}" }, "generalist");
        var (_, idx) = Build(items);

        var bottom = idx.Window(40, height: 3, bottomOffset: 0);
        Assert.Equal(3, bottom.Count);
        Assert.Same(items[2], bottom[0].Item);
        Assert.Same(items[4], bottom[2].Item);
        Assert.All(bottom, w => Assert.Equal(0, w.FirstRowWithin));

        var scrolled = idx.Window(40, height: 3, bottomOffset: 2);   //offset two scrolls the window up two rows
        Assert.Same(items[0], scrolled[0].Item);
        Assert.Same(items[2], scrolled[^1].Item);
    }

    [Fact]
    public void Window_clips_a_partial_item_at_the_top_with_FirstRowWithin()
    {
        var tall = new ReasoningItem(new[] { "r0", "r1", "r2" });   //three rows, tall enough for the window to clip it
        var b = new AssistantBlockItem(new[] { "bee" }, "generalist");
        var c = new AssistantBlockItem(new[] { "cee" }, "generalist");
        var (_, idx) = Build(tall, b, c);   //three items, five rows in all

        var win = idx.Window(40, height: 3, bottomOffset: 0);
        Assert.Same(tall, win[0].Item);
        Assert.Equal(2, win[0].FirstRowWithin);                 //the window starts two rows into the top item
        Assert.Same(b, win[1].Item);
        Assert.Equal(0, win[1].FirstRowWithin);
    }

    [Fact]
    public void Growing_the_open_item_invalidates_its_cached_count()
    {
        var m = new TranscriptModel("generalist");
        var idx = new LineIndex(m, T, glyphs: GlyphSet.Unicode);
        m.AppendOpenLine("one", reasoning: false);
        var item = m.Items[0];
        Assert.Equal(1, idx.Count(item, 40));   //fill the cache here, so the next count can show it was dropped

        m.AppendOpenLine("two", reasoning: false);   //appending to the open item raises the change that drops the cached count
        Assert.Equal(2, idx.Count(item, 40));   //the cache was dropped, so the count covers both lines
    }
}
