using System.Globalization;
using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

public enum ShellView { Window, All }

public enum ShellInk { Ok, Warn, Err, ErrLine, Dim, Link, OutLine }

public enum ShellAction { None, ShowAll, ShowLess }

public sealed record ShellBlockInput(string Command, ShellOutput Output, int ResultChars, string? RawGloss, string PaintedGloss,
    bool HasResult, string Role, RgbColor? BulletTint = null, string? FollowUp = null, string? Marker = null)
{
    public static ShellBlockInput Live(string command, string role, string? marker) =>
        new(command, ShellOutput.Empty, 0, null, "", HasResult: false, role, Marker: marker);
}

public readonly record struct ShellBlockState(bool Collapsed, ShellView View, int OutputTop, int CommandFromTail)
{
    public static ShellBlockState Closed => new(true, ShellView.Window, 0, 0);
}

public sealed record ShellBlockLayout(IReadOnlyList<RenderedRow> Rows,
    int CommandFirst, int CommandShown, int CommandTotal,
    int ResultRow, int LinkStart, int LinkEnd,
    int OutputFirst, int OutputShown, int OutputTotal,
    int FooterRow, int ActionStart, int ActionEnd, ShellAction FooterAction, bool Hides, bool Opens = false);

//every row of a shell block and the map of those rows, one function the paint, the mouse and the keys all read
public static class ShellBlockRender
{
    public const int CommandWindow = 3;
    public const int OutputWindow = ToolWindow.Height;
    public const int PlainLineCells = 120;
    public const string ExpandWords = "click to expand";
    public const string CollapseWords = "click to collapse";

    public static ShellBlockLayout Layout(ShellBlockInput input, ShellBlockState state, int width, Theme theme, GlyphSet g)
    {
        var budget = width > 0 ? width : int.MaxValue;
        var rows = new List<RenderedRow>();
        var command = TermText.SanitizeProse(input.Command);
        var marker = input.Marker ?? theme.Paint(ItemRender.ToolMarkerOf(g), input.BulletTint ?? theme.RoleTint(input.Role));
        var leadCells = UnicodeWidth.Of(TermText.StripAnsiForWidth(marker)) + 1 + "shell".Length;
        var head = marker + " " + theme.Paint("shell", Theme.ToolName);
        var lines = command.Split('\n');
        var inline = lines.Length == 1 && (width <= 0 || leadCells + 1 + UnicodeWidth.Of(command) <= width);

        if (inline)
            rows.Add(new RenderedRow(command.Length > 0 ? head + " " + ItemRender.PaintToolArgs("shell", command, theme) : head, false));
        else
            rows.Add(new RenderedRow(Fit(head + (lines.Length > 1 ? theme.Paint($" {g.Dot} {Plural.Of(lines.Length, "line")}", Theme.Dim) : ""), width, g), false));

        var all = inline ? new List<RenderedRow>() : CommandRows(command, width, theme);
        var open = !state.Collapsed;
        var showAll = open && state.View == ShellView.All;
        var maxFromTail = Math.Max(0, all.Count - CommandWindow);
        var from = showAll ? 0 : Math.Max(0, all.Count - CommandWindow - Math.Clamp(state.CommandFromTail, 0, maxFromTail));
        var shown = showAll ? all : all.Skip(from).Take(CommandWindow).ToList();
        var commandFirst = rows.Count;
        rows.AddRange(shown);

        var entries = Entries(input.Output);
        var hidesCommand = all.Count > CommandWindow;
        var hidesOutput = entries.Count > OutputWindow;
        var hides = hidesCommand || hidesOutput;
        int resultRow = -1, linkStart = 0, linkEnd = 0;
        //a command off the header shows only in the open block
        var opens = !inline && command.Length > 0;
        if (input.HasResult)
        {
            var prefix = GutterWrap.Hang + theme.Paint(g.Elbow, Theme.Dim) + " ";
            var prefixCells = GutterWrap.Hang.Length + UnicodeWidth.Of(g.Elbow) + 1;
            var gloss = input.PaintedGloss;
            if (ShellOutput.ExitCodeOf(input.RawGloss) is int exit)
            {
                var room = budget == int.MaxValue ? budget : budget - prefixCells;
                List<(string Text, ShellInk Ink)> Words(bool isOpen)
                {
                    var only = isOpen ? null : OnlyLine(input.Output, exit);
                    var words = ResultWords(input.Output, exit, isOpen, input.ResultChars, entries.Count, g, only, hidesCommand).ToList();
                    //the whole line or the link, a cut line would pass for the full output
                    return only is not null && words.Sum(p => UnicodeWidth.Of(Clean(p))) > room
                        ? ResultWords(input.Output, exit, isOpen, input.ResultChars, entries.Count, g).ToList() : words;
                }
                var shownWords = Words(open);
                (gloss, linkStart, linkEnd) = PaintWords(shownWords, room, theme, g);
                if (linkEnd > linkStart) { linkStart += prefixCells; linkEnd += prefixCells; }
                //read from the closed block's row, the link means the output holds more, and a cut row hides the rest of its line
                var closed = open ? Words(false) : shownWords;
                opens |= closed.Any(p => p.Ink == ShellInk.Link) || closed.Sum(p => UnicodeWidth.Of(Clean(p))) > room;
            }
            else opens |= width > 0 && prefixCells + UnicodeWidth.Of(TermText.StripAnsiForWidth(gloss)) > width;
            resultRow = rows.Count;
            rows.Add(new RenderedRow(Fit(prefix + gloss, width, g), false, 0, prefixCells));
            if (linkEnd > budget) linkEnd = budget;
            if (input.FollowUp is { } follow)
                rows.Add(new RenderedRow(Fit(GutterWrap.Hang + theme.Paint(TermText.Sanitize(follow), Theme.Dim), width, g), false));
        }

        int outputFirst = -1, outputShown = 0, footerRow = -1, actionStart = 0, actionEnd = 0;
        var action = ShellAction.None;
        if (input.HasResult && open)
        {
            var window = ToolWindow.Layout(new WindowInput(entries, NumbersAlways: false, "line", hidesCommand, "show full command"),
                new WindowState(state.View, state.OutputTop), width, theme, g);
            outputFirst = rows.Count;
            outputShown = window.Shown;
            if (window.FooterRow >= 0) footerRow = outputFirst + window.FooterRow;
            (actionStart, actionEnd, action) = (window.ActionStart, window.ActionEnd, window.Action);
            rows.AddRange(window.Rows);
        }

        return new ShellBlockLayout(rows, commandFirst, shown.Count, all.Count, resultRow, linkStart, linkEnd,
            outputFirst, outputShown, entries.Count, footerRow, actionStart, actionEnd, action, hides, opens);
    }

    //the words of the result row, in pieces so the paint and the plain renderer share them
    public static IReadOnlyList<(string Text, ShellInk Ink)> ResultWords(ShellOutput output, int exit, bool open, int resultChars, int entries, GlyphSet g,
        string? only = null, bool opens = true)
    {
        var dot = $" {g.Dot} ";
        var link = open ? CollapseWords : ExpandWords;
        var tok = resultChars > 0 ? $"{dot}~{InputFrame.KFormat(resultChars / 4)} tok" : "";
        var lastErr = output.LastStderr;
        var w = new List<(string, ShellInk)>();
        if (exit == 0)
        {
            w.Add((g.Ok, ShellInk.Ok));
            if (entries == 0) { w.Add((" ran" + dot + "no output", ShellInk.Dim)); return w; }
            w.Add((" ran", ShellInk.Dim));
            //a count and no text, stderr on a clean exit is often progress
            if (lastErr is not null) { w.Add((dot, ShellInk.Dim)); w.Add(($"stderr {output.Stderr.Count.ToString(CultureInfo.InvariantCulture)}", ShellInk.Warn)); }
            if (only is not null) { w.Add((dot, ShellInk.Dim)); w.Add((only, ShellInk.OutLine)); }
            if (only is null || opens) { w.Add((dot, ShellInk.Dim)); w.Add((link, ShellInk.Link)); }
            if (tok.Length > 0) w.Add((tok, ShellInk.Dim));
            return w;
        }
        if (!output.StdoutBlank)
        {
            w.Add((g.Ok, ShellInk.Warn));
            w.Add((" ran" + dot, ShellInk.Dim));
            w.Add(($"exit {exit.ToString(CultureInfo.InvariantCulture)}", ShellInk.Warn));
            if (lastErr is not null) { w.Add((dot, ShellInk.Dim)); w.Add((lastErr, ShellInk.ErrLine)); }
            w.Add((dot, ShellInk.Dim));
            w.Add((link, ShellInk.Link));
            if (tok.Length > 0) w.Add((tok, ShellInk.Dim));
            return w;
        }
        w.Add((g.Bad, ShellInk.Err));
        w.Add(($" exit {exit.ToString(CultureInfo.InvariantCulture)}", ShellInk.Err));
        if (lastErr is not null) { w.Add((dot, ShellInk.Dim)); w.Add((lastErr, ShellInk.ErrLine)); }
        if (entries > (lastErr is null ? 0 : 1)) { w.Add((dot, ShellInk.Dim)); w.Add((link, ShellInk.Link)); }
        return w;
    }

    //the result words for a surface with no click, the link and its separator dropped
    public static string PlainWords(ShellOutput output, int exit, int resultChars, GlyphSet g)
    {
        var entries = Entries(output).Count;
        var only = OnlyLine(output, exit);
        if (only is not null && UnicodeWidth.Of(TermText.Sanitize(only)) > PlainLineCells) only = null;   //no width to fit here, so a long line stays out
        var words = ResultWords(output, exit, open: false, resultChars, entries, g, only, opens: false).ToList();
        var i = words.FindIndex(p => p.Ink == ShellInk.Link);
        if (i > 0) words.RemoveRange(i - 1, 2);
        return string.Concat(words.Select(Clean));
    }

    //the one line a clean exit printed, null when there is more to show than that
    private static string? OnlyLine(ShellOutput o, int exit) =>
        exit == 0 && o.Stdout.Count == 1 && !o.StdoutBlank && o.LastStderr is null && o.Harness.Count == 0 ? o.Stdout[0] : null;

    private static string Clean((string Text, ShellInk Ink) p) =>
        p.Ink is ShellInk.ErrLine or ShellInk.OutLine ? TermText.Sanitize(p.Text) : p.Text;

    //the command rows under the header, each hung by two cells and soft-wrapped, in the powershell colours on a truecolour terminal
    internal static List<RenderedRow> CommandRows(string text, int width, Theme theme)
    {
        var rows = new List<RenderedRow>();
        if (text.Length == 0) return rows;
        var roles = theme.TrueColor ? SyntaxHighlight.Roles(CodeLanguage.PowerShell, text) : null;
        if (roles is not null && roles.Length != text.Length) roles = null;
        var budget = width > 0 ? Math.Max(1, width - GutterWrap.Hang.Length) : 0;
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var segs = SoftWrap.Wrap(line, budget, budget);
            if (segs.Count == 0) segs = new[] { new WrapSeg("", 0) };
            var start = 0;
            for (var s = 0; s < segs.Count; s++)
            {
                var seg = segs[s].Text;
                var painted = roles is not null && string.CompareOrdinal(line, start, seg, 0, seg.Length) == 0
                    ? theme.PaintRuns(SyntaxHighlight.Runs(text, roles, offset + start, seg.Length), Theme.ToolArgs, CodeLanguage.PowerShell)
                    : theme.Paint(seg, Theme.ToolArgs);
                rows.Add(new RenderedRow(GutterWrap.Hang + painted, s > 0, GutterWrap.Hang.Length, s > 0 ? 0 : GutterWrap.Hang.Length));
                start += segs[s].SourceChars;
            }
            offset += line.Length + 1;
        }
        return rows;
    }

    //a harness line has no number and sits at the text column, so it is a plain row in the note ink
    internal static List<ToolBodyRow> Entries(ShellOutput o)
    {
        var list = new List<ToolBodyRow>();
        var n = 0;
        if (!o.StdoutBlank) foreach (var l in o.Stdout) list.Add(new ToolBodyRow(l, BodyKind.Numbered, BodyInk.Text, ++n));
        if (o.LastStderr is not null) foreach (var l in o.Stderr) list.Add(new ToolBodyRow(l, BodyKind.Numbered, BodyInk.Err, ++n));
        foreach (var h in o.Harness) list.Add(new ToolBodyRow(h, BodyKind.Plain, BodyInk.Note));
        return list;
    }

    //the stderr piece is cut first, so the link and the token count stay while any stderr text fits
    private static (string Painted, int LinkStart, int LinkEnd) PaintWords(IReadOnlyList<(string Text, ShellInk Ink)> words, int room, Theme theme, GlyphSet g)
    {
        var pieces = words.Select(p => (Text: Clean(p), p.Ink)).ToList();
        var total = pieces.Sum(p => UnicodeWidth.Of(p.Text));
        var errAt = pieces.FindIndex(p => p.Ink == ShellInk.ErrLine);
        if (total > room && errAt >= 0)
        {
            var avail = room - (total - UnicodeWidth.Of(pieces[errAt].Text));
            if (avail >= 1 + UnicodeWidth.Of(g.Ellipsis)) pieces[errAt] = (TermText.TruncateCells(pieces[errAt].Text, avail, g), ShellInk.ErrLine);
            else pieces.RemoveRange(errAt - 1, 2);
        }
        var sb = new System.Text.StringBuilder();
        int col = 0, linkStart = 0, linkEnd = 0;
        foreach (var (text, ink) in pieces)
        {
            if (ink == ShellInk.Link) { linkStart = col; linkEnd = col + UnicodeWidth.Of(text); }
            sb.Append(Ink(theme, text, ink));
            col += UnicodeWidth.Of(text);
        }
        return (sb.ToString(), linkStart, linkEnd);
    }

    private static string Ink(Theme theme, string text, ShellInk ink) => ink switch
    {
        ShellInk.Ok => theme.Paint(text, Theme.Ok),
        ShellInk.Warn => theme.Paint(text, Theme.Warn),
        ShellInk.Err or ShellInk.ErrLine => theme.Paint(text, Theme.Err),
        ShellInk.Link => theme.Paint(text, Theme.Thought),
        ShellInk.OutLine => theme.Paint(text, Theme.CodeBlockFg),
        _ => theme.Paint(text, Theme.Dim),
    };

    private static string Fit(string painted, int width, GlyphSet g) =>
        width > 0 ? TermText.TruncateCells(painted, width, g) : painted;
}
