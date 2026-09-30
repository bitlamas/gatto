using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the rate divides only the bytes moved during this sitting by this sitting's clock, and the byte figure stays cumulative
public class ResumedRateTests
{
    private const long Gib = 1024L * 1024 * 1024;

    //10 MB moved in this sitting's one second, with 7.9 GB already on disk
    private static FetchTick Resumed(long resumedBytes) =>
        new("m.gguf", 1, 1, resumedBytes + (10L * 1024 * 1024), (long)(16.9 * Gib), 1_000,
            Resumed: resumedBytes);

    [Fact]
    public void A_RESUMED_FETCH_RATES_THIS_SITTING_NOT_THE_WHOLE_FILE() =>
        Assert.Equal("10.0 MB/s", Widget.Rate(Resumed((long)(7.9 * Gib))));

    //the remaining time comes from the same rate, so at about 9 GB away it reads in minutes rather than ~0 s left
    [Fact]
    public void AND_THE_REMAINDER_FOLLOWS_THE_SAME_RATE()
    {
        var left = Widget.Remaining(Resumed((long)(7.9 * Gib)));

        Assert.EndsWith(" min left", left, StringComparison.Ordinal);
        Assert.DoesNotContain("~0 ", left, StringComparison.Ordinal);
    }

    //this case is a control, since it proves the two tests above fail on resuming rather than on the arithmetic
    [Fact]
    public void A_FETCH_THAT_RESUMED_NOTHING_IS_UNCHANGED()
    {
        var fresh = new FetchTick("m.gguf", 1, 1, 10L * 1024 * 1024, (long)(16.9 * Gib), 1_000);

        Assert.Equal("10.0 MB/s", Widget.Rate(fresh));
        Assert.Equal(Widget.Rate(fresh), Widget.Rate(Resumed(0)));
        Assert.Equal(Widget.Remaining(fresh), Widget.Remaining(Resumed(0)));
    }

    //assert the whole row, since it is the only check that sees the byte figure and the rate disagree
    [Fact]
    public void THE_ROW_SAYS_WHAT_IS_ON_DISK_AND_WHAT_IS_MOVING()
    {
        var line = Widget.Line(Resumed((long)(7.9 * Gib)), glyphs: GlyphSet.Unicode);

        Assert.Contains("7.9 of 16.9 GB", line, StringComparison.Ordinal);
        Assert.Contains("10.0 MB/s", line, StringComparison.Ordinal);
        Assert.DoesNotContain("~0 s left", line, StringComparison.Ordinal);
    }
}
