using Gatto.Core.Hardware;
using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Cli.Setup;

//the row's words (name, size, fit, badge, context) and nothing more, and the badge says what was measured rather than recommending
internal static class SearchRow
{
    public static string Label(ShelfRow r) => $"{r.RepoId}";

    //keep Gb, FitWords, BadgeWordsBare and Ctx here, the table, the pane and the tier line all call them

    //the badge words are a measurement, so they never say "recommended" or a bare "tested". the pane draws the tick itself, so these words come without a glyph
    internal static string BadgeWordsBare(Gatto.Core.Acquire.Badge b) =>
        $"tool-calling verified {b.Measured:yyyy-MM}{Margin(b)}";

    //the score appears only when a task is missing, as "4 of 5 tasks" (a bare fraction reads as a rating). a task the run cut short says unfinished
    private static string Margin(Gatto.Core.Acquire.Badge b)
    {
        if (b.Ran <= 0 || b.Passed >= b.Ran) return "";
        var tail = b.Unfinished switch
        {
            0 => "",
            1 => ", one unfinished",
            var n => $", {n} unfinished",
        };
        return $" ({b.Passed} of {b.Ran} tasks{tail})";
    }

    //fit in words, said as a computation, the estimate errs conservative and the words must not promise more than the arithmetic knows

    //the words name where the weights sit, since fit arithmetic can't speak for speed, and "slower" needs a faster tier to exist
    internal static string FitWords(FitRegime fit, MachineShape shape, Gatto.Terminal.GlyphSet g) => fit switch
    {
        FitRegime.FitsGpu => shape switch
        {
            MachineShape.Discrete => "fits on the graphics card",
            MachineShape.UnifiedWithShare => "fits in the graphics share",
            _ => "fits in memory",
        },
        FitRegime.FitsRamOnly => shape switch
        {
            MachineShape.Discrete => $"fits in memory {g.Dot} slower",
            MachineShape.UnifiedWithShare => $"fits in system memory {g.Dot} slower",
            _ => "fits in memory",
        },
        FitRegime.DoesNotFit => "too big for this machine",
        _ => "size not known",
    };

    //the shelf's size column and the pane show one figure, so both go through SizeWords.Gb
    internal static string Gb(long bytes) =>
        Gatto.Cli.SizeWords.Gb(bytes);

    //the context window as digits with comma groups from the invariant culture ("32 768" reads as a typo). the table's column and the pane both read it here
    internal static string Ctx(long tokens) =>
        tokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
