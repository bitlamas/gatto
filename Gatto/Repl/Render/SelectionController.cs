using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the live text selection over the transcript and the chrome block, clearing itself on a reflow or a touched item's Rev change
public sealed class SelectionController
{
    private readonly TranscriptModel _model;
    private SelectionSpan? _current;
    private DragKind _kind;
    private readonly Dictionary<int, int> _snapshot = new();   //item index to Rev at span time, one entry per touched item
    private ChromeSpan? _chromeCurrent;
    private DragKind _chromeKind;
    private readonly Dictionary<int, string> _chromeSnapshot = new();   //row to the Visible text it had at span time
    private int _chromeTotalRows;   //the block's row count when the span was taken
                                     //a pure shift can leave every snapshotted row's text unchanged, so the count catches what the text compare can't

    public SelectionController(TranscriptModel model)
    {
        _model = model;
        model.OnReset += () => Clear();          //a reset is /new or /compact, fresh items can reuse indices so clear the whole span
        model.OnItemRemoved += _ => Clear();     //a removal shifts the later indices, so drop the anchor
    }

    public SelectionSpan? Current => _current;

    //the live chrome span, null when there is none (one domain is set at a time)
    public ChromeSpan? ChromeCurrent => _chromeCurrent;

    //true when either domain holds a span, the one predicate the Esc and Ctrl+C sinks consult
    public bool HasSelection => _current is not null || _chromeCurrent is not null;

    //starts a drag, Char anchors the head on the press point and any live chrome span is cleared first
    public void BeginDrag(SelCell at, DragKind kind, int width)
    {
        _chromeCurrent = null;
        _chromeSnapshot.Clear();
        _kind = kind;
        _current = new SelectionSpan(at, at, width);
        SnapshotTouched();
    }

    //grows the drag to the head, does nothing when the span went dead, a Move must not bring it back
    public void ExtendTo(SelCell head, int width)
    {
        if (_current is not { } cur) return;
        _current = cur with { Head = head, Width = width };
        SnapshotTouched();
    }

    //ends the drag, a bare click at one cell is dropped so no phantom highlight shows
    public void EndDrag()
    {
        if (_current is { } cur && _kind == DragKind.Char && cur.Anchor == cur.Head) Clear();
    }

    //drops both domains, returns true when one was live (the Esc key uses that)
    public bool Clear()
    {
        var had = _current is not null || _chromeCurrent is not null;
        _current = null;
        _snapshot.Clear();
        _chromeCurrent = null;
        _chromeSnapshot.Clear();
        return had;
    }

    //clears the span at a different width, a repaint at the same width leaves it alone
    public void OnWidthChanged(int width)
    {
        if (_current is { } cur && cur.Width != width) Clear();
    }

    //checks without changing anything, false when the index is out of range or a touched item's Rev moved
    public bool StillValid(IReadOnlyList<TranscriptItem> items)
    {
        if (_current is not { } cur) return false;
        var lo = System.Math.Min(cur.Anchor.ItemIndex, cur.Head.ItemIndex);
        var hi = System.Math.Max(cur.Anchor.ItemIndex, cur.Head.ItemIndex);
        if (lo < 0 || hi >= items.Count) return false;            //the span points outside the item list
        foreach (var (idx, rev) in _snapshot)
            if (items[idx].Rev != rev) return false;              //a touched item changed under the span
        return true;
    }

    //the one call the compositor makes per frame, clears a stale span and says whether to paint it
    public bool ValidFor(int width)
    {
        OnWidthChanged(width);
        if (_current is null) return false;
        if (!StillValid(_model.Items)) { Clear(); return false; }
        return true;
    }

    //clears the chrome span at a different width, a repaint at the same width leaves it alone
    public void OnChromeWidthChanged(int width)
    {
        if (_chromeCurrent is { } cur && cur.Width != width) Clear();
    }

    //the chrome half of the per-frame gate, it clears the span itself so no caller can leave it live with nothing painted
    public bool ChromeValidFor(int width, IReadOnlyList<ChromeRow> rows)
    {
        OnChromeWidthChanged(width);
        if (_chromeCurrent is null) return false;
        if (!ChromeStillValid(rows)) { Clear(); return false; }
        return true;
    }

    //gives the visible cell range when the row is inside the span, both endpoints included, a blank row stays inside
    public bool RowRange(int itemIndex, int rel, int rowCells, out int start, out int end)
    {
        start = end = 0;
        if (_current is not { } cur) return false;
        var (lo, hi) = cur.Normalized();
        var afterLo = itemIndex > lo.ItemIndex || (itemIndex == lo.ItemIndex && rel >= lo.Rel);
        var beforeHi = itemIndex < hi.ItemIndex || (itemIndex == hi.ItemIndex && rel <= hi.Rel);
        if (!afterLo || !beforeHi) return false;
        var isFirst = itemIndex == lo.ItemIndex && rel == lo.Rel;
        var isLast = itemIndex == hi.ItemIndex && rel == hi.Rel;
        start = System.Math.Clamp(isFirst ? lo.Col : 0, 0, rowCells);
        end = System.Math.Clamp(isLast ? hi.Col + 1 : rowCells, 0, rowCells);   //the end is one past the head column, so the head cell is copied
        return true;
    }

    //starts a chrome drag, clears the transcript span first, and needs the full block (the validity check counts its rows)
    public void BeginChromeDrag(ChromeCell at, DragKind kind, int width, IReadOnlyList<ChromeRow> rows)
    {
        _current = null;
        _snapshot.Clear();
        _chromeKind = kind;
        _chromeCurrent = new ChromeSpan(at, at, width);
        SnapshotChromeTouched(rows);
    }

    //grows the chrome drag, does nothing when the chrome span went dead
    public void ExtendChromeTo(ChromeCell head, int width, IReadOnlyList<ChromeRow> rows)
    {
        if (_chromeCurrent is not { } cur) return;
        _chromeCurrent = cur with { Head = head, Width = width };
        SnapshotChromeTouched(rows);
    }

    //compares the Visible text of every touched row and the row count, chrome has no Rev to compare
    public bool ChromeStillValid(IReadOnlyList<ChromeRow> rows)
    {
        if (_chromeCurrent is not { } cur) return false;
        if (rows.Count != _chromeTotalRows) return false;
        var lo = System.Math.Min(cur.Anchor.Row, cur.Head.Row);
        var hi = System.Math.Max(cur.Anchor.Row, cur.Head.Row);
        if (lo < 0 || hi >= rows.Count) return false;              //the span points outside the row list
        foreach (var (row, visible) in _chromeSnapshot)
            if (row < 0 || row >= rows.Count || rows[row].Visible != visible) return false;
        return true;
    }

    //gives the visible cell range when the row is inside the chrome span, both endpoints included
    public bool ChromeRowRange(int row, int rowCells, out int start, out int end)
    {
        start = end = 0;
        if (_chromeCurrent is not { } cur) return false;
        var (lo, hi) = cur.Normalized();
        if (row < lo.Row || row > hi.Row) return false;
        var isFirst = row == lo.Row;
        var isLast = row == hi.Row;
        start = System.Math.Clamp(isFirst ? lo.Col : 0, 0, rowCells);
        end = System.Math.Clamp(isLast ? hi.Col + 1 : rowCells, 0, rowCells);   //the end is one past the head column, so the head cell is copied
        return true;
    }

    //plain text of whichever span is live, re-renders each touched item, keeps blank rows as empty lines and a collapsed item as its summary
    public string? CopyText(int width, Theme theme, IReadOnlyList<ChromeRow>? chromeRows,
        GlyphSet? glyphs)
    {
        if (_chromeCurrent is not null) return chromeRows is null ? null : CopyChromeText(chromeRows);
        if (_current is not { } cur) return null;
        var (lo, hi) = cur.Normalized();
        //a rejoined continuation drops the wrap prefix the render layer reports and joins with one space, the first row keeps its marker
        var lines = new List<string>();
        (int Item, int Rel)? prev = null;   //the previously-copied row's coordinate, a row only rejoins onto its immediate predecessor
        for (var idx = lo.ItemIndex; idx <= hi.ItemIndex; idx++)
        {
            if (idx < 0 || idx >= _model.Items.Count) continue;
            var rows = _model.Items[idx].Render(width, theme, glyphs);
            var wraps = _model.Items[idx].RowWraps(width, theme, glyphs);
            for (var rel = 0; rel < rows.Count; rel++)
            {
                var plain = TermText.StripAnsiForWidth(rows[rel]);
                if (!RowRange(idx, rel, UnicodeWidth.Of(plain), out var cs, out var ce)) continue;
                var wrap = rel < wraps.Count ? wraps[rel] : default;
                var rejoin = wrap.Continuation
                             && prev is { } p && p.Item == idx && p.Rel == rel - 1;   //the previous row has to be the one just copied for a rejoin
                if (rejoin)
                {
                    var piece = SliceCells(plain, System.Math.Max(cs, wrap.PrefixCells), ce);   //the slice starts past the wrap prefix, dropping the wrap chrome
                    //a head inside the wrap gutter selects nothing of that row, so the joined line gets no trailing space
                    if (piece.Length > 0) lines[^1] += " " + piece;
                }
                else
                    lines.Add(SliceCells(plain, System.Math.Max(cs, wrap.LeadCells), ce));   //a row's own chrome, a rail or a line number, stays out of the copy
                prev = (idx, rel);
            }
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    //chrome text needs no re-render, it refuses when the snapshot moved (Ctrl+C arrives between paints) and CopyText is the only way in
    private string? CopyChromeText(IReadOnlyList<ChromeRow> rows)
    {
        if (_chromeCurrent is not { } cur) return null;
        if (!ChromeStillValid(rows)) return null;   //refuse before slicing, the rows may have changed since the paint
        var (lo, hi) = cur.Normalized();
        var lines = new List<string>();
        int? prev = null;   //the row copied before this one, a row only rejoins onto its immediate predecessor
        for (var row = lo.Row; row <= hi.Row; row++)
        {
            if (row < 0 || row >= rows.Count) continue;
            var plain = rows[row].Visible;
            if (!ChromeRowRange(row, UnicodeWidth.Of(plain), out var cs, out var ce)) continue;
            var chromeRow = rows[row];
            var rejoin = chromeRow.Continuation && prev is { } p && p == row - 1;   //the previous row has to be the one just copied for a rejoin
            if (rejoin)
            {
                var piece = SliceCells(plain, System.Math.Max(cs, chromeRow.PrefixCells), ce);   //the slice starts past the wrap prefix, dropping the wrap chrome
                //a head inside the wrap gutter selects nothing of that row, so the joined line gets no trailing space
                if (piece.Length > 0) lines[^1] += " " + piece;
            }
            else
                lines.Add(SliceCells(plain, cs, ce));   //an empty string when the row is blank or the range is empty, keeping the line
            prev = row;
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private void SnapshotTouched()
    {
        _snapshot.Clear();
        if (_current is not { } cur) return;
        var lo = System.Math.Min(cur.Anchor.ItemIndex, cur.Head.ItemIndex);
        var hi = System.Math.Max(cur.Anchor.ItemIndex, cur.Head.ItemIndex);
        for (var idx = lo; idx <= hi; idx++)
            if (idx >= 0 && idx < _model.Items.Count) _snapshot[idx] = _model.Items[idx].Rev;
    }

    //snapshots the Visible text of every touched chrome row plus the row count, re-taken as the drag grows
    private void SnapshotChromeTouched(IReadOnlyList<ChromeRow> rows)
    {
        _chromeSnapshot.Clear();
        if (_chromeCurrent is not { } cur) return;
        _chromeTotalRows = rows.Count;
        var lo = System.Math.Min(cur.Anchor.Row, cur.Head.Row);
        var hi = System.Math.Max(cur.Anchor.Row, cur.Head.Row);
        for (var row = lo; row <= hi; row++)
            if (row >= 0 && row < rows.Count) _chromeSnapshot[row] = rows[row].Visible;
    }

    private static string SliceCells(string plain, int start, int end)
    {
        var sb = new StringBuilder();
        var w = 0;
        foreach (var rune in plain.EnumerateRunes())
        {
            if (w >= end) break;
            var rw = UnicodeWidth.OfRune(rune);
            if (w >= start) sb.Append(rune.ToString());   //a rune is kept when its first cell is in the range, a wide one is copied whole
            w += rw;
        }
        return sb.ToString();
    }
}
