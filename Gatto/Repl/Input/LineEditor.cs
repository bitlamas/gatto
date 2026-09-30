using Gatto.Terminal;

namespace Gatto.Repl.Input;

//the editor state a redraw reads, with SelStart/SelEnd as the normalized (anchor, caret) range, both null when no selection is live
public sealed record EditorView(IReadOnlyList<string> Lines, int CursorLine, int CursorCol,
    (int Line, int Col)? SelStart = null, (int Line, int Col)? SelEnd = null);

//multiline editing and history over a redraw callback, with width making up/down move by display row of a wrapped line
public sealed class LineEditor(IComposerSource input, History history, Func<int>? width = null)
{
    private const int MaxConsecutiveReadFailures = 100;

    private List<string> _lines = new() { "" };
    private int _line, _col;

    //the token typed before the first Tab, since the buffer holds a whole command name after that, cleared by HandleKey on any other key
    private string? _completionToken;
    private int _completionCycle;

    //the Alt+V clipboard image paste, null when it isn't wired so the key falls through to the default branch
    public Action? OnPasteImage { get; set; }

    //the start of an [Image #N] placeholder ending exactly at the cursor, so a placeholder elsewhere counts as ordinary text
    private static int? PlaceholderEndingAt(string line, int col)
    {
        if (col <= 0 || col > line.Length || line[col - 1] != ']') return null;
        var open = line.LastIndexOf('[', col - 1);
        if (open < 0) return null;
        var span = line[open..col];
        return PlaceholderShape.IsMatch(span) ? open : null;
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderShape =
        new(@"^\[Image #\d+\]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    //type text at the cursor as if the user had, single lines only since a newline would corrupt the line and column bookkeeping
    public void InsertAtCursor(string text)
    {
        if (text.Length == 0 || text.Contains('\n')) return;
        _lines[_line] = _lines[_line].Insert(_col, text);
        _col += text.Length;
    }
    //the Esc·Esc clear and Ctrl+C·Ctrl+C quit chords live in the session, so an armed chord survives a submit

    //the queue pull: every queued message in order and the queue emptied in the same call, so a pull can't half-happen
    private Func<IReadOnlyList<string>>? _pullQueue;

    private ArmedChord? _chords;
    private Action? _onQuit;
    private Action? _onHintChanged;
    //the fixed end of the composer selection range, with the caret as the moving end and null meaning nothing is selected
    private (int Line, int Col)? _selAnchor;
    private Action<string>? _onCopyText;
    private Action? _onComposerSelectionSet;

    //true while a composer selection is live, and the cancel sink must not read it (Esc already reaches the editor's own deselect)
    public bool HasComposerSelection => _selAnchor is not null;

    //a paste's trailing newline is an Enter with a bursty predecessor and nothing behind it, so it's stripped and a paste never auto-submits
    private bool _thisBursty, _prevBursty;

    //blocks until submit and repaints on every buffer or cursor change, with an up-arrow pull from the queue replacing the composer
    public string Read(Action<EditorView> redraw, string? initial = null,
        Func<IReadOnlyList<string>>? pullQueue = null, ArmedChord? chords = null, Action? onQuit = null,
        Action? onHintChanged = null,
        Action<string>? onCopyText = null, Action? onComposerSelectionSet = null)
    {
        _pullQueue = pullQueue;
        _chords = chords;
        _onQuit = onQuit;
        _onHintChanged = onHintChanged;
        _onCopyText = onCopyText;
        _onComposerSelectionSet = onComposerSelectionSet;
        _selAnchor = null;
        //a read starts with no completion cycle, since this block re-seeds the buffer and a token left from the old text would be stale
        _completionToken = null;
        _lines = string.IsNullOrEmpty(initial)
            ? new List<string> { "" }
            : new List<string>(initial.Split('\n'));
        if (_lines.Count == 0) _lines.Add("");
        _line = _lines.Count - 1;
        _col = _lines[_line].Length;
        Paint(redraw);
        var readFailures = 0;
        while (true)
        {
            ComposerInput ev;
            try { ev = input.Read(); readFailures = 0; }
            catch (Exception)
            {
                //skip a failed read and keep editing, and a source that fails every time escapes to the app backstop instead of spinning
                if (++readFailures >= MaxConsecutiveReadFailures) throw;
                continue;
            }

            if (ev is ComposerInput.Key(var k))
            {
                //sample the burst state for this key before handling it, and a source whose KeyAvailable throws counts as not bursty
                _prevBursty = _thisBursty;
                try { _thisBursty = input.KeyAvailable; } catch (Exception) { _thisBursty = false; }
                try
                {
                    if (Handle(k) is { } submitted) return submitted;
                }
                catch (Exception) { } //exotic key state, ignore the key
            }
            else if (ev is ComposerInput.Select sel)
            {
                //the gesture arrives already resolved against a layout snapshot, so this thread only sets the selection and does no layout work
                if (sel.Kind == ComposerGesture.Begin) BeginComposerSelect(sel.Line, sel.Col);
                else if (sel.Kind == ComposerGesture.Extend) ExtendComposerSelect(sel.Line, sel.Col);
                else ClearComposerSelection();
            }
            Paint(redraw);
        }
    }

    //every redraw goes through here so a paint fault reaches the app backstop, while a dead key source stays the pump-death signal
    private void Paint(Action<EditorView> redraw)
    {
        try { redraw(View()); }
        catch (ComposerPaintException) { throw; }               //already tagged, so it's rethrown as is
        catch (Exception ex) { throw new ComposerPaintException(ex); }
    }

    private string? Handle(ConsoleKeyInfo k)
    {
        //a live selection is consumed by the next edit, and a plain Left/Right collapses to the near edge without falling through to Move
        if (HasComposerSelection && !IsShiftArrow(k))
        {
            if (k.Key == ConsoleKey.Backspace || k.Key == ConsoleKey.Delete)
            { var (s, e) = SelRange()!.Value; DeleteRange(s, e); return null; }
            if (IsCtrlC(k)) { _onCopyText?.Invoke(TextOfRange()); return null; }   //copy leaves the selection in place
            //plain Left/Right collapse to the near edge and stop, with no fall-through to Move
            if (k.Key == ConsoleKey.LeftArrow && !k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { var (s, _) = SelRange()!.Value; _line = s.Line; _col = s.Col; _selAnchor = null; return null; }
            if (k.Key == ConsoleKey.RightArrow && !k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { var (_, e) = SelRange()!.Value; _line = e.Line; _col = e.Col; _selAnchor = null; return null; }
            if (k.KeyChar >= ' ' && !k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { var (s, e) = SelRange()!.Value; DeleteRange(s, e); }    //delete the range, then fall through to insert the key
            else if (k.Key is not ConsoleKey.A)   //the Esc and move keys deselect here and fall through to their own handling
                _selAnchor = null;
        }

        //any key but Esc or Ctrl+C disarms a pending chord, with a repaint only when one was armed
        if (_chords is not null && !IsChordKey(k) && _chords.Armed != Chord.None)
        {
            _chords.Disarm();
            _onHintChanged?.Invoke();
        }

        //the cycle survives consecutive Tab presses and nothing else, since a stale token would complete a command the user never started
        if (_completionToken is not null && k.Key != ConsoleKey.Tab) _completionToken = null;

        switch (k.Key)
        {
            case ConsoleKey.Enter when k.Modifiers.HasFlag(ConsoleModifiers.Shift):
                SplitLine();
                return null;
            case ConsoleKey.Enter when _col > 0 && _lines[_line][_col - 1] == '\\':
                _lines[_line] = _lines[_line].Remove(_col - 1, 1);
                _col--;
                SplitLine();
                return null;
            case ConsoleKey.Enter when _thisBursty:   //a queued key behind this Enter means a pasted newline, so split the line
                SplitLine();
                return null;
            //strip the newline at the end of a pasted burst so a paste never auto-submits, the user presses Enter to send
            case ConsoleKey.Enter when _prevBursty:
                return null;
            case ConsoleKey.Enter:
                return Submit();

            //these Ctrl+arrow cases must come before the plain arrows, since a plain case with no when guard would swallow them
            case ConsoleKey.LeftArrow when k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                           && !k.Modifiers.HasFlag(ConsoleModifiers.Alt):
                if (_col > 0)
                {
                    var s = _lines[_line]; var i = _col;
                    while (i > 0 && s[i - 1] == ' ') i--;      //skip spaces first, matching DeleteWordBack's boundary
                    while (i > 0 && s[i - 1] != ' ') i--;      //then back to the start of the word
                    _col = i;
                }
                else if (_line > 0) { _line--; _col = _lines[_line].Length; }
                return null;
            case ConsoleKey.RightArrow when k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                            && !k.Modifiers.HasFlag(ConsoleModifiers.Alt):
                if (_col < _lines[_line].Length)
                {
                    var s = _lines[_line]; var i = _col; var n = s.Length;
                    while (i < n && s[i] == ' ') i++;
                    while (i < n && s[i] != ' ') i++;          //then to the end of the word
                    _col = i;
                }
                else if (_line < _lines.Count - 1) { _line++; _col = 0; }
                return null;

            //arm the anchor at the caret on the first press, then move through MoveLeft/MoveRight so the two paths can't drift apart
            case ConsoleKey.LeftArrow when k.Modifiers.HasFlag(ConsoleModifiers.Shift)
                                           && !k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                           && !k.Modifiers.HasFlag(ConsoleModifiers.Alt):
                ArmSelectionAnchor();
                MoveLeft();
                return null;
            case ConsoleKey.RightArrow when k.Modifiers.HasFlag(ConsoleModifiers.Shift)
                                            && !k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                            && !k.Modifiers.HasFlag(ConsoleModifiers.Alt):
                ArmSelectionAnchor();
                MoveRight();
                return null;

            case ConsoleKey.LeftArrow:
                MoveLeft();
                return null;
            case ConsoleKey.RightArrow:
                MoveRight();
                return null;
            case ConsoleKey.Home:
                _col = 0;
                return null;
            case ConsoleKey.End:
                _col = _lines[_line].Length;
                return null;

            //up/down move by display row of a wrapped line, so history recall only fires at the top or bottom row
            case ConsoleKey.UpArrow:
                MoveDisplayRow(up: true);
                return null;
            case ConsoleKey.DownArrow:
                MoveDisplayRow(up: false);
                return null;

            case ConsoleKey.Backspace:
                //backspace over an image placeholder removes it whole, a half-deleted "[Image #7" would still look like an attachment
                if (PlaceholderEndingAt(_lines[_line], _col) is int start)
                {
                    _lines[_line] = _lines[_line].Remove(start, _col - start);
                    _col = start;
                }
                else if (_col > 0)
                {
                    _lines[_line] = _lines[_line].Remove(_col - 1, 1);
                    _col--;
                }
                else if (_line > 0)
                {
                    int junction = _lines[_line - 1].Length;
                    _lines[_line - 1] += _lines[_line];
                    _lines.RemoveAt(_line);
                    _line--;
                    _col = junction;
                }
                return null;
            case ConsoleKey.Delete:
                if (_col < _lines[_line].Length)
                    _lines[_line] = _lines[_line].Remove(_col, 1);
                else if (_line < _lines.Count - 1)
                {
                    _lines[_line] += _lines[_line + 1];
                    _lines.RemoveAt(_line + 1);
                }
                return null;

            //an AltGr-composed character arrives as Ctrl+Alt, so it must insert rather than fire the chord
            case ConsoleKey.U when k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                   && !k.Modifiers.HasFlag(ConsoleModifiers.Alt):
                _lines = new List<string> { "" };
                _line = 0;
                _col = 0;
                return null;
            case ConsoleKey.W when k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                   && !k.Modifiers.HasFlag(ConsoleModifiers.Alt):
                DeleteWordBack();
                return null;

            //select the whole buffer, anchor at the start and caret at the end, only when the composer holds something
            case ConsoleKey.A when k.Modifiers.HasFlag(ConsoleModifiers.Control)
                                   && !k.Modifiers.HasFlag(ConsoleModifiers.Alt) && !IsEmpty:
                _selAnchor = (0, 0);
                _line = _lines.Count - 1; _col = _lines[_line].Length;
                _onComposerSelectionSet?.Invoke();
                return null;

            //a non-empty composer arms on the first Esc press and clears on the second within the window
            case ConsoleKey.Escape when _chords is not null && !IsEmpty:
                if (_chords.Fire(Chord.ClearComposer, Environment.TickCount64))
                {
                    _lines = new List<string> { "" };
                    _line = 0;
                    _col = 0;
                }
                _onHintChanged?.Invoke();
                return null;

            //the first Ctrl+C on an empty composer arms and hints, the second quits, and it does nothing while the composer holds text
            case ConsoleKey.C when IsCtrlC(k) && _chords is not null && IsEmpty:
                if (_chords.Fire(Chord.Quit, Environment.TickCount64)) _onQuit?.Invoke();
                _onHintChanged?.Invoke();
                return null;

            //this case must precede default, since Alt+V arrives with a printable KeyChar and default would type a v
            case ConsoleKey.V when IsAltOnly(k) && OnPasteImage is not null:
                OnPasteImage.Invoke();
                return null;

            //bare Tab only, since Shift+Tab is wild mode and never reaches this switch
            case ConsoleKey.Tab when k.Modifiers == 0:
                if (_completionToken is null)
                {
                    var fresh = CommandHint.For(_lines, _line, _col);
                    if (!fresh.Any) return null;      //no candidates, so Tab stays the no-op it has always been
                    _completionToken = fresh.Token;
                    _completionCycle = 0;
                }
                else _completionCycle++;

                var cycle = CommandHint.For(new[] { _completionToken }, 0, _completionToken.Length);
                if (!cycle.Any) { _completionToken = null; return null; }
                _lines[_line] = cycle.Completion(_completionCycle);
                _col = _lines[_line].Length;
                return null;

            default:
                if (k.KeyChar >= ' ')
                {
                    _lines[_line] = _lines[_line].Insert(_col, k.KeyChar.ToString());
                    _col++;
                }
                return null;
        }
    }

    private void SplitLine()
    {
        string cur = _lines[_line];
        string head = cur.Substring(0, _col);
        string tail = cur.Substring(_col);
        _lines[_line] = head;
        _lines.Insert(_line + 1, tail);
        _line++;
        _col = 0;
    }

    private string Submit()
    {
        var joined = string.Join("\n", _lines);
        if (joined.Trim().Length > 0) history.Record(joined);
        return joined;
    }

    private void MoveLeft()
    {
        if (_col > 0) _col--;
        else if (_line > 0) { _line--; _col = _lines[_line].Length; }
    }

    private void MoveRight()
    {
        if (_col < _lines[_line].Length) _col++;
        else if (_line < _lines.Count - 1) { _line++; _col = 0; }
    }

    //arm the anchor at the current caret when nothing is live and the composer has text, so an empty range never fires the cross-clear
    private void ArmSelectionAnchor()
    {
        if (_selAnchor is null && !IsEmpty)
        {
            _selAnchor = (_line, _col);
            _onComposerSelectionSet?.Invoke();
        }
    }

    //the live selection in document order, since the anchor may sit before or after the caret
    private ((int Line, int Col) Start, (int Line, int Col) End)? SelRange()
    {
        if (_selAnchor is not { } a) return null;
        var caret = (_line, _col);
        return Before(a, caret) ? (a, caret) : (caret, a);
    }

    private static bool Before((int Line, int Col) x, (int Line, int Col) y) =>
        x.Line < y.Line || (x.Line == y.Line && x.Col <= y.Col);

    //splice the buffer between s and e, which must be in document order, and leave the caret at s
    private void DeleteRange((int Line, int Col) s, (int Line, int Col) e)
    {
        var head = _lines[s.Line][..s.Col];
        var tail = _lines[e.Line][e.Col..];
        var joined = head + tail;
        _lines.RemoveRange(s.Line, e.Line - s.Line + 1);
        _lines.Insert(s.Line, joined);
        _line = s.Line; _col = s.Col;
        _selAnchor = null;
    }

    //the selected text for a copy, an empty string when nothing is selected
    private string TextOfRange()
    {
        if (SelRange() is not { } r) return "";
        if (r.Start.Line == r.End.Line) return _lines[r.Start.Line][r.Start.Col..r.End.Col];
        var sb = new System.Text.StringBuilder();
        sb.Append(_lines[r.Start.Line][r.Start.Col..]);
        for (var li = r.Start.Line + 1; li < r.End.Line; li++) { sb.Append('\n'); sb.Append(_lines[li]); }
        sb.Append('\n'); sb.Append(_lines[r.End.Line][..r.End.Col]);
        return sb.ToString();
    }

    //the composer-selection extend gesture, which the range-consume block above must never treat as a consume key
    private static bool IsShiftArrow(ConsoleKeyInfo k) =>
        k.Modifiers.HasFlag(ConsoleModifiers.Shift) && !k.Modifiers.HasFlag(ConsoleModifiers.Control)
        && !k.Modifiers.HasFlag(ConsoleModifiers.Alt)
        && k.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow;

    private int ClampLine(int line) => Math.Clamp(line, 0, _lines.Count - 1);

    private int ClampCol(int line, int col) => Math.Clamp(col, 0, _lines[ClampLine(line)].Length);

    //set the anchor and put the caret there, clamped against the buffer so a stale mouse gesture can't index out of range
    public void BeginComposerSelect(int line, int col)
    {
        var clampedLine = ClampLine(line);
        _selAnchor = (clampedLine, ClampCol(clampedLine, col));
        _line = _selAnchor.Value.Line; _col = _selAnchor.Value.Col;
        _onComposerSelectionSet?.Invoke();
    }

    //move the caret to a clamped position, arming the anchor at the old caret if none is live
    public void ExtendComposerSelect(int line, int col)
    {
        if (_selAnchor is null) _selAnchor = (_line, _col);
        _line = ClampLine(line); _col = ClampCol(_line, col);
    }

    //drop the live selection, called from the gesture drain so the pump thread never writes the anchor itself
    public void ClearComposerSelection() => _selAnchor = null;

    //test-only seams: internal members the tests drive the selection with (internal only, visible to the test project)

    internal void SeedForTest(string joined)
    {
        _lines = string.IsNullOrEmpty(joined) ? new List<string> { "" } : new List<string>(joined.Split('\n'));
        if (_lines.Count == 0) _lines.Add("");
        _line = _lines.Count - 1;
        _col = _lines[_line].Length;
    }

    internal void SetComposerSelectionForTest((int Line, int Col) anchor, (int Line, int Col) caret)
    {
        _selAnchor = anchor;
        _line = caret.Line; _col = caret.Col;
    }

    internal bool HasComposerSelectionForTest => HasComposerSelection;

    internal IReadOnlyList<string> LinesForTest => _lines;

    internal string? HandleForTest(ConsoleKeyInfo k, Action<string>? onCopyText = null)
    {
        if (onCopyText is not null) _onCopyText = onCopyText;
        return Handle(k);
    }

    //move one display row, holding the cell column, and recall history at the top or bottom row
    private void MoveDisplayRow(bool up)
    {
        var w = width?.Invoke() ?? 0;
        var budget = w > 3 ? w - 3 : 0;   //the same budget InputFrame.Compose gives the composer, width minus 3
        if (budget <= 0)
        {
            if (up) { if (_line > 0) { _line--; ClampCol(); } else if (!TryPullQueue()) PullHistory(older: true); }
            else { if (_line < _lines.Count - 1) { _line++; ClampCol(); } else PullHistory(older: false); }
            return;
        }

        var segs = SoftWrap.Wrap(_lines[_line], budget, budget);
        var (segRow, cell) = SoftWrap.MapCursor(segs, _lines[_line], _col);
        if (up)
        {
            if (segRow > 0) _col = ColAtSegCell(segs, segRow - 1, cell);
            else if (_line > 0)
            {
                _line--;
                var above = SoftWrap.Wrap(_lines[_line], budget, budget);
                _col = ColAtSegCell(above, above.Count - 1, cell);   //the caret goes to the last display row of the line above
            }
            else if (!TryPullQueue()) PullHistory(older: true);       //at the top display row, pull the queue first and recall history only if nothing was queued
        }
        else
        {
            if (segRow < segs.Count - 1) _col = ColAtSegCell(segs, segRow + 1, cell);
            else if (_line < _lines.Count - 1)
            {
                _line++;
                var below = SoftWrap.Wrap(_lines[_line], budget, budget);
                _col = ColAtSegCell(below, 0, cell);                 //the caret goes to the first display row of the line below
            }
            else PullHistory(older: false);                          //the bottom display row recalls newer history, or the draft
        }
    }

    //the logical column nearest the desired cell without overshooting, shared with ComposerLayout's mouse mapping
    internal static int ColAtSegCell(IReadOnlyList<WrapSeg> segs, int segIndex, int targetCell)
    {
        var start = 0;
        for (var i = 0; i < segIndex && i < segs.Count; i++) start += segs[i].SourceChars;
        var cells = 0; var chars = 0;
        foreach (var rune in segs[segIndex].Text.EnumerateRunes())
        {
            var rw = UnicodeWidth.OfRune(rune);
            if (cells + rw > targetCell) break;
            cells += rw; chars += rune.Utf16SequenceLength;
        }
        var col = start + chars;
        //cap the column below a row boundary, since MapCursor attributes the boundary to the lower row and an up-arrow would bounce back down
        if (segIndex < segs.Count - 1) col = System.Math.Min(col, start + segs[segIndex].SourceChars - 1);
        return col;
    }

    private void PullHistory(bool older)
    {
        var text = older ? history.Older(string.Join("\n", _lines)) : history.Newer();
        if (text is null) return;
        _lines = new List<string>(text.Split('\n'));
        if (_lines.Count == 0) _lines.Add("");
        _line = _lines.Count - 1;
        _col = _lines[_line].Length;
    }

    private void DeleteWordBack()
    {
        string cur = _lines[_line];
        int i = _col;
        while (i > 0 && cur[i - 1] == ' ') i--;       //back over the spaces before the word
        while (i > 0 && cur[i - 1] != ' ') i--;       //then back over the word itself
        _lines[_line] = cur.Remove(i, _col - i);
        _col = i;
    }

    //the composer holds nothing, one empty logical line, defined once for every chord rung that asks
    private bool IsEmpty => _lines.Count == 1 && _lines[0].Length == 0;

    //the pump's Ctrl+C, with AltGr (Ctrl+Alt) excluded so a composed character still inserts

    //a real Alt chord holds Alt without Control, which is how it differs from an AltGr character (AltGr is Ctrl+Alt)
    private static bool IsAltOnly(ConsoleKeyInfo k) =>
        k.Modifiers.HasFlag(ConsoleModifiers.Alt) && !k.Modifiers.HasFlag(ConsoleModifiers.Control);

    private static bool IsCtrlC(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)
        && !k.Modifiers.HasFlag(ConsoleModifiers.Alt);

    //the keys that take part in a two-press chord, so they must not disarm a pending arm
    private static bool IsChordKey(ConsoleKeyInfo k) => k.Key == ConsoleKey.Escape || IsCtrlC(k);

    //pull the whole queue into the buffer ahead of the typed text, and return false when nothing was queued so history recall runs
    private bool TryPullQueue()
    {
        if (_pullQueue?.Invoke() is not { Count: > 0 } queued) return false;

        var typed = string.Join("\n", _lines);
        var pulled = string.Join("\n\n", queued);
        var whole = typed.Length == 0 ? pulled : pulled + "\n\n" + typed;

        _lines = [.. whole.Split('\n')];
        _line = _lines.Count - 1;
        _col = _lines[^1].Length;
        _selAnchor = null;
        return true;
    }

    private void ClampCol() => _col = Math.Min(_col, _lines[_line].Length);

    private EditorView View()
    {
        var r = SelRange();
        return new(_lines.ToList(), _line, _col, r?.Start, r?.End);
    }

    //the composer's contents for a caller that has to repaint, since a refusal arrives while the editor sits in Read with no key coming
    public EditorView Snapshot() => View();
}
