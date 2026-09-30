using Gatto.Core.Acquire;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the one live widget, pure composition with no clock, since the face passes in whatever the moment holds
internal static class Widget
{
    //repeat the whole member, since a glyph can be more than one char
    private static string Repeat(string mark, int n) =>
        n <= 0 ? "" : string.Concat(Enumerable.Repeat(mark, n));

    //the bar's cells at the two drawn widths, folded at the same point EngineFetchView uses
    private const int Cells = 30, CompactCells = 24, CompactBelow = 90;

    //floor the fraction, since a rounded bar fills its last cell early and looks finished while the fetch runs
    internal static int Filled(FetchTick t, int cells) =>
        t.Total <= 0 ? 0 : (int)Math.Min(cells, Math.Max(0, t.Done) * cells / t.Total);

    internal static int BarCells(int width) => width < CompactBelow ? CompactCells : Cells;

    //the bar: an accented run of what's arrived, a dim run of what hasn't
    internal static string Bar(FetchTick t, int width, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var cells = BarCells(width);
        var filled = Filled(t, cells);
        //repeat the whole member (one character of a longer twin would draw a third of it)
        return Repeat(g.HeavyRule, filled) + Repeat(g.Rule, cells - filled);
    }

    //the rate is this sitting's bytes over this sitting's clock, always MB/s (otherwise a resume prices the whole file against a fresh stopwatch)
    internal static string Rate(FetchTick t) =>
        t.ElapsedMs <= 0 || Moved(t) <= 0
            ? "0.0 MB/s"
            : (Moved(t) / (1024.0 * 1024) / (t.ElapsedMs / 1000.0))
                .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB/s";

    //what this sitting has moved, the cumulative figure less what was already on disk when it began
    private static long Moved(FetchTick t) => Math.Max(0, t.Done) - Math.Max(0, t.Resumed);

    //the remainder comes from the same two numbers as the rate, so the two can't disagree
    internal static string Remaining(FetchTick t)
    {
        //the remainder counts the whole file, the rate dividing into it counts this sitting (mixing the two prices it at ~0 s left)
        var left = Math.Max(0, t.Total - Math.Max(0, t.Done));
        if (t.ElapsedMs <= 0 || Moved(t) <= 0 || left == 0) return "";
        var seconds = left / (Moved(t) / (t.ElapsedMs / 1000.0));
        return seconds < 60
            ? $"~{Math.Round(seconds)} s left"
            : $"~{Math.Round(seconds / 60)} min left";
    }

    //call ChromeTicker.PurrHead rather than spelling the frames here (they pad to the widest in their set, so the timer beside them stays still)
    internal static string Purr(FetchTick t, Gatto.Terminal.GlyphSet? glyphs) =>
        Purr(t.ElapsedMs, PurrFrames.Short, glyphs);

    //same purr from a bare elapsed, the browser watch has no fetch and so no tick
    internal static string Purr(long elapsedMs, Gatto.Terminal.GlyphSet? glyphs) =>
        Purr(elapsedMs, PurrFrames.Short, glyphs);

    //pick the frame set by how long the wait can run (a download can go for hours)
    internal static string Purr(long elapsedMs, PurrFrames frames,
        Gatto.Terminal.GlyphSet? glyphs) =>
        ChromeTicker.PurrHead(Gatto.Repl.Cats.Face(glyphs), Math.Max(0, elapsedMs), frames);

    //the purr in the past tense, spelled by ChromeTicker.FormatElapsed like the REPL (no token tail, a download has no tokens)
    internal static string Settled(long elapsedMs, Gatto.Terminal.GlyphSet? glyphs) =>
        Gatto.Repl.Cats.Face(glyphs) + " purred for "
        + ChromeTicker.FormatElapsed(Math.Max(0, elapsedMs));

//on the drop screen the fetch has already stopped, so Esc only costs the bytes
    internal static string CostOfKept(FetchTick t) =>
        $"Esc again: deletes the {SizeWords.Auto(Math.Max(0, t.Done))} already here";

    //priced off the same t.Done the bar drew, so the two can't disagree
    internal static string Cost(FetchTick t) =>
        $"Esc again: stops the fetch and deletes the {SizeWords.Auto(Math.Max(0, t.Done))} already here";

    //the whole widget on one line, for a face that has no frame to put rows in
    public static string Line(FetchTick t, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var (done, total) = SizeWords.Pair(t.Done, t.Total);
        var tail = string.Join($" {g.Dot} ", new[] { Rate(t), Remaining(t) }.Where(s => s.Length > 0));
        return $"{Purr(t, g)}  {Bar(t, CompactBelow, g)}  {done} of {total} {g.Dot} {tail}";
    }

    //the then row names the CUDA pair's other half so nobody closes the wizard on one file
    public static IReadOnlyList<WizardRow> Rows(EngineView v, FetchTick t, int width,
        GlyphSet? glyphs) =>
        Rows(glyphs ?? GlyphSet.Unicode, t, width, SetupFlow.EngineFacts.Column, v.Into,
            //the engine's companion belongs to the same fetch, so it's announced only while the tick says one is still coming
            t.Index < t.Count && v.WithName is { Length: > 0 } next
                ? $"{next} {(glyphs ?? GlyphSet.Unicode).Dot} {v.WithSize}"
                : null);

    //the marker counts the files of the unit in flight, the then row announces the next unit. the engine fetch has no pause key, so only this overload takes paused
    public static IReadOnlyList<WizardRow> Rows(ModelView v, FetchTick t, int width,
        GlyphSet glyphs, bool paused = false) =>
        Rows(glyphs, t, width, SetupFlow.ModelFacts.Column, v.Into,
            v.Next(t) is { } next
                ? $"{next.Name} {(glyphs ?? GlyphSet.Unicode).Dot} {next.Size}"
                : null, paused);

    //one composer for both fetches, they differ only in the column and where then comes from
    private static IReadOnlyList<WizardRow> Rows(
        GlyphSet g, FetchTick t, int width, int column, string into, string? then, bool paused = false) =>
    [
        Row(column, "fetching",
            t.Count > 1 ? $"{t.FileName} {g.Dot} file {t.Index} of {t.Count}" : t.FileName,
            lift: t.FileName),
        BarRow(g, t, width, column, paused),
        .. then is { Length: > 0 } coming
            ? (IReadOnlyList<WizardRow>)[Row(column, "then", coming)]
            : [],
        Row(column, "into", into),
        //the block's closing blank, a screen with nothing to choose has no option list to keep its last fact off the footer rule
        "",
    ];

    //the filled run and the two figures are marked by their literal text, so keep each unique in what precedes it
    private static WizardRow BarRow(GlyphSet g, FetchTick t, int width, int column, bool paused = false)
    {
        var cells = BarCells(width);
        var filled = Repeat(g.HeavyRule, Filled(t, cells));
        var (done, total) = SizeWords.Pair(t.Done, t.Total);
        var tail = paused
            ? "paused"
            : string.Join($" {g.Dot} ", new[] { Rate(t), Remaining(t) }.Where(s => s.Length > 0));
        var text = new string(' ', column)
            + Bar(t, width, g) + "  " + done + " of " + total + $" {g.Dot} " + tail;
        return new WizardRow(text, RowTone.Aside, Highlight: filled.Length > 0 ? [filled] : null,
            Hang: column, Lift: [done, total]);
    }

    //the same shape EngineFetchView draws, so the screens read as one block (the column is a parameter, each block has its own width)
    private static WizardRow Row(int column, string label, string value, string? lift = null) =>
        new(label.PadRight(column) + value, RowTone.Aside,
            Hang: column, Lift: lift is null ? null : [lift]);
}
