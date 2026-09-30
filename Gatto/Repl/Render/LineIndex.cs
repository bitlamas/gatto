using Gatto.Terminal;

namespace Gatto.Repl.Render;

//maps a scroll offset to the visible items. counts come from TranscriptItem.Render, and the cache is keyed by reference identity, items are value-equal records
public sealed class LineIndex
{
    private readonly TranscriptModel _model;
    private readonly Theme _theme;
    //the glyph set chosen once at launch, handed down beside the theme
    private readonly GlyphSet _glyphs;
    private readonly Dictionary<TranscriptItem, (int Count, int Rev)> _counts = new(ReferenceEqualityComparer.Instance);
    private int _width = -1;

    //the one theme both the counts and the compositor draw with, so a count can't diverge from the painted row
    public Theme Theme => _theme;
    //the one glyph set the counts and the compositor draw with, so a layout read elsewhere matches the paint
    public GlyphSet Glyphs => _glyphs;

    public LineIndex(TranscriptModel model, Theme theme, GlyphSet? glyphs)
    {
        _glyphs = glyphs ?? GlyphSet.Unicode;
        _model = model;
        _theme = theme;
        //drop a changed item's cached count, it is recomputed on the next read at the current width
        _model.OnAppendedOrExtended += item => _counts.Remove(item);
        //a reset drops every item, so clear the cache and let the next query rebuild at its width
        _model.OnReset += () => { _counts.Clear(); _width = -1; };
    }

    //cached counts belong to one width, so a query at a new width rebuilds first and the cache can't go stale
    private void EnsureWidth(int width) { if (width != _width) Rebuild(width); }

    //the item's wrapped row count at this width, taken from its own Render. the Rev check heals an in-place change that fires no model event, such as a shell window moving
    public int Count(TranscriptItem item, int width)
    {
        EnsureWidth(width);
        if (_counts.TryGetValue(item, out var c) && c.Rev == item.Rev) return c.Count;
        c = (item.Render(width, _theme, _glyphs).Count, item.Rev);
        _counts[item] = c;
        return c.Count;
    }

    //force a recount of one item, the append event normally does this
    public void OnItemChanged(TranscriptItem item, int width) =>
        _counts[item] = (item.Render(width, _theme, _glyphs).Count, item.Rev);

    //re-wrap every item at a new width, a resize is rare so the cost is fine
    public void Rebuild(int width)
    {
        _width = width;
        _counts.Clear();
        foreach (var it in _model.Items) _counts[it] = (it.Render(width, _theme, _glyphs).Count, it.Rev);
    }

    public int TotalRows(int width)
    {
        EnsureWidth(width);
        var total = 0;
        foreach (var it in _model.Items) total += Count(it, width);
        return total;
    }

    //the items intersecting the visible window, counted up from the bottom by bottomOffset, each with the rows clipped off the window top
    public IReadOnlyList<(TranscriptItem Item, int FirstRowWithin, int ItemIndex)> Window(int width, int height, int bottomOffset)
    {
        var total = TotalRows(width);
        var end = System.Math.Clamp(total - bottomOffset, 0, total);   //exclusive bottom row of the window
        var start = System.Math.Max(0, end - height);                  //inclusive top row of the window
        var result = new List<(TranscriptItem, int, int)>();
        var cursor = 0;
        var idx = 0;
        foreach (var it in _model.Items)
        {
            var c = Count(it, width);
            var itemStart = cursor;
            var itemEnd = cursor + c;
            cursor = itemEnd;
            if (itemEnd > start && itemStart < end)   //the item overlaps the window
                result.Add((it, System.Math.Max(0, start - itemStart), idx));
            idx++;
            if (itemStart >= end) break;   //later items all start below the window, so stop here
        }
        return result;
    }
}
