//assert the rendered frame and its row order. an unreplayed hold still flushes when its using scope disposes, the defect moves a row rather than dropping it
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

[Collection("e2e")]
public class MemoryRichSurfaceTests
{
    [Fact]
    public async Task AWarningRaisedDuringTheBoundaryRecompose_SurvivesTheRotation_AndIsVISIBLE()
    {
        //the /new path recomposes and warns, then rotates and resets the display model
        var h = new RichReplHarness(Path.GetTempPath());
        var warned = false;
        h.Recompose = () =>
        {
            if (!warned) { warned = true; h.Warn.Warn("memory index over budget — 7 facts not shown; prune .gatto\\memory\\"); }
            return "sys";
        };
        h.Keys.Line("/new").Line("/quit");

        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.Contains("7 facts not shown", screen);          //the payload survived intact.
        //assert the order, an unreplayed hold still flushes when its using scope disposes and shows below the confirmation
        var iWarn = screen.IndexOf("memory index over budget", StringComparison.Ordinal);
        var iNew = screen.IndexOf("new conversation", StringComparison.Ordinal);
        Assert.True(iWarn >= 0, $"the warning never reached the frame:\n{screen}");
        Assert.True(iNew > iWarn, $"the warning must precede the rotation's confirmation:\n{screen}");
    }

    [Fact]
    public async Task TheWarningLandsBesideTheConfirmation_NotInsteadOfIt()
    {
        //assert both rows and their order, the recompose warning precedes the rotation's own line
        var h = new RichReplHarness(Path.GetTempPath());
        var warned = false;
        h.Recompose = () =>
        {
            if (!warned) { warned = true; h.Warn.Warn("context files not re-read — .gatto.json is not valid JSON"); }
            return "sys";
        };
        h.Keys.Line("/new").Line("/quit");

        await h.RunAsync();

        var screen = h.ScreenText();
        var iWarn = screen.IndexOf("context files not re-read", StringComparison.Ordinal);
        var iNew = screen.IndexOf("new conversation", StringComparison.Ordinal);
        Assert.True(iWarn >= 0, $"the degrade warning never reached the frame:\n{screen}");
        Assert.True(iNew > iWarn, $"the confirmation should follow the warning:\n{screen}");
    }

    //the harness Warnings list stays empty by design, the rich loop routes the sink to the renderer for the whole session
}
