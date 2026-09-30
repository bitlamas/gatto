//the harness's own oracles, every test built on RichReplHarness is meaningless when they fail. each run drives the whole rich loop, so it takes seconds
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

[Collection("e2e")]
public class RichReplHarnessSmokeTests
{
    [Fact]
    public async Task RichLoop_RunsHeadlessly_AndExitsCleanlyOnQuit()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/quit");

        var exit = await h.RunAsync();

        Assert.Equal(0, exit);
    }

    //this runs the join between the loop and the clock's pause, an unwired sink leaves every other guard green while the clock counts
    [Fact]
    public async Task RichLoop_ArmsTheModalWaitSink_SoTheTurnClockCanPause()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/quit");

        var exit = await h.RunAsync();

        Assert.Equal(0, exit);
        Assert.True(h.Pump.HasModalWaitSink, "the loop never wired the clock's pause to the pump");
    }

    [Fact]
    public async Task RichLoop_DispatchesAMultiLineScript_InOrder()
    {
        //a queued line must survive a composer rebuild. otherwise multi-step tests quietly run shorter scripts.
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/new").Line("/quit");

        var exit = await h.RunAsync();

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task RichLoop_PaintsThroughTheLiveRenderer_NotASeam()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/new").Line("/quit");

        await h.RunAsync();

        //assert the position of the rows, a row that is not replayed still flushes later below its slot
        var screen = h.ScreenText();
        var iBanner = screen.IndexOf("/quit exits", StringComparison.Ordinal);
        var iNew = screen.IndexOf("new conversation", StringComparison.Ordinal);
        Assert.True(iBanner >= 0, $"the banner never reached the frame:\n{screen}");
        Assert.True(iNew > iBanner, $"the rotation's line must follow the banner:\n{screen}");
    }
}
