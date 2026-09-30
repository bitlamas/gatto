namespace Gatto.Repl.Render;

//one end of a selection: item index, row inside that item, and visible column, so it survives scroll and append
public readonly record struct SelCell(int ItemIndex, int Rel, int Col);

//a selection over the transcript: press point, drag head, and the width it was anchored at (a paint at another width clears it)
public readonly record struct SelectionSpan(SelCell Anchor, SelCell Head, int Width)
{
    //returns the endpoints in reading order, item index then row then column, so the copy path only moves forward
    public (SelCell Lo, SelCell Hi) Normalized()
    {
        var a = Anchor;
        var h = Head;
        var aFirst = a.ItemIndex != h.ItemIndex ? a.ItemIndex < h.ItemIndex
                   : a.Rel != h.Rel ? a.Rel < h.Rel
                   : a.Col <= h.Col;
        return aFirst ? (a, h) : (h, a);
    }

    //true when the item index falls between the endpoints, whichever end came first
    public bool Touches(int itemIndex)
    {
        var lo = System.Math.Min(Anchor.ItemIndex, Head.ItemIndex);
        var hi = System.Math.Max(Anchor.ItemIndex, Head.ItemIndex);
        return itemIndex >= lo && itemIndex <= hi;
    }
}

//how a drag grows: a character, a word on a double-click, or a visible line on a triple-click
public enum DragKind { Char, Word, Line }

//a chrome selection endpoint: a flat row index into the composed ChromeBlock and a visible column, chrome rows have no item index
public readonly record struct ChromeCell(int Row, int Col);

//a selection over the chrome block: press point, drag head, and the width it was anchored at (a paint at another width clears it)
public readonly record struct ChromeSpan(ChromeCell Anchor, ChromeCell Head, int Width)
{
    //returns the endpoints in reading order, row then column, chrome has no item index to compare
    public (ChromeCell Lo, ChromeCell Hi) Normalized()
    {
        var a = Anchor;
        var h = Head;
        var aFirst = a.Row != h.Row ? a.Row < h.Row : a.Col <= h.Col;
        return aFirst ? (a, h) : (h, a);
    }
}
