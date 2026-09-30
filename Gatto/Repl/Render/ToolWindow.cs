using System.Globalization;
using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the marks painter takes a body row's index, its sanitized text, a start and a length, and paints that piece. the footer part takes the top and the rows shown
public sealed record WindowInput(IReadOnlyList<ToolBodyRow> Body, bool NumbersAlways, string Unit,
    bool HidesOutside = false, string? OutsideAction = null, Func<int, string, int, int, string>? Marks = null,
    Func<int, int, string?>? FooterPart = null);

public readonly record struct WindowState(ShellView View, int Top);

//row indices count from the window's own first row, the caller adds its offset
public sealed record WindowLayout(IReadOnlyList<RenderedRow> Rows, int Shown, int Total, int Top,
    int FooterRow, int ActionStart, int ActionEnd, ShellAction Action, bool Hides);

//the open body of a tool block, a window of rows with a footer that counts what it hides, or every row wrapped
public static class ToolWindow
{
    public const int Height = 5;

    //what every row of one body shares, so a row is painted from its own fields and these
    private sealed record Frame(int W, bool Signs, string Rail, int RailCells, int TextCol, int Width, Theme Theme, GlyphSet G,
        Func<int, string, int, int, string>? Marks);

    public static WindowLayout Layout(WindowInput input, WindowState state, int width, Theme theme, GlyphSet g)
    {
        var body = input.Body;
        var rows = new List<RenderedRow>();
        var showAll = state.View == ShellView.All;
        var taller = body.Count > Height;
        var max = body.Max(r => r.Number) ?? 0;
        //numbers tell the position in a long body, a short one reads them as data unless the caller says they are
        var w = (input.NumbersAlways || taller) && max > 0 ? max.ToString(CultureInfo.InvariantCulture).Length : 0;
        var signs = body.Any(r => r.Kind is BodyKind.Added or BodyKind.Removed);
        var rail = theme.Paint($"{g.Box.Vertical} ", Theme.Dim);
        var railCells = UnicodeWidth.Of(g.Box.Vertical) + 1;
        //a body with changed rows holds a sign after the number, so every row of it leaves that column
        var textCol = railCells + 2 + (w > 0 ? w + (signs ? 1 : 2) : 0) + (signs ? 3 : 0);
        var f = new Frame(w, signs, rail, railCells, textCol, width, theme, g, input.Marks);
        var top = showAll ? 0 : Rest(body, state.Top);
        var bodyShown = showAll ? body.Count : Shown(body, top);
        var cut = false;
        //a match at the top keeps its path above it, added so no row of the body is hidden
        if (!showAll && Sticky(body, top)) cut |= AddRow(rows, body[top].Heading, body[body[top].Heading], false, f);
        for (var i = showAll ? 0 : top; i < (showAll ? body.Count : top + bodyShown); i++)
            cut |= AddRow(rows, i, body[i], showAll, f);
        //a row cut at the window's edge hides its rest as much as a row below the window does
        var hides = taller || input.HidesOutside || cut;
        //a last position with fewer rows keeps the window's height, so the transcript under it does not move
        if (!showAll && taller && rows.Count < Height) rows.Add(new RenderedRow(Fit(rail, width, g), true, railCells, 0));
        var shown = rows.Count;

        int footerRow = -1, actionStart = 0, actionEnd = 0;
        var action = ShellAction.None;
        //show all draws show less only while the window would hide something at this width
        if (hides)
        {
            var parts = new List<(string Text, bool Action)>();
            if (showAll)
            {
                parts.Add(("show less", true));
                parts.Add(($" {g.Dot} {Plural.Of(body.Count, input.Unit)}", false));
                action = ShellAction.ShowLess;
            }
            else
            {
                if (top > 0) parts.Add(($"{g.Up} {top} above {g.Dot} ", false));
                if (taller)
                {
                    var below = body.Count - (top + bodyShown);
                    parts.Add((below > 0 ? $"{g.Ellipsis} {below} more" : "end of output", false));
                    parts.Add(($" {g.Dot} ", false));
                }
                var partAt = -1;
                if (input.FooterPart?.Invoke(top, bodyShown) is { } part)
                {
                    partAt = parts.Count;
                    parts.Add((part, false));
                    parts.Add(($" {g.Dot} ", false));
                }
                parts.Add((taller || !input.HidesOutside ? "show all" : input.OutsideAction ?? "show all", true));
                action = ShellAction.ShowAll;
                //a footer wider than the window drops the count of other changes, then the rows above, so the action keeps its words
                int Cells() => railCells + 2 + parts.Sum(p => UnicodeWidth.Of(p.Text));
                if (width > 0 && partAt >= 0 && Cells() > width) parts.RemoveRange(partAt, 2);
                if (width > 0 && top > 0 && Cells() > width) parts.RemoveAt(0);
            }
            var col = railCells + 2;
            var painted = rail + "  ";
            foreach (var (text, isAction) in parts)
            {
                if (isAction) { actionStart = col; actionEnd = col + UnicodeWidth.Of(text); }
                painted += theme.Paint(text, isAction ? Theme.Thought : Theme.Dim);
                col += UnicodeWidth.Of(text);
            }
            footerRow = rows.Count;
            rows.Add(new RenderedRow(Fit(painted, width, g), false, 0, railCells + 2));
            if (width > 0 && actionEnd > width) actionEnd = Math.Max(actionStart, width);
        }
        return new WindowLayout(rows, shown, body.Count, top, footerRow, actionStart, actionEnd, action, hides);
    }

    //the last position the window rests on, the first whose rows reach the body's end
    public static int MaxTop(IReadOnlyList<ToolBodyRow> body)
    {
        if (body.Count <= Height) return 0;
        var t = 0;
        while (Alias(body, t) || t + Shown(body, t) < body.Count) t++;
        return t;
    }

    //moves up to n positions, a positive n toward row 0, and a skipped position costs nothing, so the units used and the rows moved can differ
    public static (int Top, int Used) Step(IReadOnlyList<ToolBodyRow> body, int top, int n)
    {
        var max = MaxTop(body);
        var at = Rest(body, top);
        var dir = n > 0 ? -1 : 1;
        var used = 0;
        while (used < Math.Abs(n))
        {
            var next = at + dir;
            if (Alias(body, next)) next += dir;
            if (next < 0 || next > max) break;
            at = next;
            used++;
        }
        return (at, n > 0 ? used : -used);
    }

    //a top clamped to the positions the window rests on, one that draws the rows of the position above it is drawn as that one
    private static int Rest(IReadOnlyList<ToolBodyRow> body, int top)
    {
        var t = Math.Clamp(top, 0, MaxTop(body));
        return Alias(body, t) ? t - 1 : t;
    }

    private static bool Sticky(IReadOnlyList<ToolBodyRow> body, int t) =>
        t >= 0 && t < body.Count && body.Count > Height && body[t].Kind == BodyKind.Numbered && body[t].Heading >= 0;

    //the first match under a path draws the same rows as the path's own position
    private static bool Alias(IReadOnlyList<ToolBodyRow> body, int t) => Sticky(body, t) && body[t].Heading == t - 1;

    private static int Shown(IReadOnlyList<ToolBodyRow> body, int t) => Math.Min(Sticky(body, t) ? Height - 1 : Height, body.Count - t);

    //a heading or a note sits at the rail, any other row at the text column behind its number and its sign. true when the window cuts the row, or would in show all
    private static bool AddRow(List<RenderedRow> rows, int index, ToolBodyRow r, bool showAll, Frame f)
    {
        var theme = f.Theme;
        var atRail = r.Kind is BodyKind.Heading or BodyKind.Note;
        var col = atRail ? f.RailCells : f.TextCol;
        RgbColor? ground = r.Kind switch { BodyKind.Added => Theme.DiffAddedBg, BodyKind.Removed => Theme.DiffRemovedBg, _ => null };
        var number = f.W > 0
            ? theme.Paint(r.Number is int n ? n.ToString(CultureInfo.InvariantCulture).PadLeft(f.W) : new string(' ', f.W), Theme.Dim) + (f.Signs ? " " : "  ")
            : "";
        var sign = !f.Signs ? "" : r.Kind switch
        {
            BodyKind.Added => theme.Paint("+", Theme.Ok) + "  ",
            BodyKind.Removed => theme.Paint("-", Theme.Err) + "  ",
            _ => "   ",
        };
        var lead = atRail ? "" : number + sign;
        var clean = TermText.Sanitize(r.Text);
        var room = f.Width > 0 ? Math.Max(1, f.Width - col) : int.MaxValue;
        var marked = f.Marks is not null && !atRail && r.Ink == BodyInk.Text;
        if (!showAll)
        {
            var cut = TermText.TruncateCells(clean, room, f.G);
            string painted;
            if (!marked) painted = Ink(theme, cut, r.Ink);
            else if (cut == clean) painted = f.Marks!(index, clean, 0, clean.Length);
            else
            {
                //the kept head keeps its marks and the ellipsis takes the text ink, since the ellipsis is not in the source
                var mark = UnicodeWidth.Of(f.G.Ellipsis) >= room ? "" : f.G.Ellipsis;
                var head = cut[..(cut.Length - mark.Length)];
                painted = f.Marks!(index, clean, 0, head.Length) + Ink(theme, mark, r.Ink);
            }
            rows.Add(new RenderedRow(Fit(Compose(f, atRail, lead, painted, ground), f.Width, f.G), false, 0, col));
            return cut != clean;
        }
        var segs = SoftWrap.Wrap(clean, room == int.MaxValue ? 0 : room, room == int.MaxValue ? 0 : room);
        if (segs.Count == 0) segs = new[] { new WrapSeg("", 0) };
        var start = 0;
        for (var s = 0; s < segs.Count; s++)
        {
            var seg = segs[s].Text;
            var pre = s == 0 ? lead : new string(' ', col - f.RailCells - (atRail ? 0 : 2));
            var ink = marked && string.CompareOrdinal(clean, start, seg, 0, seg.Length) == 0 ? f.Marks!(index, clean, start, seg.Length) : Ink(theme, seg, r.Ink);
            rows.Add(new RenderedRow(Fit(Compose(f, atRail, pre, ink, ground), f.Width, f.G), s > 0, col, s > 0 ? 0 : col));
            start += segs[s].SourceChars;
        }
        return TermText.TruncateCells(clean, room, f.G) != clean;
    }

    //the rail, then the lead and the text, on the row's ground from the number to the row's end when it has one
    private static string Compose(Frame f, bool atRail, string lead, string painted, RgbColor? ground)
    {
        var railLead = f.Rail + (atRail ? "" : "  ");
        return ground is { } bg ? railLead + f.Theme.Ground(lead + painted, bg) : railLead + lead + painted;
    }

    private static string Ink(Theme theme, string text, BodyInk ink) => ink switch
    {
        BodyInk.Heading => theme.Paint(text, Theme.ToolArgs),
        BodyInk.Note => theme.Paint(text, Theme.Dim),
        BodyInk.Err => theme.Paint(text, Theme.Err),
        _ => theme.Paint(text, Theme.CodeBlockFg),
    };

    private static string Fit(string painted, int width, GlyphSet g) =>
        width > 0 ? TermText.TruncateCells(painted, width, g) : painted;
}
