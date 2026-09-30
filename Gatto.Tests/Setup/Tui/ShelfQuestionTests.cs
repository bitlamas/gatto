using Gatto.Cli.Setup;
using Gatto.Core.Hardware;

namespace Gatto.Tests.Setup.Tui;

//a populated shelf must show the path's question, so drive real runs and locate rows by content rather than by index
public class ShelfQuestionTests
{
    //a unified machine hides the share from firmware, so the gap between the two tracks vram and that is the classifier's unified signal
    private static HardwareSnapshot Unified => new(103079215104, 34359738368, GpuKind.Integrated, 68719476736);

    private static IReadOnlyList<string> Painted(bool inSession)
    {
        var flow = new SetupFlow(ModelFetchTests.ShelfProbes(Unified));
        var shelf = Assert.IsType<WizardScreen.Choice>(
            inSession ? flow.StartAtModelSegment() : flow.StartPastEngine());
        Assert.NotNull(shelf.Shelf);
        Assert.NotEmpty(shelf.Shelf!.Rows);
        return WalkRender.Choice(shelf, 100).Rows;
    }

    //locate the question and its blank row by content, since a fixed index is right on one path and wrong on the other
    private static (string Question, string Blank) Pair(IReadOnlyList<string> rows, string where)
    {
        var i = -1;
        for (var n = 0; n < rows.Count; n++)
            if (rows[n].TrimStart().StartsWith("Which model should gatto", StringComparison.Ordinal))
            {
                i = n;
                break;
            }

        Assert.True(i >= 0, $"{where} draws no question over its shelf at all");
        Assert.True(i + 1 < rows.Count, $"{where} has no row under its question");
        return (rows[i], rows[i + 1]);
    }

    //check each path against its own golden, whose text came from the mockups rather than from this code
    [Theory]
    [InlineData(false, "s5", "unified-96-hub-found", 100)]
    [InlineData(true, "s10", "shelf", 100)]
    public void A_POPULATED_SHELF_CARRIES_THE_ROADS_QUESTION(
        bool inSession, string generator, string screen, int width)
    {
        var drawn = Pair(Golden.Panel(generator, screen, width), $"the {generator} frame");
        var painted = Pair(Painted(inSession), inSession ? "the in-session shelf" : "the setup shelf");

        Assert.Equal(drawn.Question, painted.Question);
        Assert.Equal("", painted.Blank.Trim());
        Assert.Equal(drawn.Blank.Trim(), painted.Blank.Trim());
    }
}
