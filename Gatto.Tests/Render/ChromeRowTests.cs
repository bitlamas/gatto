using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ChromeRowTests
{
    [Fact]
    public void Plain_rows_carry_the_text_in_both_faces_and_no_wrap_chrome()
    {
        var r = ChromeRow.Plain("❯ hi");
        Assert.Equal("❯ hi", r.Rendered);
        Assert.Equal("❯ hi", r.Visible);
        Assert.False(r.Continuation);
        Assert.Equal(0, r.PrefixCells);
    }

    [Fact]
    public void FromText_preserves_row_order_and_the_caret()
    {
        var b = ChromeBlock.FromText(new[] { "a", "b" }, caretRow: 1, caretCol: 2);
        Assert.Equal(new[] { "a", "b" }, b.Rows.Select(x => x.Visible));
        Assert.Equal(1, b.CaretRow);
        Assert.Equal(2, b.CaretCol);
    }
}
