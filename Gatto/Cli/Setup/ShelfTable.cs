using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//how much of the shelf survives the width. each step drops the least decisive thing, so size and quant merge first and the measured marks go last
internal enum ShelfStage
{
    //model, params, context, size, quant, marks
    Full,

    //model, params, context, size+quant, marks
    MergedSizeQuant,

    //model, params, size+quant, marks. the floor keeps the marks, so the context column is what gives at narrow widths
    NoContext,
}

//the shelf as a table built for the REPL's own renderer, nothing rendered here. the ladder buys width, and a name past the floor wraps rather than being cut
internal static class ShelfTable
{
    //the parameter count comes from the GGUF's own total, so no expert count is read from the repo name. absent means an empty cell rather than a guess
    internal static string ParamsCell(ModelRow r)
    {
        if (r.Params is not { } p || p <= 0) return r.ParamsLabel ?? "";
        var billions = p / 1_000_000_000.0;
        //1000B is a number with no unit, so the billions roll to T with the same format. sizes are not rolled past GB
        if (billions >= 1000)
            return (billions / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "T";
        return billions >= 1
            ? billions.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "B"
            : Math.Round(p / 1_000_000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "M";
    }

    //the tier a row is grouped under, using SearchRow.FitWords rather than a second vocabulary for the same arithmetic
    public static string TierLabel(FitRegime fit, Gatto.Core.Hardware.MachineShape shape,
        GlyphSet? glyphs) =>
        SearchRow.FitWords(fit, shape, glyphs ?? GlyphSet.Unicode);

    //the rule character that opens a tier line, already shipped beside the composer frame's label

    //a function of the glyph set, since a const cannot read one, and two table horizontals keep the tier rule and the screen rule identical
    public static string TierRuleOf(GlyphSet g) => g.Rule + g.Rule;

    //the stage that fits, measured against the renderer's own column arithmetic rather than a table of guessed thresholds
    public static ShelfStage StageFor(IReadOnlyList<ModelRow> rows, int width,
        Gatto.Core.Hardware.MachineShape shape, GlyphSet glyphs)
    {
        foreach (var stage in new[] { ShelfStage.Full, ShelfStage.MergedSizeQuant })
            if (Fits(Spec(rows, stage, shape, glyphs), width)) return stage;

        //the floor is returned whether or not it fits, since nothing is left to drop and the renderer wraps instead of cutting
        return ShelfStage.NoContext;
    }

    private static bool Fits(TableSpec spec, int width)
    {
        var natural = TableLayout.Natural(spec);
        return natural.Sum() + (2 * (natural.Length - 1)) <= width;
    }

    //the shelf as a spec: a header row, then each tier's label row followed by its models. the separator is a row, and rows arrive already ordered
    public static TableSpec Spec(IReadOnlyList<ModelRow> rows, ShelfStage stage,
        Gatto.Core.Hardware.MachineShape shape, GlyphSet? glyphs)
    {
        //the context column exists only when some row has one, since a column of blanks costs its header and gap for nothing
        var ctx = rows.Any(r => r.NativeCtx is > 0);
        var g = glyphs ?? GlyphSet.Unicode;
        var headers = Headers(stage, ctx, g);
        var body = new List<IReadOnlyList<string>>();

        //model rows only, with the tier labels interleaved by the caller so they stop setting the model column's floor
        foreach (var row in rows) body.Add(Cells(row, stage, ctx, g));

        //sizes right-align so a column can be compared, and the alignment is set where the header is built rather than read back off its text
        return new TableSpec(
            [.. headers.Select(h => h.Text)], [.. headers.Select(h => h.Align)], body);
    }

    //the laid-out shelf: one call, one renderer, one set of column widths
    public static IReadOnlyList<RenderedRow> Render(
        IReadOnlyList<ModelRow> rows, Theme theme, int width,
        Gatto.Core.Hardware.MachineShape shape, GlyphSet glyphs) =>
        TableLayout.AlignedRows(
            Spec(rows, StageFor(rows, width, shape, glyphs), shape, glyphs),
            theme, width);

    private const string SizeHeader = "size";

    //a header is its text plus the alignment it was built with, so the alignment is never read back off the text
    private readonly record struct Header(string Text, ColumnAlign Align);

    private static Header Left(string text) => new(text, ColumnAlign.Left);

    private static Header Right(string text) => new(text, ColumnAlign.Right);

    //the merged header, composed from the glyph table so the ASCII set does not draw tofu in the header row
    private static string SizeQuantHeaderOf(GlyphSet g) => $"size {g.Dot} quant";

    //params survives every rung, since the count is the first thing anyone asks about a model

    //context joins the ladder and drops at the floor, since the model's own page still has the number
    private static IReadOnlyList<Header> Headers(ShelfStage stage, bool ctx, GlyphSet g) => stage switch
    {
        ShelfStage.Full =>
            [.. Ins([Left("model"), Left("params")], ctx, Left(CtxHeader)),
             Right(SizeHeader), Left("quant"), Left("")],
        ShelfStage.MergedSizeQuant =>
            [.. Ins([Left("model"), Left("params")], ctx, Left(CtxHeader)),
             Right(SizeQuantHeaderOf(g)), Left("")],
        //the floor drops context and keeps the marks
        _ => [Left("model"), Left("params"), Right(SizeQuantHeaderOf(g)), Left("")],
    };

    //the lead columns, with the context column appended when it is on. one helper so headers and cells cannot disagree
    private static IReadOnlyList<T> Ins<T>(IReadOnlyList<T> lead, bool ctx, T cell) =>
        ctx ? [.. lead, cell] : lead;

    private const string CtxHeader = "context";

    //an absent window shows an empty cell rather than a guess, with the digits from SearchRow.Ctx
    private static string Ctx(ModelRow r) =>
        r.NativeCtx is { } ctx and > 0 ? SearchRow.Ctx(ctx) : "";

    private static IReadOnlyList<string> Cells(
        ModelRow r, ShelfStage stage, bool ctx, GlyphSet g) =>
        stage switch
        {
            ShelfStage.Full =>
                [.. Ins([Name(r), ParamsCell(r)], ctx, Ctx(r)),
                 SearchRow.Gb(SearchRow.RowBytes(r)), Quant(r), Marks(r, g)],
            ShelfStage.MergedSizeQuant =>
                [.. Ins([Name(r), ParamsCell(r)], ctx, Ctx(r)), SizeQuant(r), Marks(r, g)],
            _ => [Name(r), ParamsCell(r), SizeQuant(r), Marks(r, g)],
        };

    //the model's own name, which no publisher prefixes
    private static string Name(ModelRow r) => r.Model;

    //an unconventional filename shows an empty cell, since QuantToken.Of omits rather than guesses
    private static string Quant(ModelRow r) =>
        Gatto.Core.Acquire.QuantToken.Of(SearchRow.RowFileName(r)) ?? "";

    private static string SizeQuant(ModelRow r)
    {
        var q = Quant(r);
        return q.Length == 0 ? SearchRow.Gb(SearchRow.RowBytes(r)) : $"{SearchRow.Gb(SearchRow.RowBytes(r))} {q}";
    }

    //the marks column is headerless, glyphs only and never adjectives, since a mark is a fact rather than a verdict
    private static string Marks(ModelRow r, GlyphSet g)
    {
        var marks = new List<string>();
        if (r.Vision) marks.Add(g.Vision);
        if (r.Badge is not null) marks.Add(g.Ok);
        //the marker ships as a mark, since its words do not fit any rung and the legend holds them
        if (ArchNote.Marker(r) is not null) marks.Add(g.OtherBuild);
        return string.Join(" ", marks);
    }

    //what the marks on this shelf mean, null when no row has one. each segment appears only when a row has that glyph
    public static string? Legend(IReadOnlyList<ModelRow> rows, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var parts = new List<string>();
        if (rows.Any(r => r.Vision)) parts.Add($"{g.Vision} vision");
        if (rows.Any(r => r.Badge is not null)) parts.Add($"{g.Ok} tool-calling verified");
        if (rows.Any(r => ArchNote.Marker(r) is not null))
            parts.Add($"{g.OtherBuild} {ArchNote.MarkerText}");
        return parts.Count == 0 ? null : string.Join($" {g.Dot} ", parts);
    }

    private static IReadOnlyList<string> Pad(List<string> cells, int columns)
    {
        while (cells.Count < columns) cells.Add("");
        return cells;
    }

    //one line of the shelf, a model or the tier heading that opens its group
    internal readonly record struct ShelfLine(ModelRow? Row, FitRegime Tier);

    //a change of tier starts a group, and the caller reads its rows from here so the options and the table describe one screen
    internal static IReadOnlyList<ShelfLine> Lines(IReadOnlyList<ModelRow> rows)
    {
        var lines = new List<ShelfLine>();
        FitRegime? tier = null;
        foreach (var row in rows)
        {
            if (tier != row.Fit)
            {
                tier = row.Fit;
                lines.Add(new ShelfLine(null, row.Fit));
            }
            lines.Add(new ShelfLine(row, row.Fit));
        }
        return lines;
    }
}
