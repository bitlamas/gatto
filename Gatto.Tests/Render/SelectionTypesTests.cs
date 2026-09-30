using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class SelectionTypesTests
{
    [Fact]
    public void Normalized_orders_endpoints_by_item_then_row_then_col()
    {
        var span = new SelectionSpan(new SelCell(2, 1, 3), new SelCell(0, 0, 5), 80);
        var (lo, hi) = span.Normalized();
        Assert.Equal(new SelCell(0, 0, 5), lo);
        Assert.Equal(new SelCell(2, 1, 3), hi);
    }

    [Fact]
    public void Normalized_within_one_item_orders_by_row_then_col()
    {
        var span = new SelectionSpan(new SelCell(1, 2, 4), new SelCell(1, 2, 1), 80);
        var (lo, hi) = span.Normalized();
        Assert.Equal(1, lo.Col);
        Assert.Equal(4, hi.Col);
    }

    [Fact]
    public void Touches_is_inclusive_and_order_independent()
    {
        var span = new SelectionSpan(new SelCell(3, 0, 0), new SelCell(1, 0, 0), 80);   //the anchor is passed before the head, out of reading order on purpose
        Assert.True(span.Touches(1));
        Assert.True(span.Touches(2));
        Assert.True(span.Touches(3));
        Assert.False(span.Touches(0));
        Assert.False(span.Touches(4));
    }
}
