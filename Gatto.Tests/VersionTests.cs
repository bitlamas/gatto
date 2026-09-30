using Gatto.Core;
using Gatto.Terminal;

namespace Gatto.Tests;

//the 0.4.x strings in this file are fixtures for the banner formatters, and a version bump leaves them alone
public class VersionTests
{
    //a literal on purpose, nothing here reads the csproj, and a version bump updates it in the same commit
    [Fact]
    public void Version_IsTheCsprojVersion() => Assert.Equal("0.5.3-rc", GattoVersion.String);

    //the build stamp is always a commit sha, and this test is the only one that fails if the csproj's --exclude=* is dropped
    [Fact]
    public void The_BUILD_STAMP_IS_A_SHA_NEVER_A_TAG_NAME()
    {
        var build = GattoVersion.Build;
        //empty is legitimate: built from a source zip, or a CI checkout with no git directory
        if (build.Length == 0) return;

        //keep one assertion, [0-9a-f] cannot match a v so a DoesNotContain("v") would only look like coverage
        Assert.Matches("^[0-9a-f]{7}(-dirty)?$", build);
    }

    //feeds the parse rule straight in, so this check runs even where the guard above cannot
    [Theory]
    //a real describe answer, clean and dirty
    [InlineData("0.4.0+d9b2860", "d9b2860")]
    [InlineData("0.4.0+d9b2860-dirty", "d9b2860-dirty")]
    //the tag-bearing shape, in case --exclude=* is ever dropped
    [InlineData("0.4.0+v0.3.4-12-gd9b2860", "d9b2860")]
    //a tag name and a git error message both parse to nothing
    [InlineData("0.4.0+v0.4.0", "")]
    [InlineData("0.4.0+fatal: bad revision 'HEAD'", "")]
    [InlineData("0.4.0+fatal: not a git repository", "")]
    //no metadata at all
    [InlineData("0.4.0", "")]
    [InlineData("", "")]
    public void THE_STAMP_IS_A_SHA_OR_IT_IS_NOTHING(string informational, string expected)
        => Assert.Equal(expected, GattoVersion.ParseStamp(informational));

    [Fact]
    public void ArgRouter_VersionFlag_RoutesToVersionCommand()
    {
        var args = Gatto.Cli.ArgRouter.Parse(new[] { "--version" });
        Assert.Equal("version", args.Command);
    }

    //the dev-build marker

    //each call site adds its own frame, --version keeps the parentheses the release gate checks
    [Theory]
    [InlineData("b4c778b", true, "build b4c778b 👾")]
    [InlineData("b4c778b", false, "build b4c778b")]
    //no sha means no marker, even on a dev build
    [InlineData("", true, "")]
    [InlineData("", false, "")]
    public void BUILD_TAIL_IS_THE_STAMP_ONLY(string build, bool dev, string expected)
        => Assert.Equal(expected, Gatto.Core.GattoVersion.BuildTail(build, dev));

    //the release gate's regex needs the parenthesised form, only the --version line produces it
    [Fact]
    public void A_RELEASE_VERSION_LINE_SATISFIES_THE_GATES_OWN_REGEX()
    {
        var line = Gatto.Cli.CommandBanner.VersionLine("0.4.1", "b4c778b", dev: false);

        Assert.Matches(@"\(build [0-9a-f]{7}\)", line);
        Assert.Equal("gatto 0.4.1 (build b4c778b)", line);
    }

    //the 👾 sits between the sha and the closing bracket, so the regex no longer matches
    [Fact]
    public void A_DEV_VERSION_LINE_IS_REFUSED_BY_THE_GATES_OWN_REGEX()
    {
        var line = Gatto.Cli.CommandBanner.VersionLine("0.4.1", "b4c778b", dev: true);

        Assert.Contains("👾", line, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\(build [0-9a-f]{7}\)", line);
    }

    //no sha keeps the bare form, empty parentheses would read as a bug
    [Fact]
    public void A_VERSION_LINE_WITH_NO_STAMP_CARRIES_NO_EMPTY_BRACKETS()
        => Assert.Equal("gatto 0.4.1", Gatto.Cli.CommandBanner.VersionLine("0.4.1", "", dev: false));

    //the banner marker must be BMP, char.IsSurrogate refuses the astral 👾 that --version keeps
    [Fact]
    public void THE_BANNER_WEARS_A_BMP_MARKER_and_version_keeps_the_astral_one()
    {
        var banner = Gatto.Cli.CommandBanner.DottedVersion("0.4.1", "b4c778b", dev: true, glyphs: GlyphSet.Unicode);
        var version = Gatto.Cli.CommandBanner.VersionLine("0.4.1", "b4c778b", dev: true);

        Assert.Equal("v0.4.1 · build b4c778b ✦", banner);
        Assert.DoesNotContain(banner, char.IsSurrogate);   //the banner's BMP rule is asserted here too, at the glyph's own source

        Assert.Equal("gatto 0.4.1 (build b4c778b 👾)", version);
    }

    [Fact]
    public void A_RELEASE_BUILD_WEARS_NO_MARKER_AT_EITHER_SITE()
    {
        Assert.Equal("v0.4.1 · build b4c778b", Gatto.Cli.CommandBanner.DottedVersion("0.4.1", "b4c778b", dev: false, glyphs: GlyphSet.Unicode));
        Assert.Equal("gatto 0.4.1 (build b4c778b)", Gatto.Cli.CommandBanner.VersionLine("0.4.1", "b4c778b", dev: false));
    }
}
