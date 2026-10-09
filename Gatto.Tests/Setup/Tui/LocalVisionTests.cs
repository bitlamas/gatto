using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests.Setup.Tui;

//a local row has no reading of whether its model sees images, so its pane says nothing about vision rather than claim text only
public class LocalVisionTests
{
    private const MachineShape Shape = MachineShape.UnifiedWithShare;

    private static ModelRow Row() =>
        ShelfRows.Of("unsloth/Qwen3.5-9B-GGUF", "unsloth",
            new HubQuant("Qwen3.5-9B-IQ4_XS.gguf", 4_800_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 9_000_000_000, Arch: "qwen35");

    private static readonly ModelFacts Local = new() { LocalPath = @"C:\weights\qwen3.5-9b\", FilesHere = "1 file" };

    private static bool SaysVision(IEnumerable<Gatto.Terminal.PaintedRow> rows) =>
        rows.Any(r => r.Text.Contains("text only", StringComparison.Ordinal) || r.Text.Contains("vision", StringComparison.Ordinal));

    [Fact]
    public void A_LOCAL_ROWS_PANE_SAYS_NOTHING_ABOUT_VISION_IN_EITHER_LAYOUT()
    {
        Assert.False(SaysVision(Pane.Rows(Row(), Local, Shape, 100, cursor: 0, focused: false, glyphs: null)));
        Assert.False(SaysVision(Pane.Fold(Row(), Local, Shape, 80, focused: false)));
    }

    //the twin: a Hub row read its vision from the repo's files, so its folded pane keeps the fact
    [Fact]
    public void A_HUB_ROWS_FOLDED_PANE_STILL_SAYS_TEXT_ONLY()
    {
        Assert.True(SaysVision(Pane.Fold(Row(), new ModelFacts(), Shape, 80, focused: false)));
    }
}
