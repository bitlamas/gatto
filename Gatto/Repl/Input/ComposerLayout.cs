using Gatto.Terminal;

namespace Gatto.Repl.Input;

//the composer's mouse-cell to text-position map, built from a snapshot of lines and width, counting composer rows only
public sealed class ComposerLayout
{
    //every composer row's content starts here, the prompt and hang prefixes are both exactly 2 cells
    private const int PrefixCells = 2;

    private readonly IReadOnlyList<string> _lines;
    private readonly IReadOnlyList<IReadOnlyList<WrapSeg>> _segsPerLine;
    private readonly int[] _rowsBeforeLine;   //composer rows before each logical line

    //the captured logical lines this layout maps against
    public IReadOnlyList<string> Lines => _lines;

    //the terminal width captured at construction
    public int Width { get; }

    //the wrap budget from the width, 2 cells for the prompt and hang and 1 for the continuation mark
    public int Budget { get; }

    //each line's wrap segments, built once from the snapshot and reused by every mapping call, don't re-wrap
    public IReadOnlyList<IReadOnlyList<WrapSeg>> SegsPerLine => _segsPerLine;

    //total composer rows over every logical line, the flat row space both maps index
    public int RowCount { get; }

    public ComposerLayout(IReadOnlyList<string> lines, int width)
    {
        _lines = lines.Count > 0 ? lines : new[] { "" };
        Width = width;
        Budget = width > 3 ? width - 3 : 0;

        var segsPerLine = new List<IReadOnlyList<WrapSeg>>(_lines.Count);
        var rowsBeforeLine = new int[_lines.Count];
        var rows = 0;
        for (var li = 0; li < _lines.Count; li++)
        {
            rowsBeforeLine[li] = rows;
            var segs = SoftWrap.Wrap(_lines[li], Budget, Budget);
            segsPerLine.Add(segs);
            rows += segs.Count;
        }
        _segsPerLine = segsPerLine;
        _rowsBeforeLine = rowsBeforeLine;
        RowCount = rows;
    }

    //turn a click's row and cell into a (line, col), the inverse of PositionToCell, with four clamp rules for off-grid clicks
    public (int Line, int Col) ComposerCellToPosition(int regionRow, int cellCol)
    {
        //a row below the last one goes to the buffer end, the only way RowCount can be reached since wrap never returns zero segments
        if (regionRow >= RowCount)
            return (_lines.Count - 1, _lines[^1].Length);

        //a row above the composer's top has no defined target, treat it as the top row rather than throwing
        var row = Math.Max(regionRow, 0);

        var (li, segIndex) = LineAndSegAt(row);
        var segs = _segsPerLine[li];

        //a cell inside the prefix clamps to this row's first content char, a cell at or past PrefixCells is untouched
        var contentCell = Math.Max(cellCol - PrefixCells, 0);

        //the two remaining clamps (mid-glyph snap and the past-end cap) are already in ColAtSegCell, so it is reused here
        var col = LineEditor.ColAtSegCell(segs, segIndex, contentCell);
        return (li, col);
    }

    //a text position to the cell it renders at, the same call and snapshot as InputFrame.Compose so the two directions can't drift
    public (int RegionRow, int CellCol) PositionToCell(int line, int col)
    {
        var li = Math.Clamp(line, 0, _lines.Count - 1);
        var lc = Math.Clamp(col, 0, _lines[li].Length);
        var segs = _segsPerLine[li];
        var (segRow, cells) = SoftWrap.MapCursor(segs, _lines[li], lc);
        return (_rowsBeforeLine[li] + segRow, PrefixCells + cells);
    }

    //which logical line and which wrap segment owns a flat composer row, a linear scan is fine at this size
    private (int Line, int SegIndex) LineAndSegAt(int row)
    {
        for (var li = 0; li < _lines.Count; li++)
        {
            var start = _rowsBeforeLine[li];
            var end = start + _segsPerLine[li].Count;
            if (row < end) return (li, row - start);
        }
        //the caller already guards on RowCount, so this is only a fallback
        var lastLi = _lines.Count - 1;
        return (lastLi, _segsPerLine[lastLi].Count - 1);
    }
}
