using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ComposerScrollTests
{
    [Fact]
    public void FollowingByDefault_OffsetShowsTheTail()
    {
        var s = new ComposerScroll();
        Assert.True(s.Following);
        Assert.Equal(6, s.Offset(totalRows: 10, allowed: 4));
    }

    [Fact]
    public void ContentThatFits_OffsetIsAlwaysZero_RegardlessOfScrollState()
    {
        var s = new ComposerScroll();
        s.ScrollBy(50);
        Assert.Equal(0, s.Offset(totalRows: 3, allowed: 10));
    }

    [Fact]
    public void ScrollBy_MovesTowardOlderContent_SameSignAsScrollController()
    {
        var s = new ComposerScroll();
        s.ScrollBy(2);
        Assert.Equal(4, s.Offset(totalRows: 10, allowed: 4));
        Assert.False(s.Following);
    }

    [Fact]
    public void ScrollBy_ClampsAtTheTop_NeverNegative()
    {
        var s = new ComposerScroll();
        s.ScrollBy(9999);
        Assert.Equal(0, s.Offset(totalRows: 10, allowed: 4));
    }

    [Fact]
    public void ScrollBy_TowardNewer_ReattachesFollowOnReachingTheBottom()
    {
        var s = new ComposerScroll();
        s.ScrollBy(6);
        s.ScrollBy(-6);
        Assert.True(s.Following);
    }

    [Fact]
    public void Reset_ReturnsToFollowing()
    {
        var s = new ComposerScroll();
        s.ScrollBy(3);
        Assert.False(s.Following);
        s.Reset();
        Assert.True(s.Following);
        Assert.Equal(6, s.Offset(totalRows: 10, allowed: 4));
    }

    [Fact]
    public void ScrollBy_ZeroDelta_IsANoOp_NeverDetachesFollow()
    {
        var s = new ComposerScroll();
        s.ScrollBy(0);
        Assert.True(s.Following);
    }
}
