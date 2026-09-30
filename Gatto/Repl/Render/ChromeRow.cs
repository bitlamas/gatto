namespace Gatto.Repl.Render;

//the part of the chrome stack a row comes from, which drives chrome selection
public enum ChromeRegion { Other, Tail, Tool, Notice, Purr, Queue, Prompt, Composer, Rule }

//one physical chrome row, the painted string for the terminal and the plain string for width and selection math. the wrap and region tags sit on it
public readonly record struct ChromeRow(
    string Rendered, string Visible, bool Continuation, int PrefixCells,
    ChromeRegion Region, int RegionRow)
{
    //an untagged row with no wrap chrome and no region, for tests and degrade paths
    public static ChromeRow Plain(string text) =>
        new(text, text, false, 0, ChromeRegion.Other, 0);
}

//the chrome block pinned to the bottom of the frame, rows plus caret cell. caret visible false hides the cursor for a panel that takes no typed input
public readonly record struct ChromeBlock(
    IReadOnlyList<ChromeRow> Rows, int CaretRow, int CaretCol, bool CaretVisible = true)
{
    //builds a block of plain rows, for tests and degrade paths
    public static ChromeBlock FromText(IReadOnlyList<string> rows, int caretRow, int caretCol) =>
        new(rows.Select(ChromeRow.Plain).ToList(), caretRow, caretCol);
}
