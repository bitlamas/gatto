using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the alt-screen frame is redrawn each paint with no scrollback touched, and Stop() must run before AltScreen.Restore so a late tick can't paint chrome
public sealed class ViewportCompositor(ITermSurface surface, LineIndex index, object gate,
    GlyphSet? glyphs)
{
    //the run's glyph set, handed down from the session, so this painter can't disagree with the one beside it about which host this is
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    private bool _stopped;

    //the one fallback height for a terminal that can't report its own, shared by the offset math and the frame so they can't disagree
    public const int HeightFloor = 24;

    //the Repl sets this at wire time, until then the chrome is empty and the transcript fills the whole frame
    public Func<int, int, ChromeBlock>? ComposeChrome { get; set; }

    //the model-item index focused for expand and collapse, or null, and its rows get a reverse-video marker while it is in the window
    public int? FocusedItemIndex { get; set; }

    //true makes the offset count the chrome as the document's last rows, so a scrolled view shows the chrome's top rows or none of it
    public bool ChromeScrolls { get; set; }

    //the live selection, set by the mouse sink at wire time, self-clearing on reflow or mutation so a same-width repaint keeps it
    public SelectionController? Selection { get; set; }

    private FrameLayout? _layout;   //the layout of the last painted frame, which the hit tests read

    //map a click row to what is on screen, from the last painted layout, so a hit can't drift. a layout whose dims no longer match the surface answers NoTarget
    public MouseTarget HitTest(int y)
    {
        lock (gate)
        {
            if (_layout is not { } layout) return NoTarget.Instance;
            var curH = surface.Height <= 0 ? HeightFloor : surface.Height;
            if (layout.Width != surface.Width || layout.Height != curH) return NoTarget.Instance;   //the layout predates a resize, so its rows no longer match the screen
            if (y == layout.HintRow) return JumpHintTarget.Instance;
            if (layout.ChromeRows.TryGetValue(y, out var ci))
            {
                var cr = layout.ChromeRowInfo[ci];
                return new ChromeTarget(ci, cr.Region, cr.RegionRow);
            }
            return layout.Rows.TryGetValue(y, out var t) ? t : NoTarget.Instance;
        }
    }

    //map a click cell to the anchor drawn on that row, from the last painted layout. null on a padding, chrome or hint row, or a stale frame
    public SelCell? CellAt(int x, int y)
    {
        lock (gate)
        {
            if (_layout is not { } layout) return null;
            var curH = surface.Height <= 0 ? HeightFloor : surface.Height;
            if (layout.Width != surface.Width || layout.Height != curH) return null;   //the layout predates a resize, so its rows no longer match the screen
            if (!layout.Cells.TryGetValue(y, out var c)) return null;
            return new SelCell(c.ItemIndex, c.Rel, System.Math.Max(0, x));
        }
    }

    //map a click cell to the chrome row drawn there, from the last painted layout. null on any other row or a stale frame
    public ChromeCell? ChromeCellAt(int x, int y)
    {
        lock (gate)
        {
            if (_layout is not { } layout) return null;
            var curH = surface.Height <= 0 ? HeightFloor : surface.Height;
            if (layout.Width != surface.Width || layout.Height != curH) return null;   //the layout predates a resize, so its rows no longer match the screen
            if (!layout.ChromeRows.TryGetValue(y, out var ci)) return null;
            return new ChromeCell(ci, System.Math.Max(0, x));
        }
    }

    //the screen row the painted chrome begins on, the frame's height when none of it is on screen, so a drag finds the boundary wherever the scroll put it
    public int ChromeTop
    {
        get
        {
            lock (gate)
            {
                if (_layout is not { } layout) return surface.Height <= 0 ? HeightFloor : surface.Height;
                return layout.ChromeRows.Count > 0 ? layout.ChromeRows.Keys.Min() : layout.Height;
            }
        }
    }

    //the chrome rows of the last painted frame, exposed so the mouse code reads ChromeRow.Continuation without a second map. empty before the first paint
    public IReadOnlyList<ChromeRow> ChromeRowInfo { get { lock (gate) { return _layout?.ChromeRowInfo ?? System.Array.Empty<ChromeRow>(); } } }

    //drop the layout too, so a stopped painter answers NoTarget outright
    public void Stop() { lock (gate) { _stopped = true; _layout = null; Selection = null; } }

    public void Paint(int bottomOffset, bool following)
    {
        lock (gate)
        {
            if (_stopped) return;
            var width = surface.Width;
            if (width <= 0) return;
            //a height of 0 means Console.WindowHeight threw or came back 0, so use the floor and still render the frame
            var height = surface.Height <= 0 ? HeightFloor : surface.Height;
            surface.Write(BuildFrame(width, height, bottomOffset, following));
        }
    }

    private string BuildFrame(int width, int height, int bottomOffset, bool following)
    {
        var chrome = ComposeChrome?.Invoke(width, height) ?? new ChromeBlock(System.Array.Empty<ChromeRow>(), 0, 0);

        //a chrome block taller than the frame keeps only its bottom rows, which is where the composer and status line sit
        var chromeRows = chrome.Rows;
        var dropped = 0;
        if (chromeRows.Count > height)
        {
            dropped = chromeRows.Count - height;
            chromeRows = chromeRows.Skip(dropped).ToList();
        }

        //the whole block, which the selection and the copy read, while only its top rows may be on screen
        var wholeChrome = chromeRows;
        if (ChromeScrolls && !following && bottomOffset > 0)
        {
            //the chrome's bottom rows leave first, and the offset left over is the transcript's own
            var away = System.Math.Min(bottomOffset, chromeRows.Count);
            chromeRows = chromeRows.Take(chromeRows.Count - away).ToList();
            bottomOffset -= away;
        }
        var chromeGone = ChromeScrolls && chromeRows.Count == 0 && wholeChrome.Count > 0;

        var transcriptHeight = height - chromeRows.Count;

        //the scrolled-up hint floats above the chrome and says how much is below and how to reach it, on the bottom transcript row. a scrolling chrome shows it only once the chrome is gone
        var showHint = !following && transcriptHeight > 1 && (ChromeScrolls ? chromeGone : bottomOffset > 0);
        //a waiting prompt with a row off the window pins one row at the window's bottom that says so, in the hint's place
        var lastPromptRow = -1;
        for (var i = 0; i < wholeChrome.Count; i++) if (wholeChrome[i].Region == ChromeRegion.Prompt) lastPromptRow = i;
        var showPin = ChromeScrolls && chrome.PromptWaits && lastPromptRow >= chromeRows.Count && transcriptHeight > 1;
        if (showPin) { showHint = false; transcriptHeight -= 1; }
        //moving the offset with the height keeps the window's top row fixed and displaces the bottom one, the row the hint takes
        var contentOffset = bottomOffset + (showPin ? 1 : 0);
        if (showHint) { transcriptHeight -= 1; contentOffset += 1; }

        //visible rows come from each window item's own render, which is SGR-self-contained, so a clipped top item paints right
        var content = new List<string>();
        //rows is the item's own rendered height, so a hit test can tell the item's last row from the frame's last row
        var meta = new List<(int ItemIndex, int Rel, bool LeadingBlank, int Rows)>();   //one entry per content row, for the hit-test map
        if (transcriptHeight > 0)
            foreach (var (item, firstWithin, itemIndex) in index.Window(width, transcriptHeight, contentOffset))
            {
                var rendered = item.Render(width, ThemeOf(), _glyphs); //the theme must come from the index, so the row renders exactly as the index counted it
                for (var i = firstWithin; i < rendered.Count && content.Count < transcriptHeight; i++)
                {
                    content.Add(rendered[i]);
                    meta.Add((itemIndex, i, item.LeadingBlank, rendered.Count));
                }
            }

        //keep one blank row above the hint. if the last row has text it goes away (the hint counts it)
        var hintGap = showHint && content.Count > 1 && TermText.StripAnsiForWidth(content[^1]).Trim().Length > 0;
        if (hintGap) { content[^1] = ""; meta.RemoveAt(meta.Count - 1); }

        //item targets are built at paint time, where the item is in hand and the hit test has no model. a padding row left out of the map answers NoTarget
        var topPad = transcriptHeight - content.Count;
        var hitRows = new Dictionary<int, MouseTarget>(meta.Count);
        var cellRows = new Dictionary<int, (int, int)>(meta.Count);   //physical row to the logical item and offset it draws
        for (var j = 0; j < meta.Count; j++)
        {
            var (itemIndex, rel, lb, rows) = meta[j];
            //the item's own final row, resolved here because the item is in hand and the hit test has no model. a row only advertises a click it can take
            hitRows[topPad + j] = lb && rel == 0
                ? NoTarget.Instance                                          //the leading blank row, which takes no click
                : new ItemTarget(itemIndex, HeaderRow: rel == (lb ? 1 : 0), LastRow: rel == rows - 1);
            cellRows[topPad + j] = (itemIndex, rel);                          //every content row anchors a selection, the blank gap included
        }

        //the chrome rows sit after the transcript and the hint, computed here so the map and the paint can't disagree
        var chromeStart = transcriptHeight + (showHint ? 1 : 0);
        var chromeRowMap = new Dictionary<int, int>(chromeRows.Count);
        for (var j = 0; j < chromeRows.Count; j++) chromeRowMap[chromeStart + j] = j;

        _layout = new FrameLayout(width, height, showHint ? transcriptHeight : showPin ? height - 1 : -1, hitRows, cellRows, chromeRowMap, wholeChrome);

        //the transcript is bottom-anchored against the chrome, so a short session pads blanks at the top
        var frame = new List<string>(height);
        for (var i = 0; i < transcriptHeight - content.Count; i++) frame.Add("");
        frame.AddRange(content);
        if (showHint) frame.Add(JumpHint(contentOffset + (hintGap ? 1 : 0), width));   //the floating hint, just above the chrome, in the row the transcript gave up
        frame.AddRange(chromeRows.Select(r => r.Rendered));
        if (showPin) frame.Add(PromptPin(width));   //below the chrome rows still on screen, on the window's last row
        //frame holds exactly height rows, transcript plus hint plus chrome

        //a caret whose chrome row scrolled away is parked and hidden
        var caretShown = chrome.CaretRow - dropped < chromeRows.Count;
        var caretRow = System.Math.Min(
            transcriptHeight + (showHint ? 1 : 0)
                + System.Math.Clamp(chrome.CaretRow - dropped, 0, System.Math.Max(0, chromeRows.Count - 1)),
            height - 1);   //never address a row past the last one, which an empty chrome would allow
        var caretCol = System.Math.Clamp(chrome.CaretCol, 0, System.Math.Max(0, width - 1));

        //the selection is validated once per frame, and a repaint at the same width leaves it alone, so the highlight survives purr and blink frames
        var sel = Selection;
        var selValid = sel is not null && sel.ValidFor(width);
        var selBgOn = selValid ? ThemeOf().SelectionBgOn : "";

        //chrome spans go through the same single per-frame gate, which self-clears on a reflow or a row-count change
        var chromeSelValid = sel is not null && sel.ChromeValidFor(width, wholeChrome);
        var chromeSelBgOn = chromeSelValid ? ThemeOf().SelectionBgOn : "";

        var sb = new StringBuilder(Ansi.SyncStart).Append(Ansi.HideCursor).Append(Ansi.Reset);
        for (var r = 0; r < height; r++)
        {
            //an over-wide row would autowrap under CUP and shift every row below it down, so clip to visible cells
            var row = r < frame.Count ? frame[r] : "";
            if (width > 0) row = TermText.TruncateCells(row, width, glyphs: _glyphs);
            //the highlight goes in over visible cells and before LinkClose, and a blank row has nothing to paint
            if (selValid && cellRows.TryGetValue(r, out var cr))
            {
                var rowCells = UnicodeWidth.Of(TermText.StripAnsiForWidth(row));
                if (sel!.RowRange(cr.Item1, cr.Item2, rowCells, out var cs, out var ce) && cs < ce)
                    row = TermText.HighlightCells(row, cs, ce, selBgOn);
            }
            //the same highlight over chrome row space, and the map holds only painted chrome rows so a transcript row never matches
            if (chromeSelValid && chromeRowMap.TryGetValue(r, out var ci))
            {
                var rowCells = UnicodeWidth.Of(TermText.StripAnsiForWidth(row));
                if (sel!.ChromeRowRange(ci, rowCells, out var cs, out var ce) && cs < ce)
                    row = TermText.HighlightCells(row, cs, ce, chromeSelBgOn);
            }
            //close the link at the end of every row. a truncated url drops its own close and a reset doesn't close a link scope, so it leaks over the rows after it
            sb.Append(Ansi.Cup(r + 1, 1)).Append(Ansi.ClearLine).Append(row).Append(Ansi.LinkClose);
            //the focus cursor is a separate Cup writing an accent bar in column 0, so it never edits the row's own SGR
            if (FocusedItemIndex is int fi && hitRows.TryGetValue(r, out var ht)
                && ht is ItemTarget { } itF && itF.ItemIndex == fi)
                sb.Append(Ansi.Cup(r + 1, 1)).Append(ThemeOf().Paint($"{_glyphs.Box.Vertical}", Theme.Accent));
        }
        //the caret is always parked where it belongs and only shown when the chrome asks for it, so an input-less panel keeps the cursor hidden
        sb.Append(Ansi.Cup(caretRow + 1, caretCol + 1))
            .Append(chrome.CaretVisible && caretShown ? Ansi.ShowCursor : "").Append(Ansi.SyncEnd);
        return sb.ToString();
    }

    //the index already holds the theme it counts with, so reuse it and the counts and the drawn rows stay the same render
    private Theme ThemeOf() => index.Theme;

    //the hint row for a reader scrolled up into history, so a streaming answer or a tool below it is not missed
    private string JumpHint(int rowsBelow, int width)
    {
        var text = $"{_glyphs.Down} {rowsBelow} more {_glyphs.Dot} Ctrl+End or click";
        var fit = width > 0 ? TermText.TruncateCells(text, width, glyphs: _glyphs) : text;
        return ThemeOf().Paint(fit, Theme.Accent);
    }

    //the row that says a prompt waits below the window and how to reach it
    private string PromptPin(int width)
    {
        var text = $"{_glyphs.Down} a prompt is waiting for your answer {_glyphs.Dot} Ctrl+End or click";
        var fit = width > 0 ? TermText.TruncateCells(text, width, glyphs: _glyphs) : text;
        return ThemeOf().Paint(fit, Theme.Warn);
    }

    //focus is marked by the gutter rail ItemRender draws, don't go back to washing the whole row in reverse video
}
