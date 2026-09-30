using Gatto.Terminal;

namespace Gatto.Tests;

public class UnicodeWidthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("❯", 1)]                    //the glyph U+276F is one cell, it is not in the wide ranges
    [InlineData("・", 2)]                   //the katakana middle dot U+30FB is wide
    [InlineData("じし", 4)]                 //hiragana is wide
    [InlineData("（ﾟ、｡ ７", 9)]            //fullwidth paren 2, halfwidth kana 1, wide 、2, halfwidth ｡1, space 1, fullwidth ７2, nine in all
    [InlineData("é", 1)]        //e plus a combining acute is still one cell
    [InlineData("a​b", 2)]             //the zero-width space adds no cell
    public void Of_MatchesTerminalCells(string s, int expected) =>
        Assert.Equal(expected, UnicodeWidth.Of(s));

    [Theory]
    [InlineData("abcdef", 3, 2)]   //6 cells at width 3 is 2 rows
    [InlineData("", 10, 1)]        //even an empty string takes 1 row
    [InlineData("・・", 3, 2)]     //the second ・ needs 2 cells and only 1 is left, so it wraps to row 2
    public void Rows_WrapAware(string s, int width, int expected) =>
        Assert.Equal(expected, UnicodeWidth.Rows(s, width));
}
