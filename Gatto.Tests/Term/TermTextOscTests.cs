using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Term;

//a refactor of the escape handling that leaks an OSC 8 scope must fail here. no raw ESC or BEL belongs in the source, build every control byte from a code point.
public class TermTextOscTests
{
    private static readonly string Esc = ((char)0x1b).ToString();
    private static readonly string Bel = ((char)0x07).ToString();
    private static readonly string St = Esc + ((char)0x5c);   //the string terminator, an ESC and a backslash

    private static string Link(string url, string text) => Esc + "]8;;" + url + St + text + Esc + "]8;;" + St;

    [Fact]
    public void StripAnsiForWidth_removes_an_OSC8_hyperlink_with_an_ST_terminator()
        => Assert.Equal("text", TermText.StripAnsiForWidth(Link("https://x.com", "text")));

    [Fact]
    public void StripAnsiForWidth_removes_an_OSC8_hyperlink_with_a_BEL_terminator()
    {
        var link = Esc + "]8;;https://x.com" + Bel + "text" + Esc + "]8;;" + Bel;
        Assert.Equal("text", TermText.StripAnsiForWidth(link));
    }

    [Fact]
    public void StripAnsiForWidth_consumes_an_unterminated_OSC_instead_of_leaking_it()
    {
        //consume the whole string when the terminator is missing, otherwise the URL shows as text
        Assert.Equal("", TermText.StripAnsiForWidth(Esc + "]8;;https://x.com/never-closed"));
    }

    [Fact]
    public void TruncateCells_carries_the_LinkClose_through_the_cut()
    {
        //the close must survive the cut, otherwise the link scope leaks over every following row
        var cut = TermText.TruncateCells(Link("u", "abcdefghij"), 5, glyphs: GlyphSet.Unicode);
        Assert.EndsWith(Esc + "]8;;" + St, cut, System.StringComparison.Ordinal);   //ends with the OSC 8 close
    }
}
