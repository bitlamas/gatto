using System.Globalization;
using System.Text;
using Gatto.Repl.Commands;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl.Input;

//a snapshot of the status line's facts
public sealed record StatusInfo(
    string Cwd, string Model, string Role, CtxState Ctx, string UserProfile,
    string? Branch = null, long TokensUp = 0, long TokensDown = 0, bool Wild = false,
    string? Thinking = null, bool ThinkingToggle = false,
    IReadOnlyList<StatusHint>? Hints = null,   //the live hints in the ruled order, the paste hint first
    int HintIndex = 0,          //the chosen hint's place in the composed list, clamped against it at compose
    string? ChordHint = null,   //the live Esc·Esc / Ctrl+C·Ctrl+C hint, drawn in the top rule's label slot
    bool Cloud = false,         //the endpoint is in the cloud, and the mark stands before the model
    Gatto.Core.Client.QuotaReading? Quota = null,
    decimal? Cost = null,       //the session sum of what the provider reported as cost, null when it reported none
    string? SessionName = null);   //the name /name gave the session, which takes the word gatto's place on the top rule

//which hint a status slot holds, what a refresh matches on, since a hint's words can change while it stays the same hint
public enum HintKind { Paste, Focus, Teaser, Background }

//one status hint and its forms, longest first, each naming the key and what it does
public sealed record StatusHint(HintKind Kind, IReadOnlyList<string> Forms);

//what a press on the status line can land on
public enum StatusCell { Folder, Branch, Model, Tokens, Ctx, Background, Quota, Cost, Hint, HintNext }

//what runs between turns as the painter reads it under the gate at each paint, never stored on the status record
public readonly record struct BackgroundFacts(int Running, bool WatchArmed, bool WatchPaused, bool CellSelected, bool CellHovered = false)
{
    //the cell is on while a shell runs or a watch is armed
    public bool CellOn => Running > 0 || WatchArmed;
}

//one painted piece of the status line and its columns, the start inclusive and the end exclusive
public readonly record struct StatusCellSpan(StatusCell Kind, int Start, int End);

//the composed rows and the caret's row and column. the Composer field is the layout a mouse gesture must map through, null in an unframed frame
public sealed record FrameLayout(
    IReadOnlyList<string> Rows, IReadOnlyList<string> VisibleRows, IReadOnlyList<int> ScreenRows,
    int TotalRows, int CursorRowOffset, int CursorCol,
    IReadOnlyList<(bool Continuation, int PrefixCells)> EditorRowTags,
    ComposerLayout? Composer = null,
    int PanelRowCount = 0,
    int EditorWindowTop = 0,       //the first composer region row emitted, 0 when every row is
    int CursorRegionRow = 0,       //the caret's composer region row, counted over every row, emitted or not
    IReadOnlyList<int>? MarkRows = null,   //the editor rows drawn as a hidden-rows mark, which stand for no line a press could land on
    IReadOnlyList<StatusCellSpan>? StatusCells = null,   //the status line's pieces by column, what a press on that row resolves through
    IReadOnlyList<HintKind>? StatusHints = null,   //the composed hint list, the one a triangle steps through
    int StatusHintShown = -1);   //the index of the hint painted, -1 when none fit

//the framed typing area: top rule with its role label, null on the default role, editor rows, bottom rule, status line. composes layout only, ChromePainter writes every byte
public sealed class InputFrame(ITermSurface surface, Theme theme, string? role, StatusInfo status,
    GlyphSet? glyphs, Func<IReadOnlyList<SlashCommands.Cmd>>? knownCommands = null)
{
    //the rows the ghost completes from, the same source the editor's Tab reads. null is the built-in table, for a frame with no session behind it
    private readonly Func<IReadOnlyList<SlashCommands.Cmd>> _known = knownCommands ?? (() => SlashCommands.All);

    //the run's glyph set, handed down from the session (two painters resolving it themselves could disagree about the host)
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    private const int MinFramedWidth = 20;

    //pure layout, no pixels (ChromePainter owns the chrome stack). panelRows swaps in a modal prompt's rows: no composer layout, and view goes unread
    public FrameLayout Compose(EditorView view, IReadOnlyList<string>? panelRows = null,
        (int Row, int Col)? panelCaret = null, (int Top, int Count)? editorWindow = null, bool markHidden = false,
        BackgroundFacts facts = default)
    {
        var width = surface.Width;
        var framed = width >= MinFramedWidth;
        var lines = view.Lines.Count > 0 ? view.Lines : new[] { "" };

        //row order: top rule, editor or panel rows, then bottom rule and status
        var rendered = new List<string>();
        var visibleRows = new List<string>();
        var screenRows = new List<int>();

        if (framed)
        {
            var (topR, topV) = TopRuleCore(role, status.Wild, width, theme, status.ChordHint, _glyphs, status.SessionName);
            rendered.Add(topR);
            visibleRows.Add(topV);
            screenRows.Add(UnicodeWidth.Rows(topV, width));
        }

        var editorStart = rendered.Count;

        //the bottom rule and status pair go out together when framed, in both the editor and the panel branch (ChromePainter's editorStart arithmetic needs it)
        StatusRow? statusRow = null;
        void AddFooter()
        {
            if (!framed) return;
            var bottomV = RuleOf(_glyphs, width);
            rendered.Add(theme.Paint(bottomV, Theme.Rule));
            visibleRows.Add(bottomV);
            screenRows.Add(1);

            var row = StatusCore(status, facts, width, theme, _glyphs);
            statusRow = row;
            rendered.Add(row.Rendered);
            visibleRows.Add(row.Visible);
            screenRows.Add(UnicodeWidth.Rows(row.Visible, width));
        }

        if (panelRows is { Count: > 0 })
        {
            foreach (var r in panelRows)
            {
                //panel rows can be stale after a pure resize, so cut them to the current width. a soft-wrapped row would cost a physical row the caret park counts as one
                var vis = TermText.StripAnsiForWidth(r);
                var ren = r;
                if (width > 0 && UnicodeWidth.Of(vis) > width)
                {
                    vis = TermText.TruncateCells(vis, width, glyphs: _glyphs);
                    ren = TermText.TruncateCells(ren, width, glyphs: _glyphs);
                }
                rendered.Add(ren);
                visibleRows.Add(vis);
                screenRows.Add(UnicodeWidth.Rows(vis, width));
            }
            AddFooter();

            var panelTotal = 0;
            foreach (var r in screenRows) panelTotal += r;

            //park at the panel's own input point if it names one, else at the end of the last row. the column caps at width-1 like the terminal's own clamp
            var caretIdx = editorStart + (panelCaret is { } pc && pc.Row >= 0 && pc.Row < panelRows.Count
                ? pc.Row : panelRows.Count - 1);
            var panelCursorRow = 0;
            for (var i = 0; i < caretIdx; i++) panelCursorRow += screenRows[i];
            var caretCells = panelCaret is { } pcc && pcc.Row >= 0 && pcc.Row < panelRows.Count
                ? Math.Max(0, pcc.Col) : UnicodeWidth.Of(visibleRows[caretIdx]);
            var panelCol = width > 0 ? Math.Min(caretCells, width - 1) : caretCells;

            return new FrameLayout(rendered, visibleRows, screenRows, panelTotal, panelCursorRow, panelCol,
                Array.Empty<(bool, int)>(), Composer: null, PanelRowCount: panelRows.Count,
                StatusCells: statusRow?.Cells, StatusHints: statusRow?.Hints, StatusHintShown: statusRow?.Shown ?? -1);
        }

        //2 cells for the prompt or hang and 1 for the \ mark, so a marked row never reaches the edge. the \ belongs to a logical join, so a visual wrap shows none
        var budget = width > 3 ? width - 3 : 0;

        //the highlight and the mouse hit test both read this one wrap, so neither can derive its own. layout only translates SelStart and SelEnd into row and cell
        var layout = new ComposerLayout(lines, width);
        var hasSelection = view.SelStart is not null && view.SelEnd is not null;
        var (selStartRow, selStartCell) = hasSelection ? layout.PositionToCell(view.SelStart!.Value.Line, view.SelStart!.Value.Col) : (0, 0);
        var (selEndRow, selEndCell) = hasSelection ? layout.PositionToCell(view.SelEnd!.Value.Line, view.SelEnd!.Value.Col) : (0, 0);

        //the ghost comes from the same function Tab calls, so the hint and what Tab types can't disagree. index 0, a Tab leaves the whole command name in the buffer
        var completion = CommandHint.For(lines, view.CursorLine, view.CursorCol, _known());
        var ghost = completion.Any ? completion.Hint(0) : "";

        //tag each editor row as a continuation or a logical head right in the loop that already knows it. a continuation row's 2-space hang counts as chrome
        var editorRowTags = new List<(bool Continuation, int PrefixCells)>();
        var markRows = new List<int>();
        var regionRow = 0;   //a flat row counter in ComposerLayout's RegionRow space
        var windowTop = editorWindow is { } ew ? Math.Clamp(ew.Top, 0, Math.Max(0, layout.RowCount - 1)) : 0;
        var windowEnd = editorWindow is null ? int.MaxValue : windowTop + Math.Max(1, editorWindow.Value.Count);
        //the window's edge rows say how many rows it hides, the painter keeps the caret off them
        var markAbove = markHidden && editorWindow is not null && windowTop > 0;
        var markBelow = markHidden && editorWindow is not null && windowEnd < layout.RowCount;
        for (var li = 0; li < lines.Count; li++)
        {
            var segs = layout.SegsPerLine[li];   //the drawn text, tabs already widened, so no raw tab reaches the terminal
            for (var si = 0; si < segs.Count; si++)
            {
                //a row outside the window is skipped whole, the highlight below is decided per region row so the rows kept need no remapping
                if (regionRow < windowTop || regionRow >= windowEnd) { regionRow++; continue; }
                if ((markAbove && regionRow == windowTop) || (markBelow && regionRow == windowEnd - 1))
                {
                    var markText = "  " + (regionRow == windowTop && markAbove
                        ? $"{_glyphs.Up} {windowTop + 1} more" : $"{_glyphs.Down} {layout.RowCount - windowEnd + 1} more");
                    rendered.Add(theme.Paint(markText, Theme.Dim));
                    visibleRows.Add(markText);
                    screenRows.Add(UnicodeWidth.Rows(markText, width));
                    markRows.Add(editorRowTags.Count);
                    editorRowTags.Add((false, 0));
                    regionRow++;
                    continue;
                }
                var isPrompt = li == 0 && si == 0;
                var join = li < lines.Count - 1 && si == segs.Count - 1;
                var textStyle = Theme.Bright;
                var prompt = isPrompt
                    ? theme.Paint(_glyphs.Prompt, Theme.Accent, bold: true) + " "
                    : "  ";
                var mark = join ? theme.Paint("\\", Theme.Dim) : "";
                var textRun = theme.Paint(segs[si].Text, textStyle);
                //paints only the selected cells of the text run, the prompt, hang and mark stay plain. only rendered changes, so the erase math and row count stay put
                if (hasSelection && regionRow >= selStartRow && regionRow <= selEndRow)
                {
                    var rowContentCells = UnicodeWidth.Of(segs[si].Text);
                    const int prefixCells = 2;
                    var rowStartCell = regionRow == selStartRow ? selStartCell : prefixCells;
                    var rowEndCell = regionRow == selEndRow ? selEndCell : prefixCells + rowContentCells;
                    var selStartInRun = Math.Max(rowStartCell - prefixCells, 0);
                    var selEndInRun = Math.Min(rowEndCell - prefixCells, rowContentCells);
                    if (selEndInRun > selStartInRun)
                        textRun = TermText.HighlightCells(textRun, selStartInRun, selEndInRun, theme.SelectionBgOn);
                }
                //the ghost draws only when it fits the row budget, dropped whole. it goes into visible as well as rendered, or the painter leaves stale ink when it shrinks
                var ghostRun = ghost.Length > 0
                    && UnicodeWidth.Of(segs[si].Text) + UnicodeWidth.Of(ghost) <= budget
                    ? ghost : "";
                //the ghost is not buffer text, so it is added after the highlight and stays out of the selection's cell math
                rendered.Add(prompt + textRun + (ghostRun.Length > 0 ? theme.Paint(ghostRun, Theme.Dim) : "") + mark);
                //the row can't exceed the width, but the screen-row count is computed anyway so tiny widths keep the erase math sound
                var visible = (isPrompt ? _glyphs.Prompt + " " : "  ") + segs[si].Text + ghostRun + (join ? "\\" : "");
                visibleRows.Add(visible);
                screenRows.Add(UnicodeWidth.Rows(visible, width));
                editorRowTags.Add((si > 0, si > 0 ? 2 : 0));
                regionRow++;
            }
        }

        AddFooter();

        var total = 0;
        foreach (var r in screenRows) total += r;

        //map the cursor through the layout the highlight and the mouse use, a second mapping derived here would drift from it
        var (cursorRegionRow, col) = layout.PositionToCell(view.CursorLine, view.CursorCol);

        //add up the screen rows to reach the cursor's row, a segment is one screen row only until a wide glyph overflows the width. a caret outside the window parks on its nearest edge
        var flatCursorEntry = editorStart + Math.Clamp(cursorRegionRow - windowTop, 0, Math.Max(0, editorRowTags.Count - 1));
        var targetRow = 0;
        for (var i = 0; i < flatCursorEntry; i++) targetRow += screenRows[i];

        return new FrameLayout(rendered, visibleRows, screenRows, total, targetRow, col, editorRowTags,
            framed ? layout : null, EditorWindowTop: windowTop, CursorRegionRow: cursorRegionRow, MarkRows: markRows,
            StatusCells: statusRow?.Cells, StatusHints: statusRow?.Hints, StatusHintShown: statusRow?.Shown ?? -1);
    }

    public static string BuildTopRule(string? role, int width, Theme theme, bool wild = false, string? hint = null,
        GlyphSet? glyphs = null, string? name = null) =>
        TopRuleCore(role, wild, width, theme, hint, glyphs, name).rendered;

    //the ANSI-free half of TopRuleCore, so a test can measure the top rule's width
    public static string BuildTopRuleVisible(string? role, int width, Theme theme, bool wild = false, string? hint = null,
        GlyphSet? glyphs = null, string? name = null) =>
        TopRuleCore(role, wild, width, theme, hint, glyphs, name).visible;

    public static string BuildStatusLine(StatusInfo s, int width, Theme theme, GlyphSet? glyphs, BackgroundFacts facts = default) =>
        StatusCore(s, facts, width, theme, glyphs).Rendered;

    //the status line's pieces by column at one width, so a test asks where a cell is rather than counting
    internal static IReadOnlyList<StatusCellSpan> StatusCellsOf(StatusInfo s, int width, Theme theme, GlyphSet? glyphs,
        BackgroundFacts facts = default) =>
        StatusCore(s, facts, width, theme, glyphs).Cells;

    //internals

    //the status row's separator comes from a method, a const can't read a glyph set (every site that measures it takes the same set)
    private static string SepOf(GlyphSet g) => " " + g.Dot + " ";

    //a rule of n cells, the whole glyph repeated so a twin of two characters can't become one
    private static string RuleOf(GlyphSet g, int n) => string.Concat(Enumerable.Repeat(g.Rule, n));

    //one place builds the top rule. the live hint borrows the label slot so the chrome block keeps its height, and a wide label truncates rather than wrapping
    private static (string rendered, string visible) TopRuleCore(string? role, bool wild, int width, Theme theme,
        string? hint = null, GlyphSet? glyphs = null, string? name = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var sep = SepOf(g);
        //a named session leads with the tab's glyph and the name, the glyph only where the glyph set draws beyond ASCII
        var mark = name is null || g == GlyphSet.Ascii ? "" : TerminalTitle.Glyph + " ";
        var lead = name ?? "gatto";
        var labelVisible = hint ?? (mark + lead + (role is null ? "" : sep + role) + (wild ? sep + "wild" : ""));
        //the 4 cells of overhead: two spaces around the label and the 2-cell closing rule
        var dashes = width - UnicodeWidth.Of(labelVisible) - 4;
        if (dashes < 0) dashes = 0;
        var dashStr = RuleOf(g, dashes);
        var labelRendered = hint is not null
            ? theme.Paint(hint, Theme.Accent)
            : mark + theme.Paint(lead, Theme.Accent, bold: true)
                + (role is null ? "" : theme.Paint(sep, Theme.Dim) + theme.Paint(role, theme.RoleTint(role)))
                + (wild ? theme.Paint(sep, Theme.Dim) + theme.Paint("wild", Theme.Err) : "");
        var rendered = theme.Paint(dashStr, Theme.Rule) + " " + labelRendered + " " + theme.Paint(RuleOf(g, 2), Theme.Rule);
        var visible = dashStr + " " + labelVisible + " " + RuleOf(g, 2);
        if (width > 0 && UnicodeWidth.Of(visible) > width)
        {
            visible = TermText.TruncateCells(visible, width, glyphs: g);
            rendered = TermText.TruncateCells(rendered, width, glyphs: g);
        }
        return (rendered, visible);
    }

    //one footer section: the visible text for the fit and erase math, paired with its themed rendering and what a press on it lands on
    private readonly record struct Part(string Visible, string Rendered, StatusCell Kind);

    //the composed status row, its pieces by column and the hint list a triangle steps through
    private readonly record struct StatusRow(string Rendered, string Visible, IReadOnlyList<StatusCellSpan> Cells,
        IReadOnlyList<HintKind> Hints, int Shown);

    //the minimum gap in cells between the left cluster and the flush-right hint, wide enough that the two never read as one run of words
    internal const int HintGap = 4;

    //the rungs of the shed ladder after the hint, each gives up one more thing than the one before
    private const int DropCtxCount = 1, ShortBranch = 2, DropBranch = 3, FolderLast = 4, ModelMid = 5,
        DropTokens = 6, FolderCapped = 7, ModelShort = 8;

    //quota warns at or below this share of the total, and only when the provider reported a total
    private const int QuotaWarnPercent = 20;

    //quota also warns at this many requests left whatever the total, since a turn with tool calls spends several
    private const int QuotaWarnRemaining = 10;

    //the sections a rung can shorten or drop, each with its forms longest first, so the ladder and the fill speak one vocabulary
    private enum Piece { Folder, Branch, Model, Tokens, Ctx }

    private const int PieceCount = 5;

    //the floor's forms, past the ladder: no folder, and the model as its mark and effort with no name
    private const int FolderGone = 3, ModelBare = 3;

    //the hints the fill composes, in the ruled order. the frame and the Repl's refresh read this one list
    internal static IReadOnlyList<StatusHint> ComposedHints(StatusInfo s, BackgroundFacts facts, GlyphSet g) =>
        facts.CellOn ? [.. s.Hints ?? [], BackgroundHint(facts.CellSelected, g)] : s.Hints ?? [];

    //the background hint's two states, the caret in the composer or the cell selected, one short form each
    internal static StatusHint BackgroundHint(bool selected, GlyphSet g) =>
        new(HintKind.Background, [selected ? "Enter opens" : g.Down + " selects footer"]);

    //one physical row at any width: the ladder finds the base, the fill gives back what fits, then the hint
    private static StatusRow StatusCore(StatusInfo s, BackgroundFacts facts, int width, Theme theme, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var hints = ComposedHints(s, facts, g);
        var kinds = hints.Select(h => h.Kind).ToList();

        //a non-positive width skips the ladder and the fill, the test and plain cases
        if (width <= 0) return Finish(Assemble(s, facts, theme, MapOf(0), g), null);

        int[]? map = null;
        for (var rung = 0; rung <= ModelShort && map is null; rung++)
            if (Fits(MapOf(rung))) map = MapOf(rung);

        //past the last rung the head goes before the tail: no folder, then the model without its name, then the cut
        if (map is null)
        {
            var floor = MapOf(ModelShort);
            floor[(int)Piece.Folder] = FolderGone;
            if (!Fits(floor)) floor[(int)Piece.Model] = ModelBare;
            var cut = Finish(Assemble(s, facts, theme, floor, g), null);
            if (UnicodeWidth.Of(cut.Visible) <= width) return cut;
            var cells = cut.Cells.Where(c => c.Start < width).Select(c => c with { End = Math.Min(c.End, width) }).ToList();
            return cut with
            {
                Visible = TermText.TruncateCells(cut.Visible, width, glyphs: g),
                Rendered = TermText.TruncateCells(cut.Rendered, width, glyphs: g),
                Cells = cells,
            };
        }

        //presence, last dropped first: a section's being there is the fact, so it comes back before any section grows
        TryForm(Piece.Tokens, 0, onlyFrom: 1);
        TryForm(Piece.Branch, 1, onlyFrom: 2);
        TryForm(Piece.Ctx, 0, onlyFrom: 1);

        //length, last shortened first, each piece to the longest of its forms that fits. ctx is never here, its count is a presence
        foreach (var piece in new[] { Piece.Model, Piece.Folder, Piece.Branch })
        {
            var at = map[(int)piece];
            if (piece == Piece.Branch && at == 2) continue;
            for (var f = 0; f < at; f++)
                if (TryForm(piece, f, onlyFrom: at)) break;
        }

        var parts = Assemble(s, facts, theme, map, g);
        if (hints.Count == 0) return Finish(parts, null);

        //the hint last, the chosen one in its longest form that fits, its triangle counted when a second hint is live
        var chosen = Math.Clamp(s.HintIndex, 0, hints.Count - 1);
        var arrow = hints.Count > 1 ? UnicodeWidth.Of(g.Triangle) + 1 : 0;
        var core = VisWidth(parts, g);
        foreach (var form in hints[chosen].Forms)
            if (core + HintGap + arrow + UnicodeWidth.Of(form) <= width)
                return Finish(parts, (form, arrow > 0, chosen));
        return Finish(parts, null);

        bool Fits(int[] m) => VisWidth(Assemble(s, facts, theme, m, g), g) <= width;

        //moves one piece to a form and keeps it when the row still fits, from the form named only
        bool TryForm(Piece piece, int form, int onlyFrom)
        {
            if (map![(int)piece] != onlyFrom) return false;
            var trial = (int[])map.Clone();
            trial[(int)piece] = form;
            if (!Fits(trial)) return false;
            map = trial;
            return true;
        }

        //paints the core, then pads so the hint and its triangle end at the right edge, and maps every piece to its columns
        StatusRow Finish(List<Part> ps, (string Form, bool Arrow, int Index)? hint)
        {
            var (rendered, visible, cells) = Render(ps, theme, g);
            if (hint is not { } h) return new StatusRow(rendered, visible, cells, kinds, -1);
            var slot = UnicodeWidth.Of(h.Form) + (h.Arrow ? UnicodeWidth.Of(g.Triangle) + 1 : 0);
            var pad = width - UnicodeWidth.Of(visible) - slot;
            var spaces = new string(' ', pad);
            var start = UnicodeWidth.Of(visible) + pad;
            var r = new StringBuilder(rendered + spaces);
            var v = new StringBuilder(visible + spaces);
            r.Append(theme.Paint(h.Form, Theme.Dim));
            v.Append(h.Form);
            cells.Add(new StatusCellSpan(StatusCell.Hint, start, start + UnicodeWidth.Of(h.Form)));
            start += UnicodeWidth.Of(h.Form);
            if (h.Arrow)
            {
                var right = " " + g.Triangle;
                r.Append(theme.Paint(right, Theme.Dim));
                v.Append(right);
                cells.Add(new StatusCellSpan(StatusCell.HintNext, start, start + UnicodeWidth.Of(right)));
            }
            return new StatusRow(r.ToString(), v.ToString(), cells, kinds, h.Index);
        }
    }

    //the form each piece takes at one rung of the ladder, read against Assemble's rung arms
    private static int[] MapOf(int rung)
    {
        var map = new int[PieceCount];
        map[(int)Piece.Folder] = rung >= FolderCapped ? 2 : rung >= FolderLast ? 1 : 0;
        map[(int)Piece.Branch] = rung >= DropBranch ? 2 : rung >= ShortBranch ? 1 : 0;
        map[(int)Piece.Model] = rung >= ModelShort ? 2 : rung >= ModelMid ? 1 : 0;
        map[(int)Piece.Tokens] = rung >= DropTokens ? 1 : 0;
        map[(int)Piece.Ctx] = rung >= DropCtxCount ? 1 : 0;
        return map;
    }

    //every form of a piece, longest first, an empty form is the piece absent. the mark and the effort ride inside every model form
    private static string[] Forms(Piece piece, StatusInfo s, GlyphSet g)
    {
        switch (piece)
        {
            case Piece.Folder:
                var seg = g.Ellipsis + Path.DirectorySeparatorChar + LastSegment(s.Cwd);
                return [FolderTail(AbbreviateHome(s.Cwd, s.UserProfile), g), seg, HeadTruncate(seg, 12, g), ""];
            case Piece.Branch:
                return s.Branch is null ? ["", "", ""] : [s.Branch, ShortBranchText(s.Branch, g), ""];
            case Piece.Model:
                var effort = EffortWord(s.Thinking, s.ThinkingToggle) is { } e ? "(" + e + ")" : null;
                var mark = s.Cloud ? g.Cloud : null;
                var name = Gatto.Core.Models.ServedModelName.Display(s.Model);
                string Dress(string n) => string.Join(" ", new[] { mark, n, effort }.Where(x => x is not null));
                return [Dress(name), Dress(MiddleName(name, g)), Dress(HeadTruncate(name, 6, g)),
                    string.Join(" ", new[] { mark, effort }.Where(x => x is not null))];
            case Piece.Tokens:
                return s.TokensUp != 0 || s.TokensDown != 0
                    ? [$"{g.Up} {KFormat(s.TokensUp)} {g.Down} {KFormat(s.TokensDown)}", ""]
                    : ["", ""];
            default:
                //keep the count in parentheses, the · separator would make it read as a second section
                if (s.Ctx?.Percent is not int p) return ["", ""];
                return [s.Ctx.Tokens is int t ? $"ctx {p}% ({KFormat(t)})" : $"ctx {p}%", $"ctx {p}%"];
        }
    }

    //the footer's sections at one map of forms. the mark, the effort, ctx and the quota or cost are never shed, the floor keeps the mark and the effort too
    private static List<Part> Assemble(StatusInfo s, BackgroundFacts facts, Theme theme, int[] map, GlyphSet g)
    {
        var parts = new List<Part>();
        void Add(Piece piece, StatusCell kind, RgbColor ink)
        {
            var text = Forms(piece, s, g)[map[(int)piece]];
            if (text.Length > 0) parts.Add(new Part(text, theme.Paint(text, ink), kind));
        }

        Add(Piece.Folder, StatusCell.Folder, Theme.Dim);
        Add(Piece.Branch, StatusCell.Branch, Theme.Dim);
        Add(Piece.Model, StatusCell.Model, Theme.Dim);
        Add(Piece.Tokens, StatusCell.Tokens, Theme.Dim);
        if (s.Ctx?.Percent is int p) Add(Piece.Ctx, StatusCell.Ctx, p >= 80 ? Theme.Warn : Theme.Dim);

        //what runs between turns, never shed, in the inks that say a click acts, each word on its own band under the pointer or a Down selection
        if (BackgroundCell(facts, g) is { } cell)
            parts.Add(new Part(cell, InkedCell(facts, theme, s.Role, g, banded: facts.CellSelected || facts.CellHovered), StatusCell.Background));

        //the provider supplies the numbers and core writes the words, a quota turns warn with few requests left, or when low against a reported total
        if (s.Quota is { } q)
        {
            var quota = $"{q.Label} {q.Remaining.ToString(CultureInfo.InvariantCulture)} left";
            var low = q.Remaining <= QuotaWarnRemaining
                || (q.Total is long total and > 0 && q.Remaining * 100 <= total * QuotaWarnPercent);
            parts.Add(new Part(quota, theme.Paint(quota, low ? Theme.Warn : Theme.Dim), StatusCell.Quota));
        }
        if (s.Cost is decimal cost)
        {
            var money = "$" + cost.ToString("0.00", CultureInfo.InvariantCulture);
            parts.Add(new Part(money, theme.Paint(money, Theme.Dim), StatusCell.Cost));
        }

        return parts;
    }

    private static int VisWidth(List<Part> parts, GlyphSet g)
    {
        var w = 2;   //the 2-cell indent
        for (var i = 0; i < parts.Count; i++)
            w += (i > 0 ? UnicodeWidth.Of(SepOf(g)) : 0) + UnicodeWidth.Of(parts[i].Visible);
        return w;
    }

    private static (string rendered, string visible, List<StatusCellSpan> cells) Render(List<Part> parts, Theme theme, GlyphSet g)
    {
        var rendered = new StringBuilder("  ");
        var visible = new StringBuilder("  ");
        var cells = new List<StatusCellSpan>();
        var sep = SepOf(g);
        var col = 2;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) { rendered.Append(theme.Paint(sep, Theme.Dim)); visible.Append(sep); col += UnicodeWidth.Of(sep); }
            rendered.Append(parts[i].Rendered);
            visible.Append(parts[i].Visible);
            var w = UnicodeWidth.Of(parts[i].Visible);
            cells.Add(new StatusCellSpan(parts[i].Kind, col, col + w));
            col += w;
        }
        return (rendered.ToString(), visible.ToString(), cells);
    }

    //a word in its ink on the band its ink makes, the band opened again after the paint's reset and closed with the word
    internal static string Banded(string word, RgbColor ink, Theme theme)
    {
        var on = Ansi.Bg(theme.BandOf(ink), theme.TrueColor);
        return on + theme.Paint(word, ink).Replace(Ansi.Reset, Ansi.Reset + on, StringComparison.Ordinal) + Ansi.Reset;
    }

    //the cell's words in their inks, dim kept for the separator and for what a click does not reach: the shells in PowerShell's yellow, the watch in the role tint
    internal static string InkedCell(BackgroundFacts facts, Theme theme, string role, GlyphSet g, bool banded = false)
    {
        var words = new List<string>();
        void Word(string text, RgbColor ink) => words.Add(banded ? Banded(text, ink, theme) : theme.Paint(text, ink));
        if (facts.Running > 0) Word(facts.Running == 1 ? "1 shell" : $"{facts.Running} shells", Theme.CampbellBrightYellow);
        if (facts.WatchArmed) Word(facts.WatchPaused ? "watch paused" : "watch", theme.RoleTint(role));
        return string.Join(theme.Paint(SepOf(g), Theme.Dim), words);
    }

    //the cell's words: the shells that run, then the watch, absent when both are
    internal static string? BackgroundCell(BackgroundFacts facts, GlyphSet g)
    {
        var words = new List<string>();
        if (facts.Running > 0) words.Add(facts.Running == 1 ? "1 shell" : $"{facts.Running} shells");
        if (facts.WatchArmed) words.Add(facts.WatchPaused ? "watch paused" : "watch");
        return words.Count == 0 ? null : string.Join(SepOf(g), words);
    }

    //section text

    //verbatim until 6 cells, then an ellipsis plus the last 5 chars
    private static string ShortBranchText(string branch, GlyphSet g) =>
        UnicodeWidth.Of(branch) <= 6 ? branch : g.Ellipsis + TailCells(branch, 5);

    //the model's middle form: verbatim up to 15 cells, else 7 cells, a middle ellipsis and the last 7
    private static string MiddleName(string model, GlyphSet g) =>
        UnicodeWidth.Of(model) <= 15 ? model : HeadCells(model, 7) + g.Ellipsis + TailCells(model, 7);

    //the word in parentheses after the model: a toggle reads reasoning or non-reasoning, a level its name, and a model with no switch gets none
    internal static string? EffortWord(string? thinking, bool toggle)
    {
        if (thinking is null) return null;
        if (toggle) return thinking.EndsWith(" on", StringComparison.Ordinal) ? "reasoning" : "non-reasoning";
        return thinking == "none" ? "non-reasoning" : thinking;
    }

    //the last two segments, the ellipsis standing for what is above them. a lone ~ above is kept, it says as much in the same cell
    internal static string FolderTail(string path, GlyphSet g)
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        var parts = path.TrimEnd('/', '\\').Split('/', '\\');
        if (parts.Length <= 2 || parts.Length == 3 && parts[0] == "~") return path;
        return g.Ellipsis + sep + parts[^2] + sep + parts[^1];
    }

    //the last path component after the final separator, with a path that has none returned whole
    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    //cut to max cells with a trailing ellipsis, or return the string when it already fits
    private static string HeadTruncate(string s, int max, GlyphSet g)
    {
        if (max <= 0) return "";
        if (UnicodeWidth.Of(s) <= max) return s;
        return max == 1 ? g.Ellipsis : HeadCells(s, max - 1) + g.Ellipsis;
    }

    //under 1000 as it is, above that one decimal with k, and with M past a million. invariant culture, so the decimal point can't become a comma
    public static string KFormat(long n) =>
        n < 1000
            ? n.ToString(CultureInfo.InvariantCulture)
            : n < 1_000_000
                ? (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k"
                : (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";

    private static string AbbreviateHome(string cwd, string profile) => HomePath.AbbreviateProfile(cwd, profile);

    private static string HeadCells(string s, int cells)
    {
        var sb = new StringBuilder();
        var w = 0;
        foreach (var r in s.EnumerateRunes())
        {
            var rw = UnicodeWidth.OfRune(r);
            if (w + rw > cells) break;
            sb.Append(r.ToString());
            w += rw;
        }
        return sb.ToString();
    }

    private static string TailCells(string s, int cells)
    {
        var runes = new List<System.Text.Rune>();
        foreach (var r in s.EnumerateRunes()) runes.Add(r);
        var sb = new StringBuilder();
        var w = 0;
        for (var i = runes.Count - 1; i >= 0; i--)
        {
            var rw = UnicodeWidth.OfRune(runes[i]);
            if (w + rw > cells) break;
            w += rw;
            sb.Insert(0, runes[i].ToString());
        }
        return sb.ToString();
    }
}
