using System.Globalization;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//what one key did to a panel: nothing, a repaint, or the answer the show ends on
internal readonly record struct PanelStep(bool Repaint, SelectOutcome? Outcome = null)
{
    public static readonly PanelStep None = new(false);
    public static readonly PanelStep Changed = new(true);
}

//a mouse gesture resolved against the frame the user saw, queued for the loop thread
internal abstract record PanelGesture
{
    private protected PanelGesture() { }

    public sealed record Hover(PanelHit? Hit) : PanelGesture;
    public sealed record Press(PanelHit Hit) : PanelGesture;
    public sealed record Wheel(int Rows) : PanelGesture;
    public sealed record Leave : PanelGesture;
}

//what a panel adds to the select loop, owned by the loop thread. the layout, the band flag and the gesture queue are shared under the lock
internal sealed class PanelState
{
    //the key that wakes the loop for a queued gesture. no input path produces it and no printable arm can take it
    public static readonly ConsoleKeyInfo Wake = new('\0', ConsoleKey.NoName, false, false, false);

    //the detail offset that follows the end, so a growing tail stays in view until the user scrolls
    public const int End = int.MaxValue;

    private static readonly FooterKey[] DetailBack = [new("Esc", "back")];

    private readonly PanelFields _panel;
    private readonly IReadOnlyList<SelectOption>[] _lists;
    private readonly TabState[] _tabs;
    private readonly bool _numbered;
    private readonly IReadOnlyList<char>? _controls;
    private readonly object _lock = new();
    private readonly Queue<PanelGesture> _gestures = new();
    private PanelLayout? _layout;
    private int _layoutTab;
    private bool _banded;   //a key word wears the hover band, read by the pump thread under the lock
    private int _tab;
    private bool _inBox;
    private int? _moved;   //the setting's level index moved on the cursor row, reset when the cursor leaves it
    private string? _hoverKey;
    private string? _detail;   //the open detail's row key, null on the list
    private int _detailIndex = -1;
    private int _detailOffset = End;
    private IReadOnlyList<DetailRow> _detailRows = [];

    //each tab keeps its own cursor, filter and window, so a switch and back finds the tab as it was left
    private sealed class TabState
    {
        public string Draft = "";
        public int Cursor;   //a position in Shown
        public int Offset;
        public int[] Shown = [];
    }

    public PanelState(PanelFields panel, IReadOnlyList<SelectOption> options, bool numbered, int initialCursor,
        IReadOnlyList<char>? controls, string? detail = null, int detailOffset = End)
    {
        _panel = panel;
        _numbered = numbered;
        _controls = controls;
        _lists = panel.Tabs is { Count: > 0 } tabs ? [.. tabs.Select(t => t.Options)] : [options];
        _tabs = [.. _lists.Select(l => new TabState { Shown = [.. Enumerable.Range(0, l.Count)] })];
        _tabs[0].Cursor = _lists[0].Count == 0 ? -1 : Math.Clamp(initialCursor, 0, _lists[0].Count - 1);
        for (var t = 1; t < _tabs.Length; t++) _tabs[t].Cursor = _lists[t].Count == 0 ? -1 : 0;
        //the detail is found by what its row is, so a row that left under it drops it rather than opening a neighbour
        if (detail is not null && Enumerable.Range(0, _lists[0].Count).FirstOrDefault(i => KeyOf(i) == detail, -1) is var at
            && at >= 0 && _panel.DetailOf?.Invoke(at) is not null)
        {
            _detail = detail;
            _detailIndex = at;
            _detailOffset = detailOffset;
        }
    }

    public IReadOnlyList<SelectOption> OptionsOf(int tab) => _lists[tab];

    public bool Empty => _lists.All(l => l.Count == 0);

    //the live rows came back with another count, which only a re-entry may lay out
    public bool Stale { get; private set; }

    private TabState Here => _tabs[_tab];

    //the option's original index under the cursor, -1 when the filter shows nothing
    public int CursorIndex => Here.Cursor >= 0 && Here.Cursor < Here.Shown.Length ? Here.Shown[Here.Cursor] : -1;

    //the open detail's row key and its window's offset, End while it follows the tail
    public string? Detail => _detail;
    public int DetailOffset => _detailOffset;

    public string? SettingValue => _panel.Under is PanelUnder.Setting s && CursorIndex >= 0 ? ValueOf(s, CursorIndex) : null;

    //true only when the shown value differs from the row's own, moving away and back is no move
    public bool SettingMoved => _panel.Under is PanelUnder.Setting s && CursorIndex >= 0
        && _moved is not null && SettingValue != s.Value(CursorIndex);

    //the copy a frame is composed from, every caller function asked here on the loop thread so the paint thread runs none
    public PanelView View()
    {
        if (_panel.OptionsNow is { } now && _lists.Length == 1)
        {
            var fresh = now();
            if (fresh.Count == _lists[0].Count) _lists[0] = fresh;
            else Stale = true;
        }
        IReadOnlyList<FooterKey>? keys = null;
        if (_detail is not null)
        {
            _detailRows = [.. _panel.DetailOf?.Invoke(_detailIndex) ?? []];
            keys = _panel.DetailKeysFor?.Invoke(_detailIndex);
        }
        else if (!_inBox && CursorIndex >= 0) keys = _panel.KeysFor?.Invoke(CursorIndex);
        return new(_tab, [.. Here.Shown], Here.Shown.Length == 0 ? -1 : Here.Cursor, Here.Draft, _inBox,
            Here.Offset, SettingValue,
            _panel.Under is PanelUnder.Sentence sen && CursorIndex >= 0 ? sen.Text(CursorIndex) : null, _hoverKey,
            keys, _detail, _detail is null ? null : _lists[0][_detailIndex].Label, _detail is null ? null : _detailRows, _detailOffset);
    }

    //the factory's half, under the painter's gate: the frame it just composed, for the tab it composed
    public void Record(PanelLayout layout, int tab) { lock (_lock) { _layout = layout; _layoutTab = tab; } }

    public PanelLayout? Layout { get { lock (_lock) return _layout; } }

    //the layout the loop may act on: composed for this tab and this mode, so the first key after Enter or Esc never reads the other frame
    private PanelLayout? Mine
    {
        get { lock (_lock) return _layout is { } l && _layoutTab == _tab && l.Detail == _detail ? l : null; }
    }

    //the pump thread's half: an event at another width than the frame the user saw is dropped, the rest become one queued gesture and one wake
    public bool Mouse(MouseEvent e, int row, int width, Action wake)
    {
        PanelGesture? gesture;
        lock (_lock)
        {
            if (_layout is not { } l) return false;
            if (row < 0) gesture = new PanelGesture.Leave();
            else
            {
                if (l.Width != width) return false;
                //a detail's text is the chrome's, so a press there starts the copy drag and a move there only takes the band off
                if (l.Detail is not null && e.Kind is MouseKind.Press or MouseKind.Move && row >= l.BodyStart && row < l.BodyStart + l.BodyCount)
                {
                    if (e.Kind == MouseKind.Press || !_banded) return false;
                    gesture = new PanelGesture.Leave();
                }
                else
                {
                    var hit = l.At(row, e.X);
                    gesture = e.Kind switch
                    {
                        MouseKind.Move => new PanelGesture.Hover(hit),
                        MouseKind.Press when e.Button == MouseButton.Left && hit is not null => new PanelGesture.Press(hit),
                        //the event carries the lines the controller resolved from the notch, a list takes its sign and a detail the lines
                        MouseKind.Wheel => new PanelGesture.Wheel(l.Detail is null ? (e.WheelDelta > 0 ? -1 : 1) : -e.WheelDelta),
                        _ => null,
                    };
                }
            }
            if (gesture is null) return row >= 0;   //a press on a row no press acts on is still the panel's, no copy drag starts there
            //a run of moves needs only the last, so a hover replaces a hover still waiting and wakes nobody twice
            if (gesture is PanelGesture.Hover && _gestures.Count > 0 && _gestures.Last() is PanelGesture.Hover)
            {
                var kept = _gestures.ToArray();
                _gestures.Clear();
                foreach (var g in kept.Take(kept.Length - 1)) _gestures.Enqueue(g);
                _gestures.Enqueue(gesture);
                return true;
            }
            _gestures.Enqueue(gesture);
        }
        wake();
        return true;
    }

    public PanelStep Key(ConsoleKeyInfo k)
    {
        if (k.Key == ConsoleKey.NoName && k.KeyChar == '\0')
        {
            PanelGesture? g;
            lock (_lock) g = _gestures.Count > 0 ? _gestures.Dequeue() : null;
            return g is null ? PanelStep.None : Apply(g);
        }
        //the window the user saw is where the next move starts, so it only slides when the cursor reaches its edge. a detail following its tail keeps the sentinel
        if (Mine is { } l)
        {
            if (_detail is null) Here.Offset = l.Offset;
            else if (_detailOffset != End) _detailOffset = l.Offset;
        }
        return _detail is not null ? DetailKey(k) : _inBox ? BoxKey(k) : ListKey(k);
    }

    private PanelStep BoxKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                //a draft clears first, an empty box hands the keys back with the filter kept
                if (Here.Draft.Length > 0) { Filter(""); return PanelStep.Changed; }
                _inBox = false;
                return PanelStep.Changed;
            case ConsoleKey.Enter:
            case ConsoleKey.DownArrow:
                _inBox = false;
                MoveTo(Here.Shown.Length == 0 ? -1 : 0);
                return PanelStep.Changed;
            case ConsoleKey.Tab:
                _inBox = false;
                return PanelStep.Changed;
            case ConsoleKey.Backspace:
                if (Here.Draft.Length == 0) return PanelStep.None;
                Filter(Here.Draft[..^1]);
                return PanelStep.Changed;
            //the box has no caret to move, so these are spent here and reach no list arm and no setting
            case ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow
                or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.PageUp or ConsoleKey.PageDown:
                return PanelStep.None;
        }
        var chorded = (k.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0;
        if (chorded || k.KeyChar < ' ' || k.KeyChar == (char)0x7F) return PanelStep.None;
        Filter(Here.Draft + k.KeyChar);
        return PanelStep.Changed;
    }

    private PanelStep ListKey(ConsoleKeyInfo k)
    {
        var shown = Here.Shown.Length;
        switch (k.Key)
        {
            case ConsoleKey.UpArrow: return MoveTo(Math.Max(0, Here.Cursor - 1));
            case ConsoleKey.DownArrow: return MoveTo(Math.Min(shown - 1, Here.Cursor + 1));
            case ConsoleKey.PageUp: return MoveTo(Math.Max(0, Here.Cursor - Page()));
            case ConsoleKey.PageDown: return MoveTo(Math.Min(shown - 1, Here.Cursor + Page()));
            case ConsoleKey.Home: return MoveTo(0);
            case ConsoleKey.End: return MoveTo(shown - 1);
            case ConsoleKey.LeftArrow: return Side(-1);
            case ConsoleKey.RightArrow: return Side(+1);
            case ConsoleKey.Enter:
                return CursorIndex < 0 ? PanelStep.None : Choose(CursorIndex);
            case ConsoleKey.Escape:
                return new PanelStep(false, new SelectOutcome.Cancelled());
            case ConsoleKey.Tab when Searchable:
                _inBox = true;
                return PanelStep.Changed;
        }
        var c = k.KeyChar;
        var chorded = (k.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0;
        if (chorded) return PanelStep.None;
        if (c == '?' && Searchable) { _inBox = true; return PanelStep.Changed; }

        //the digit column numbers the shown rows, so a digit over a filtered list picks the row it is drawn beside
        if (_numbered && c >= '1' && c <= '9' && c - '1' < shown && c - '1' < SelectPrompt.NumberedRows)
        {
            MoveTo(c - '1');
            return new PanelStep(true, new SelectOutcome.Chosen(CursorIndex));
        }
        if (CursorIndex >= 0 && Takes(c, _panel.KeysFor?.Invoke(CursorIndex) ?? _panel.Keys))
            return new PanelStep(false, new SelectOutcome.Control(c, CursorIndex));
        return PanelStep.None;
    }

    //in a detail the arms move its window and never the cursor, and a control key names the open row
    private PanelStep DetailKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.UpArrow: return MoveDetail(-1);
            case ConsoleKey.DownArrow: return MoveDetail(+1);
            case ConsoleKey.PageUp: return MoveDetail(-Page());
            case ConsoleKey.PageDown: return MoveDetail(Page());
            case ConsoleKey.Home: return ParkDetail(0);
            case ConsoleKey.End:
                if (_detailOffset == End) return PanelStep.None;
                _detailOffset = End;
                return PanelStep.Changed;
            case ConsoleKey.Escape:
                _detail = null;
                _detailIndex = -1;
                _detailRows = [];
                return PanelStep.Changed;
        }
        var c = k.KeyChar;
        if ((k.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0) return PanelStep.None;
        if (Takes(c, _panel.DetailKeysFor?.Invoke(_detailIndex) ?? DetailBack))
            return new PanelStep(false, new SelectOutcome.Control(c, _detailIndex));
        return PanelStep.None;
    }

    //a declared control key the row's key row names, so a key the screen does not show is not taken either
    private bool Takes(char c, IReadOnlyList<FooterKey> named) =>
        _controls is { Count: > 0 } controls && controls.Contains(c)
        && named.Any(f => f.Key.Length == 1 && f.Key[0] == c);

    //the act of Enter on a row: its detail when it has one, the answer otherwise
    private PanelStep Choose(int index)
    {
        if (_panel.DetailOf?.Invoke(index) is null) return new PanelStep(false, new SelectOutcome.Chosen(index));
        _detail = KeyOf(index);
        _detailIndex = index;
        _detailOffset = _panel.DetailEndFirst ? End : 0;   //each open starts where the screen reads from, a reopened one too
        _inBox = false;
        return PanelStep.Changed;
    }

    //a row's identity across re-entries, the caller's key or its place when it set none
    private string KeyOf(int index) => _lists[0][index].Key ?? index.ToString(CultureInfo.InvariantCulture);

    //the last offset a detail window can start at, measured on the frame of this detail when one was composed
    private int DetailMax()
    {
        var rows = Mine?.BodyRows ?? Layout?.BodyRows ?? 1;
        return PanelComposer.Window(Mine?.BodyTotal ?? _detailRows.Count, -1, End, rows).Offset;
    }

    //a move parks the window at a number, starting from the end the user saw while it was following
    private PanelStep MoveDetail(int delta)
    {
        var max = DetailMax();
        var from = _detailOffset == End ? max : Math.Min(_detailOffset, max);
        return ParkDetail(from + delta);
    }

    private PanelStep ParkDetail(int to)
    {
        var max = DetailMax();
        to = Math.Clamp(to, 0, max);
        if (_detailOffset == End ? to == max : to == _detailOffset) return PanelStep.None;   //a move to where the window already is keeps the tail followed
        _detailOffset = to;
        return PanelStep.Changed;
    }

    //the box opens only where it is drawn at rest, a list longer than the body or a filter already live, so ? never opens a box nobody was shown
    private bool Searchable => _panel.SearchWhenLong
        && (Here.Draft.Length > 0 || (Layout is { } l && _lists[_tab].Count > l.BodyRows));

    private int Page()
    {
        var rows = Mine?.BodyRows ?? Layout?.BodyRows ?? 1;
        return Math.Max(1, rows - 2);
    }

    //left and right switch the tab, or on a screen with no tabs step the setting, clamped at the ends
    private PanelStep Side(int step)
    {
        if (_tabs.Length > 1)
        {
            var to = Math.Clamp(_tab + step, 0, _tabs.Length - 1);
            if (to == _tab) return PanelStep.None;
            _tab = to;
            _moved = null;
            return PanelStep.Changed;
        }
        if (_panel.Under is not PanelUnder.Setting s || CursorIndex < 0) return PanelStep.None;
        if (s.Levels(CursorIndex) is not { Count: > 0 } levels || s.Value(CursorIndex) is null) return PanelStep.None;
        var at = _moved ?? Math.Max(0, IndexOf(levels, s.Value(CursorIndex)!));
        var next = Math.Clamp(at + step, 0, levels.Count - 1);
        if (_moved == next) return PanelStep.None;
        _moved = next;
        return PanelStep.Changed;
    }

    private string? ValueOf(PanelUnder.Setting s, int index)
    {
        if (s.Levels(index) is not { Count: > 0 } levels || s.Value(index) is not { } value) return null;
        return _moved is int m && m < levels.Count ? levels[m] : value;
    }

    private static int IndexOf(IReadOnlyList<string> levels, string value)
    {
        for (var i = 0; i < levels.Count; i++)
            if (string.Equals(levels[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }

    //the cursor moves within the shown rows, and a level moved on the row it leaves is not kept
    private PanelStep MoveTo(int at)
    {
        if (Here.Shown.Length == 0 || at < 0) return PanelStep.None;
        if (at == Here.Cursor) return PanelStep.None;
        Here.Cursor = at;
        _moved = null;
        return PanelStep.Changed;
    }

    //a case-insensitive substring of the label, a column, the description or a trail word, applied at each keystroke
    private void Filter(string draft)
    {
        var keep = CursorIndex;
        Here.Draft = draft;
        var list = _lists[_tab];
        Here.Shown = [.. Enumerable.Range(0, list.Count).Where(i => Matches(list[i], draft))];
        var at = Array.IndexOf(Here.Shown, keep);
        if (at != Here.Cursor || at < 0) _moved = null;
        Here.Cursor = Here.Shown.Length == 0 ? -1 : Math.Max(0, at);
        Here.Offset = 0;
    }

    private static bool Matches(SelectOption o, string draft)
    {
        if (draft.Length == 0) return true;
        bool Has(string? s) => s is not null && s.Contains(draft, StringComparison.OrdinalIgnoreCase);
        return Has(o.Label) || Has(o.Description) || (o.Columns?.Any(Has) ?? false) || (o.Trail?.Any(t => Has(t.Text)) ?? false);
    }

    private PanelStep Apply(PanelGesture g)
    {
        var step = ApplyGesture(g);
        lock (_lock) _banded = _hoverKey is not null;
        return step;
    }

    private PanelStep ApplyGesture(PanelGesture g)
    {
        switch (g)
        {
            case PanelGesture.Leave:
                if (_hoverKey is null) return PanelStep.None;
                _hoverKey = null;
                return PanelStep.Changed;
            case PanelGesture.Hover(var hit):
            {
                //a move over a row moves the cursor there, a move over a key word bands it
                var key = hit is PanelHit.Key { Word: var word } ? word : null;
                var changed = key != _hoverKey;
                _hoverKey = key;
                if (hit is PanelHit.Option { Index: var i } && !_inBox && _detail is null && Array.IndexOf(Here.Shown, i) is var at && at >= 0)
                    changed |= MoveTo(at).Repaint;
                return changed ? PanelStep.Changed : PanelStep.None;
            }
            case PanelGesture.Press(PanelHit.Option { Index: var i }):
            {
                var at = Array.IndexOf(Here.Shown, i);
                if (at < 0 || _detail is not null) return PanelStep.None;
                _inBox = false;
                //a press on the cursor row is Enter, on another row it moves the cursor
                return at == Here.Cursor ? Choose(i) : MoveTo(at);
            }
            case PanelGesture.Press(PanelHit.Key { Word: var word }):
                return FooterKeys.KeyOf(word) is { } k ? Key(k) : PanelStep.None;
            case PanelGesture.Press(PanelHit.Tab { Index: var t }):
                if (t == _tab || t < 0 || t >= _tabs.Length) return PanelStep.None;
                _tab = t;
                _moved = null;
                return PanelStep.Changed;
            case PanelGesture.Press(PanelHit.Box):
                if (_inBox || !Searchable) return PanelStep.None;
                _inBox = true;
                return PanelStep.Changed;
            case PanelGesture.Wheel(var rows):
                return _detail is not null ? MoveDetail(rows) : Scroll(rows);
        }
        return PanelStep.None;
    }

    //the wheel moves the window, and the cursor is carried along so it stays inside
    private PanelStep Scroll(int rows)
    {
        if (Mine is not { } l || Here.Shown.Length == 0) return PanelStep.None;
        var (offset, _, _, count) = PanelComposer.Window(Here.Shown.Length, -1, l.Offset + rows, l.BodyRows);
        if (offset == l.Offset) return PanelStep.None;
        Here.Offset = offset;
        var cursor = Math.Clamp(Here.Cursor, offset, offset + count - 1);
        if (cursor != Here.Cursor) { Here.Cursor = cursor; _moved = null; }
        return PanelStep.Changed;
    }
}
