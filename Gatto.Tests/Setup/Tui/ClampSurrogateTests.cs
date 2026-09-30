using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//a clamp that cuts by char can split an astral glyph in a model name. only the width between the halves shows the replacement glyph
public class ClampSurrogateTests
{
    //an unpaired surrogate reads as U+FFFD under EnumerateRunes and no input holds that code point. a U+FFFD in the output can only come from a bad cut
    private static bool HasLoneSurrogate(string s) =>
        s.EnumerateRunes().Any(r => r.Value == 0xFFFD);

    //the Astral glyph is two chars, one rune and two cells, the shape a clamp must never split
    private const string Astral = "👾";

    [Theory]
    [InlineData("gatto ")]                       //the pair starts at an even offset here
    [InlineData("gatto x")]                      //the pair starts at an odd offset, so the width that could split it differs from the other row
    public void NO_WIDTH_EVER_CUTS_AN_ASTRAL_GLYPH_IN_HALF(string prefix)
    {
        var row = PaintedRow.Of(prefix + Astral + " build");

        for (var width = 1; width <= Gatto.Terminal.UnicodeWidth.Of(row.Text) + 2; width++)
        {
            var cut = row.Clamp(width);

            Assert.False(HasLoneSurrogate(cut.Text),
                $"clamping at {width} left a lone surrogate: [{cut.Text}]");

            Assert.True(Gatto.Terminal.UnicodeWidth.Of(cut.Text) <= width,
                $"clamping at {width} produced {Gatto.Terminal.UnicodeWidth.Of(cut.Text)} cells");
        }
    }

    //a real frame is many runs, so the width budget can run out inside one, and the cut must stay on rune boundaries there too
    [Fact]
    public void A_MULTI_RUN_ROW_IS_CUT_ON_RUNE_BOUNDARIES_TOO()
    {
        var row = new PaintedRow([
            new Run("gatto ", RunInk.Bright),
            new Run(Astral, RunInk.Accent),
            new Run(" build", RunInk.Dim)]);

        for (var width = 1; width <= 16; width++)
        {
            var cut = row.Clamp(width);
            Assert.False(HasLoneSurrogate(cut.Text),
                $"clamping a multi-run row at {width} left a lone surrogate: [{cut.Text}]");
            Assert.True(Gatto.Terminal.UnicodeWidth.Of(cut.Text) <= width);
        }
    }

    //a clamp that dropped every astral glyph would pass the lone-surrogate assertions
    [Fact]
    public void AN_ASTRAL_GLYPH_THAT_FITS_IS_KEPT_WHOLE()
    {
        var row = PaintedRow.Of("ab" + Astral);

        Assert.Equal("ab" + Astral, row.Clamp(4).Text);
        Assert.Equal("ab", row.Clamp(3).Text);     //width three leaves no room for both cells, so the clamp drops the glyph entirely
    }
}
