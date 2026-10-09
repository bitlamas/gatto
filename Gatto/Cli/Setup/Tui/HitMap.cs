using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//what a press on a span does. a pane line names its publisher and its file, a regime jump the regimes it counts
internal enum HitKind { Row, PaneLine, PaneFile, FoldedFile, Chip, Door, ParamsHeader, Clause, RegimeJump, EscapeRow, OptionRow, FooterKey }

internal sealed record HitTag(HitKind Kind, int Index = -1, int File = -1, string? Answer = null,
    string? Key = null, IReadOnlySet<FitRegime>? Regimes = null, Region? Area = null);

//one clickable span in display cells, 0-based like the console's mouse coordinates
internal readonly record struct HitTarget(int Row, int FirstCol, int LastCol, HitTag Tag);

internal sealed record HitMap(IReadOnlyList<HitTarget> Targets, int Width, int Height)
{
    public static readonly HitMap None = new([], 0, 0);

    //computed once from the rows the frame painted, by summing display widths run by run, so no layout arithmetic can move a target off its text
    internal static HitMap Of(IReadOnlyList<PaintedRow> painted, int width, int height)
    {
        //a frame taller than the terminal scrolled it by the excess, so the user sees each row that much higher
        var excess = height > 0 ? Math.Max(0, painted.Count - height) : 0;
        var targets = new List<HitTarget>();
        for (var r = 0; r < painted.Count; r++)
        {
            var y = r - excess;
            var col = 0;
            HitTarget? open = null;
            foreach (var run in painted[r].Runs)
            {
                var w = UnicodeWidth.Of(run.Text);
                //an empty run takes no cells, so it can neither end a span nor start one
                if (w == 0) continue;
                var tag = TagOf(run.Tag);
                if (open is { } o && !o.Tag.Equals(tag))
                {
                    if (y >= 0) targets.Add(o);
                    open = null;
                }
                if (tag is not null)
                    open = open is { } same ? same with { LastCol = col + w - 1 } : new HitTarget(y, col, col + w - 1, tag);
                col += w;
            }
            if (open is { } last && y >= 0) targets.Add(last);
        }
        return new HitMap(targets, width, height);
    }

    //a footer run carries its key as a string, since the footer is composed below Cli and knows no HitTag
    private static HitTag? TagOf(object? tag) => tag switch
    {
        HitTag t => t,
        string key => new HitTag(HitKind.FooterKey, Key: key),
        _ => null,
    };

    public HitTarget? At(int x, int y)
    {
        foreach (var t in Targets)
            if (t.Row == y && x >= t.FirstCol && x <= t.LastCol) return t;
        return null;
    }
}
