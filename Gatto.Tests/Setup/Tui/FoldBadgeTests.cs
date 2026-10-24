using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the folded pane's tools line follows the wide pane's at the fold's indent, on a local row and a hub row alike, and an untested file has none
public class FoldBadgeTests
{
    private static string Fold(Badge? badge, string? sha256 = null, bool local = true)
    {
        var row = ShelfRows.Of("local/m", "local", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, sha256),
            FitRegime.FitsGpu, 32768, false, Badge: badge);
        return string.Join("\n", Pane.Fold(row, new ModelFacts(LocalPath: local ? @"D:\models" : null), MachineShape.Discrete, 70,
            focused: false, glyphs: GlyphSet.Unicode).Select(r => r.Text));
    }

    private static Badge Here(bool passed) =>
        new("m-Q4_K_M.gguf", null, passed, [], new DateOnly(2026, 8, 10), "abc1234", "temp 0.7");

    [Fact]
    public void A_LOCAL_ROW_PASSED_HERE_FOLDS_TO_ITS_TOOLS_LINE()
    {
        var text = Fold(Here(passed: true));

        Assert.Contains($"  tools · {GlyphSet.Unicode.PassedHere} {SearchRow.PassedHereWords}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_LOCAL_ROW_FAILED_HERE_FOLDS_TO_THE_FAILED_MARK_AND_NAMES_NO_TASK()
    {
        var text = Fold(Here(passed: false) with { FellShort = ["B1", "B2"] });

        Assert.Contains($"{GlyphSet.Unicode.FailedHere} {SearchRow.FailedHereWords}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fell short", text, StringComparison.Ordinal);
    }

    //a hub row carries the tested mark now, so the fold says so there too
    [Fact]
    public void A_HUB_ROW_FOLDS_TO_THE_TESTED_MARK()
    {
        var shipped = ShippedRows.Any(fellShort: true);
        var text = Fold(null, shipped.Sha256, local: false);

        Assert.Contains($"  tools · {GlyphSet.Unicode.Tested} {SearchRow.TestedWords}, fell short: "
            + ShippedRows.Labels(shipped.FellShort, GlyphSet.Unicode), text, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_UNTESTED_ROW_FOLDS_TO_NO_TOOLS_LINE()
    {
        var text = Fold(null);
        Assert.DoesNotContain("tools", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not measured", text, StringComparison.Ordinal);
    }
}
