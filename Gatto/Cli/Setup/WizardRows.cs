using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//how a row becomes lines at a live width, for both faces. the indent comes off the row, and a continuation wraps in a budget narrowed by the hang
internal static class WizardRows
{
    private const string Gutter = "  ";

    //the lines this row becomes, each with its lead, with the text sanitized. width 0 means unknown and nothing wraps, and margin frees cells at the right edge
    public static IReadOnlyList<(string Lead, string Text)> Wrap(WizardRow row, int width, int margin = 0,
        Gatto.Terminal.GlyphSet? glyphs = null, string frameGutter = Gutter)
    {
        var text = TermText.Sanitize(row.Text);
        var gutter = frameGutter + new string(' ', Math.Max(0, row.Indent));
        var hang = gutter + new string(' ', Math.Max(0, row.Hang));
        //the glyph opens the lead, padded to the gutter's width so a wide mark cannot shift the line. it sits in the row's own gutter, indent included
        var indent = new string(' ', Math.Max(0, row.Indent));
        //the mark is resolved once and measured, so the pad and the mark cannot disagree about its width
        var mark = row.Glyph is { } g ? Glyphs.Of(g, glyphs) : null;
        var lead = mark is not null
            ? indent + mark
                + new string(' ', Math.Max(1, UnicodeWidth.Of(frameGutter) - UnicodeWidth.Of(mark)))
            : gutter;

        if (width <= 0 || text.Length == 0) return [(lead, text)];

        //a wide row ignores the right margin and wraps to the full width
        var budget = width - (row.Wide ? 0 : Math.Max(0, margin));

        //an item list is packed, with the break falling between items and nowhere else. the widow rule is skipped, since an item cannot split
        if (row.Items is { Count: > 0 } items)
            return Packed(items.Select(TermText.Sanitize).ToList(), text, lead, hang, budget,
                glyphs ?? Gatto.Terminal.GlyphSet.Unicode);
        //the widths are cells rather than chars, and only the glyph makes the difference, since every other lead here is spaces
        var segs = SoftWrap.Wrap(Unbreakable(text, row.Highlight),
            Math.Max(1, budget - UnicodeWidth.Of(lead)), Math.Max(1, budget - UnicodeWidth.Of(hang)));
        var lines = segs.Select((s, i) => (Lead: i == 0 ? lead : hang, s.Text)).ToList();

        //the blank first line goes when padding wraps off on its own. the survivor keeps the hang, and a row blank all through stays blank on purpose
        if (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[0].Text))
            lines.RemoveAt(0);

        if (margin > 0) DeWidow(lines, Math.Max(1, budget - UnicodeWidth.Of(hang)));
        //the widow rule can move a whole word and strand the separator on an edge, so the dot drops after it and before the placeholders return
        DropEdgeSeparators(lines, glyphs ?? Gatto.Terminal.GlyphSet.Unicode);

        //restored last, after the separator drop, so a command still holding its placeholders stays atomic through the widow rule
        return Spaced(lines);
    }

    //a highlighted span goes onto a line whole, since accents are matched per line. a span with no space is atomic already, and a wider one is still hard-broken
    private static string Unbreakable(string text, IReadOnlyList<string>? spans)
    {
        if (spans is not { Count: > 0 }) return text;
        foreach (var span in spans)
            if (span.Contains(' '))
                text = text.Replace(span, span.Replace(' ', Bound), StringComparison.Ordinal);
        return text;
    }

    //the placeholder back to a space once no wrapper can see it, since the face matches highlights against the row's original spelling
    private static IReadOnlyList<(string Lead, string Text)> Spaced(
        IReadOnlyList<(string Lead, string Text)> lines) =>
        [.. lines.Select(l => (l.Lead, l.Text.Replace(Bound, ' ')))];

    //a separator split by the break reads as a dot stranded on an edge, and the packed rule drops it there, so it goes with the space beside it
    private static void DropEdgeSeparators(List<(string Lead, string Text)> lines,
        Gatto.Terminal.GlyphSet g)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var (lead, text) = lines[i];
            //the break can strand a space after the dot at the edge, and a highlight makes that space a placeholder, so both are trimmed
            var open = text.TrimEnd(' ', Bound);
            var start = text.TrimStart(' ', Bound);
            //only a dot with its separator space beside it is one, since the ascii dot is also a full stop and opens a name like .gatto.json
            if (open == g.Dot) text = "";
            else if (open.EndsWith(g.Dot, StringComparison.Ordinal) && open[^(g.Dot.Length + 1)] is ' ' or Bound)
                text = open[..^g.Dot.Length].TrimEnd(' ', Bound);
            else if (start.StartsWith(g.Dot, StringComparison.Ordinal) && start.Length > g.Dot.Length && start[g.Dot.Length] is ' ' or Bound)
                text = start[g.Dot.Length..].TrimStart(' ', Bound);
            lines[i] = (lead, text);
        }
    }

    //figure space, one cell wide so the width sums hold. it survives TermText.Sanitize and never reaches the terminal, since Spaced puts it back
    private const char Bound = '\u2007';

    //items broken only between them, with the separator dropped at a break. an item wider than the row overflows, since breaking inside an item is the defect
    private static IReadOnlyList<(string Lead, string Text)> Packed(
        IReadOnlyList<string> items, string leadText, string lead, string hang, int budget,
        Gatto.Terminal.GlyphSet g)
    {
        var Sep = " " + g.Dot + " ";
        var lines = new List<(string Lead, string Text)>();
        var room = Math.Max(1, budget - UnicodeWidth.Of(lead));
        var line = leadText;
        var onThisLine = 0;

        foreach (var item in items)
        {
            var candidate = onThisLine == 0 ? line + item : line + Sep + item;
            //the guard is onThisLine being greater than zero, so a line holding nothing yet takes its item at any width
            if (onThisLine > 0 && UnicodeWidth.Of(candidate) > room)
            {
                lines.Add((lines.Count == 0 ? lead : hang, line));
                room = Math.Max(1, budget - UnicodeWidth.Of(hang));
                (line, onThisLine) = (item, 1);
                continue;
            }
            (line, onThisLine) = (candidate, onThisLine + 1);
        }

        lines.Add((lines.Count == 0 ? lead : hang, line));
        return lines;
    }

    //the last line pulls a companion word down from the line above, only inside the frame and only when the merged line still fits
    private static IReadOnlyList<(string Lead, string Text)> DeWidow(
        List<(string Lead, string Text)> lines, int budget)
    {
        if (lines.Count < 2) return lines;

        var last = lines[^1];
        if (last.Text.Contains(' ')) return lines;

        var donor = lines[^2];
        var cut = donor.Text.LastIndexOf(' ');
        if (cut < 0) return lines;

        var merged = donor.Text[(cut + 1)..] + " " + last.Text;
        if (UnicodeWidth.Of(merged) > budget) return lines;

        lines[^2] = (donor.Lead, donor.Text[..cut]);
        lines[^1] = (last.Lead, merged);
        return lines;
    }

    //the same rows as plain strings, so ScreenPainter clamps them by visible cell rather than by escape byte

    //the same rows holding their tone, with the lead folded into the text, since plain strings leave the face nothing to paint from
    public static IEnumerable<WizardRow> Toned(IReadOnlyList<WizardRow>? rows, int width, int margin = 0,
        Gatto.Terminal.GlyphSet? glyphs = null) =>
        (rows ?? []).SelectMany(r =>
        {
            //materialised so the tail can find the last line, at the price of one list per row
            var segs = Wrap(r, width, margin, glyphs).ToList();
            //the glyph stays on the first line and the tail on the last, since a continuation would ink the wrong cells
            return segs.Select((seg, i) => r with
            {
                Text = seg.Text.Length == 0 ? "" : seg.Lead + seg.Text,
                Glyph = i == 0 ? r.Glyph : null,
                Tail = i == segs.Count - 1 ? r.Tail : null,
            });
        });

    //the default frameGutter is the frame's two spaces, and the record printed to scrollback passes none, since its body sits at column zero
    public static IEnumerable<string> Plain(IReadOnlyList<WizardRow>? rows, int width,
        Gatto.Terminal.GlyphSet glyphs, int margin = 0, string frameGutter = Gutter) =>
        (rows ?? []).SelectMany(r => Wrap(r, width, margin, glyphs, frameGutter)
            //a spacer renders as a truly empty row, since the goldens are byte-pinned and trailing spaces on a blank line would differ
            .Select(seg => seg.Text.Length == 0 ? "" : seg.Lead + seg.Text));

    //two cells kept free at the right edge for wrapped prose
    public const int FrameMargin = 2;


}
