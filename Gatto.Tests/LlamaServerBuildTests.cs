using Gatto.Core.Tools;
using Xunit;

namespace Gatto.Tests;

//the build number is read from the banner the probe already matches, so no extra probe call is needed
public class LlamaServerBuildTests
{
    //measured from the pinned engine's --version output, where releases from 10703 on name a package version, the build and the commit
    private const string PinnedBanner = "version: 0.4.1-dev (build 11071, commit 6ad1af560)\nbuilt with Clang 20.1.8 for Windows x86_64";

    //measured from release 10076, the last pin that printed the build and the commit alone.
    private const string BareBanner = "version: 10076 (305ba519a)\nbuilt with Clang 20.1.8 for Windows x86_64";

    //measured from a vendor mix build, which prints the current shape with its own commit.
    private const string MixBanner = "version: 0.4.0-dev (build 10909, commit 329b6160f)\nbuilt with Clang 20.1.8 for Windows x86_64";

    //measured from the fork engine's own --version output
    private const string ForkBanner = "version: 1 (2bd55d0)\nbuilt with MSVC 19.44.35228.0 for x64";

    [Fact]
    public void THE_PINNED_RELEASE_REPORTS_ITS_BUILD()
    {
        var probed = Probe(PinnedBanner);
        Assert.Equal(ProbeShape.ClassicServer, probed.Shape);
        Assert.Equal("11071", probed.Build);
    }

    //both banner shapes must identify the server, or a user gets told their server is the wrong program
    [Theory]
    [InlineData(BareBanner, "10076")]
    [InlineData(MixBanner, "10909")]
    public void EITHER_BANNER_SHAPE_REPORTS_ITS_BUILD(string banner, string build)
    {
        var probed = Probe(banner);
        Assert.Equal(ProbeShape.ClassicServer, probed.Shape);
        Assert.Equal(build, probed.Build);
    }

    //the detail is the banner line, since the pinned release writes a log line before it and doctor quotes the detail to the user
    [Fact]
    public void THE_DETAIL_QUOTES_THE_BANNER_LINE_NOT_THE_LOG_LINE_BEFORE_IT()
    {
        var probed = Probe("0.00.000.682 I srv  llama_server: initializing ...\n" + PinnedBanner);
        Assert.Equal(ProbeShape.ClassicServer, probed.Shape);
        Assert.Equal("version: 0.4.1-dev (build 11071, commit 6ad1af560)", probed.Detail);
    }

    //a banner that names no build must not report the leading digits of its version
    [Fact]
    public void A_PACKAGE_VERSION_ALONE_IS_NOT_A_BUILD() =>
        Assert.Equal(ProbeShape.NotClassic, Probe("version: 0.4.1-dev (commit 6ad1af560)\nbuilt with Clang 20.1.8 for Windows x86_64").Shape);

    //a real fork answers version: 1, a number indistinguishable from a first release, so the fork arm rests on provenance
    [Fact]
    public void A_REAL_FORK_REPORTS_A_MEANINGLESS_NUMBER_AND_NAMES_NO_FORK()
    {
        var probed = Probe(ForkBanner);

        Assert.Equal(ProbeShape.ClassicServer, probed.Shape);   //the fork is still the server gatto drives.
        Assert.Equal("1", probed.Build);
        Assert.DoesNotContain("laguna", probed.Build!, StringComparison.OrdinalIgnoreCase);
    }

    //every non-classic result holds no build, so the caller can tell "not our server" from "answered with an empty build"
    [Theory]
    [InlineData("some other program v3\nno banner here")]
    [InlineData("")]
    public void A_SHAPE_THAT_IS_NOT_THE_CLASSIC_SERVER_CARRIES_NO_BUILD(string banner) =>
        Assert.Null(Probe(banner).Build);

    //the capture group must not widen what the pattern matches, since a banner missing the built with line is still not classic
    [Fact]
    public void ADDING_THE_CAPTURE_DID_NOT_WIDEN_THE_CLASSIFICATION()
    {
        var noBuiltWith = Probe("version: 10076 (305ba519a)");
        Assert.Equal(ProbeShape.NotClassic, noBuiltWith.Shape);
        Assert.Null(noBuiltWith.Build);

        var currentNoBuiltWith = Probe("version: 0.4.1-dev (build 11071, commit 6ad1af560)");
        Assert.Equal(ProbeShape.NotClassic, currentNoBuiltWith.Shape);
        Assert.Null(currentNoBuiltWith.Build);
    }

    private static ProbeResult Probe(string stderr) =>
        LlamaServerProbe.Interpret(exitCode: 0, stdout: "", stderr: stderr,
            timedOut: false, vcRuntimeAbsent: false);

    //measured from the pinned engine's --list-devices on a machine with an 8060S and a Vega 64, CRLF and the log line included
    private const string Listing = "0.00.000.717 I srv  llama_server: initializing ...\r\nAvailable devices:\r\n"
        + "  Vulkan0: AMD Radeon(TM) 8060S Graphics (126805 MiB, 120465 MiB free)\r\n"
        + "  Vulkan1: Radeon RX Vega (8176 MiB, 7351 MiB free)\r\n";

    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics", "Vulkan0")]
    [InlineData("Radeon RX Vega", "Vulkan1")]
    [InlineData("AMD Radeon(TM) 8060S", null)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", null)]
    public void A_DEVICE_IS_FOUND_BY_ITS_EXACT_PRODUCT_NAME(string gpu, string? expected) =>
        Assert.Equal(expected, LlamaServerProbe.DeviceIn(Listing, gpu));
}
