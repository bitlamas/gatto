using Gatto.Cli;

namespace Gatto.Tests;

//the accepted shape is v?N.N.N with only the running suffix stripped, and the comparison stays ordered so an older republished tag is no update
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.4.1", 0, 4, 1)]
    [InlineData("v0.4.1", 0, 4, 1)]
    [InlineData("V10.0.0", 10, 0, 0)]
    public void PARSES_EXACTLY_THREE_NUMBERS_with_an_optional_v(string s, int a, int b, int c)
    {
        Assert.True(ReleaseVersion.TryParse(s, out var v));
        Assert.Equal(new ReleaseVersion(a, b, c), v);
    }

    //the running-only parse strips from the first dash, then the strict parse runs underneath.
    [Theory]
    [InlineData("0.5.3-rc", 0, 5, 3)]
    [InlineData("0.4.1-beta", 0, 4, 1)]
    [InlineData("v0.4.1-rc1", 0, 4, 1)]
    public void RUNNING_STRIPS_A_SUFFIX_THAT_THE_STRICT_PARSE_STILL_REFUSES(string s, int a, int b, int c)
    {
        Assert.False(ReleaseVersion.TryParse(s, out _));
        Assert.True(ReleaseVersion.TryParseRunning(s, out var v));
        Assert.Equal(new ReleaseVersion(a, b, c), v);
    }

    //a running build with a suffix compares on its numbers alone, so doctor calls it ahead of the release
    [Fact]
    public void A_SUFFIXED_RUNNING_VERSION_COMPARES_ON_ITS_NUMBERS_ALONE()
    {
        Assert.True(ReleaseVersion.TryParseRunning("0.5.3-rc", out var mine));
        Assert.True(ReleaseVersion.TryParse("0.5.2", out var released));

        Assert.True(mine.IsNewerThan(released));
    }

    //a remote tag stays a candidate gatto update could act on, so it never gets the running-only strip.
    [Fact]
    public void A_SUFFIXED_TAG_IS_NOT_SOMETHING_TRYPARSERUNNING_HELPS_WITH()
        => Assert.False(ReleaseVersion.TryParse("v0.5.3-rc1", out _));

    //build metadata must stay refused, the caller strips at the plus and that strip has one home so don't widen the parser
    [Theory]
    [InlineData("0.4")]
    [InlineData("0.4.1.0")]
    [InlineData("0.4.1-beta")]
    [InlineData("0.4.1+b4c778b")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" 0.4.1")]
    [InlineData("0.4.1 ")]
    //the digits are unbounded in the pattern and bounded in the type, so a plain int.Parse would throw inside a try-parse
    [InlineData("99999999999.0.0")]
    //arabic-indic digits are refused by int.TryParse, so this row shows the outcome and proves nothing about the digit class in the pattern
    [InlineData("٤.٠.١")]
    public void ANYTHING_ELSE_IS_UNPARSEABLE_and_that_is_the_rule_not_a_gap(string? s)
        => Assert.False(ReleaseVersion.TryParse(s, out _));

    //0.10.0 must beat 0.9.9, and string order would invert that so gatto offers to update a newer install down
    [Fact]
    public void ORDER_IS_NUMERIC_not_lexical()
    {
        Assert.True(ReleaseVersion.TryParse("0.10.0", out var ten));
        Assert.True(ReleaseVersion.TryParse("0.9.9", out var nine));
        Assert.True(ten.IsNewerThan(nine));
        Assert.False(nine.IsNewerThan(ten));
        Assert.False(ten.IsNewerThan(ten));   //equal must not count as newer, the caller reads it as already current
    }

    //drops the tag's leading v so both versions read in one style, and nothing compares on this text

    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.4.0", "0.4.0")]
    public void Bare_drops_a_leading_v_from_a_tag(string tag, string expected)
        => Assert.Equal(expected, ReleaseVersion.Bare(tag));

    [Theory]
    [InlineData("version-2", "version-2")]   //the leading v drops only when a digit follows it
    [InlineData("v", "v")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Bare_returns_anything_that_is_not_a_tag_as_it_arrived(string? text, string expected)
        => Assert.Equal(expected, ReleaseVersion.Bare(text));

    [Fact]
    public void The_refusals_two_versions_read_as_one_kind_of_thing()
    {
        //after Bare neither version keeps a prefix the other lacks
        Assert.Equal("0.4.0", ReleaseVersion.Bare("v0.4.0"));
        Assert.DoesNotContain("v", ReleaseVersion.Bare("v0.4.0"), StringComparison.Ordinal);
    }
}
