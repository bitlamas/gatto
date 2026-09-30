using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ScrollControllerTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (TranscriptModel, LineIndex, ScrollController) Build(int count)
    {
        var m = new TranscriptModel("generalist");
        var idx = new LineIndex(m, T, glyphs: GlyphSet.Unicode);
        for (var i = 0; i < count; i++) { m.Append(new AssistantBlockItem(new[] { $"line {i}" }, "generalist")); }
        foreach (var it in m.Items) it.LeadingBlank = false;   //clear LeadingBlank so each item takes one row and the scroll math stands alone
        return (m, idx, new ScrollController(m, idx));
    }

    [Fact]
    public void Follows_the_bottom_by_default()
    {
        var (_, _, sc) = Build(10);
        Assert.True(sc.Following);
        Assert.Equal(0, sc.BottomOffset(40, 5));
    }

    [Fact]
    public void PageUp_detaches_and_scrolls_toward_earlier_content()
    {
        var (_, idx, sc) = Build(20);
        sc.PageUp(40, 6);

        Assert.False(sc.Following);
        var off = sc.BottomOffset(40, 6);
        Assert.True(off > 0);
        var top = idx.Window(40, 6, off)[0];
        Assert.Contains("line 11", ((AssistantBlockItem)top.Item).RawLines[0]);
    }

    [Fact]
    public void Appending_while_detached_does_not_move_the_view_no_yank()
    {
        var (m, idx, sc) = Build(20);
        sc.PageUp(40, 6);
        var before = idx.Window(40, 6, sc.BottomOffset(40, 6))[0].Item;

        m.Append(new AssistantBlockItem(new[] { "brand new streaming line" }, "generalist"));
        m.Items[^1].LeadingBlank = false;
        sc.OnModelGrew(40);

        var after = idx.Window(40, 6, sc.BottomOffset(40, 6))[0].Item;
        Assert.Same(before, after);   //the top item must not change when content is appended while detached
    }

    [Fact]
    public void End_reattaches_to_the_bottom()
    {
        var (_, _, sc) = Build(20);
        sc.PageUp(40, 6);
        Assert.False(sc.Following);

        sc.End();
        Assert.True(sc.Following);
        Assert.Equal(0, sc.BottomOffset(40, 6));
    }

    [Fact]
    public void PageDown_to_the_bottom_reattaches()
    {
        var (_, _, sc) = Build(20);
        sc.Home(40, 6);
        Assert.False(sc.Following);
        for (var i = 0; i < 20; i++) sc.PageDown(40, 6);   //page down repeatedly, past the bottom.

        Assert.True(sc.Following);       //reaching the bottom by scrolling must re-attach following.
    }

    [Fact]
    public void Home_shows_the_very_first_item()
    {
        var (_, idx, sc) = Build(20);
        sc.Home(40, 6);
        var top = idx.Window(40, 6, sc.BottomOffset(40, 6))[0];
        Assert.Contains("line 0", ((AssistantBlockItem)top.Item).RawLines[0]);
    }

    [Fact]
    public void Resize_keeps_the_anchored_item_visible()
    {
        var m = new TranscriptModel("generalist");
        var idx = new LineIndex(m, T, glyphs: GlyphSet.Unicode);
        //the fixture items wrap differently at different widths.
        for (var i = 0; i < 12; i++) m.Append(new AssistantBlockItem(new[] { $"item {i} with enough words to wrap narrow" }, "generalist"));
        foreach (var it in m.Items) it.LeadingBlank = false;
        var sc = new ScrollController(m, idx);

        idx.Rebuild(60);
        sc.PageUp(60, 6);
        var anchoredText = ((AssistantBlockItem)idx.Window(60, 6, sc.BottomOffset(60, 6))[0].Item).RawLines[0];

        idx.Rebuild(20);
        var topAfter = idx.Window(20, 6, sc.BottomOffset(20, 6));
        Assert.Contains(topAfter, w => ((AssistantBlockItem)w.Item).RawLines[0] == anchoredText);   //the anchored item must still be visible after the rebuild.
    }

    [Fact]
    public void ScrollBy_up_from_following_detaches_and_scrolls()
    {
        var (_, _, sc) = Build(20);
        sc.ScrollBy(3, 40, 6);
        Assert.False(sc.Following);
        Assert.True(sc.BottomOffset(40, 6) > 0);
    }

    [Fact]
    public void ScrollBy_up_clamps_at_the_top_never_past_row_0()
    {
        var (_, idx, sc) = Build(20);
        for (var i = 0; i < 50; i++) sc.ScrollBy(3, 40, 6);   //repeat the scroll far past the top.
        var off = sc.BottomOffset(40, 6);
        Assert.Equal(idx.TotalRows(40) - 6, off);              //the offset sits pinned at the very top, with no overshoot.
    }

    [Fact]
    public void ScrollBy_down_past_the_bottom_reattaches()
    {
        var (_, _, sc) = Build(20);
        sc.Home(40, 6);
        sc.ScrollBy(-100, 40, 6);
        Assert.True(sc.Following);                             //a scroll that reaches the bottom must re-attach following.
    }

    [Fact]
    public void ScrollBy_zero_does_NOT_detach_follow_mode()
    {
        var (_, _, sc) = Build(20);
        Assert.True(sc.Following);
        sc.ScrollBy(0, 40, 6);
        Assert.True(sc.Following);                             //a wheel event of zero lines must never detach following.
    }

    [Fact]
    public void ScrollBy_is_a_noop_when_content_fits()
    {
        var (_, _, sc) = Build(3);                             //three rows in a six-row viewport give nothing to scroll.
        sc.ScrollBy(3, 40, 6);
        Assert.True(sc.Following);
        Assert.Equal(0, sc.BottomOffset(40, 6));
    }
}
