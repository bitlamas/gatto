using System.Text;
using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//emulate the terminal semantics the painter relies on, such as deferred wrap. rows pushed off the top go to Scrollback, so a byte recorder cannot catch drift.
public sealed class VtScreenSurface : ITermSurface
{
    private readonly List<string[]> _rows = new();
    private int _row, _col;
    private bool _pendingWrap;

    //the alt escape saves the main screen and the matching escape restores it. while in alt, a bottom-row feed never touches Scrollback, so that list stays clean.
    private List<string[]>? _savedRows;
    private int _savedRow, _savedCol;
    public bool AltScreen { get; private set; }

    //the visible rows, trimmed and copied, since the live buffer keeps changing
    public IReadOnlyList<string> Viewport => ScreenRows().ToList();

    public VtScreenSurface(int width, int height)
    {
        Width = width;
        Height = height;
        for (var i = 0; i < height; i++) _rows.Add(NewRow());
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int CursorRow => _row;
    public int CursorCol => _col;

    //false like the interface default, a test that resizes while code waits for a key sets it
    public bool ReportsResize { get; set; }

    //a resize never reflows text like conpty does. width pads or truncates each row, and a smaller height drops top rows without counting them in Scrolled
    public void Resize(int width, int height)
    {
        if (width != Width)
        {
            for (var r = 0; r < _rows.Count; r++)
            {
                var nr = new string[width];
                for (var c = 0; c < width; c++) nr[c] = c < _rows[r].Length ? _rows[r][c] : " ";
                _rows[r] = nr;
            }
            Width = width;
        }
        if (height != Height)
        {
            while (_rows.Count > height) _rows.RemoveAt(0);
            while (_rows.Count < height) _rows.Add(NewRow());
            Height = height;
        }
        _row = Math.Min(_row, Height - 1);
        _col = Math.Min(_col, Width - 1);
        _pendingWrap = false;
    }
    //the total rows pushed off the top since construction.
    public int Scrolled { get; private set; }
    public List<string> Scrollback { get; } = new();

    public string RowText(int r) => string.Concat(_rows[r]).TrimEnd();
    public IEnumerable<string> ScreenRows()
    {
        for (var r = 0; r < Height; r++) yield return RowText(r);
    }

    //the index of the last row holding visible text, or -1 when nothing is on screen.
    public int LastContentRow()
    {
        for (var r = Height - 1; r >= 0; r--)
            if (RowText(r).Length > 0) return r;
        return -1;
    }

    //once the buffer has scrolled, the last content row must sit on the bottom row. any row below it is dead space the painter left.
    public int DeadRowsBelow() => Scrolled == 0 ? 0 : Height - 1 - LastContentRow();

    private string[] NewRow()
    {
        var row = new string[Width];
        Array.Fill(row, " ");
        return row;
    }

    private void Scroll()
    {
        //the alt buffer scrolls without capturing rows into main-screen scrollback.
        if (!AltScreen) { Scrollback.Add(RowText(0)); Scrolled++; }
        _rows.RemoveAt(0);
        _rows.Add(NewRow());
    }

    private void EnterAlt()
    {
        if (AltScreen) return;
        _savedRows = _rows.Select(r => (string[])r.Clone()).ToList();
        _savedRow = _row; _savedCol = _col;
        for (var r = 0; r < _rows.Count; r++) _rows[r] = NewRow();   //the alt buffer begins cleared.
        _row = 0; _col = 0; _pendingWrap = false;
        AltScreen = true;
    }

    //a real alt-screen exit discards the buffer, so the final frame is recorded here before it is lost. the live buffer is still restored faithfully.
    public IReadOnlyList<string>? AltFrameAtExit { get; private set; }

    private void ExitAlt()
    {
        if (!AltScreen || _savedRows is null) return;
        AltFrameAtExit = ScreenRows().ToList();
        _rows.Clear();
        _rows.AddRange(_savedRows.Select(r => (string[])r.Clone()));
        _row = Math.Min(_savedRow, Height - 1);
        _col = Math.Min(_savedCol, Width - 1);
        _pendingWrap = false;
        _savedRows = null;
        AltScreen = false;
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        _col = 0;                     //conpty default: a line feed also returns the column to zero.
        if (_row == Height - 1) Scroll();
        else _row++;
    }

    public void Write(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\x1b') { i = Escape(s, i); continue; }
            if (c == '\r') { _col = 0; _pendingWrap = false; i++; continue; }
            if (c == '\n') { LineFeed(); i++; continue; }
            if (c == '\a' || c == '\b') { if (c == '\b' && _col > 0) _col--; i++; continue; }

            var rune = char.IsHighSurrogate(c) && i + 1 < s.Length
                ? new System.Text.Rune(c, s[i + 1]) : new System.Text.Rune(c);
            i += rune.Utf16SequenceLength;
            var w = UnicodeWidth.OfRune(rune);
            if (w <= 0) continue;   //combining and zero-width runes claim no cell in this model.

            if (_pendingWrap || _col + w > Width)
            {
                _pendingWrap = false;
                _col = 0;
                if (_row == Height - 1) Scroll();
                else _row++;
            }
            _rows[_row][_col] = rune.ToString();
            for (var k = 1; k < w && _col + k < Width; k++) _rows[_row][_col + k] = "";
            _col += w;
            if (_col >= Width) { _col = Width - 1; _pendingWrap = true; }
        }
    }

    private int Escape(string s, int i)
    {
        //the index passed in points at the escape character, and the return is the index after the full sequence
        if (i + 1 >= s.Length) return i + 1;
        var kind = s[i + 1];
        if (kind == ']')
        {
            //the operating-system command is swallowed up to BEL or string terminator.
            var j = i + 2;
            while (j < s.Length && s[j] != '\a' && !(s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\')) j++;
            return j < s.Length ? (s[j] == '\a' ? j + 1 : j + 2) : s.Length;
        }
        if (kind == '\\') return i + 2;   //a stray string terminator consumes two characters.
        if (kind != '[') return i + 2;    //a one-letter escape is skipped, gatto never emits one

        var p = i + 2;
        var sb = new StringBuilder();
        while (p < s.Length && (char.IsDigit(s[p]) || s[p] == ';' || s[p] == '?')) sb.Append(s[p++]);
        if (p >= s.Length) return s.Length;
        var final = s[p];
        var body = sb.ToString();
        int N(int fallback = 1) =>
            int.TryParse(body.TrimStart('?').Split(';')[0], out var n) && n > 0 ? n : fallback;

        switch (final)
        {
            case 'A': _row = Math.Max(0, _row - N()); _pendingWrap = false; break;              //cursor-up clamps at the top edge and never scrolls.
            case 'B': _row = Math.Min(Height - 1, _row + N()); _pendingWrap = false; break;     //cursor-down clamps at the bottom edge and never scrolls.
            case 'G': _col = Math.Min(Width - 1, Math.Max(0, N() - 1)); _pendingWrap = false; break;
            case 'H':                                                                            //cursor-position addresses an absolute row and column, counted from one.
            {
                var parts = body.Split(';');
                var rr = parts.Length > 0 && int.TryParse(parts[0], out var pr) && pr > 0 ? pr : 1;
                var cc = parts.Length > 1 && int.TryParse(parts[1], out var pc) && pc > 0 ? pc : 1;
                _row = Math.Min(Height - 1, rr - 1);
                _col = Math.Min(Width - 1, cc - 1);
                _pendingWrap = false;
                break;
            }
            case 'h': if (body == "?1049") EnterAlt(); break;   //only the alt-screen mode acts here. every other private mode is ignored.
            case 'l': if (body == "?1049") ExitAlt(); break;    //the matching escape leaves the alt screen.
            case 'J':                                                                            //only the erase-from-cursor variant is modeled, gatto never emits the others
                for (var x = _col; x < Width; x++) _rows[_row][x] = " ";
                for (var r = _row + 1; r < Height; r++) _rows[r] = NewRow();
                break;
            case 'K':
                if (body == "2") _rows[_row] = NewRow();
                else for (var x = _col; x < Width; x++) _rows[_row][x] = " ";
                break;
            //colour escapes and other modes move no cell and leave the cursor where it is.
        }
        return p + 1;
    }
}
