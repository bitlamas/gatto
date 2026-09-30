namespace Gatto.Repl.Render;

//what a click row maps to, resolved into the layout at paint time so HitTest reads the frame that was painted
public abstract record MouseTarget;

//an item's rows, HeaderRow skipping the leading blank separator and LastRow marking the thinking block's affordance row
public sealed record ItemTarget(int ItemIndex, bool HeaderRow, bool LastRow = false) : MouseTarget;

//the floating jump row shown while scrolled up, a click jumps to the bottom
public sealed record JumpHintTarget : MouseTarget
{
    public static readonly JumpHintTarget Instance = new();
}

//a chrome row, indexed flat into the composed rows like ChromeCell, with its region and region row
public sealed record ChromeTarget(int Row, ChromeRegion Region, int RegionRow) : MouseTarget;

//padding, a leading-blank gap, plain text or a frame the layout has not caught up to
public sealed record NoTarget : MouseTarget
{
    public static readonly NoTarget Instance = new();
}

//the hit-test geometry from one paint, with the dims it was built at so a stale frame is caught
public sealed record FrameLayout(int Width, int Height, int HintRow,
    IReadOnlyDictionary<int, MouseTarget> Rows,
    IReadOnlyDictionary<int, (int ItemIndex, int Rel)> Cells,
    IReadOnlyDictionary<int, int> ChromeRows,
    IReadOnlyList<ChromeRow> ChromeRowInfo);
