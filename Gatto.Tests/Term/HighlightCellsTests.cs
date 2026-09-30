using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Term;

//the highlight must not split an escape and has to restore the ambient background on range exit, reconstructed from the row
public class HighlightCellsTests
{
    private static readonly string E = ((char)0x1b).ToString();
    private static readonly string St = E + ((char)0x5c);   //the OSC string terminator, ESC then a backslash
    private static string BgOn => E + "[48;2;40;44;52m";     //a sentinel selection background
    private static string Visible(string s) => TermText.StripAnsiForWidth(s);

    [Fact]
    public void Highlights_a_plain_cell_range_and_restores_default_bg()
    {
        var outp = TermText.HighlightCells("hello world", 0, 5, BgOn);
        Assert.Equal("hello world", Visible(outp));      //the text is unchanged
        Assert.Contains(BgOn, outp);                      //the range opens the selection background
        Assert.Contains(E + "[49m", outp);                //the range exit restores the default background rather than resetting everything
        Assert.DoesNotContain(E + "[0m", outp);           //a full reset would drop the foreground and bold, so it must not appear
    }

    [Fact]
    public void Banded_row_selection_ending_mid_row_keeps_the_band_to_the_edge()
    {
        //the band is restored at the range exit, before the row's own re-open, and the selection bg stops at c
        var band = E + "[48;5;236m";
        var row = band + "abcdef" + E + "[0m" + band + "ghij";
        var outp = TermText.HighlightCells(row, 0, 3, BgOn);
        Assert.Equal("abcdefghij", Visible(outp));
        var betweenCandD = outp[(outp.IndexOf('c') + 1)..outp.IndexOf('d')];
        Assert.Contains(band, betweenCandD);              //the exit emits the band itself, before the row's own re-open
        Assert.DoesNotContain(BgOn, outp[outp.IndexOf('d')..]);   //the selection background stops at the range end
    }

    [Fact]
    public void Compound_sgr_restore_is_reconstructed_not_replayed()
    {
        //the exit re-emits only the background, since replaying the compound escape would re-assert bold
        var row = E + "[1;48;2;10;20;30m" + "compound";
        var outp = TermText.HighlightCells(row, 0, 4, BgOn);   //selects comp, the first four cells
        var afterRange = outp[(outp.IndexOf("comp") + 4)..];
        Assert.Contains(E + "[48;2;10;20;30m", afterRange);              //the background is reconstructed, so the escape starts at 48
        Assert.DoesNotContain(E + "[1;48;2;10;20;30m", afterRange);      //the compound escape is not replayed, or bold would come back on
    }

    [Fact]
    public void Empty_param_reset_is_treated_as_default()
    {
        var row = E + "[48;5;236m" + "abc" + E + "[m" + "def";   //an empty SGR parameter means reset, the same as 0
        var outp = TermText.HighlightCells(row, 4, 6, BgOn);      //selects ef, which sits after the reset
        Assert.Contains(E + "[49m", outp[outp.IndexOf('f')..]);  //after the reset the ambient background is the default, so the exit emits 49
    }

    [Fact]
    public void Hyperlink_bearing_row_keeps_its_osc8_open_and_close()   //an escape that opens a hyperlink must survive whole, open and close
    {
        var link = E + "]8;;https://example.com" + St;
        var close = E + "]8;;" + St;
        var row = "see " + link + "here" + close + " ok";
        var outp = TermText.HighlightCells(row, 2, 10, BgOn);
        Assert.Contains(link, outp);                      //the hyperlink's opening escape comes through whole
        Assert.Contains(close, outp);                     //and its closing escape comes through too
        Assert.Equal("see here ok", Visible(outp));
    }

    [Fact]
    public void Cjk_width2_range_counts_cells_not_chars()
    {
        var outp = TermText.HighlightCells("日本語ab", 0, 2, BgOn);   //one CJK glyph is 2 cells wide
        Assert.Contains(BgOn, outp);
        Assert.Equal("日本語ab", Visible(outp));
    }

    [Fact]
    public void Bright_bg_ambient_is_restored()   //the 100-107 range counts as a background escape too
    {
        var row = E + "[101m" + "abcdef";   //101 is a bright red background
        var outp = TermText.HighlightCells(row, 0, 3, BgOn);
        Assert.Contains(E + "[101m", outp[(outp.IndexOf('c') + 1)..outp.IndexOf('d')]);   //the exit re-emits the bright background
    }

    [Fact]
    public void Zero_width_range_returns_the_row_unchanged()
        => Assert.Equal("hello", TermText.HighlightCells("hello", 3, 3, BgOn));

    [Fact]
    public void Selection_to_the_last_cell_still_restores_before_row_end()   //a selection to the last cell still restores before the row ends, so the next row's clear cannot paint the band
    {
        var outp = TermText.HighlightCells("abc", 0, 3, BgOn);   //selects the whole row
        Assert.EndsWith(E + "[49m", outp);   //the background must be restored at the end, or it leaks into the next row's clear
    }
}
