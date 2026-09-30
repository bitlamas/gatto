using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the badge row draws one ok mark, asserted on the composed row since the fragment cannot see what the pane adds
public class PaneBadgeRowTests
{
    private const MachineShape Shape = MachineShape.UnifiedWithShare;

    private static ShelfRow Row(Badge? badge) =>
        new("qwen/qwen3.5-4b", "qwen", new HubQuant("qwen3.5-4b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: badge, Downloads: 5, Gated: false,
            Params: 4_000_000_000);

    private static Badge Verified =>
        new("qwen/qwen3.5-4b", new DateOnly(2026, 8, 1), "b", "greedy", Passed: 5, Ran: 5);

    private static int OkGlyphs(string s) =>
        s.Split(Gatto.Terminal.GlyphSet.Unicode.Ok, StringSplitOptions.None).Length - 1;

    [Fact]
    public void THE_BADGE_ROW_CARRIES_ONE_OK_GLYPH()
    {
        var row = Pane.Rows(Row(Verified), new ModelFacts(), Shape, 100,
                cursor: 0, focused: false, build: -1, focus: Region.List)
            .Single(r => r.Text.Contains("tool-calling verified", StringComparison.Ordinal));

        Assert.Equal(1, OkGlyphs(row.Text));
    }

    //the sentence survives the split, it is why the badge is words a reader can check
    [Fact]
    public void THE_BADGE_ROW_STILL_SAYS_WHAT_WAS_MEASURED_AND_WHEN()
    {
        var row = Pane.Rows(Row(Verified), new ModelFacts(), Shape, 100,
                cursor: 0, focused: false, build: -1, focus: Region.List)
            .Single(r => r.Text.Contains("tool-calling verified", StringComparison.Ordinal));

        Assert.Contains("tool-calling verified 2026-08", row.Text, StringComparison.Ordinal);
        //one space after the mark, slicing the glyph off would leave a doubled space behind
        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Ok + " tool-calling", row.Text, StringComparison.Ordinal);
    }

    //an unmeasured row draws the other mark and no ok glyph, a tick for not measured would read as an endorsement
    [Fact]
    public void A_ROW_WITH_NO_BADGE_CARRIES_NO_OK_GLYPH_ON_ITS_BADGE_ROW()
    {
        var row = Pane.Rows(Row(null), new ModelFacts(), Shape, 100,
                cursor: 0, focused: false, build: -1, focus: Region.List)
            .Single(r => r.Text.Contains("not measured", StringComparison.Ordinal));

        Assert.Equal(0, OkGlyphs(row.Text));
    }
}
