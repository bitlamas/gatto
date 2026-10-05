using System.Linq;
using System.Text;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the rows a panel factory composes for one width, plus where the caret goes in them. a null caret means the panel takes no input and the cursor stays hidden
public readonly record struct PanelContent(IReadOnlyList<string> Rows, (int Row, int Col)? Caret = null, bool KeepsRoom = false);   //true takes the whole room rather than half the window

//everything the chrome block shows, mutated only under the painter's _gate, so the paint reads it in the same lock without a snapshot
public sealed class ChromeState
{
    public string? TailRaw;            //the in-progress logical line, held until it commits
    public bool TailReasoning;         //paint the streaming line dim and italic
    public bool TailFence;             //the streaming line sits inside a code fence, so it takes the code band, its colours wait for the commit
    public string? TailMarker;         //the marker drawn when this tail opens a block, null to hang under the row above
    public bool TailBlank;             //the visible tail opens a block that will get a leading blank when it commits
                                       //the live tail shows the same blank the committed rows will, so the separator doesn't pop in at commit
    public (string Name, string Args)? Tool;   //the tool row that is running, its dot blinks with BlinkOn
    public string? ToolInput;          //the running shell call's full command, line breaks kept, and null for any other tool
    public string? ToolWait;           //the ticker's "waiting for your input …" suffix on the tool row
    public string? ToolProgress;       //the run_agent liveness row, "<agent> · turn N", replaced in place
    public string? Notice;             //a status row that shows for a moment, such as a working notice
    public string? PurrText;           //the full purr row text, composed by the ticker, null when hidden
    public bool BlinkOn = true;        //the ticker-driven 500 ms phase for the tool dot
    //true while reasoning is suppressed, the ticker adds " ~ thinking" to the purr row
    public bool Thinking;
    //the queued messages as the user typed them, oldest first
    public IReadOnlyList<string> QueueTexts = Array.Empty<string>();
    //non-null swaps the composer frame's editor rows for these panel rows, keeping the rules and status line. the editor itself is left alone so a clear restores it
    public IReadOnlyList<string>? PanelRows;
    //non-null makes PanelRows a cache, re-derived at the live width and allowance each compose, so the closure must capture a snapshot of its inputs
    public Func<int, int, PanelContent>? PanelFactory;
    //false keeps the hardware cursor hidden while this panel is up, true only for panels that take typed input
    public bool PanelCaretVisible = true;
    //where a factory panel's input caret sits, as a row within PanelRows and a column. null parks it on the last row, or hides it for an input-less panel
    public (int Row, int Col)? PanelCaret;
    //the panel is an answer to read, not a prompt: Esc or a key closes it, and any prompt that sets its own panel replaces it
    public bool PanelInfo;
    //the panel takes the whole room the furniture leaves rather than half the window, a permission prompt whose detail is what the user approves
    public bool PanelKeepsRoom;
    public EditorView Composer = new(new List<string> { "" }, 0, 0);
    //the predicate every panel read site uses, true only when the panel has rows (an empty panel would park the caret on nothing)
    public bool PanelUp => PanelRows is { Count: > 0 };
}

//chrome is always the buffer suffix, erased from the last paint and rewritten inside one DEC sync-update window under the session gate
public sealed class ChromePainter
{
    private readonly ITermSurface _surface;
    private readonly Theme _theme;
    //this run's glyph set, handed in rather than resolved here
    private readonly GlyphSet _glyphs;

    //the ticker reads this too, it composes the purr and the completion line so both must come out in the same glyph set
    internal GlyphSet Glyphs => _glyphs;
    private readonly object _gate;
    private readonly ScrollController _scroll;
    private readonly ViewportCompositor _compositor;
    private readonly FocusController _focus;
    private readonly SelectionController _selection;
    private readonly MouseController _mouse;
    private readonly AltScreen _altScreen;
    private bool _tornDown;
    //the layout the last Compose built, so the mouse reads the snapshot the displayed frame used
    private ComposerLayout? _lastComposerLayout;
    private int _composerTop;   //the composer window's first region row, kept across paints and moved only by the caret
    //the prompt panel's own scroll window, driven by MouseController through the onComposerScroll callback wired in the constructor. public for tests
    private readonly ComposerScroll _composerScroll = new();
    public ComposerScroll ComposerScroll => _composerScroll;

    public ChromeState State { get; } = new();

    //rebuilt by the Repl's RefreshStatus, read here to compose the frame
    public InputFrame Frame { get; set; } = null!;

    //the retained transcript this painter draws, shared with StreamRenderer and the Repl
    public TranscriptModel Model { get; }
    //the live terminal width, so a test can render the model at the width the compositor uses
    public int Width => _surface.Width;
    //the viewport scroll owner, the Repl wires the scroll keys to it
    public ScrollController Scroll => _scroll;
    //the focus owner for collapsible items, the Repl wires Ctrl+↑/↓/R to it. built here because the index, scroll and compositor it needs are painter-owned
    public FocusController Focus => _focus;
    //the mouse router, the Repl's mouse sink calls Mouse.Handle. built here because the compositor, scroll and focus it drives are painter-owned
    public MouseController Mouse => _mouse;
    //the text-selection engine, the compositor paints the highlight from it and the copy sink reads CopyText. painter-owned so it shares the one model and gate
    public SelectionController Selection => _selection;
    //the chrome block's last-painted row list, so the copy sink hands CopyText the same rows ChromeStillValid snapshots against, without reaching into the compositor
    public IReadOnlyList<ChromeRow> ChromeRowInfo => _compositor.ChromeRowInfo;
    //the alt-screen lifecycle, the Repl enters it and registers the exit hooks
    public AltScreen AltScreen => _altScreen;

    //the painter owns the index, scroll and compositor, pins the chrome to the physical bottom and the compositor draws the transcript window above
    public ChromePainter(ITermSurface surface, Theme theme, object gate, TranscriptModel? model = null, int wheelLines = 3,
        Action<int, int, ComposerGesture>? onComposerSelect = null,
        GlyphSet? glyphs = null, ITerminalTitle? terminalTitle = null)
    {
        _surface = surface; _theme = theme; _gate = gate;
        Model = model ?? new TranscriptModel("generalist");
        var index = new LineIndex(Model, theme, _glyphs);
        _scroll = new ScrollController(Model, index);
        _altScreen = new AltScreen(surface, terminalTitle);   //a null title lets AltScreen fall back to its own default
        _glyphs = glyphs ?? GlyphSet.Unicode;
        _compositor = new ViewportCompositor(surface, index, gate, _glyphs);
        _compositor.ComposeChrome = ComposeChromeBlock;
        _focus = new FocusController(Model, index, _scroll, _compositor);
        _selection = new SelectionController(Model);
        _compositor.Selection = _selection;   //the compositor reads the selection each paint and clears it itself
        //composerLayout reads what the last Compose cached, the snapshot the displayed frame was built from, so the mouse never recomputes off live state
        _mouse = new MouseController(_compositor, _scroll, _focus, Model, wheelLines, _selection, theme,
            glyphs: _glyphs,
            onComposerSelect: onComposerSelect, composerLayout: () => _lastComposerLayout,
            //the Repl's mouse sink already holds the gate and repaints after every Handle call, so this callback must not repaint or lock again
            onComposerScroll: n => { if (!State.PanelUp) return false; _composerScroll.ScrollBy(n); return true; });
    }

    public void Repaint() { lock (_gate) { if (!_tornDown) DriveCompositor(); } }

    //the rows are already in the model when this is called, so it takes no argument. a detached reader keeps its anchor, a following one shows the bottom
    public void NotifyCommitted()
    {
        lock (_gate) { if (!_tornDown) { _scroll.OnModelGrew(_surface.Width); DriveCompositor(); } }
    }

    //false once Teardown has run. a dead painter swallows Repaint and NotifyCommitted, so a late caller must fall back to its own surface
    public bool Alive { get { lock (_gate) return !_tornDown; } }

    //the one way to publish a prompt, under the gate so the assignment and the paint are one step. null or empty rows restores the editor rows
    public void SetPanel(IReadOnlyList<string>? rows)
    {
        lock (_gate)
        {
            if (_tornDown) return;
            State.PanelFactory = null;   //a static publish or a clear retires the recompose factory
            State.PanelInfo = false;
            //a static panel takes input, and so does the composer a clear brings back, so the caret comes back either way
            State.PanelCaretVisible = true;
            State.PanelCaret = null;
            State.PanelKeepsRoom = false;
            State.PanelRows = rows;
            _composerScroll.Reset();   //a fresh publish is always a new panel or a clear, so reset the scroll
            DriveCompositor();
        }
    }

    //the rows are re-derived from the factory at the live width on every compose, which is what makes a resize repaint re-wrap them
    public void SetPanelFactory(Func<int, IReadOnlyList<string>> factory) =>
        SetPanelFactory((w, _) => new PanelContent(factory(w)));

    //rows and a row allowance, for a caller whose panel is sized by the space it gets. no caret, the wizard's panels take input through the face's key loop
    public void SetPanelFactory(Func<int, int, IReadOnlyList<string>> factory) =>
        SetPanelFactory((w, h) => new PanelContent(factory(w, h)));

    //the width-only form for a caller with no height opinion, SelectPrompt's option list is as long as the list rather than the screen
    public void SetPanelFactory(Func<int, PanelContent> factory) =>
        SetPanelFactory((w, _) => factory(w));

    //the caret-aware form, the factory reports the caret at each width and the panel's own height budget so a vertical resize re-asks it
    public void SetPanelFactory(Func<int, int, PanelContent> factory)
    {
        lock (_gate)
        {
            if (_tornDown) return;
            State.PanelFactory = factory;
            State.PanelInfo = false;
            State.PanelKeepsRoom = false;   //a new panel is first asked at the half, its own content then says whether it keeps the room
            _composerScroll.Reset();   //a new factory is always a new panel, so reset the scroll
            RefreshPanel(factory(_surface.Width, PanelRowsAvailable()));
            DriveCompositor();
        }
    }

    //an answer drawn where the composer was, with no caret, opened at its top row so the header reads first
    public void SetInfoPanel(Func<int, int, IReadOnlyList<string>> factory)
    {
        lock (_gate)
        {
            //a prompt replaces an answer, never the other way, so an answer that arrives over a prompt is dropped
            if (_tornDown || (State.PanelUp && !State.PanelInfo)) return;
            SetPanelFactory(factory);
            State.PanelInfo = true;
            _composerScroll.ScrollBy(int.MaxValue / 2);
            DriveCompositor();
        }
    }

    //closes an info panel and leaves any other panel alone, true when one was open
    public bool CloseInfoPanel()
    {
        lock (_gate)
        {
            if (!State.PanelInfo) return false;
            SetPanel(null);
            return true;
        }
    }

    //moves an open panel's window, a positive count toward its top
    public void ScrollPanel(int linesTowardTop)
    {
        lock (_gate)
        {
            if (_tornDown || !State.PanelUp) return;
            _composerScroll.ScrollBy(linesTowardTop);
            DriveCompositor();
        }
    }

    //writes the rows, caret and caret-visibility into the state under the gate, and resets the scroll only when they changed
    private void RefreshPanel(PanelContent content)
    {
        if (!content.Rows.SequenceEqual(State.PanelRows ?? Array.Empty<string>()) || content.Caret != State.PanelCaret)
            _composerScroll.Reset();
        State.PanelRows = content.Rows;
        State.PanelCaret = content.Caret;
        State.PanelKeepsRoom = content.KeepsRoom;
        State.PanelCaretVisible = content.Caret is not null;
    }

    //stops painting and restores the main screen. the compositor stops before the alt-screen restore, so a late tick can't paint chrome onto the restored screen
    public void Teardown()
    {
        lock (_gate)
        {
            if (_tornDown) return;
            _tornDown = true;
            _compositor.Stop();
            _altScreen.Restore();
        }
    }

    //one full-frame paint, the transcript window bottom-anchored at the scroll offset plus the chrome block pinned to the bottom
    private void DriveCompositor() =>
        _compositor.Paint(_scroll.BottomOffset(_surface.Width, TranscriptRows()), _scroll.Following);

    //the rows left for the transcript above the chrome block, the height the scroll controller must page against or the top rows stay unreachable
    public int ViewportRows() { lock (_gate) return TranscriptRows(); }

    private int TranscriptRows()
    {
        var h = _surface.Height <= 0 ? ViewportCompositor.HeightFloor : _surface.Height;
        var chromeH = System.Math.Min(ComposeChromeBlock(_surface.Width, h).Rows.Count, h);
        return System.Math.Max(1, h - chromeH);
    }

    //how many rows a panel may use before Compose drops them from the top, the same expression the clamp uses. an unknown height comes back as 0
    public int PanelRowsAvailable()
    {
        lock (_gate) return _surface.Height <= 0 ? 0 : PanelRowsAvailable(_surface.Width, _surface.Height);
    }

    //a panel holds at most half the window's rows and the composer at most a third, so the transcript keeps the rest whatever the content
    private const int PanelShare = 2;
    private const int ComposerShare = 3;

    //the room a panel that keeps it is given, the same expression the clamp uses for a permission prompt
    internal int RoomRowsAvailable()
    {
        lock (_gate) return _surface.Height <= 0 ? 0 : RoomRows(_surface.Width, _surface.Height);
    }

    private int PanelRowsAvailable(int width, int height)
    {
        var room = RoomRows(width, height);
        return State.PanelKeepsRoom ? room : Share(room, height, PanelShare);
    }

    private int ComposerRowsAvailable(int width, int height) => Share(RoomRows(width, height), height, ComposerShare);

    //the share counts the whole window, the rows the user sees, and never exceeds the room the frame's furniture leaves
    private static int Share(int room, int height, int parts) =>
        room == int.MaxValue ? room : Math.Min(room, Math.Max(1, height / parts));

    //takes the width and height it was handed rather than the surface, and keeps int.MaxValue for an unknown height
    private int RoomRows(int width, int height)
    {
        var budgetRows = BudgetRows(height);
        if (budgetRows == int.MaxValue) return int.MaxValue;

        //measures the frame's own furniture through the same composition the clamp uses, and one probe row is enough because a panel row is single-physical
        var probe = Frame.Compose(State.Composer, panelRows: ProbePanel, panelCaret: null);
        var furniture = probe.TotalRows - probe.PanelRowCount;
        return Math.Max(1, budgetRows - furniture - ReservedLiveRows(width, height));
    }

    //rows the frame reserves while a turn is live, and the purr line stays even though PurrText is null whenever a panel is up
    private int ReservedLiveRows(int width, int height)
    {
        var n = 0;
        if (State.PurrText is not null) n += 2;                                                  //two rows, a blank and the purr line
        //a panel over the live call may take that call's rows, which repeat what the prompt shows, and the clamp drops them when it does
        if (State.PanelUp) return n + NewestQueuedMessageReservation(width, height);
        if (State.Tool is not null) n += 2 + (State.ToolProgress is not null ? 1 : 0);            //two rows, a blank and the tool bullet, plus one more for the progress row
        if (State.Tool is { } live && live.Name == "shell" && State.ToolInput is { } liveCommand)   //the live shell rows past the bullet, and the wait row under them
            n += ShellBlockRender.Layout(ShellBlockInput.Live(liveCommand, RoleForTint, null), ShellBlockState.Closed, width, _theme, _glyphs).Rows.Count - 1 + (State.ToolWait is not null ? 1 : 0);
        n += NewestQueuedMessageReservation(width, height);
        return n;
    }

    //the newest message's block capped at height/3, the one number the allowance needs that depends on nothing else
    private int NewestQueuedMessageReservation(int width, int height)
    {
        var q = State.QueueTexts;
        if (q.Count == 0) return 0;
        var newestRows = QueueBlock(q[^1], width).Count;
        var cap = Math.Max(1, height / 3);
        return 1 + Math.Min(newestRows, cap) + (q.Count > 1 ? 1 : 0);   //a blank, the newest message capped, and a roll-up row when older ones exist
    }

    //both Compose and the reservation wrap through this, so the rows offered and the rows enforced come from one wrap
    private List<(string R, string V)> QueueBlock(string message, int width)
    {
        var wrapAt = Math.Max(1, width - 2);
        var band = Ansi.Bg(_theme.Map(Theme.UserInputBg), _theme.TrueColor);
        var rows = new List<(string, string)>();
        foreach (var line in message.Split('\n'))
            foreach (var seg in SoftWrap.Wrap(TermText.ExpandTabs(line, out _), wrapAt, wrapAt))   //tabs widen as the composer drew them
            {
                var lead = rows.Count == 0 ? _glyphs.Prompt + " " : GutterWrap.Hang;
                var v = lead + seg.Text;
                rows.Add((band + Ansi.ClearToEol + _theme.Paint(v, Theme.Bright), v));
            }
        if (rows.Count == 0) rows.Add((band + Ansi.ClearToEol, ""));
        return rows;
    }

    private static readonly string[] ProbePanel = [""];

    //a window marks its hidden rows only when a row is left between the two marks for the caret
    internal const int MarkedWindowRows = 3;

    //the dim edge row of a window that hides rows, counting the row it covers
    private string HiddenMark(string arrow, int hidden) => _theme.Paint($"{GutterWrap.Hang}{arrow} {hidden} more", Theme.Dim);

    //a mark never covers the caret's row, the window moves one more row instead, and a caret outside the window leaves it where it is
    private static int ClearOfMarks(int top, int caret, int total, int rows)
    {
        if (rows < MarkedWindowRows || caret < top || caret >= top + rows) return top;
        if (top > 0 && caret == top) top--;
        else if (top + rows < total && caret == top + rows - 1) top++;
        return Math.Clamp(top, 0, total - rows);
    }

    //the height clamp in one place, the viewport less one row of headroom

    //the chat output keeps at least this many rows, and every consumer reads it through BudgetRows so they all shrink together
    private const int TranscriptMinRows = 3;
    private static int BudgetRows(int height) => height <= 0 ? int.MaxValue : Math.Max(3, height - TranscriptMinRows);

    //the clamp's category for the newest queued message and the blank above it, dropped last after the purr
    private const string NewestQueue = "queue-newest";

    //composes the bottom chrome block, with the caret converted from up-from-end to row-from-top
    public ChromeBlock ComposeChromeBlock(int width, int height)
    {
        var composed = Compose(width, height);
        var caretRow = System.Math.Max(0, composed.Rows.Count - 1 - composed.CaretUpFromEnd);
        //the cursor stays visible unless the frame shows an input-less panel, or a scroll left its caret row outside the window
        var caretVisible = !State.PanelUp || (State.PanelCaretVisible && composed.CaretInWindow);
        return new ChromeBlock(composed.Rows, caretRow, composed.CaretCol, caretVisible);
    }

    //test-only, exposes Compose so the alignment test asserts against what the painter really paints
    internal (List<string> Rendered, List<string> Visible, int CaretUpFromEnd, int CaretRowsFromEnd, int CaretCol)
        ComposeForTest(int width, int height)
    {
        var c = Compose(width, height);
        return (c.Rows.Select(r => r.Rendered).ToList(), c.Rows.Select(r => r.Visible).ToList(),
            c.CaretUpFromEnd, c.CaretRowsFromEnd, c.CaretCol);
    }

    //chrome rows are cut to width and tail rows pre-wrapped. the clamp drops tail rows first, then whole categories, keeping the composer and status
    private (List<ChromeRow> Rows, int CaretUpFromEnd, int CaretRowsFromEnd, int CaretCol, bool CaretInWindow)
        Compose(int width, int height)
    {
        var built = new List<ChromeRow>();
        void Row(string r, string v, ChromeRegion region = ChromeRegion.Other, int regionRow = 0,
            bool continuation = false, int prefixCells = 0)
        {
            if (width > 0 && UnicodeWidth.Of(v) > width)
            {
                //the width measure counts visible cells and skips CSI runs, so an ANSI-painted row cuts correctly
                v = ChromeTrunc(v, width, _glyphs);
                r = ChromeTrunc(r, width, _glyphs);
            }
            built.Add(new ChromeRow(r, v, continuation, prefixCells, region, regionRow));
        }

        var tailRows = new List<(string R, string V, bool Continuation, int PrefixCells)>();
        //show the streaming tail only while following the bottom, a scrolled-up view already has the jump-to-bottom hint
        if (_scroll.Following && State.TailRaw is { Length: > 0 } fenced && State.TailFence)
        {
            //the committed fence row's own layout with no code roles, so the row keeps its shape at the commit and only its colours arrive
            var block = BlockState.Fence;
            var open = true;
            var blank = false;
            foreach (var row in ItemRender.ProseLineTagged(fenced, _theme, RoleForTint, width, ref block, ref open, ref blank, _glyphs))
                tailRows.Add((row.Text, TermText.StripAnsiForWidth(row.Text), row.Continuation, row.PrefixCells));
            if (State.TailBlank && tailRows.Count > 0) tailRows.Insert(0, ("", "", false, 0));
        }
        else if (_scroll.Following && State.TailRaw is { Length: > 0 } tail)
        {
            var marker = State.TailMarker ?? GutterWrap.Hang;
            Func<string, string>? paint = State.TailReasoning
                ? s => _theme.Paint(s, Theme.Dim, italic: true) : null;
            var paintedRows = GutterWrap.Rows(marker, "@ ", tail, width, paint);
            //the marker is a plain 2-cell stand-in here, RowsTagged never reads it, so this call yields the visible text and matching tags
            var taggedRows = GutterWrap.RowsTagged("@ ", "@ ", tail, width);
            for (var i = 0; i < paintedRows.Count; i++)
                tailRows.Add((paintedRows[i], taggedRows[i].Text, taggedRows[i].Continuation, taggedRows[i].PrefixCells));
            //the blank above the live tail when it opens a block, so the separator doesn't first appear at commit. it goes at index 0 so the clamp sheds it first
            if (State.TailBlank) tailRows.Insert(0, ("", "", false, 0));
        }

        //an empty placeholder keeps the tail slot while a turn is live, so the chrome height only grows during streaming
        if (tailRows.Count == 0 && State.Tool is null && State.PurrText is not null)
            tailRows.Add(("", "", false, 0));

        //each fixed row is tagged with its drop category, so the clamp removes whole categories without touching the others. the region comes from the same string
        var fixedRows = new List<(string R, string V, string Category, ChromeRegion Region, int RegionRow)>();
        var regionCounters = new Dictionary<ChromeRegion, int>();
        int NextRow(ChromeRegion region)
        {
            regionCounters.TryGetValue(region, out var n);
            regionCounters[region] = n + 1;
            return n;
        }
        if (State.Tool is { } t)
        {
            //the blank above the live tool bullet separates it from the transcript above, tagged so the clamp drops it with the tool
            fixedRows.Add(("", "", "tool", ChromeRegion.Tool, NextRow(ChromeRegion.Tool)));
            //the off phase paints a space in the dot cell, the row repaints every tick so alignment holds
            var dot = State.BlinkOn ? _theme.Paint(ItemRender.ToolMarkerOf(_glyphs), _theme.RoleTint(RoleForTint)) : " ";
            var args = t.Args;
            if (State.ToolWait is { } w && width > 0)
            {
                //the waiting suffix wins the row, so shrink the args to fit and drop them below a useful budget
                var budget = width - (2 + UnicodeWidth.Of(t.Name) + 1 + 1 + UnicodeWidth.Of(w));
                if (UnicodeWidth.Of(args) > budget)
                    args = budget < 8 ? "" : TermText.TruncateCells(args, budget, glyphs: _glyphs);
            }
            var line = args.Length > 0 ? t.Name + " " + args : t.Name;
            var lineR = _theme.Paint(t.Name, Theme.ToolName) + (args.Length > 0 ? " " + ItemRender.PaintToolArgs(t.Name, args, _theme) : "");
            if (t.Name == "shell" && State.ToolInput is { } liveCommand)
            {
                var live = ShellBlockRender.Layout(ShellBlockInput.Live(liveCommand, RoleForTint, dot), ShellBlockState.Closed, width, _theme, _glyphs).Rows;
                for (var k = 0; k < live.Count; k++)
                {
                    var visible = TermText.StripAnsiForWidth(live[k].Text);
                    if (k == 0) visible = _glyphs.Loaded + visible[TermText.StripAnsiForWidth(dot).Length..];   //the visible text takes the steady glyph, since the blink phase is paint only
                    fixedRows.Add((live[k].Text, visible, "tool", ChromeRegion.Tool, NextRow(ChromeRegion.Tool)));
                }
                if (State.ToolWait is { } shellWait)
                    fixedRows.Add((GutterWrap.Hang + _theme.Paint(shellWait, Theme.Dim), GutterWrap.Hang + shellWait, "tool", ChromeRegion.Tool, NextRow(ChromeRegion.Tool)));
            }
            else
            {
                if (State.ToolWait is { } ws)
                {
                    line += " " + ws;
                    lineR += " " + _theme.Paint(ws, Theme.Dim);
                }
                fixedRows.Add((dot + " " + lineR, _glyphs.Loaded + " " + line, "tool", ChromeRegion.Tool,
                    NextRow(ChromeRegion.Tool)));
            }
        }
        if (State.ToolProgress is { } tp)
            fixedRows.Add((GutterWrap.Hang + _theme.Paint(tp, Theme.Dim), GutterWrap.Hang + tp, "tool",
                ChromeRegion.Tool, NextRow(ChromeRegion.Tool)));
        //a notice and the purr each get a blank row above, tagged with their category so the clamp drops the pair together
        if (State.Notice is { } n)
        {
            fixedRows.Add(("", "", "notice", ChromeRegion.Notice, NextRow(ChromeRegion.Notice)));
            fixedRows.Add((_theme.Paint(n, Theme.Warn), n, "notice", ChromeRegion.Notice, NextRow(ChromeRegion.Notice)));
        }
        if (State.PurrText is { } p)
        {
            //the live purr row is painted in the role tint, and it settles to a grey purred-for line at turn end
            fixedRows.Add(("", "", "purr", ChromeRegion.Purr, NextRow(ChromeRegion.Purr)));
            fixedRows.Add((_theme.Paint(p, _theme.RoleTint(RoleForTint)), p, "purr", ChromeRegion.Purr, NextRow(ChromeRegion.Purr)));
        }

        //the queue shows the user's own messages, and the newest are the ones that fit since the composer is below the block

        //the clamp is computed before the frame, since a too-tall panel can only be trimmed on the way in
        var budgetRows = BudgetRows(height);

        //a factory panel re-derives its rows from the live width every compose, so a resize repaint re-wraps them, and the allowance comes with it
        if (State.PanelFactory is { } panelFactory)
            RefreshPanel(panelFactory(width, PanelRowsAvailable(width, height)));
        //an empty panel is not a panel, so SetPanel([]) leaves the editor rows showing. the frame guards it too, this read is the product rule
        var panel = State.PanelUp ? State.PanelRows : null;
        var panelCaret = panel is null ? null : State.PanelCaret;
        var frame = Frame.Compose(State.Composer, panelRows: panel, panelCaret: panelCaret);
        //asks PanelRowsAvailable rather than comparing TotalRows, so the offered and enforced numbers stay one expression, and each call makes a probe frame
        var panelAllowed = PanelRowsAvailable(width, height);
        var caretInWindow = true;
        if (panel is { } tall && tall.Count > panelAllowed)
        {
            //the window is the ComposerScroll offset, following shows the tail while a detached scroll shows earlier rows. it runs even at offset 0
            var drop = _composerScroll.Offset(tall.Count, panelAllowed);
            if (panelCaret is { } at) drop = ClearOfMarks(drop, at.Row, tall.Count, panelAllowed);
            var window = tall.Skip(drop).Take(panelAllowed).ToList();
            if (window.Count >= MarkedWindowRows)
            {
                if (drop > 0) window[0] = HiddenMark(_glyphs.Up, drop + 1);
                if (drop + window.Count < tall.Count) window[^1] = HiddenMark(_glyphs.Down, tall.Count - drop - window.Count + 1);
            }
            panel = window;
            if (panelCaret is { } pc)
            {
                //a scrolled-up view can leave the caret row past the visible slice. caretInWindow hides the cursor rather than parking it on an option row
                var shifted = pc.Row - drop;
                caretInWindow = shifted >= 0 && shifted < panelAllowed;
                panelCaret = (Math.Max(0, shifted), pc.Col);
            }
            frame = Frame.Compose(State.Composer, panelRows: panel, panelCaret: panelCaret);
        }
        else if (panel is null && frame.EditorRowTags.Count is var total && total > ComposerRowsAvailable(width, height))
        {
            //the window moves only as far as keeps the caret inside, so typing within it never scrolls
            var composerAllowed = ComposerRowsAvailable(width, height);
            var caret = frame.CursorRegionRow;
            var top = Math.Min(_composerTop, caret);
            top = Math.Max(top, caret - composerAllowed + 1);
            _composerTop = ClearOfMarks(Math.Clamp(top, 0, total - composerAllowed), caret, total, composerAllowed);
            frame = Frame.Compose(State.Composer, editorWindow: (_composerTop, composerAllowed), markHidden: composerAllowed >= MarkedWindowRows);
        }
        //caches the layout this frame was built from, and a panel frame reports null, which the mouse's layout bail expects
        _lastComposerLayout = frame.Composer;

        var frameRows = frame.TotalRows;

        //with frameRows known, size the older messages from the room left. the cap and queueRoom are two ceilings, so fill the content first, then the roll-up
        var q = State.QueueTexts;
        if (q.Count > 0)
        {
            var newest = QueueBlock(q[^1], width);
            var cap = Math.Max(1, height / 3);
            var queueRoom = budgetRows - frameRows - fixedRows.Count - 1 - newest.Count;
            var queueAvail = Math.Max(0, Math.Min(cap - newest.Count, queueRoom));

            var older = new List<List<(string R, string V)>>();
            for (var m = 0; m < q.Count - 1; m++) older.Add(QueueBlock(q[m], width));

            (List<List<(string R, string V)>> Blocks, int First, int Used) Fill(int avail)
            {
                var blocks = new List<List<(string R, string V)>> { newest };
                var first = q.Count - 1;
                var used = 0;
                for (var m = q.Count - 2; m >= 0; m--)
                {
                    var b = older[m];
                    if (used + b.Count > avail) break;
                    blocks.Insert(0, b);
                    used += b.Count;
                    first = m;
                }
                return (blocks, first, used);
            }

            var fill = Fill(queueAvail);
            //the roll-up row must fit in queueRoom, the physical room left, which can be tighter than the cap. refill only when the first fill left something out
            if (fill.First > 0 && fill.Used + 1 > queueRoom)
                fill = Fill(Math.Max(0, queueRoom - 1));
            var (blocks, first, _) = fill;
            //the blank above the queue block, tagged with the newest message's category so the clamp drops it with that message, and region row -1
            fixedRows.Add(("", "", NewestQueue, ChromeRegion.Queue, -1));
            if (first > 0)
            {
                var v = $"(+{first} more)";
                fixedRows.Add((GutterWrap.Hang + _theme.Paint(v, Theme.Dim), GutterWrap.Hang + v, "queue",
                    ChromeRegion.Queue, -1));
            }
            for (var b = 0; b < blocks.Count; b++)
                foreach (var (r, v) in blocks[b])
                    fixedRows.Add((r, v, b == blocks.Count - 1 ? NewestQueue : "queue", ChromeRegion.Queue, first + b));
        }

        var room = budgetRows - frameRows - fixedRows.Count;
        var tailStart = room >= tailRows.Count ? 0 : Math.Max(0, tailRows.Count - Math.Max(0, room));

        //drop whole categories in order, older queue, notice, tool, purr, and the newest message last, so a queued message is never hidden before the purr
        var keptTail = tailRows.Count - tailStart;
        var overflow = keptTail + fixedRows.Count + frameRows - budgetRows;
        if (overflow > 0)
            foreach (var category in new[] { "queue", "notice", "tool", "purr", NewestQueue })
            {
                if (overflow <= 0) break;
                var before = fixedRows.Count;
                fixedRows.RemoveAll(row => row.Category == category);
                overflow -= before - fixedRows.Count;
            }

        for (var i = tailStart; i < tailRows.Count; i++)
        {
            var tr = tailRows[i];
            Row(tr.R, tr.V, ChromeRegion.Tail, i - tailStart, tr.Continuation, tr.PrefixCells);
        }
        foreach (var (r, v, _, region, regionRow) in fixedRows) Row(r, v, region, regionRow);

        //the blank between the last chrome row and the frame, added only when the budget has a spare row so it yields to content
        var gap = budgetRows == int.MaxValue || built.Count + frameRows + 1 <= budgetRows;
        if (gap) Row("", "");
        //never truncate frame rows, their counts are physical and cutting them would desync the caret park. region Composer marks the typed text and Rule the furniture
        var bodyCount = frame.PanelRowCount > 0 ? frame.PanelRowCount : frame.EditorRowTags.Count;
        var editorStart = frame.Rows.Count - bodyCount > 0 ? 1 : 0;
        var ruleRow = 0;
        for (var i = 0; i < frame.Rows.Count; i++)
        {
            var editorIdx = i - editorStart;
            if (frame.PanelRowCount > 0)
            {
                //panel rows get the Prompt region, so drag-select-copy treats them as chrome and no gesture hit-tests them as composer text. this is its only producer
                built.Add(editorIdx >= 0 && editorIdx < frame.PanelRowCount
                    ? new ChromeRow(frame.Rows[i], frame.VisibleRows[i], false, 0, ChromeRegion.Prompt, editorIdx)
                    : new ChromeRow(frame.Rows[i], frame.VisibleRows[i], false, 0, ChromeRegion.Rule, ruleRow++));
            }
            else if (editorIdx >= 0 && editorIdx < frame.EditorRowTags.Count)
            {
                var (cont, prefix) = frame.EditorRowTags[editorIdx];
                //the region row counts over the whole layout, so a mouse gesture on a windowed row maps to its own text
                built.Add(new ChromeRow(frame.Rows[i], frame.VisibleRows[i], cont, prefix, ChromeRegion.Composer,
                    frame.EditorWindowTop + editorIdx));
            }
            else
            {
                built.Add(new ChromeRow(frame.Rows[i], frame.VisibleRows[i], false, 0, ChromeRegion.Rule, ruleRow++));
            }
        }

        //the park distance, frame rows after the cursor row. a panel parks on the same contract, the frame reports the cursor row of whichever body it emitted
        var caretUp = frame.TotalRows - 1 - frame.CursorRowOffset;

        //the caret's logical row from the end, which AppendErase needs to index _lastVisible. it differs from the physical row when a row below the caret wraps
        var caretEntry = frame.ScreenRows.Count - 1;
        var acc = 0;
        for (var i = 0; i < frame.ScreenRows.Count; i++)
        {
            if (acc + frame.ScreenRows[i] > frame.CursorRowOffset) { caretEntry = i; break; }
            acc += frame.ScreenRows[i];
        }
        //the frame is the suffix of the composed list, so the end of frame rows is the end of the whole list
        var caretRowsFromEnd = frame.Rows.Count - 1 - caretEntry;
        return (built, caretUp, caretRowsFromEnd, frame.CursorCol, caretInWindow);
    }

    //the role name that tints the blinking dot, set once by the Repl
    public string RoleForTint { get; set; } = "generalist";

    //the glyph set comes in as an argument, since a static that resolved its own would undo the one glyph table
    private static string ChromeTrunc(string s, int width, GlyphSet g) =>
        TermText.TruncateCells(s, width, g);
}
