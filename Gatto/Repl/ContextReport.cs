using System.Globalization;
using Gatto.Core.Loop;
using Gatto.Terminal;

namespace Gatto.Repl;

//the /context screen: a header, the source of the counts, one bar of the window by group, the auto-compaction mark, a table of every part and the cache line
public static class ContextReport
{
    //the colours of the four groups and of the free cells
    private static RgbColor GroupColour(ContextGroup g) => g switch
    {
        ContextGroup.Prefix => Theme.ContextPrefix,
        ContextGroup.Messages => Theme.Bright,
        ContextGroup.Reasoning => Theme.ContextReasoning,
        _ => Theme.ToolArgs,
    };
    private static readonly RgbColor Free = Theme.Rule;
    private static readonly RgbColor PastLine = Theme.ContextPastLine;

    //the table goes into two columns from this width up
    internal const int TwoColumnsFrom = 100;

    //one row as painted and as seen, for a transcript item or a panel
    public readonly record struct Row(string Rendered, string Visible);

    //the rows as the transcript takes them, the screen's two-cell margin off each so the transcript's own hang stands in for it
    public static IReadOnlyList<string> Unhung(ContextFigures f, int width, Theme theme, GlyphSet g) =>
        [.. Rows(f, width, theme, g).Select(r => r.Rendered.StartsWith("  ", StringComparison.Ordinal) ? r.Rendered[2..] : r.Rendered)];

    public static IReadOnlyList<Row> Rows(ContextFigures f, int width, Theme theme, GlyphSet g)
    {
        var rows = new List<Row>();
        var wide = width >= TwoColumnsFrom;
        var window = f.Window is > 0 ? f.Window.Value : 0;
        var auto = f.AutoCompactAt;

        string Num(int n) => f.Exact ? N(n) : g.Approx + N((int)Math.Round(n / 10.0) * 10);
        string Pct(int n) => window > 0 ? (n * 100.0 / window).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";

        var sep = " " + g.Dot + " ";
        var head = new Line().Add("context", theme, Theme.Bright, bold: true).Add(sep, theme, Theme.Dim)
            .Add(window > 0 ? $"{N(f.Total)} of {N(window)} tokens" : $"{N(f.Total)} tokens", theme, null);
        if (window > 0)
            head.Add(sep, theme, Theme.Dim).Add($"{(int)Math.Round(f.Total * 100.0 / window)}%", theme, Theme.Accent, bold: true);
        rows.Add(head.Indent(2));
        rows.Add(new Line().Add(Source(f, wide, window, g), theme, Theme.Dim).Indent(2));
        rows.Add(new Row("", ""));

        var groups = new[] { ContextGroup.Prefix, ContextGroup.Messages, ContextGroup.Reasoning, ContextGroup.ToolResults };
        int Sum(ContextGroup grp) => f.Parts.Where(p => p.Group == grp).Sum(p => p.Tokens);
        if (window > 0)
        {
            var cells = Math.Max(1, width - 4);
            var k = Allocate(groups.Select(Sum).ToArray(), window, cells);
            var used = Math.Min(cells, k.Sum());
            var mark = auto is double a ? (int)Math.Round(a * cells, MidpointRounding.AwayFromZero) : cells;
            var bar = new Line();
            for (var i = 0; i < groups.Length; i++) bar.Add(Repeat(g.Block, k[i]), theme, GroupColour(groups[i]));
            bar.Add(Repeat(g.Shade, Math.Max(0, mark - used)), theme, Free)
               .Add(Repeat(g.Shade, Math.Max(0, cells - Math.Max(used, mark))), theme, PastLine);
            rows.Add(bar.Indent(2));
            if (auto is double at)
            {
                var label = $"{g.Up} auto-compacts at {(int)Math.Round(at * 100)}%";
                var col = 2 + mark;
                rows.Add(col + label.Length <= width
                    ? new Line().Add(label, theme, Theme.Accent).Indent(col)
                    : new Line().Add($"auto-compacts at {(int)Math.Round(at * 100)}% {g.Up}", theme, Theme.Accent).Indent(Math.Max(0, col - label.Length + 1)));
            }
            rows.Add(new Row("", ""));
        }

        Line Header(string label, int n) => new Line().Add(PadR(label, 24), theme, Theme.Bright, bold: true)
            .Add(PadL(Num(n), 8), theme, Theme.Bright, bold: true).Add(PadL(Pct(n), 8), theme, Theme.Dim);
        Line Item(string swatch, RgbColor colour, string label, int n, bool exactNumber = false) => new Line()
            .Add(swatch + " ", theme, colour).Add(PadR(label, 22), theme, null)
            .Add(PadL(exactNumber ? N(n) : Num(n), 8), theme, Theme.Bright).Add(PadL(Pct(n), 8), theme, Theme.Dim);
        string Label(ContextPart p) => p.Detail is null ? p.Label : p.Label + sep + p.Detail;

        var prefix = Sum(ContextGroup.Prefix);
        var conversation = f.Parts.Where(p => p.Group != ContextGroup.Prefix).Sum(p => p.Tokens);
        var left = new List<Line> { Header("prefix", prefix) };
        left.AddRange(f.Parts.Where(p => p.Group == ContextGroup.Prefix).Select(p => Item(g.Swatch, GroupColour(p.Group), Label(p), p.Tokens)));
        var right = new List<Line>();
        if (!f.BeforeFirstRequest)
        {
            right.Add(Header("conversation", conversation));
            right.AddRange(f.Parts.Where(p => p.Group != ContextGroup.Prefix).Select(p => Item(g.Swatch, GroupColour(p.Group), Label(p), p.Tokens)));
        }
        if (window > 0)
        {
            if (right.Count > 0) right.Add(new Line());
            right.Add(Item(g.Swatch, Free, "free", Math.Max(0, window - f.Total), exactNumber: true));
            if (auto is double past)
                right.Add(new Line().Add(g.Swatch + " ", theme, PastLine)
                    .Add($"past {(int)Math.Round(past * 100)}%, auto-compaction runs first", theme, Theme.Dim));
        }

        if (wide)
        {
            for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                var l = i < left.Count ? left[i] : new Line();
                var row = new Line().Append(l).Pad(40).Add("      ", theme, null);
                if (i < right.Count) row.Append(right[i]);
                rows.Add(row.Indent(2));
            }
        }
        else
        {
            foreach (var l in left) rows.Add(l.Indent(2));
            if (right.Count > 0) rows.Add(new Row("", ""));
            foreach (var r in right) rows.Add(r.Indent(2));
        }

        rows.Add(new Row("", ""));
        var cache = new Line().Add("cache", theme, Theme.Bright, bold: true);
        if (f.Cache is { } c)
            cache.Add("  last request ", theme, Theme.Dim).Add(N(c.FromCache), theme, Theme.Bright)
                 .Add($" of {N(c.FromCache + c.Fresh)} tokens from cache, ", theme, null)
                 .Add(N(c.Fresh), theme, Theme.Bright).Add(" read fresh", theme, null);
        else
            cache.Add(f.BeforeFirstRequest ? "  no request yet" : "  not reported by this endpoint", theme, Theme.Dim);
        rows.Add(cache.Indent(2));

        //a narrow window cuts a row rather than wrapping it, so the panel's row count stays the rows it was given
        return [.. rows.Select(r => UnicodeWidth.Of(r.Visible) <= width ? r
            : new Row(TermText.TruncateCells(r.Rendered, width, g), TermText.TruncateCells(r.Visible, width, g)))];
    }

    //where the counts came from, the long form from the two-column width up, and when they were taken
    private static string Source(ContextFigures f, bool wide, int window, GlyphSet g)
    {
        var sep = " " + g.Dot + " ";
        var source = f.Exact
            ? wide && window > 0 ? $"counted by llama-server's tokenizer{sep}the window is {N(window)}" : "counted by the server's tokenizer"
            : f.FellBack
                ? wide ? "the server answered no count, so the parts are estimates (chars/4 scaled to the total)" : "no count from the server, parts estimated"
                : wide ? "the total is the server's count, the parts are estimates (chars/4 scaled to it)" : "parts estimated, total from the server";
        return source + sep + (f.BeforeFirstRequest ? "before the first request" : "as of the last request");
    }

    //the cells each group takes of the bar, by largest remainder so the cells add up to the used share
    internal static int[] Allocate(int[] counts, int window, int cells)
    {
        var exact = counts.Select(n => (double)n * cells / window).ToArray();
        var outCells = exact.Select(e => (int)Math.Floor(e)).ToArray();
        var want = Math.Min(cells, (int)Math.Round(exact.Sum(), MidpointRounding.AwayFromZero));
        foreach (var i in Enumerable.Range(0, exact.Length).OrderByDescending(i => exact[i] - Math.Floor(exact[i])))
        {
            if (outCells.Sum() >= want) break;
            outCells[i]++;
        }
        return outCells;
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string Repeat(string s, int n) => n <= 0 ? "" : string.Concat(Enumerable.Repeat(s, n));
    private static string PadR(string s, int n) => s + new string(' ', Math.Max(0, n - UnicodeWidth.Of(s)));
    private static string PadL(string s, int n) => new string(' ', Math.Max(0, n - UnicodeWidth.Of(s))) + s;

    //a row built from coloured runs, keeping its painted and its visible forms together
    private sealed class Line
    {
        private readonly System.Text.StringBuilder _rendered = new();
        private readonly System.Text.StringBuilder _visible = new();

        public Line Add(string text, Theme theme, RgbColor? colour, bool bold = false)
        {
            if (text.Length == 0) return this;
            _rendered.Append(colour is { } c ? theme.Paint(text, c, bold: bold) : text);
            _visible.Append(text);
            return this;
        }

        public Line Append(Line other)
        {
            _rendered.Append(other._rendered);
            _visible.Append(other._visible);
            return this;
        }

        public Line Pad(int cells)
        {
            var gap = cells - UnicodeWidth.Of(_visible.ToString());
            if (gap > 0) { _rendered.Append(' ', gap); _visible.Append(' ', gap); }
            return this;
        }

        public Row Indent(int cells) =>
            new(new string(' ', cells) + _rendered, (new string(' ', cells) + _visible).TrimEnd());
    }
}
