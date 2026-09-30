using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the plain face's line must read the same widget as the framed one, so both answer progress alike (no golden covers this face)
public class PlainFaceProgressTests
{
    //each reading is distinct and none is round, so no two quantities can coincide and pass by accident.
    private static FetchTick Tick => new("m.gguf", 1, 2, Done: 419_430_400, Total: 1_048_576_000,
        ElapsedMs: 10_000);

    //the two figures must stay distinct, or a fetch at 40 percent reads as finished
    [Fact]
    public void THE_LINE_SAYS_WHAT_HAS_ARRIVED_NOT_THE_WHOLE_SIZE()
    {
        var t = Tick;
        var (done, total) = SizeWords.Pair(t.Done, t.Total);

        var line = Widget.Line(t, glyphs: GlyphSet.Unicode);

        Assert.Contains($"{done} of {total}", line, StringComparison.Ordinal);
        Assert.NotEqual(done, total);        //the fixture stays mid-fetch, or the assertion above proves nothing
    }

    //two faces answering progress differently is the defect the shared widget exists to prevent
    [Fact]
    public void AND_ITS_RATE_AND_REMAINDER_ARE_THE_FRAMED_FACES_OWN()
    {
        var t = Tick;

        var line = Widget.Line(t, glyphs: GlyphSet.Unicode);

        Assert.Contains(Widget.Rate(t), line, StringComparison.Ordinal);
        Assert.Contains(Widget.Remaining(t), line, StringComparison.Ordinal);
    }

    //every assertion above is a Contains, so a line stuffed with plausible figures would pass them all
    [Fact]
    public void AND_A_DIFFERENT_TICK_DRAWS_A_DIFFERENT_LINE()
    {
        var early = Tick with { Done = 104_857_600 };
        var (lateDone, _) = SizeWords.Pair(Tick.Done, Tick.Total);

        var line = Widget.Line(early, glyphs: GlyphSet.Unicode);

        Assert.NotEqual(Widget.Line(Tick, glyphs: GlyphSet.Unicode), line);
        Assert.DoesNotContain($"{lateDone} of ", line, StringComparison.Ordinal);
    }
}
