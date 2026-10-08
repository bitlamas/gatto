using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the files heading appears only when there are file rows, since a bare files over empty space promises a list that is not coming
public class PaneFilesHeadingTests
{
    private const MachineShape Shape = MachineShape.UnifiedWithShare;

    private static ModelRow Row() =>
        ShelfRows.Of("qwen/qwen3.5-4b", "qwen", new HubQuant("qwen3.5-4b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 4_000_000_000);

    //a hub row whose tree listed no quants, so FactsFor leaves Files and FileCount null and sets no LocalPath
    private static ModelFacts AHubRowWhoseTreeListedNoQuants() =>
        new(Structure: "dense", Experts: null, Have: HaveMark.None, HaveId: null);

    private static ModelFacts WithFiles() =>
        new(Structure: "dense", FileCount: 2, Files: [
            new PaneFile("Q4_K_M", 3_000_000_000, FitRegime.FitsGpu),
            new PaneFile("Q6_K", 5_000_000_000, FitRegime.FitsGpu)]);

    private static List<string> Expanded(ModelFacts f, int width = 100) =>
        [.. Pane.Rows(Row(), f, Shape, width, cursor: 0, focused: false,
            glyphs: null).Select(r => r.Text.TrimEnd())];

    //site 1: the expanded pane (Pane.Rows)

    //no rows means no heading and no sentence in its place, since nothing was read or refused and there is no state to name
    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    public void THE_EXPANDED_PANE_DRAWS_NO_FILES_HEADING_WHEN_THERE_ARE_NO_FILE_ROWS(int width)
    {
        var rows = Expanded(AHubRowWhoseTreeListedNoQuants(), width);

        Assert.DoesNotContain(rows, r => r.TrimStart().StartsWith("files", StringComparison.Ordinal));
    }

    //site 2: the fold (Pane.Fold)

    //the fold's files line always has something after the heading, the row's own picked quant when there is no file list
    [Fact]
    public void THE_FOLD_ALWAYS_HAS_CONTENT_AFTER_ITS_FILES_HEADING()
    {
        var rows = Pane.Fold(Row(), AHubRowWhoseTreeListedNoQuants(), Shape, 100, focused: false)
            .Select(r => r.Text.TrimEnd()).ToList();

        var line = Assert.Single(rows, r => r.Contains("files ·", StringComparison.Ordinal));
        //the row's own picked quant, which is what the fold falls back to with no files
        Assert.Contains("Q4_K_M", line, StringComparison.Ordinal);
    }
}
