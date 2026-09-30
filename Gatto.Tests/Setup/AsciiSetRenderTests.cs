using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//render under the ASCII set, since a census only shows a literal gone. the Unicode half is covered by the golden renders
public class AsciiSetRenderTests
{
    //the characters the table owns in their Unicode spelling. a frame drawn under the ASCII set must contain none of them.
    private const string Owned = "\u2713\u2717\u26a0\u25c8\u276f\u25cf\u2500\u00b7\u25b3\u2013";

    //assert every byte is ascii, since a mark-only check cannot see a cat drawn from katakana and block characters
    private static void AssertPureAscii(string what, string text)
    {
        foreach (var ch in text)
            Assert.True(ch <= 0x7F,
                $"{what} drew U+{(int)ch:X4} under the ASCII set, which a legacy console has no glyph "
                + $"for: {text}");
    }

    private static void AssertAscii(string what, string text)
    {
        foreach (var ch in text)
            Assert.False(Owned.Contains(ch),
                $"{what} drew U+{(int)ch:X4} under the ASCII set, so that site is a literal rather "
                + $"than a read: {text}");
    }

    //only rendering says which vocabulary came out, since a census counts a literal as gone once it moves into a helper
    [Fact]
    public void THE_FIT_MARKS_ARE_ASCII_UNDER_THE_ASCII_SET()
    {
        foreach (var shape in Enum.GetValues<MachineShape>())
        {
            foreach (var fit in Enum.GetValues<FitRegime>())
                AssertAscii($"FitMarks.Of({fit}, {shape})",
                    FitMarks.Of(fit, shape, GlyphSet.Ascii).Text);

            AssertAscii($"FitMarks.LegendFor({shape})",
                FitMarks.LegendFor(shape, GlyphSet.Ascii).Text);
        }
    }

    //the same calls must still draw the Unicode marks. without this, an ASCII set leaking into every path would keep the guard above green.
    [Fact]
    public void AND_THE_UNICODE_SET_STILL_DRAWS_THE_MARKS()
    {
        Assert.Contains('\u2713',
            FitMarks.Of(FitRegime.FitsGpu, MachineShape.Discrete, GlyphSet.Unicode).Text);
        Assert.Contains('\u00b7',
            FitMarks.LegendFor(MachineShape.Discrete, GlyphSet.Unicode).Text);
    }

    //the dot appears twice in the fetch line and four times in the engine rows, so the check covers most of what they draw
    [Fact]
    public void THE_FETCH_LINE_AND_THE_ENGINE_VIEW_ARE_ASCII_UNDER_THE_ASCII_SET()
    {
        var tick = new FetchTick("llama-vulkan.zip", 1, 2, 5_000_000, 10_000_000, 5_000);

        AssertAscii("Widget.Line", Widget.Line(tick, GlyphSet.Ascii));

        var view = new EngineView(true, "llama-vulkan.zip", "18 MB", "b1234", @"C:\e\", "cuda.zip", "3 MB");
        foreach (var row in EngineFetchView.Rows(view, 100, GlyphSet.Ascii))
            AssertAscii("EngineFetchView", row.Text);
    }
    //assert the header row and the cursor prefix only, since the table rows go through a surface the conversion never touched
    [Fact]
    public void THE_SHELFS_HEADER_AND_CURSOR_ARE_ASCII_UNDER_THE_ASCII_SET()
    {
        var shelf = Gatto.Tests.Setup.Tui.ShelfTests.Unified96();
        var rows = Shelf.Table(shelf, row: 0, focused: true, GlyphSet.Ascii);

        AssertAscii("the shelf's header row", rows[0].Text);
        AssertAscii("the cursored row's prefix", rows[1].Text[..2]);
    }

    //under the ASCII set every role draws the one ruled cat, since extra mascots cannot render on a legacy console
    [Theory]
    [InlineData("coder")]
    [InlineData("oracle")]
    [InlineData("generalist")]
    public void EVERY_ROLE_DRAWS_AN_ASCII_CAT_AND_FACE_UNDER_THE_ASCII_SET(string role)
    {
        foreach (var line in Gatto.Repl.Cats.For(role, GlyphSet.Ascii).Split('\n'))
            AssertPureAscii($"Cats.For({role})", line);

        //the face takes no role, so all three arms get the same string (a role must not reach a different face under the ASCII set)
        AssertPureAscii("Cats.Face", Gatto.Repl.Cats.Face(GlyphSet.Ascii));
        AssertPureAscii("Cats.WaitingOf", Gatto.Repl.Cats.WaitingOf(GlyphSet.Ascii));
        AssertPureAscii("Cats.EmptyOf", Gatto.Repl.Cats.EmptyOf(GlyphSet.Ascii));

        //the wild and header faces are fixed choices, held to the same pure-ascii bar as the cat
        AssertPureAscii("GlyphSet.Ascii.Wild", GlyphSet.Ascii.Wild);
        AssertPureAscii("GlyphSet.Ascii.Header", GlyphSet.Ascii.Header);
    }

    //the Unicode set must still draw each role its own cat. without it the ascii guard passes against a table that lost the role drawings.
    [Fact]
    public void AND_THE_UNICODE_SET_STILL_DRAWS_THE_ROLE_CATS()
    {
        var coder = Gatto.Repl.Cats.For("coder", GlyphSet.Unicode);
        var oracle = Gatto.Repl.Cats.For("oracle", GlyphSet.Unicode);

        Assert.NotEqual(coder, oracle);
        Assert.Contains('\u2580', coder);                       //the coder cat keeps its visor character.
        Assert.Equal(6, oracle.Split('\n').Length);              //the oracle hat makes its cat two rows taller.
    }

}
