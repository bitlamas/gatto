using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//sweep Enum.GetValues, so a member with no mark or no ink fails here rather than on the one screen that draws it
public class RowGlyphTests
{
    [Fact]
    public void EVERY_ROW_GLYPH_HAS_A_MARK_AND_AN_INK_OF_ITS_OWN()
    {
        var seen = 0;
        foreach (var glyph in Enum.GetValues<RowGlyph>())
        {
            Assert.False(string.IsNullOrEmpty(Glyphs.Of(glyph, glyphs: GlyphSet.Unicode)), $"{glyph} has no mark");
            //the ink Plain is real, but a status mark in the row's own colour is a check nobody sees
            Assert.True(TuiWizardSurface.GlyphInk(glyph) != RunInk.Plain, $"{glyph} has no ink of its own");
            seen++;
        }

        //the count proves the sweep saw something, since a loop over an empty set asserts nothing and passes
        Assert.Equal(Enum.GetValues<RowGlyph>().Length, seen);
        Assert.True(seen > 0, "no row glyphs exist, so nothing above was checked");
    }
}
