using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Cli;

//a pipe cannot repaint, so the row is committed whenever the words change
public class LoadingLineTests
{
    private static readonly GlyphSet G = GlyphSet.Unicode;

    private static LoadingLine Piped(List<string> noted) =>
        new(rich: false, live: _ => throw new Xunit.Sdk.XunitException("a pipe must not repaint"),
            note: noted.Add, G, theme: null);

    private static LoadingLine Painted(List<string> live) =>
        new(rich: true, live: live.Add,
            note: _ => throw new Xunit.Sdk.XunitException("a live line must not commit"), G, theme: null);

    //assert the four texts in order, a count of four would pass on four wrong lines
    [Fact]
    public void THE_PIPE_PRINTS_ONE_LINE_PER_RUNG_AND_NO_MORE()
    {
        var noted = new List<string>();
        var row = Piped(noted);

        for (var second = 0; second <= 200; second++)
            row.Draw(TimeSpan.FromSeconds(second), "qwen3.6-35b-a3b", "~23.8 GB");

        Assert.Equal(
            ["loading qwen3.6-35b-a3b…",
             "still loading qwen3.6-35b-a3b…",
             "pumping qwen3.6-35b-a3b…",
             "still pumping qwen3.6-35b-a3b, ~23.8 GB…"],
            noted);
    }

    //thirty draws and one line, so the rung decides when a pipe hears anything
    [Fact]
    public void A_PIPE_HEARS_NOTHING_WHILE_THE_RUNG_HOLDS()
    {
        var noted = new List<string>();
        var row = Piped(noted);

        for (var second = 0; second < 30; second++)
            row.Draw(TimeSpan.FromSeconds(second), "m", null);

        Assert.Single(noted);
    }

    //a live host gets a row on every draw, the pulse and the timer are the motion
    [Fact]
    public void A_LIVE_LINE_REDRAWS_ON_EVERY_TICK()
    {
        var live = new List<string>();
        var row = Painted(live);

        for (var second = 0; second < 5; second++)
            row.Draw(TimeSpan.FromSeconds(second), "m", null);

        Assert.Equal(5, live.Count);
        Assert.All(live, l => Assert.Contains("loading m…", l, StringComparison.Ordinal));
    }

    //the pulse advances with the frame and not with the clock, so a row drawn twice in one second still moves
    [Fact]
    public void THE_PULSE_ADVANCES_PER_DRAW_NOT_PER_SECOND()
    {
        var live = new List<string>();
        var row = Painted(live);

        for (var i = 0; i < 3; i++) row.Draw(TimeSpan.Zero, "m", null);

        Assert.Equal(3, live.Distinct().Count());
    }
}
