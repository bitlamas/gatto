using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests;

public class GutterWrapTests
{
    [Fact]
    public void SingleShortLine_OneRow_MarkerPlusText()
    {
        var rows = GutterWrap.Rows("♯ ", "♯ ", "journal updated", 40);
        Assert.Single(rows);
        Assert.Equal("♯ journal updated", rows[0], StringComparer.Ordinal);
    }

    [Fact]
    public void WrapsAtWidthMinusTwo_ContinuationsHang()
    {
        //the gutter takes two cells, so a width of 12 leaves 10 for text.
        var rows = GutterWrap.Rows("● ", "● ", "aaaa bbbb cccc", 12);
        Assert.Equal(2, rows.Count);
        Assert.Equal("● aaaa bbbb", rows[0], StringComparer.Ordinal);
        Assert.Equal("  cccc", rows[1], StringComparer.Ordinal);
    }

    [Fact]
    public void ZeroWidth_DegradesToSingleUnwrappedRow()
    {
        var rows = GutterWrap.Rows("● ", "● ", "anything at all", 0);
        Assert.Single(rows);
        Assert.Equal("● anything at all", rows[0], StringComparer.Ordinal);
    }

    [Fact]
    public void CjkNeverStraddles()   //the wrap rule from SoftWrap applies here, so no row splits a double-width character
    {
        var rows = GutterWrap.Rows("● ", "● ", "日本語のテキストです", 8);
        Assert.All(rows, r => Assert.True(Gatto.Terminal.UnicodeWidth.Of(r) <= 8));
    }

    [Fact]
    public void SharpSign_PinnedWidthOne()
    {
        Assert.Equal(1, Gatto.Terminal.UnicodeWidth.Of("♯"));
    }
}
