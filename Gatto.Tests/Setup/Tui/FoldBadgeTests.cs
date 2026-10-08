using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the folded pane's last row follows the wide pane: a measured local row shows its badge, an unmeasured one says so
public class FoldBadgeTests
{
    private static string Fold(Badge? badge)
    {
        var row = ShelfRows.Of("local/m", "local", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 32768, false, Badge: badge);
        return string.Join("\n", Pane.Fold(row, new ModelFacts(LocalPath: @"D:\models"), MachineShape.Discrete, 70,
            focused: false, glyphs: GlyphSet.Unicode).Select(r => r.Text));
    }

    [Fact]
    public void A_BADGED_LOCAL_ROW_FOLDS_TO_ITS_BADGE()
    {
        var text = Fold(new Badge("local/m", new DateOnly(2026, 8, 10), "abc1234", "temp 0.7"));

        Assert.Contains("✓ " + SearchRow.BadgeWordsBare(new Badge("local/m", new DateOnly(2026, 8, 10), "abc1234", "temp 0.7")),
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("✗", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_UNBADGED_LOCAL_ROW_STILL_SAYS_NOT_MEASURED() =>
        Assert.Contains("✗ not measured yet", Fold(null), StringComparison.Ordinal);
}
