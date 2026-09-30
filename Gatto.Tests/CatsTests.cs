using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

public class CatsTests
{
    [Fact]
    public void Generalist_HasCaretEyes()
    {
        var cat = Cats.For("generalist", glyphs: GlyphSet.Unicode);
        //put no space before the ７. the fullwidth ＾ has wide side bearing, so a space makes a larger gap than the coder cat has in MS Gothic
        Assert.Contains("（＾､＾７", cat);
    }

    [Fact]
    public void CoderWearsTheVisor_OracleWearsTheHat()
    {
        Assert.Contains("（▀､▀ ７", Cats.For("coder", glyphs: GlyphSet.Unicode));
        Assert.Contains("╱☆╲", Cats.For("oracle", glyphs: GlyphSet.Unicode));
    }

    //the eyes of the coder and the hat of the oracle are the only differences. a separate body for one role would look like three mascots
    [Fact]
    public void All_three_share_one_body()
    {
        foreach (var role in new[] { "generalist", "coder", "oracle" })
        {
            var cat = Cats.For(role, glyphs: GlyphSet.Unicode);
            Assert.Contains("l  ~ヽ", cat, StringComparison.Ordinal);
            Assert.Contains("じしf_,)ノ", cat, StringComparison.Ordinal);
        }
        //the generalist and the coder must differ only on the face row, which is row 1.
        var general = Cats.For("generalist", glyphs: GlyphSet.Unicode).Split('\n');
        var coder = Cats.For("coder", glyphs: GlyphSet.Unicode).Split('\n');
        Assert.Equal(general.Length, coder.Length);
        for (var i = 0; i < general.Length; i++)
            if (i != 1) Assert.Equal(general[i], coder[i]);
    }

    //the oracle is six lines tall and the other cats are four. every banner call site must split on the newlines and must not assume a height
    [Fact]
    public void The_oracle_is_two_lines_taller_and_the_others_are_four()
    {
        Assert.Equal(4, Cats.For("generalist", glyphs: GlyphSet.Unicode).Split('\n').Length);
        Assert.Equal(4, Cats.For("coder", glyphs: GlyphSet.Unicode).Split('\n').Length);
        Assert.Equal(6, Cats.For("oracle", glyphs: GlyphSet.Unicode).Split('\n').Length);
    }

    //every banner row must fit in one physical row of an 80-column terminal. the wide glyphs and the fullwidth kana are where a width bug hides.
    [Theory]
    [InlineData("generalist")]
    [InlineData("coder")]
    [InlineData("oracle")]
    public void No_banner_row_wraps_at_eighty_columns(string role)
    {
        foreach (var line in Cats.For(role, glyphs: GlyphSet.Unicode).Split('\n'))
            Assert.True(Gatto.Terminal.UnicodeWidth.Of(line) <= 80,
                $"row overflows 80 cells ({Gatto.Terminal.UnicodeWidth.Of(line)}): \"{line}\"");
    }

    //the face takes no role, so this test covers both glyph sets and no role. a case per role would suggest a difference that does not exist.
    [Fact]
    public void The_face_is_a_single_line()
    {
        //the face goes into a chrome row that must stay one physical row, so a line break in the face breaks that row.
        foreach (var set in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        {
            var face = Cats.Face(set);
            Assert.DoesNotContain('\n', face);
            Assert.DoesNotContain('\r', face);
        }
    }
}
