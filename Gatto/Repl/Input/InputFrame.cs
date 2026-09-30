using System.Globalization;
using System.Text;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;

namespace Gatto.Repl.Input;

//a snapshot of the status line's facts. the Serving field names the model the server actually loaded, and paints as a warning chip while the two differ
public sealed record StatusInfo(
    string Cwd, string Model, string Role, CtxState Ctx, string UserProfile,
    string? Branch = null, long TokensUp = 0, long TokensDown = 0, bool Wild = false,
    string? Thinking = null, string? Serving = null, bool ThinkingToggle = false,
    string? FocusHint = null,   //the Ctrl+↑/↓/R keymap, shown while focus is active (or as the first-collapse teaser)
    string? ChordHint = null);  //the live Esc·Esc / Ctrl+C·Ctrl+C hint, drawn in the top rule's label slot

//the composed rows and the caret's row and column. the Composer field is the layout a mouse gesture must map through, null in an unframed frame
public sealed record FrameLayout(
    IReadOnlyList<string> Rows, IReadOnlyList<string> VisibleRows, IReadOnlyList<int> ScreenRows,
    int TotalRows, int CursorRowOffset, int CursorCol,
    IReadOnlyList<(bool Continuation, int PrefixCells)> EditorRowTags,
    ComposerLayout? Composer = null,
    int PanelRowCount = 0);

//the framed typing area: top rule with its role label, editor rows, bottom rule, status line. composes layout only, ChromePainter writes every byte
public sealed class InputFrame(ITermSurface surface, Theme theme, string role, StatusInfo status,
    GlyphSet? glyphs)
{
    //the run's glyph set, handed down from the session (two painters resolving it themselves could disagree about the host)
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    private const int MinFramedWidth = 20;

    //pure layout, no pixels (ChromePainter owns the chrome stack). panelRows swaps in a modal prompt's rows: no composer layout, and view goes unread
    public FrameLayout Compose(EditorView view, IReadOnlyList<string>? panelRows = null,
        (int Row, int Col)? panelCaret = null)
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
            var (topR, topV) = TopRuleCore(role, status.Wild, width, theme, status.ChordHint, _glyphs);
            rendered.Add(topR);
            visibleRows.Add(topV);
            screenRows.Add(UnicodeWidth.Rows(topV, width));
        }

        var editorStart = rendered.Count;

        //the bottom rule and status pair go out together when framed, in both the editor and the panel branch (ChromePainter's editorStart arithmetic needs it)
        void AddFooter()
        {
            if (!framed) return;
            var bottomV = RuleOf(_glyphs, width);
            rendered.Add(theme.Paint(bottomV, Theme.Rule));
            visibleRows.Add(bottomV);
            screenRows.Add(1);

            var (statR, statV) = StatusCore(status, width, theme, _glyphs);
            rendered.Add(statR);
            visibleRows.Add(statV);
            screenRows.Add(UnicodeWidth.Rows(statV, width));
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
                Array.Empty<(bool, int)>(), Composer: null, PanelRowCount: panelRows.Count);
        }

        //2 cells for the prompt or hang and 1 for the \ mark, so a marked row never reaches the edge. the \ belongs to a logical join, so a visual wrap shows none
        var budget = width > 3 ? width - 3 : 0;

        //the highlight and the mouse hit test both read this one wrap, so neither can derive its own. layout only translates SelStart and SelEnd into row and cell
        var layout = new ComposerLayout(lines, width);
        var hasSelection = view.SelStart is not null && view.SelEnd is not null;
        var (selStartRow, selStartCell) = hasSelection ? layout.PositionToCell(view.SelStart!.Value.Line, view.SelStart!.Value.Col) : (0, 0);
        var (selEndRow, selEndCell) = hasSelection ? layout.PositionToCell(view.SelEnd!.Value.Line, view.SelEnd!.Value.Col) : (0, 0);

        //the ghost comes from the same function Tab calls, so the hint and what Tab types can't disagree. index 0, a Tab leaves the whole command name in the buffer
        var completion = CommandHint.For(lines, view.CursorLine, view.CursorCol);
        var ghost = completion.Any ? completion.Hint(0) : "";

        var segsPerLine = new List<IReadOnlyList<WrapSeg>>(lines.Count);
        //tag each editor row as a continuation or a logical head right in the loop that already knows it. a continuation row's 2-space hang counts as chrome
        var editorRowTags = new List<(bool Continuation, int PrefixCells)>();
        var regionRow = 0;   //a flat row counter in ComposerLayout's RegionRow space
        for (var li = 0; li < lines.Count; li++)
        {
            var segs = SoftWrap.Wrap(lines[li], budget, budget);
            segsPerLine.Add(segs);
            for (var si = 0; si < segs.Count; si++)
            {
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

        //map the cursor through the shared wrap policy, a second wrap derived here would drift from it
        var cl = Math.Clamp(view.CursorLine, 0, lines.Count - 1);
        var cc = Math.Clamp(view.CursorCol, 0, lines[cl].Length);
        var (segRow, cells) = SoftWrap.MapCursor(segsPerLine[cl], lines[cl], cc);

        //add up the screen rows to reach the cursor's row, a segment is one screen row only until a wide glyph overflows the width
        var flatCursorEntry = editorStart;
        for (var li = 0; li < cl; li++) flatCursorEntry += segsPerLine[li].Count;
        flatCursorEntry += segRow;
        var targetRow = 0;
        for (var i = 0; i < flatCursorEntry; i++) targetRow += screenRows[i];
        var col = 2 + cells;   //2 cells of prompt or hang prefix

        return new FrameLayout(rendered, visibleRows, screenRows, total, targetRow, col, editorRowTags,
            framed ? layout : null);
    }

    public static string BuildTopRule(string role, int width, Theme theme, bool wild = false, string? hint = null,
        GlyphSet? glyphs = null) =>
        TopRuleCore(role, wild, width, theme, hint, glyphs).rendered;

    //the ANSI-free half of TopRuleCore, so a test can measure the top rule's width
    public static string BuildTopRuleVisible(string role, int width, Theme theme, bool wild = false, string? hint = null,
        GlyphSet? glyphs = null) =>
        TopRuleCore(role, wild, width, theme, hint, glyphs).visible;

    public static string BuildStatusLine(StatusInfo s, int width, Theme theme, GlyphSet? glyphs) =>
        StatusCore(s, width, theme, glyphs).rendered;

    //internals

    //the status row's separator comes from a method, a const can't read a glyph set (every site that measures it takes the same set)
    private static string SepOf(GlyphSet g) => " " + g.Dot + " ";

    //a rule of n cells, the whole glyph repeated so a twin of two characters can't become one
    private static string RuleOf(GlyphSet g, int n) => string.Concat(Enumerable.Repeat(g.Rule, n));

    //one place builds the top rule. the live hint borrows the label slot so the chrome block keeps its height, and a wide label truncates rather than wrapping
    private static (string rendered, string visible) TopRuleCore(string role, bool wild, int width, Theme theme,
        string? hint = null, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var sep = SepOf(g);
        var labelVisible = hint ?? (wild ? "gatto" + sep + role + sep + "wild" : "gatto" + sep + role);
        //the 4 cells of overhead: two spaces around the label and the 2-cell closing rule
        var dashes = width - UnicodeWidth.Of(labelVisible) - 4;
        if (dashes < 0) dashes = 0;
        var dashStr = RuleOf(g, dashes);
        var labelRendered = hint is not null
            ? theme.Paint(hint, Theme.Accent)
            : theme.Paint("gatto", Theme.Accent, bold: true)
                + theme.Paint(sep, Theme.Dim)
                + theme.Paint(role, theme.RoleTint(role))
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

    private enum Tier { Full, Mid, Narrow }

    //one footer section: the visible text for the fit and erase math, paired with its themed rendering
    private readonly record struct Part(string Visible, string Rendered);

    //the minimum gap in cells between the left cluster and the flush-right focus hint
    private const int HintGap = 2;

    //one physical row at any width: the widest tier that fits wins, below Narrow the row truncates. the focus hint sits flush-right and is shed first
    private static (string rendered, string visible) StatusCore(StatusInfo s, int width, Theme theme,
        GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var full = Assemble(s, theme, Tier.Full, g);

        //the hint is only tried against the Full core: once the core itself needs elision the hint is already gone
        if (s.FocusHint is not null && width > 0)
        {
            var (coreR, coreV) = Render(full, theme, g);
            var pad = width - UnicodeWidth.Of(coreV) - UnicodeWidth.Of(s.FocusHint) - HintGap;
            if (pad >= 0)
            {
                var spaces = new string(' ', HintGap + pad);   //pads the row so the hint ends at the right edge
                return (coreR + spaces + theme.Paint(s.FocusHint, Theme.Dim), coreV + spaces + s.FocusHint);
            }
            //the hint doesn't fit, so drop it and take the core through the ladder below
        }

        if (width <= 0 || VisWidth(full, g) <= width) return Render(full, theme, g);   //a non-positive width skips truncation

        var mid = Assemble(s, theme, Tier.Mid, g);
        if (VisWidth(mid, g) <= width) return Render(mid, theme, g);

        var (r, v) = Render(Assemble(s, theme, Tier.Narrow, g), theme, g);
        if (UnicodeWidth.Of(v) > width)   //below Narrow the row truncates rather than wrapping, the same as ChromeTrunc
        {
            v = TermText.TruncateCells(v, width, glyphs: g);
            r = TermText.TruncateCells(r, width, glyphs: g);
        }
        return (r, v);
    }

    //the footer's sections for a tier: an absent part contributes nothing. in Narrow the branch goes and tokens and ctx merge with one space
    private static List<Part> Assemble(StatusInfo s, Theme theme, Tier tier, GlyphSet g)
    {
        var parts = new List<Part>();

        //the focus hint is not assembled here, StatusCore adds it flush-right and drops it first
        var folder = FolderText(s, tier, g);
        parts.Add(new Part(folder, theme.Paint(folder, Theme.Dim)));

        if (s.Branch is not null && tier != Tier.Narrow)
        {
            var branch = BranchText(s.Branch, tier, g);
            parts.Add(new Part(branch, theme.Paint(branch, Theme.Dim)));
        }

        //the serving chip: the armed model and the loaded weights disagree. the chip stays, so a long session still sees which model it runs
        var model = ModelText(s.Model, tier, g);
        var modelVis = s.Serving is null ? model : model + $" {g.Warn} serving {s.Serving}";
        var modelRen = theme.Paint(model, Theme.Dim)
            + (s.Serving is null ? "" : theme.Paint($" {g.Warn} serving {s.Serving}", Theme.Warn));
        parts.Add(new Part(modelVis, modelRen));

        if (s.Thinking is not null)
        {
            var effort = EffortText(s.Thinking, tier, s.ThinkingToggle);
            parts.Add(new Part(effort, theme.Paint(effort, Theme.Dim)));
        }

        var tokens = s.TokensUp != 0 || s.TokensDown != 0
            ? $"{g.Up} {KFormat(s.TokensUp)} {g.Down} {KFormat(s.TokensDown)}" : null;
        var pct = s.Ctx?.Percent;
        //the token count appears at the wider tiers, a percentage alone says little in a large window. the narrow tier drops it first, the percent has to survive
        var ctxTokens = tier == Tier.Narrow ? null : s.Ctx?.Tokens;
        //keep the count in parentheses, the · separator would make it read as a second section
        var ctx = pct is int p
            ? ctxTokens is int t ? $"ctx {p}% ({KFormat(t)})" : $"ctx {p}%"
            : null;
        var hot = pct >= 80;
        var ctxColor = hot ? Theme.Warn : Theme.Dim;

        if (tier == Tier.Narrow && tokens is not null && ctx is not null)
        {
            //tokens and ctx join with one space rather than a · separator
            var vis = tokens + " " + ctx;
            var ren = theme.Paint(tokens, Theme.Dim) + " " + theme.Paint(ctx, ctxColor);
            parts.Add(new Part(vis, ren));
        }
        else
        {
            if (tokens is not null) parts.Add(new Part(tokens, theme.Paint(tokens, Theme.Dim)));
            if (ctx is not null) parts.Add(new Part(ctx, theme.Paint(ctx, ctxColor)));
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

    private static (string rendered, string visible) Render(List<Part> parts, Theme theme, GlyphSet g)
    {
        var rendered = new StringBuilder("  ");
        var visible = new StringBuilder("  ");
        var sep = SepOf(g);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) { rendered.Append(theme.Paint(sep, Theme.Dim)); visible.Append(sep); }
            rendered.Append(parts[i].Rendered);
            visible.Append(parts[i].Visible);
        }
        return (rendered.ToString(), visible.ToString());
    }

    //tier-specific section text

    //the full tier shows the home-abbreviated path, the others show / plus the last segment, head-truncated to 20 or 12 cells
    private static string FolderText(StatusInfo s, Tier tier, GlyphSet g)
    {
        if (tier == Tier.Full) return AbbreviateHome(s.Cwd, s.UserProfile);
        var seg = "/" + LastSegment(s.Cwd);
        return HeadTruncate(seg, tier == Tier.Mid ? 20 : 12, g);
    }

    //verbatim until 6 cells, then an ellipsis plus the last 5 chars. the narrow tier never calls this
    private static string BranchText(string branch, Tier tier, GlyphSet g)
    {
        if (tier == Tier.Full || UnicodeWidth.Of(branch) <= 6) return branch;
        return g.Ellipsis + TailCells(branch, 5);
    }

    //verbatim on the full tier, 15 cells with a middle ellipsis on Mid, 6 cells with a terminal one on Narrow
    private static string ModelText(string model, Tier tier, GlyphSet g)
    {
        if (tier == Tier.Full) return model;
        if (tier == Tier.Mid)
            return UnicodeWidth.Of(model) <= 15
                ? model
                : HeadCells(model, 7) + g.Ellipsis + TailCells(model, 7);
        return HeadTruncate(model, 6, g);
    }

    //the level plus effort on the full tier, plus eff at Mid, the first 3 cells at Narrow. the toggle phrase shows whole, or as its last word at Narrow
    private static string EffortText(string thinking, Tier tier, bool toggle)
    {
        if (toggle)
        {
            if (tier != Tier.Narrow) return thinking;
            var sp = thinking.LastIndexOf(' ');
            return sp >= 0 ? thinking[(sp + 1)..] : thinking;
        }
        return tier switch
        {
            Tier.Full => thinking + " effort",
            Tier.Mid => thinking + " eff",
            _ => HeadCells(thinking, 3),
        };
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

    private static string AbbreviateHome(string cwd, string profile) => ItemRender.AbbreviateProfile(cwd, profile);

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
