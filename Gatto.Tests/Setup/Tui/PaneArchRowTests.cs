using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the shelf groups by architecture, and the pane is where a row's arch explains itself
public class PaneArchRowTests
{
    private const MachineShape Shape = MachineShape.UnifiedWithShare;

    private static ModelRow Row(string? arch) =>
        ShelfRows.Of("unsloth/GLM-4.7-Flash-GGUF", "unsloth",
            new HubQuant("GLM-4.7-Flash-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 131072, false, Badge: null, Downloads: 5, Gated: false,
            Params: 4_000_000_000, Arch: arch);

    private static IReadOnlyList<PaintedRow> Wide(ModelRow r) =>
        Pane.Rows(r, new ModelFacts(), Shape, 100, cursor: 0, focused: false,
            glyphs: null);

    private static IReadOnlyList<PaintedRow> Folded(ModelRow r, int width = 80) =>
        Pane.Fold(r, new ModelFacts(), Shape, width, focused: false);

    //the widths the ladder serves, the fold only appears below the pane's threshold
    public static TheoryData<int> Rungs() => [52, 72, 80, 120];

    //the 23B repo for width guards, its params clause is one cell wider and that cell decides 80 or 81
    private static ModelRow Widest() =>
        ShelfRows.Of("unsloth/GLM-4.7-Flash-REAP-23B-A3B-GGUF", "unsloth",
            new HubQuant("GLM-4.7-Flash-REAP-Q4_K_M.gguf", 13_000_000_000, null),
            FitRegime.FitsGpu, 131072, false, Badge: null, Downloads: 5, Gated: false,
            Params: 23_000_000_000, Arch: "deepseek2");

    //the two archs the Hub reports file as deepseek and glm, and glm4moe is what stops a one-row table passing
    [Fact]
    public void deepseek2_STILL_FILES_UNDER_deepseek_AND_glm4moe_UNDER_glm()
    {
        var families = Families.Load();

        Assert.Equal("deepseek", families.FamilyOf("deepseek2"));
        Assert.Equal("glm", families.FamilyOf("glm4moe"));
    }

    //the fold shows the arch on its own row, in the label and value shape files and builds use
    [Fact]
    public void THE_FOLD_SAYS_THE_ARCH_TOO_ON_A_ROW_OF_ITS_OWN()
    {
        var row = Assert.Single(Folded(Row("deepseek2")),
            r => r.Text.Contains("arch", StringComparison.Ordinal));

        Assert.Equal($"arch {Gatto.Terminal.GlyphSet.Unicode.Dot} deepseek2", row.Text.Trim());
        //the arch gets its own row, joined to the facts line it overflowed at two of the four widths
        Assert.DoesNotContain(Folded(Row("deepseek2")),
            r => r.Text.Contains("text only", StringComparison.Ordinal)
                 && r.Text.Contains("arch", StringComparison.Ordinal));
    }

    //the guard is the width, scoped to the arch row since the facts line overflows on its own at 52 and 60
    [Theory]
    [MemberData(nameof(Rungs))]
    public void THE_FOLDS_ARCH_ROW_FITS_EVERY_WIDTH_THE_LADDER_SERVES(int width)
    {
        var row = Assert.Single(
            Pane.Fold(Widest(), new ModelFacts(), Shape, width, focused: false),
            r => r.Text.Contains("arch", StringComparison.Ordinal));

        Assert.True(row.Text.Length <= width,
            $"the fold's arch row is {row.Text.Length} cells at {width}: {row.Text}");
    }

    //no arch fact without a reported arch, in either layout, and this negative only counts beside the two tests above
    [Fact]
    public void A_LISTING_WITH_NO_ARCH_DRAWS_NO_ARCH_FACT_IN_EITHER_LAYOUT()
    {
        Assert.DoesNotContain(Wide(Row(null)), r => r.Text.Contains("arch", StringComparison.Ordinal));
        Assert.DoesNotContain(Folded(Row(null)), r => r.Text.Contains("arch", StringComparison.Ordinal));

        //a blank general.architecture is the same as absent, so the check is for a length above zero
        Assert.DoesNotContain(Wide(Row("")), r => r.Text.Contains("arch", StringComparison.Ordinal));
    }
}
