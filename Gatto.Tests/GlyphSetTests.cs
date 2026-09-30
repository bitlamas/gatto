using Gatto.Terminal;

namespace Gatto.Tests;

//the glyph table resolves once per run, since a legacy console blanks unicode. these guards cover the table, so untouched goldens prove no call site moved
public class GlyphSetTests
{
    private static readonly IReadOnlyDictionary<string, string?> Empty =
        new Dictionary<string, string?>();

    private static IReadOnlyDictionary<string, string?> Env(string key, string? value) =>
        new Dictionary<string, string?> { [key] = value };

    //the auto mode reads the host, and a named mode overrides it either way, so a misjudged host still gets a usable set
    [Theory]
    [InlineData("WT_SESSION", "1", GlyphMode.Auto, false)]          //a set WT_SESSION means a unicode host
    [InlineData("TERM_PROGRAM", "vscode", GlyphMode.Auto, false)]   //the TERM_PROGRAM value vscode also means a unicode host
    [InlineData(null, null, GlyphMode.Auto, true)]                  //a host that sets neither variable gets the ascii set.
    [InlineData("WT_SESSION", "1", GlyphMode.Ascii, true)]          //a named Ascii mode wins over a unicode host
    [InlineData(null, null, GlyphMode.Unicode, false)]              //the named Unicode mode wins over a host that sets nothing
    public void THE_AUTO_RULE_AND_THE_OVERRIDE(string? key, string? value, GlyphMode mode, bool ascii)
    {
        var env = key is null ? Empty : Env(key, value);

        Assert.Equal(ascii, ReferenceEquals(GlyphSet.Resolve(mode, env), GlyphSet.Ascii));
    }

    //an empty value is not a set variable, so WT_SESSION= reads as unset. colour detection already reads it that way, and two readings must not disagree
    [Fact]
    public void AN_EMPTY_VARIABLE_IS_NOT_SET()
    {
        Assert.Same(GlyphSet.Ascii, GlyphSet.Resolve(GlyphMode.Auto, Env("WT_SESSION", "")));
        Assert.Same(GlyphSet.Ascii, GlyphSet.Resolve(GlyphMode.Auto, Env("WT_SESSION", null)));
    }

    //a fallback must need no font, so every member stays ascii. the members come from the instance by reflection, since a hand-written list drifts
    [Fact]
    public void EVERY_ASCII_MEMBER_IS_ASCII()
    {
        foreach (var (name, text) in Members(GlyphSet.Ascii))
            Assert.All(text, ch => Assert.True(ch <= 0x7F,
                $"GlyphSet.Ascii.{name} carries U+{(int)ch:X4}, which a legacy console has no glyph for"));
    }

    //the unicode set must hold at least one character above ascii. otherwise the ascii guard passes against two identical sets
    [Fact]
    public void AND_THE_TWO_SETS_ARE_ACTUALLY_DIFFERENT()
    {
        Assert.Contains(Members(GlyphSet.Unicode), m => m.Text.Any(ch => ch > 0x7F));
    }

    //both cats have the same line count, so the face stays on the same row. the ascii row is never wider, so a frame fits either cat
    [Fact]
    public void THE_TWO_CATS_ARE_THE_SAME_SHAPE()
    {
        Assert.Equal(GlyphSet.Unicode.Cat.Count, GlyphSet.Ascii.Cat.Count);

        foreach (var (u, a) in GlyphSet.Unicode.Cat.Zip(GlyphSet.Ascii.Cat))
            Assert.True(UnicodeWidth.Of(a) <= UnicodeWidth.Of(u),
                $"the ascii cat's row is wider than the unicode one it replaces: "
                + $"{UnicodeWidth.Of(a)} against {UnicodeWidth.Of(u)}");
    }

    //check every character of the ascii cat, since the table came from a transcription and a copy can bring in one non-ascii character.
    [Fact]
    public void THE_ASCII_CAT_IS_ASCII()
    {
        Assert.All(GlyphSet.Ascii.Cat, line => Assert.All(line, ch => Assert.True(ch <= 0x7F)));
    }

    //pin the four art lines as literals here, since a generated table with no expected value is unchecked
    [Fact]
    public void AND_IT_IS_THE_RULED_CAT()
    {
        Assert.Equal(
            ["/l,", "(^,^7", " l  ~" + "\\", " UUf_,)/"],
            GlyphSet.Ascii.Cat);
    }

    //list every string member of a set by name from the type itself, so the check cannot drift from the table.
    private static IReadOnlyList<(string Name, string Text)> Members(GlyphSet set) =>
    [
        .. typeof(GlyphSet).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (p.Name, (string)p.GetValue(set)!)),
        .. set.Cat.Select((line, i) => ($"Cat[{i}]", line)),
    ];

    //yield every string the ascii set holds, including the box group and the cat lines, read from the type.
    private static IEnumerable<(string Name, string Value)> AsciiStrings()
    {
        foreach (var p in typeof(GlyphSet).GetProperties())
        {
            if (p.PropertyType == typeof(string) && p.GetValue(GlyphSet.Ascii) is string s)
                yield return (p.Name, s);

            if (p.PropertyType == typeof(IReadOnlyList<string>)
                && p.GetValue(GlyphSet.Ascii) is IReadOnlyList<string> rows)
                for (var i = 0; i < rows.Count; i++)
                    yield return ($"{p.Name}[{i}]", rows[i]);

            if (p.PropertyType == typeof(BoxSet) && p.GetValue(GlyphSet.Ascii) is BoxSet box)
                foreach (var b in typeof(BoxSet).GetProperties())
                    if (b.GetValue(box) is string bs)
                        yield return ($"{p.Name}.{b.Name}", bs);
        }
    }

    //every member of the fallback set must be ascii. a non-ascii member is worse than no fallback, since the run resolves to it and draws a glyph the host lacks
    [Fact]
    public void EVERY_MEMBER_OF_THE_ASCII_SET_IS_ASCII()
    {
        var all = AsciiStrings().ToList();
        Assert.NotEmpty(all);

        foreach (var (name, value) in all)
            foreach (var ch in value)
                Assert.True(ch <= 0x7F,
                    $"the ASCII set's {name} carries U+{(int)ch:X4}: {value}");
    }

    //a required member can still hold an empty string, which disappears on the ascii host. the ascii check above can't see this, since a blank string is ascii
    [Fact]
    public void NO_MEMBER_OF_EITHER_SET_IS_EMPTY()
    {
        foreach (var (name, value) in AsciiStrings())
            Assert.False(string.IsNullOrEmpty(value), $"the ASCII set's {name} is empty");

        foreach (var p in typeof(GlyphSet).GetProperties())
            if (p.PropertyType == typeof(string) && p.GetValue(GlyphSet.Unicode) is string s)
                Assert.False(string.IsNullOrEmpty(s), $"the Unicode set's {p.Name} is empty");
    }

    //the two sets share one shape. the names are read from each set's runtime type, so splitting them into two types with different members fails here.
    [Fact]
    public void THE_TWO_SETS_HAVE_THE_SAME_MEMBERS() =>
        Assert.Equal(
            GlyphSet.Ascii.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
            GlyphSet.Unicode.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

    //some pairs share an ascii spelling yet stay separate members. folding them would change what the unicode set draws, which the golden renders fix.
    [Fact]
    public void THE_SHARED_TWINS_ARE_DELIBERATE()
    {
        var a = GlyphSet.Ascii;
        var u = GlyphSet.Unicode;

        Assert.Equal(a.Ellipsis, a.MidEllipsis);
        Assert.NotEqual(u.Ellipsis, u.MidEllipsis);

        Assert.Equal(a.Down, a.Caret);
        Assert.NotEqual(u.Down, u.Caret);

        Assert.Equal(a.Right, a.Continue);
        Assert.NotEqual(u.Right, u.Continue);

        Assert.Equal(a.Angle, a.Triangle);
        Assert.NotEqual(u.Angle, u.Triangle);
    }

    //the range separator and the not-run mark draw the same character, yet stay two members. one is typography, the other vocabulary, so the duplicate is deliberate
    [Fact]
    public void THE_RANGE_SEPARATOR_IS_ITS_OWN_MEMBER_THOUGH_IT_DRAWS_LIKE_NOT_RUN()
    {
        Assert.Equal(GlyphSet.Unicode.NotRun, GlyphSet.Unicode.Range);
        Assert.Equal(GlyphSet.Ascii.NotRun, GlyphSet.Ascii.Range);
    }

    //the frame takes its horizontal from the Rule member, the only box-group entry that is not a corner, junction or vertical
    [Fact]
    public void THE_BOX_DRAWS_ITS_VERTICAL_AND_LEANS_ON_RULE_FOR_THE_HORIZONTAL()
    {
        Assert.Equal("|", GlyphSet.Ascii.Box.Vertical);
        Assert.Equal("\u2502", GlyphSet.Unicode.Box.Vertical);
        Assert.Equal("-", GlyphSet.Ascii.Rule);
    }

    //pin each face as one literal in \uXXXX escapes here, since a glyph copied by eye can hold the wrong bytes
    [Fact]
    public void THE_FIVE_FACES_ARE_THE_RULED_SPELLINGS()
    {
        Assert.Equal("(^\u00b7 \u00b7^)\u27c6", GlyphSet.Unicode.Face);
        Assert.Equal("(=^.w.^)>", GlyphSet.Ascii.Face);

        Assert.Equal("(^\u00b7 \u00b7^)\u2cca", GlyphSet.Unicode.Header);
        Assert.Equal("(^. .^)>", GlyphSet.Ascii.Header);

        Assert.Equal("(^\u00b7 \u00b7^)\u222b", GlyphSet.Unicode.Waiting);
        Assert.Equal("(^._.^)/", GlyphSet.Ascii.Waiting);

        Assert.Equal("(^\u00b7_\u00b7^)\uff89", GlyphSet.Unicode.Empty);
        Assert.Equal("(^. .^)/", GlyphSet.Ascii.Empty);

        Assert.Equal("\u2265^\u2022-\u2022^\u2264", GlyphSet.Unicode.Wild);
        Assert.Equal(">^.w.^<", GlyphSet.Ascii.Wild);
    }

    //purr, header and waiting share one body and differ in the last character. the Empty face is excluded, since it changes the mouth too
    [Fact]
    public void THE_CATS_DIFFER_BY_THEIR_TAIL()
    {
        string[] family = [GlyphSet.Unicode.Face, GlyphSet.Unicode.Header, GlyphSet.Unicode.Waiting];

        Assert.Single(family.Select(f => f[..^1]).Distinct());
        Assert.Equal(3, family.Select(f => f[^1]).Distinct().Count());

        //the purr face and the header share the same mouth, so a mouth comparison cannot tell them apart.
        Assert.NotEqual(GlyphSet.Unicode.Face, GlyphSet.Unicode.Header);
        Assert.NotEqual(GlyphSet.Unicode.Empty[..^1], GlyphSet.Unicode.Face[..^1]);
    }

    //measure the faces with UnicodeWidth, the function the product paints with. the old 10-cell face stays pinned, so the new number reads as a change
    [Fact]
    public void THE_FACES_ARE_THE_WIDTHS_THE_TITLE_ROW_WAS_MEASURED_AT()
    {
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Unicode.Face));
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Unicode.Header));
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Unicode.Waiting));
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Unicode.Empty));
        Assert.Equal(7, UnicodeWidth.Of(GlyphSet.Unicode.Wild));

        Assert.Equal(9, UnicodeWidth.Of(GlyphSet.Ascii.Face));
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Ascii.Header));
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Ascii.Waiting));
        Assert.Equal(8, UnicodeWidth.Of(GlyphSet.Ascii.Empty));
        Assert.Equal(7, UnicodeWidth.Of(GlyphSet.Ascii.Wild));

        //assert the old 10-cell face too, so the new width reads as a measured change rather than a bare number.
        Assert.Equal(10, UnicodeWidth.Of("(=^\uff65\u03c9\uff65^)\u309d"));
    }
}
