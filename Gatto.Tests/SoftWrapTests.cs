using Gatto.Terminal;

namespace Gatto.Tests;

public class SoftWrapTests
{
    private static string[] Texts(IReadOnlyList<WrapSeg> segs) => segs.Select(s => s.Text).ToArray();

    [Fact]
    public void WrapsAtWordBoundary_NeverMidWord()
    {
        //the word Excelente is 9 cells and " para" would cross 10, so the whole word wraps
        var segs = SoftWrap.Wrap("Excelente para achar", 10, 10);
        Assert.Equal(new[] { "Excelente", "para achar" }, Texts(segs));
    }

    [Fact]
    public void DropsSpaceAtWrapBoundary()
    {
        var segs = SoftWrap.Wrap("aaaa bbbb", 4, 4);
        Assert.Equal(new[] { "aaaa", "bbbb" }, Texts(segs));   //the joining space is dropped
    }

    [Fact]
    public void PreservesFirstRowLeadingIndent()
    {
        var segs = SoftWrap.Wrap("  indented start", 10, 10);
        Assert.Equal("  indented", segs[0].Text);
    }

    [Fact]
    public void HardBreaksWordLongerThanRow()
    {
        var segs = SoftWrap.Wrap("Supercalifragilistic", 8, 8);
        Assert.All(segs, s => Assert.True(UnicodeWidth.Of(s.Text) <= 8));
        Assert.Equal("Supercalifragilistic", string.Concat(Texts(segs)));
    }

    [Fact]
    public void WideRunesCountTwoCells()
    {
        //the two wide runes are 4 cells together, so a budget of 5 leaves room for one narrow character at most
        var segs = SoftWrap.Wrap("月球 terra", 5, 5);
        Assert.Equal("月球", segs[0].Text);
        Assert.Equal("terra", segs[1].Text);
    }

    [Fact]
    public void FirstAndContinuationBudgetsDiffer()
    {
        var segs = SoftWrap.Wrap("one two three four", 7, 5);
        Assert.Equal("one two", segs[0].Text);
        Assert.All(segs.Skip(1), s => Assert.True(UnicodeWidth.Of(s.Text) <= 5));
    }

    [Fact]
    public void SourceCharsAlwaysSumToLength()
    {
        foreach (var t in new[] { "", "a", "aaaa bbbb cc", "  x  y  ", "月球 terra nova", "wordthatislong and more", "aaaa  b", "hello " })
        {
            var segs = SoftWrap.Wrap(t, 6, 4);
            Assert.Equal(t.Length, segs.Sum(s => s.SourceChars));
        }
    }

    [Fact]
    public void BudgetZeroDisablesWrap()
    {
        var segs = SoftWrap.Wrap("anything at all here", 0, 0);
        Assert.Single(segs);
        Assert.Equal("anything at all here", segs[0].Text);
    }

    [Fact]
    public void MapCursor_WithinFirstRow()
    {
        var text = "aaaa bbbb";
        var segs = SoftWrap.Wrap(text, 4, 4);
        Assert.Equal((0, 2), SoftWrap.MapCursor(segs, text, 2));
    }

    [Fact]
    public void MapCursor_AfterDroppedSpace_LandsAtNextRowStart()
    {
        var text = "aaaa bbbb";                        //the segments are "aaaa" over 5 cells (the dropped space counts) and "bbbb" over 4
        var segs = SoftWrap.Wrap(text, 4, 4);
        Assert.Equal((1, 0), SoftWrap.MapCursor(segs, text, 5));   //the cursor is just before the b, at the start of row 1
    }

    [Fact]
    public void MapCursor_EndOfText_LandsAfterLastChar()
    {
        var text = "aaaa bbbb";
        var segs = SoftWrap.Wrap(text, 4, 4);
        Assert.Equal((1, 4), SoftWrap.MapCursor(segs, text, text.Length));
    }

    [Fact]
    public void MapCursor_ConsecutiveSpacesAtBoundary_LandsOnChar()
    {
        //a leading space dropped at the boundary is charged to the row before it, or MapCursor mislocates the next character by one column
        var text = "aaaa  b";
        var segs = SoftWrap.Wrap(text, 4, 4);
        Assert.Equal(new[] { "aaaa", "b" }, Texts(segs));
        Assert.Equal(6, segs[0].SourceChars);
        Assert.Equal(1, segs[1].SourceChars);
        Assert.Equal((1, 0), SoftWrap.MapCursor(segs, text, 6));   //the b renders at column 0 of row 1
    }

    [Fact]
    public void MapCursor_WideGlyphStraddlingBoundary_LandsOnNewRow()
    {
        //a width-2 glyph can't be split across the boundary, so it wraps whole and the spare cell on the row stays empty
        var text = "aaaaaa字";
        var segs = SoftWrap.Wrap(text, 7, 7);
        Assert.Equal(new[] { "aaaaaa", "字" }, Texts(segs));
        Assert.Equal((1, 0), SoftWrap.MapCursor(segs, text, 6));   //the cursor sits just before 字, at column 0 of the new row
    }

    [Fact]
    public void TrailingTypedSpace_SurvivesFinalFlush()
    {
        //a typed space at the end of the input is kept. the trailing-space trim only drops one that a wrap caused
        var text = "hello ";
        var segs = SoftWrap.Wrap(text, 20, 20);
        Assert.Equal(new[] { "hello " }, Texts(segs));
        Assert.Equal((0, 6), SoftWrap.MapCursor(segs, text, 6));   //the caret advances past the kept space
    }
}
