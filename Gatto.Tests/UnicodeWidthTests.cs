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

    //the emoji a model writes in a table, wide by East Asian Width, and the marks beside them that are not
    [Theory]
    [InlineData(0x2705, 2)]    //white heavy check mark
    [InlineData(0x274C, 2)]    //cross mark
    [InlineData(0x2B50, 2)]    //star
    [InlineData(0x1F680, 2)]   //rocket
    [InlineData(0x1F697, 2)]   //automobile
    [InlineData(0x1FA75, 2)]   //light blue heart
    [InlineData(0x1F7E2, 2)]   //large green circle, a status table's mark
    [InlineData(0x1F004, 2)]   //mahjong red dragon
    [InlineData(0x1F21A, 2)]   //a squared ideograph
    [InlineData(0x2630, 2)]    //a trigram
    [InlineData(0x2329, 2)]    //a left angle bracket of the wide forms
    [InlineData(0xFE0F, 1)]    //the emoji variation selector turns a narrow symbol into a two-cell emoji, so it carries the second cell
    [InlineData(0x1F6C8, 1)]   //the cloud mark of the footer stays narrow
    [InlineData(0x1F650, 1)]   //an ornamental dingbat is narrow
    [InlineData(0x2713, 1)]    //the ok glyph
    [InlineData(0x26A0, 1)]    //the warn glyph
    public void OfRune_EmojiByEastAsianWidth(int codePoint, int expected) =>
        Assert.Equal(expected, UnicodeWidth.OfRune(new System.Text.Rune(codePoint)));

    //a check mark followed by the variation selector is two cells, the shape that broke a table border
    [Fact]
    public void A_check_mark_with_its_selector_is_two_cells() =>
        Assert.Equal(2, UnicodeWidth.Of(char.ConvertFromUtf32(0x2705) + char.ConvertFromUtf32(0xFE0F)));

    //a narrow symbol with the selector draws as a two-cell emoji, the heart that pushed a table border one cell on the walk
    [Theory]
    [InlineData(0x2764)]   //heavy black heart
    [InlineData(0x26A0)]   //warning sign
    [InlineData(0x263A)]   //white smiling face
    public void A_narrow_symbol_with_its_selector_is_two_cells(int codePoint)
    {
        var emoji = char.ConvertFromUtf32(codePoint) + char.ConvertFromUtf32(0xFE0F);
        Assert.Equal(1, UnicodeWidth.Of(char.ConvertFromUtf32(codePoint)));
        Assert.Equal(2, UnicodeWidth.Of(emoji));
        Assert.Equal(2, UnicodeWidth.Rows("a" + emoji, 2));
        Assert.Equal(1, UnicodeWidth.Rows("a" + char.ConvertFromUtf32(0x2705) + char.ConvertFromUtf32(0xFE0F), 3));
    }

    [Theory]
    [InlineData("abcdef", 3, 2)]   //6 cells at width 3 is 2 rows
    [InlineData("", 10, 1)]        //even an empty string takes 1 row
    [InlineData("・・", 3, 2)]     //the second ・ needs 2 cells and only 1 is left, so it wraps to row 2
    public void Rows_WrapAware(string s, int width, int expected) =>
        Assert.Equal(expected, UnicodeWidth.Rows(s, width));
}
