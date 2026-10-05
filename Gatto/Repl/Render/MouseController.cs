using System.Text;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//routes mouse events to the existing mutators, and a composer press resolves through the painted frame's layout instead of a fresh recompute
public sealed class MouseController(
    ViewportCompositor compositor, ScrollController scroll, FocusController focus, TranscriptModel model,
    int wheelLines, SelectionController selection, Theme theme, Func<long>? now = null,
    Action<int, int, ComposerGesture>? onComposerSelect = null, Func<ComposerLayout?>? composerLayout = null,
    GlyphSet? glyphs = null, Func<int, bool>? onComposerScroll = null)
{
    //the glyph set chosen once at launch, handed down beside the theme
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    private const int WheelDelta = 120;    //the standard wheel notch, some devices send a different value
    private const int GutterCols = 2;      //the two-column left gutter where the rail glyphs sit
    private const long TripleClickMs = 500;
    private readonly Func<long> _now = now ?? (() => Environment.TickCount64);
    private int _wheelAccum;
    private int _prevClicks;                //the click count the OS reported for the previous left press, used to detect a triple
    private SelCell _prevCell;
    private long _prevTick;
    private DragKind _dragKind;             //the gesture this drag is running, separate from SelectionController's own kind
    private (int item, int start, int end) _lineAnchor;   //the logical line the triple click picked, so the drag snaps to it
    private ChromeCell _prevChromeCell;     //the previous chrome cell, the chrome-side counterpart of _prevCell
    //the previous press's domain gates the triple-click match, ChromeCell's default (0,0) is a real cell
    private enum ClickDomain { None, Transcript, Chrome }
    private ClickDomain _prevDomain;
    //true only between a press that began a drag and its release, a stale _lineAnchor would otherwise re-snap a later move
    private bool _dragLive;
    //true only between a composer press and its release, selection knows nothing about that domain
    private bool _composerDragLive;

    public void Handle(MouseEvent e, int width, int height)
    {
        switch (e.Kind)
        {
            case MouseKind.Wheel:
                //scale by wheel_lines first and divide by the wheel delta, keeping the leftover for the next notch
                _wheelAccum += e.WheelDelta * wheelLines;
                var wheelLinesResolved = _wheelAccum / WheelDelta;
                _wheelAccum %= WheelDelta;
                if (wheelLinesResolved == 0) break;
                //a notch over the prompt, rule or composer goes to onComposerScroll first, and a declined one scrolls the transcript
                var overComposer = compositor.HitTest(e.Y) is ChromeTarget(_, ChromeRegion.Prompt or ChromeRegion.Rule or ChromeRegion.Composer, _);
                if (overComposer && onComposerScroll is not null && onComposerScroll(wheelLinesResolved)) break;
                var rest = overComposer ? wheelLinesResolved : ScrollWindow(e, wheelLinesResolved, width);
                if (rest == 0) break;
                //a notch the transcript takes away from the focused block ends the focus, so the block cannot catch the wheel on the way back
                if (focus.Focused is int f && (compositor.CellAt(e.X, e.Y) is not { } over || over.ItemIndex != f)) focus.Clear();
                scroll.ScrollBy(rest, width, height);
                break;

            case MouseKind.Press when e.Button == MouseButton.Left:
                HandlePress(e, width, height);
                break;

            case MouseKind.Move:
                HandleDragMove(e, width, height);
                break;

            case MouseKind.Release:
                _dragLive = false;     //the gesture is over, a later move belongs to no drag
                _composerDragLive = false;
                selection.EndDrag();   //a zero-width char span from a bare click becomes no selection
                //bare clicks on chrome rows need the same normalization as the transcript, or a phantom zero-width highlight stays live
                if (selection.ChromeCurrent is { } cc && _dragKind == DragKind.Char && cc.Anchor == cc.Head)
                    selection.Clear();
                break;
        }
    }

    private void HandlePress(MouseEvent e, int width, int height)
    {
        //every press clears the drag state first, a bailed press must not leave the last gesture's anchor armed
        _dragLive = false;
        _composerDragLive = false;
        if (e.Modifiers.HasFlag(ConsoleModifiers.Shift)) return;   //a Shift+drag belongs to WT's own selection, so gatto ignores it
        if (WindowPress(e, width, height)) return;

        switch (compositor.HitTest(e.Y))
        {
            case JumpHintTarget:
                scroll.End();   //jump to the live bottom and re-attach the view
                return;
            case ItemTarget(var i, var headerRow, var lastRow) when focus.IsCollapsible(i, width):
                //a gutter click toggles an open block or a collapsed thinking block. it selects on the rows under a collapsed tool header, and on the rail of an open window (copy from column zero)
                var railClick = e.X < GutterCols && (!model.Items[i].Collapsed || model.Items[i] is ReasoningItem)
                    && model.Items[i] is not ToolBlockItem;
                //the affordance click is narrowed to a streaming collapsed thinking block, a collapsed tool block's last row is real content
                var affordanceClick = lastRow && model.Items[i] is ReasoningItem { Streaming: true, Collapsed: true };
                if (headerRow || railClick || affordanceClick)
                {
                    focus.Focus(i, width, height, reveal: false);   //reveal: false so the toggle below does the scrolling, matching Ctrl+R
                    focus.Toggle(i, width, height);                 //the toggle's scroll wins over anything Focus did
                    return;
                }
                //a press on an open window gives it the focus, so the wheel reaches the window again, and the press still starts a copy
                if (!model.Items[i].Collapsed && model.Items[i] is ToolBlockItem)
                    focus.Focus(i, width, height, reveal: false);
                break;   //a header row always toggles, a body row goes to StartDrag below
            case ChromeTarget ct:
                //a chrome press starts a chrome drag, BeginChromeDrag clears any live transcript selection
                if (ct.Region == ChromeRegion.Composer) focus.Clear();   //a composer click ends the keyboard focus and removes its accent bar
                StartChromeDrag(e, width);
                return;
        }

        StartDrag(e, width);
    }

    //a notch moves a window of the focused block only. the part it cannot use goes to the transcript, so a window never traps the wheel
    private int ScrollWindow(MouseEvent e, int n, int width)
    {
        if (ToolBlockAt(e) is not (int index, ToolBlockItem tb) || focus.Focused != index)
            return n;
        var row = RowIn(tb, index, e);
        var layout = tb.Layout(width, theme, _glyphs);
        if (!tb.Collapsed && tb.View == ShellView.Window && row >= layout.OutputFirst && row < layout.OutputFirst + layout.OutputShown)
            return n - tb.ScrollWindow(n);   //a positive count is a notch up, toward line 1
        if (tb.View == ShellView.Window && row >= layout.CommandFirst && row < layout.CommandFirst + layout.CommandShown)
        {
            var next = Math.Clamp(tb.CommandFromTail + n, 0, Math.Max(0, layout.CommandTotal - ShellBlockRender.CommandWindow));
            var used = next - tb.CommandFromTail;
            tb.CommandFromTail = next;
            return n - used;
        }
        return n;
    }

    //a press on a tool block's link or footer action changes its view, any other press goes on to the usual routes
    private bool WindowPress(MouseEvent e, int width, int height)
    {
        if (ToolBlockAt(e) is not (int index, ToolBlockItem tb))
            return false;
        var row = RowIn(tb, index, e);
        var layout = tb.Layout(width, theme, _glyphs);
        if (row == layout.ResultRow && layout.LinkEnd > layout.LinkStart && e.X >= layout.LinkStart && e.X < layout.LinkEnd)
        {
            focus.Focus(index, width, height, reveal: false);
            focus.Toggle(index, width, height);
            return true;
        }
        if (row == layout.FooterRow && e.X >= layout.ActionStart && e.X < layout.ActionEnd)
        {
            focus.Focus(index, width, height, reveal: false);
            focus.ShowAll(index, layout.FooterAction == ShellAction.ShowAll, width, height);
            return true;
        }
        return false;
    }

    //the tool block under the pointer, every other item gives null
    private (int Index, ToolBlockItem Block)? ToolBlockAt(MouseEvent e) =>
        compositor.CellAt(e.X, e.Y) is { } cell && cell.ItemIndex >= 0 && cell.ItemIndex < model.Items.Count
            && model.Items[cell.ItemIndex] is ToolBlockItem tb
            ? (cell.ItemIndex, tb) : null;

    //the row under the pointer counted from the block's first row, the blank above it left out
    private int RowIn(ToolBlockItem tb, int index, MouseEvent e) =>
        compositor.CellAt(e.X, e.Y) is { } cell && cell.ItemIndex == index ? cell.Rel - (tb.LeadingBlank ? 1 : 0) : -1;

    private void StartDrag(MouseEvent e, int width)
    {
        if (compositor.CellAt(e.X, e.Y) is not { } cell) return;   //padding, chrome or a stale frame leaves nothing to start from

        //a transcript drag begins by enqueueing one composer Clear, a direct write from the pump thread would break the single-writer rule
        onComposerSelect?.Invoke(0, 0, ComposerGesture.Clear);

        var kind = ClickKind(e, cell);
        _dragKind = kind;
        switch (kind)
        {
            case DragKind.Word:
                var (ws, we) = WordBounds(cell, width);
                selection.BeginDrag(new SelCell(cell.ItemIndex, cell.Rel, ws), DragKind.Word, width);
                selection.ExtendTo(new SelCell(cell.ItemIndex, cell.Rel, we), width);
                break;
            case DragKind.Line:
                //the Line unit is the whole logical line, covering every soft-wrap row of it rather than the visible row clicked
                var (ls, le) = LogicalLineRows(cell.ItemIndex, cell.Rel, width);
                var endLast = System.Math.Max(0, RowCells(new SelCell(cell.ItemIndex, le, 0), width) - 1);
                _lineAnchor = (cell.ItemIndex, ls, le);   //remembered so a later move can snap a drag to this line
                selection.BeginDrag(new SelCell(cell.ItemIndex, ls, 0), DragKind.Line, width);
                selection.ExtendTo(new SelCell(cell.ItemIndex, le, endLast), width);
                break;
            default:
                selection.BeginDrag(cell, DragKind.Char, width);
                break;
        }

        _dragLive = true;   //this press really began a drag, so moves may extend it
        _prevClicks = e.Clicks;
        _prevCell = cell;
        _prevDomain = ClickDomain.Transcript;   //the next click-run match is gated to the transcript domain
        _prevTick = _now();
    }

    //the chrome mirror of StartDrag, and its Line case measures by the chrome rows so a wrapped composer row selects as one line
    private void StartChromeDrag(MouseEvent e, int width)
    {
        if (compositor.ChromeCellAt(e.X, e.Y) is not { } cell) return;   //a stale frame means no chrome cell to drag from

        var rows = compositor.ChromeRowInfo;

        //resolve the click kind before the drag begins, so a double or triple click still selects text
        var kind = ChromeClickKind(e, cell);
        _dragKind = kind;

        //a composer press is an editor-select gesture resolved from the captured layout, and a null layout bails with no drag armed
        if (cell.Row >= 0 && cell.Row < rows.Count && rows[cell.Row].Region == ChromeRegion.Composer)
        {
            if (rows[cell.Row].Mark) return;   //a mark stands for hidden rows, not a line, so the caret stays where it was
            if (composerLayout?.Invoke() is not { } layout) return;
            StartComposerSelect(kind, cell, rows, layout);
            _dragLive = true;
            _composerDragLive = true;
            _prevClicks = e.Clicks;
            _prevChromeCell = cell;
            _prevDomain = ClickDomain.Chrome;   //composer rows share the chrome click-run bookkeeping
            _prevTick = _now();
            return;
        }

        //a non-composer chrome drag clears any live composer selection too, and the branch above returned so this cannot double-fire
        onComposerSelect?.Invoke(0, 0, ComposerGesture.Clear);

        switch (kind)
        {
            case DragKind.Word:
                var (ws, we) = ChromeWordBounds(cell.Row, cell.Col, rows);
                selection.BeginChromeDrag(new ChromeCell(cell.Row, ws), DragKind.Word, width, rows);
                selection.ExtendChromeTo(new ChromeCell(cell.Row, we), width, rows);
                break;
            case DragKind.Line:
                var (ls, le) = ChromeLogicalLineRows(cell.Row, rows);
                var endLast = System.Math.Max(0, ChromeRowCellsOf(le, rows) - 1);
                selection.BeginChromeDrag(new ChromeCell(ls, 0), DragKind.Line, width, rows);
                selection.ExtendChromeTo(new ChromeCell(le, endLast), width, rows);
                break;
            default:
                selection.BeginChromeDrag(cell, DragKind.Char, width, rows);
                break;
        }

        _dragLive = true;   //the same flag the transcript path sets, one gate for either domain
        _prevClicks = e.Clicks;
        _prevChromeCell = cell;
        _prevDomain = ClickDomain.Chrome;       //the next click-run match is gated to the chrome domain
        _prevTick = _now();
    }

    //chars send one Begin, words and lines send Begin then Extend, with both endpoints resolved from the layout
    private void StartComposerSelect(DragKind kind, ChromeCell cell, IReadOnlyList<ChromeRow> rows, ComposerLayout layout)
    {
        var regionRow = rows[cell.Row].RegionRow;
        switch (kind)
        {
            case DragKind.Word:
                var (ws, we) = ChromeWordBounds(cell.Row, cell.Col, rows);
                var (wsLine, wsCol) = layout.ComposerCellToPosition(regionRow, ws);
                var (weLine, weCol) = layout.ComposerCellToPosition(regionRow, we + 1);   //one past the last selected cell
                onComposerSelect?.Invoke(wsLine, wsCol, ComposerGesture.Begin);
                onComposerSelect?.Invoke(weLine, weCol, ComposerGesture.Extend);
                break;
            case DragKind.Line:
                var (clickedLine, _) = layout.ComposerCellToPosition(regionRow, cell.Col);
                onComposerSelect?.Invoke(clickedLine, 0, ComposerGesture.Begin);
                onComposerSelect?.Invoke(clickedLine, layout.Lines[clickedLine].Length, ComposerGesture.Extend);
                break;
            default:
                var (line, col) = layout.ComposerCellToPosition(regionRow, cell.Col);
                onComposerSelect?.Invoke(line, col, ComposerGesture.Begin);
                break;
        }
    }

    private void HandleDragMove(MouseEvent e, int width, int height)
    {
        if (!_dragLive) return;   //no press began a drag, so this move belongs to nothing

        //check this flag first, a composer gesture never touches selection so the live spans can't tell it apart
        if (_composerDragLive) { HandleComposerDragMove(e); return; }

        //route on the span that is live, the two begin calls are mutually exclusive so at most one is set
        if (selection.ChromeCurrent is not null) { HandleChromeDragMove(e, width, height); return; }
        if (selection.Current is null) return;   //a dead drag, a mid-drag invalidation cleared both domains

        //scroll at the edge and anchor the head to the edge row, the next move picks up the freshly-revealed row
        var y = e.Y;
        var edge = compositor.ChromeTop - 1;   //the transcript's last row on screen, above whatever chrome the scroll left
        if (y <= 0) { scroll.ScrollBy(1, width, height); y = 0; }
        else if (y >= edge) { scroll.ScrollBy(-1, width, height); y = edge; }

        SelCell head;
        if (compositor.CellAt(e.X, y) is { } cell)
            head = cell;
        //y can never reach a chrome row here, the clamp above keeps it inside the transcript
        else return;   //a padding, hint or stale row leaves nothing to extend to

        if (_dragKind == DragKind.Line && head.ItemIndex == _lineAnchor.item)
        {
            //a mid-drag invalidation must kill the drag until the next press, re-beginning below would resurrect the cleared span
            if (selection.Current is null) return;
            var (hs, he) = LogicalLineRows(head.ItemIndex, head.Rel, width);
            int aStart = _lineAnchor.start, aEnd = _lineAnchor.end;
            if (he >= aEnd)   //dragging down, or inside the anchor line, anchors at the line start and puts the head at the head line's end
            {
                selection.BeginDrag(new SelCell(head.ItemIndex, aStart, 0), DragKind.Line, width);
                var last = System.Math.Max(0, RowCells(new SelCell(head.ItemIndex, he, 0), width) - 1);
                selection.ExtendTo(new SelCell(head.ItemIndex, he, last), width);
            }
            else              //dragging up anchors at the anchor line's end and puts the head at its start
            {
                var aLast = System.Math.Max(0, RowCells(new SelCell(_lineAnchor.item, aEnd, 0), width) - 1);
                selection.BeginDrag(new SelCell(_lineAnchor.item, aEnd, aLast), DragKind.Line, width);
                selection.ExtendTo(new SelCell(head.ItemIndex, hs, 0), width);
            }
            return;
        }
        //a drag that leaves the anchor item extends raw, a logical line never spans items so there is no cross-item snap
        selection.ExtendTo(head, width);
    }

    //a chrome move clamps to the block's first row and never auto-scrolls, and the scan stops at the first cell that resolves
    private void HandleChromeDragMove(MouseEvent e, int width, int height)
    {
        if (compositor.ChromeCellAt(e.X, e.Y) is { } cell)
        {
            //fetch the rows only after a cell resolved, so a stale frame can't hand back a torn snapshot
            selection.ExtendChromeTo(cell, width, compositor.ChromeRowInfo);
            return;
        }

        var rows = compositor.ChromeRowInfo;
        var cy = System.Math.Max(e.Y, compositor.ChromeTop);   //floor toward the chrome block's first row on screen
        ChromeCell? found = null;
        for (var i = 0; i < rows.Count && found is null; i++, cy++) found = compositor.ChromeCellAt(e.X, cy);
        if (found is not { } clamped) return;    //no chrome cell resolved, so nothing was painted or the frame is stale
        selection.ExtendChromeTo(clamped, width, rows);
    }

    //a composer move re-resolves the cell through the same captured layout and fires Extend, and a move off the composer rows is ignored
    private void HandleComposerDragMove(MouseEvent e)
    {
        if (composerLayout?.Invoke() is not { } layout) return;
        if (compositor.ChromeCellAt(e.X, e.Y) is not { } cell) return;
        var rows = compositor.ChromeRowInfo;
        if (cell.Row < 0 || cell.Row >= rows.Count || rows[cell.Row].Region != ChromeRegion.Composer) return;
        var (line, col) = layout.ComposerCellToPosition(rows[cell.Row].RegionRow, cell.Col);
        onComposerSelect?.Invoke(line, col, ComposerGesture.Extend);
    }

    //a triple needs the previous click count and cell in the same domain, the domain check keeps the other one from misfiring
    private DragKind ClickKind(MouseEvent e, SelCell cell)
    {
        if (e.Clicks >= 2) return DragKind.Word;
        if (_prevDomain == ClickDomain.Transcript && _prevClicks == 2 && _prevCell.Equals(cell) &&
            _now() - _prevTick <= TripleClickMs) return DragKind.Line;
        return DragKind.Char;
    }

    //the chrome mirror of ClickKind, keyed off _prevChromeCell and gated by the same domain check
    private DragKind ChromeClickKind(MouseEvent e, ChromeCell cell)
    {
        if (e.Clicks >= 2) return DragKind.Word;
        if (_prevDomain == ClickDomain.Chrome && _prevClicks == 2 && _prevChromeCell.Equals(cell) &&
            _now() - _prevTick <= TripleClickMs) return DragKind.Line;
        return DragKind.Char;
    }

    //the logical line around rel, climbing to the row that starts it and absorbing the continuation rows below
    private (int start, int end) LogicalLineRows(int itemIndex, int rel, int width)
    {
        if (itemIndex < 0 || itemIndex >= model.Items.Count) return (rel, rel);
        var wraps = model.Items[itemIndex].RowWraps(width, theme, _glyphs);
        if (rel < 0 || rel >= wraps.Count) return (rel, rel);
        var start = rel; while (start > 0 && wraps[start].Continuation) start--;              //a continuation row means the line starts higher up
        var end = rel;   while (end + 1 < wraps.Count && wraps[end + 1].Continuation) end++;   //keep going while the next row is a continuation of this line
        return (start, end);
    }

    //the chrome twin of LogicalLineRows over the flat row list, a non-composer row is never a continuation so it returns itself
    private static (int start, int end) ChromeLogicalLineRows(int row, IReadOnlyList<ChromeRow> rows)
    {
        if (row < 0 || row >= rows.Count) return (row, row);
        var start = row; while (start > 0 && rows[start].Continuation) start--;
        var end = row;   while (end + 1 < rows.Count && rows[end + 1].Continuation) end++;
        return (start, end);
    }

    private string RowPlain(SelCell cell, int width)
    {
        if (cell.ItemIndex < 0 || cell.ItemIndex >= model.Items.Count) return "";
        var rows = model.Items[cell.ItemIndex].Render(width, theme, _glyphs);
        return cell.Rel >= 0 && cell.Rel < rows.Count ? TermText.StripAnsiForWidth(rows[cell.Rel]) : "";
    }

    private int RowCells(SelCell cell, int width) => UnicodeWidth.Of(RowPlain(cell, width));

    private static int ChromeRowCellsOf(int row, IReadOnlyList<ChromeRow> rows) =>
        row >= 0 && row < rows.Count ? UnicodeWidth.Of(rows[row].Visible) : 0;

    //the cell range of the word under this cell, and just the one cell for whitespace, punctuation or past the end
    private (int start, int end) WordBounds(SelCell cell, int width) => WordBoundsOf(RowPlain(cell, width), cell.Col);

    private (int start, int end) ChromeWordBounds(int row, int col, IReadOnlyList<ChromeRow> rows) =>
        WordBoundsOf(row >= 0 && row < rows.Count ? rows[row].Visible : "", col);

    private static (int start, int end) WordBoundsOf(string plain, int col)
    {
        var isWord = new List<bool>();
        foreach (var rune in plain.EnumerateRunes())
        {
            var wordy = System.Text.Rune.IsLetterOrDigit(rune) || rune.Value == '_';
            for (var k = 0; k < UnicodeWidth.OfRune(rune); k++) isWord.Add(wordy);
        }
        if (col >= isWord.Count || !isWord[col]) return (col, col);
        var s = col; while (s > 0 && isWord[s - 1]) s--;
        var e = col; while (e + 1 < isWord.Count && isWord[e + 1]) e++;
        return (s, e);
    }
}
