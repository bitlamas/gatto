using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

public class CatsTests
{
    private static readonly IReadOnlyList<string> Coder = ShippedRoleFiles.Load("coder").Cat!;
    private static readonly IReadOnlyList<string> Oracle = ShippedRoleFiles.Load("oracle").Cat!;

    [Fact]
    public void THE_DEFAULT_CAT_HAS_CARET_EYES()
    {
        //put no space before the ７. the fullwidth ＾ has wide side bearing, so a space makes a larger gap than the coder cat has in MS Gothic
        Assert.Contains("（＾､＾７", Cats.For(null, GlyphSet.Unicode));
    }

    [Fact]
    public void THE_SHIPPED_CODER_WEARS_THE_VISOR_AND_THE_ORACLE_THE_HAT()
    {
        Assert.Contains("（▀､▀ ７", Cats.For(Coder, GlyphSet.Unicode));
        Assert.Contains("╱☆╲", Cats.For(Oracle, GlyphSet.Unicode));
    }

    //the eyes of the coder and the hat of the oracle are the only differences. a separate body for one role would look like three mascots
    [Fact]
    public void ALL_THREE_SHARE_ONE_BODY()
    {
        foreach (var cat in new[] { null, Coder, Oracle })
        {
            var drawn = Cats.For(cat, GlyphSet.Unicode);
            Assert.Contains("l  ~ヽ", drawn, StringComparison.Ordinal);
            Assert.Contains("じしf_,)ノ", drawn, StringComparison.Ordinal);
        }
        var general = Cats.For(null, GlyphSet.Unicode).Split('\n');
        var coder = Cats.For(Coder, GlyphSet.Unicode).Split('\n');
        Assert.Equal(general.Length, coder.Length);
        for (var i = 0; i < general.Length; i++)
            if (i != 1) Assert.Equal(general[i], coder[i]);
    }

    //every banner call site splits on the newlines and must not assume a height
    [Fact]
    public void THE_ORACLE_IS_SIX_ROWS_AND_THE_OTHERS_FOUR()
    {
        Assert.Equal(4, Cats.For(null, GlyphSet.Unicode).Split('\n').Length);
        Assert.Equal(4, Cats.For(Coder, GlyphSet.Unicode).Split('\n').Length);
        Assert.Equal(6, Cats.For(Oracle, GlyphSet.Unicode).Split('\n').Length);
    }

    //the wide glyphs and the fullwidth kana are where a width bug hides
    [Fact]
    public void NO_ROW_WRAPS_AT_EIGHTY_COLUMNS()
    {
        foreach (var cat in new[] { null, Coder, Oracle })
            foreach (var line in Cats.For(cat, GlyphSet.Unicode).Split('\n'))
                Assert.True(UnicodeWidth.Of(line) <= 80, $"row overflows 80 cells ({UnicodeWidth.Of(line)}): \"{line}\"");
    }

    [Fact]
    public void THE_ASCII_SET_IGNORES_THE_FILES_CAT()
    {
        Assert.Equal(Cats.For(null, GlyphSet.Ascii), Cats.For(Coder, GlyphSet.Ascii));
        Assert.Equal(Cats.For(null, GlyphSet.Ascii), Cats.For(Oracle, GlyphSet.Ascii));
    }

    [Fact]
    public void THE_SHIPPED_CATS_ARE_WITHIN_THE_BOUNDS()
    {
        var rows = Coder.Concat(Oracle).Select(UnicodeWidth.Of).ToList();
        Assert.All(rows, w => Assert.InRange(w, 1, 16));
        Assert.Equal(12, rows.Max());
        Assert.Equal(new[] { 5, 6, 7 }, Oracle.Take(3).Select(UnicodeWidth.Of));
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
