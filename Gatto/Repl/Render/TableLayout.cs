using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the one table renderer, a pure function of spec, theme and width. live render, resize and replay must agree, or the rows on screen contradict the transcript
public static class TableLayout
{
    //a laid-out row up to 4 lines tall keeps the grid, 5 or more turns the whole table into records. don't change the number without new measurements
    public const int MaxRowLines = 4;

    //the verticals plus one padding cell either side of every column
    internal static int Chrome(int columns) => (columns + 1) + (columns * 2);

    //sanitize then parse the inline markdown, and measure the span text, because a markdown marker would inflate the column
    internal static IReadOnlyList<StyledSpan> CellSpans(string cell) =>
        InlineStyler.Parse(TermText.Sanitize(cell));

    private static int SpanWidth(IReadOnlyList<StyledSpan> spans)
    {
        var w = 0;
        foreach (var s in spans) w += UnicodeWidth.Of(s.Text);
        return w;
    }

    //the widest cell width per column over its header and rows, measured in display cells so CJK and emoji count 2
    internal static int[] Natural(TableSpec spec)
    {
        var w = new int[spec.Headers.Count];
        for (var c = 0; c < w.Length; c++) w[c] = SpanWidth(CellSpans(spec.Headers[c]));
        foreach (var row in spec.Rows)
            for (var c = 0; c < w.Length && c < row.Count; c++)
                w[c] = Math.Max(w[c], SpanWidth(CellSpans(row[c])));
        return w;
    }

    //the fewest cells each column can take, 2 where a cell holds a wide character since one cell cannot draw it
    internal static int[] Floors(TableSpec spec)
    {
        var f = new int[spec.Headers.Count];
        for (var c = 0; c < f.Length; c++)
        {
            var cells = spec.Rows.Select(r => c < r.Count ? r[c] : "").Prepend(spec.Headers[c]);
            f[c] = cells.Any(cell => CellSpans(cell).Any(s => s.Text.EnumerateRunes().Any(r => UnicodeWidth.OfRune(r) > 1))) ? 2 : 1;
        }
        return f;
    }

    //residual cells go one at a time to the leftmost capped column and stop at its natural width, and a zero-width column stays 0
    internal static int[] Allocate(int[] natural, int budget, int[]? floors = null)
    {
        if (natural.Sum() <= budget) return (int[])natural.Clone();

        //no column goes under its floor, and a floor never lifts a column past its natural width
        int Cell(int i, int cap) => natural[i] == 0 ? 0 : Math.Max(Math.Min(natural[i], cap), Math.Min(floors?[i] ?? 1, natural[i]));

        //the largest common cap whose sum of cells still fits the budget
        var cap = natural.Max();
        while (cap > 1 && Enumerable.Range(0, natural.Length).Sum(i => Cell(i, cap)) > budget) cap--;
        var w = Enumerable.Range(0, natural.Length).Select(i => Cell(i, cap)).ToArray();

        //leftmost capped column first, one cell at a time. the loop ends because a leftover cell means some column is still below its natural width
        for (var residual = budget - w.Sum(); residual > 0; )
            for (var i = 0; i < w.Length && residual > 0; i++)
                if (w[i] < natural[i]) { w[i]++; residual--; }
        return w;
    }

    //the frame and the horizontal come from one glyph set, so they cannot disagree. the marks are strings, a char would cut a longer mark to one character
    private static string Repeat(string mark, int n) =>
        n <= 0 ? "" : string.Concat(Enumerable.Repeat(mark, n));

    //renders the table as a grid when every laid-out row fits in MaxRowLines, and as key/value records otherwise
    public static IReadOnlyList<RenderedRow> Rows(TableSpec spec, Theme theme, int width,
        GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var n = spec.Headers.Count;
        if (n == 0) return Array.Empty<RenderedRow>();

        var budget = width - Chrome(n);
        if (budget < n) return RecordRows(spec, theme, width, g);

        var w = Allocate(Natural(spec), budget, Floors(spec));
        if (w.Sum() > budget) return RecordRows(spec, theme, width, g);   //the floors of the wide columns don't fit
        var blocks = new List<List<string>> { RowLines(spec.Headers, w, spec.Alignments, true, theme, g) };
        foreach (var row in spec.Rows) blocks.Add(RowLines(row, w, spec.Alignments, false, theme, g));

        //the check runs on the laid-out grid after wrapping, so it sees what the user sees, and the header counts like any other row
        if (blocks.Any(b => b.Count > MaxRowLines)) return RecordRows(spec, theme, width, g);

        var rows = new List<RenderedRow>
            { new(Rule(g, w, g.Box.TopLeft, g.Box.TopMid, g.Box.TopRight), false) };
        for (var b = 0; b < blocks.Count; b++)
        {
            //a box row keeps continuation false on every visual line, so a copy never rejoins a wrapped row. records keep it true, there rejoining does rebuild a value
            for (var l = 0; l < blocks[b].Count; l++) rows.Add(new RenderedRow(blocks[b][l], false, 2));
            rows.Add(new RenderedRow(Rule(g, w, g.Box.MidLeft, g.Box.Cross, g.Box.MidRight), false));
        }
        rows[^1] = new RenderedRow(Rule(g, w, g.Box.BottomLeft, g.Box.BottomMid, g.Box.BottomRight),
            false);
        return rows;
    }

    private static string Rule(GlyphSet g, int[] w, string left, string mid, string right)
    {
        var sb = new StringBuilder().Append(left);
        for (var c = 0; c < w.Length; c++)
        {
            sb.Append(Repeat(g.Rule, w[c] + 2));
            sb.Append(c == w.Length - 1 ? right : mid);
        }
        return sb.ToString();
    }

    //turn one logical row into its visual lines, SpanWrap does the wrapping and the spans are sliced per row so link flags survive
    private static List<string> RowLines(
        IReadOnlyList<string> cells, int[] w, IReadOnlyList<ColumnAlign> aligns, bool header, Theme theme,
        GlyphSet g)
    {
        var wrapped = new List<IReadOnlyList<IReadOnlyList<StyledSpan>>>();
        for (var c = 0; c < w.Length; c++)
        {
            var spans = CellSpans(c < cells.Count ? cells[c] : "");
            wrapped.Add(w[c] <= 0 || spans.Count == 0
                ? new List<IReadOnlyList<StyledSpan>> { Array.Empty<StyledSpan>() }
                : SpanWrap.Wrap(spans, w[c], w[c]));
        }

        var height = wrapped.Max(x => Math.Max(1, x.Count));
        var lines = new List<string>(height);
        for (var l = 0; l < height; l++)
        {
            var sb = new StringBuilder().Append(g.Box.Vertical);
            for (var c = 0; c < w.Length; c++)
            {
                var line = l < wrapped[c].Count ? wrapped[c][l] : Array.Empty<StyledSpan>();
                var pad = w[c] - SpanWidth(line);
                //a header is always centered whatever the column alignment says, and an odd padding cell goes right
                var align = header ? ColumnAlign.Center : aligns[c];
                var (left, right) = align switch
                {
                    ColumnAlign.Center => (pad / 2, pad - pad / 2),
                    ColumnAlign.Right => (pad, 0),
                    _ => (0, pad),
                };
                sb.Append(' ').Append(new string(' ', Math.Max(0, left)))
                  .Append(theme.Paint(line))
                  .Append(new string(' ', Math.Max(0, right))).Append(' ').Append(g.Box.Vertical);
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }

    //the key/value form a table falls back to when the grid is too tall. one header: value line per column, label emphasised, value wrapped at the full width
    private static IReadOnlyList<RenderedRow> RecordRows(TableSpec spec, Theme theme, int width,
        GlyphSet g)
    {
        var rows = new List<RenderedRow>();

        //a header-only table still renders its header lines, that is the state every streaming table passes through before its first row arrives
        if (spec.Rows.Count == 0)
        {
            foreach (var h in spec.Headers)
                rows.Add(new RenderedRow(theme.Paint(TermText.Sanitize(h) + ":", Theme.Accent), false));
            return rows;
        }

        for (var r = 0; r < spec.Rows.Count; r++)
        {
            //the rule goes before each record except the first, so none trails the last, dim and as wide as the terminal
            if (r > 0)
                rows.Add(new RenderedRow(theme.Paint(Repeat(g.Rule, Math.Max(1, width)), Theme.Dim), false));
            for (var c = 0; c < spec.Headers.Count; c++)
            {
                //the plain label is measured and the painted one emitted, or the ANSI bytes count as display cells
                var labelText = TermText.Sanitize(spec.Headers[c]) + ": ";
                var label = theme.Paint(labelText, Theme.Accent);
                var value = CellSpans(c < spec.Rows[r].Count ? spec.Rows[r][c] : "");
                var budget = Math.Max(1, width - UnicodeWidth.Of(labelText));
                var wrapped = value.Count == 0
                    ? new List<IReadOnlyList<StyledSpan>> { Array.Empty<StyledSpan>() }
                    : SpanWrap.Wrap(value, budget, Math.Max(1, width));
                for (var l = 0; l < Math.Max(1, wrapped.Count); l++)
                {
                    var line = l < wrapped.Count ? theme.Paint(wrapped[l]) : "";
                    rows.Add(new RenderedRow(l == 0 ? label + line : line, l > 0));
                }
            }
        }
        return rows;
    }

    //the borderless form for the prose listings, the grid's measurement with no box glyphs. it shares Natural and Allocate with the grid, so the forms can't drift
    public static IReadOnlyList<RenderedRow> AlignedRows(TableSpec spec, Theme theme, int width)
    {
        var n = spec.Headers.Count;
        if (n == 0) return Array.Empty<RenderedRow>();

        var gaps = 2 * (n - 1);
        var w = Allocate(Natural(spec), Math.Max(n, width - gaps), Floors(spec));
        var rows = new List<RenderedRow>();

        void Emit(IReadOnlyList<string> cells)
        {
            var wrapped = new List<IReadOnlyList<IReadOnlyList<StyledSpan>>>();
            for (var c = 0; c < n; c++)
            {
                var spans = CellSpans(c < cells.Count ? cells[c] : "");
                wrapped.Add(w[c] <= 0 || spans.Count == 0
                    ? new List<IReadOnlyList<StyledSpan>> { Array.Empty<StyledSpan>() }
                    : SpanWrap.Wrap(spans, w[c], w[c]));
            }
            var height = wrapped.Max(x => Math.Max(1, x.Count));
            for (var l = 0; l < height; l++)
            {
                var sb = new StringBuilder();
                for (var c = 0; c < n; c++)
                {
                    var line = l < wrapped[c].Count ? wrapped[c][l] : Array.Empty<StyledSpan>();
                    if (c > 0) sb.Append("  ");
                    sb.Append(theme.Paint(line)).Append(new string(' ', Math.Max(0, w[c] - SpanWidth(line))));
                }
                rows.Add(new RenderedRow(sb.ToString().TrimEnd(), l > 0));
            }
        }

        //skip the header line when every header is empty, otherwise the all-spaces row turns into a blank first line above the listing
        if (spec.Headers.Any(h => h.Length > 0)) Emit(spec.Headers);
        foreach (var row in spec.Rows) Emit(row);
        return rows;
    }
}
