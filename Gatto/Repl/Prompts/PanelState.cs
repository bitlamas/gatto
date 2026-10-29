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

//what a panel adds to the select loop: the filter and the ring, the window, each tab's own state, the moved setting and the gestures. the loop thread owns it, the layout and the queue are shared under the lock
internal sealed class PanelState
{
    //the key that wakes the loop for a queued gesture. no input path produces it and no printable arm can take it
    public static readonly ConsoleKeyInfo Wake = new('\0', ConsoleKey.NoName, false, false, false);

    private readonly PanelFields _panel;
    private readonly IReadOnlyList<SelectOption>[] _lists;
    private readonly TabState[] _tabs;
    private readonly bool _numbered;
    private readonly IReadOnlyList<char>? _controls;
    private readonly object _lock = new();
    private readonly Queue<PanelGesture> _gestures = new();
    private PanelLayout? _layout;
    private int _layoutTab;
    private int _tab;
    private bool _inBox;
    private int? _moved;   //the setting's level index moved on the cursor row, reset when the cursor leaves it
    private string? _hoverKey;

    //each tab keeps its own cursor, filter and window, so a switch and back finds the tab as it was left
    private sealed class TabState
    {
        public string Draft = "";
        public int Cursor;   //a position in Shown
        public int Offset;
        public int[] Shown = [];
    }

    public PanelState(PanelFields panel, IReadOnlyList<SelectOption> options, bool numbered, int initialCursor,
        IReadOnlyList<char>? controls)
    {
        _panel = panel;
        _numbered = numbered;
        _controls = controls;
        _lists = panel.Tabs is { Count: > 0 } tabs ? [.. tabs.Select(t => t.Options)] : [options];
        _tabs = [.. _lists.Select(l => new TabState { Shown = [.. Enumerable.Range(0, l.Count)] })];
        _tabs[0].Cursor = _lists[0].Count == 0 ? -1 : Math.Clamp(initialCursor, 0, _lists[0].Count - 1);
        for (var t = 1; t < _tabs.Length; t++) _tabs[t].Cursor = _lists[t].Count == 0 ? -1 : 0;
    }

    public IReadOnlyList<SelectOption> OptionsOf(int tab) => _lists[tab];

    public bool Empty => _lists.All(l => l.Count == 0);

    private TabState Here => _tabs[_tab];

    //the option's original index under the cursor, -1 when the filter shows nothing
    public int CursorIndex => Here.Cursor >= 0 && Here.Cursor < Here.Shown.Length ? Here.Shown[Here.Cursor] : -1;

    public string? SettingValue => _panel.Under is PanelUnder.Setting s && CursorIndex >= 0 ? ValueOf(s, CursorIndex) : null;

    //true only when the shown value differs from the row's own, moving away and back is no move
    public bool SettingMoved => _panel.Under is PanelUnder.Setting s && CursorIndex >= 0
        && _moved is not null && SettingValue != s.Value(CursorIndex);

    public PanelView View() => new(_tab, [.. Here.Shown], Here.Shown.Length == 0 ? -1 : Here.Cursor, Here.Draft, _inBox,
        Here.Offset, SettingValue,
        _panel.Under is PanelUnder.Sentence sen && CursorIndex >= 0 ? sen.Text(CursorIndex) : null, _hoverKey);

    //the factory's half, under the painter's gate: the frame it just composed, for the tab it composed
    public void Record(PanelLayout layout, int tab) { lock (_lock) { _layout = layout; _layoutTab = tab; } }

    public PanelLayout? Layout { get { lock (_lock) return _layout; } }

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
                var hit = l.At(row, e.X);
                gesture = e.Kind switch
                {
                    MouseKind.Move => new PanelGesture.Hover(hit),
                    MouseKind.Press when e.Button == MouseButton.Left && hit is not null => new PanelGesture.Press(hit),
                    MouseKind.Wheel => new PanelGesture.Wheel(e.WheelDelta > 0 ? -1 : 1),
                    _ => null,
                };
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
        //the window the user saw is where the next move starts, so it only slides when the cursor reaches its edge
        lock (_lock) if (_layout is { } l && _layoutTab == _tab) Here.Offset = l.Offset;
        return _inBox ? BoxKey(k) : ListKey(k);
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
                return CursorIndex < 0 ? PanelStep.None : new PanelStep(false, new SelectOutcome.Chosen(CursorIndex));
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
        if (_controls is { Count: > 0 } controls && controls.Contains(c) && CursorIndex >= 0)
            return new PanelStep(false, new SelectOutcome.Control(c, CursorIndex));
        return PanelStep.None;
    }

    //the box opens only where it is drawn at rest, a list longer than the body or a filter already live, so ? never opens a box nobody was shown
    private bool Searchable => _panel.SearchWhenLong
        && (Here.Draft.Length > 0 || (Layout is { } l && _lists[_tab].Count > l.BodyRows));

    private int Page()
    {
        var rows = Layout?.BodyRows ?? 1;
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

    //a case-insensitive substring of the label, a column or the description, applied at each keystroke
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
        return Has(o.Label) || Has(o.Description) || (o.Columns?.Any(Has) ?? false);
    }

    private PanelStep Apply(PanelGesture g)
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
                if (hit is PanelHit.Option { Index: var i } && !_inBox && Array.IndexOf(Here.Shown, i) is var at && at >= 0)
                    changed |= MoveTo(at).Repaint;
                return changed ? PanelStep.Changed : PanelStep.None;
            }
            case PanelGesture.Press(PanelHit.Option { Index: var i }):
            {
                var at = Array.IndexOf(Here.Shown, i);
                if (at < 0) return PanelStep.None;
                _inBox = false;
                //a press on the cursor row is Enter, on another row it moves the cursor
                return at == Here.Cursor ? new PanelStep(false, new SelectOutcome.Chosen(i)) : MoveTo(at);
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
                return Scroll(rows);
        }
        return PanelStep.None;
    }

    //the wheel moves the window, and the cursor is carried along so it stays inside
    private PanelStep Scroll(int rows)
    {
        if (Layout is not { } l || Here.Shown.Length == 0) return PanelStep.None;
        var (offset, _, _, count) = PanelComposer.Window(Here.Shown.Length, -1, l.Offset + rows, l.BodyRows);
        if (offset == l.Offset) return PanelStep.None;
        Here.Offset = offset;
        var cursor = Math.Clamp(Here.Cursor, offset, offset + count - 1);
        if (cursor != Here.Cursor) { Here.Cursor = cursor; _moved = null; }
        return PanelStep.Changed;
    }
}
