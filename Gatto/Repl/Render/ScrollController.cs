namespace Gatto.Repl.Render;

//a manual scroll anchors to an item and a row inside it, so a later append doesn't move the view
public sealed class ScrollController
{
    private readonly TranscriptModel _model;
    private readonly LineIndex _index;
    private bool _following = true;
    private int _anchorItem;    //index into _model.Items of the item at the viewport top, used while detached
    private int _anchorRow;     //row inside that item that sits at the viewport's top

    public ScrollController(TranscriptModel model, LineIndex index)
    {
        _model = model;
        _index = index;
        //a reset (/compact) empties the model, drop the stale anchor and follow the bottom again
        _model.OnReset += () => { _following = true; _anchorItem = 0; _anchorRow = 0; };
    }

    public bool Following => _following;

    //rows scrolled up from the bottom, 0 while following, so the anchored row stays put as the tail grows
    public int BottomOffset(int width, int height)
    {
        if (_following) return 0;
        var total = _index.TotalRows(width);
        var top = System.Math.Clamp(AbsTop(width), 0, System.Math.Max(0, total));
        return System.Math.Clamp(total - top - height, 0, System.Math.Max(0, total));
    }

    public void PageUp(int width, int height)
    {
        if (!Scrollable(width, height)) return;   //content fits, nothing to scroll, stay following
        DetachTo(width, CurrentTop(width, height) - HalfPage(height));
    }
    public void PageDown(int width, int height)
    {
        if (!Scrollable(width, height)) return;
        var total = _index.TotalRows(width);
        var target = CurrentTop(width, height) + HalfPage(height);
        if (target >= System.Math.Max(0, total - height)) End();   //reached the bottom, re-attach and follow
        else DetachTo(width, target);
    }

    public void Home(int width, int height)
    {
        if (!Scrollable(width, height)) return;
        DetachTo(width, 0);
    }
    public void End() => _following = true;

    //positive scrolls toward older content, a zero delta must not detach follow
    public void ScrollBy(int linesTowardOlder, int width, int height)
    {
        if (linesTowardOlder == 0) return;                     //a zero delta must not detach follow
        if (!Scrollable(width, height)) return;
        var target = CurrentTop(width, height) - linesTowardOlder;
        if (linesTowardOlder < 0)                              //toward the bottom
        {
            var total = _index.TotalRows(width);
            if (target >= System.Math.Max(0, total - height)) { End(); return; }   //reached the bottom, re-attach follow
        }
        DetachTo(width, target);
    }

    private bool Scrollable(int width, int height) => _index.TotalRows(width) > height;

    //the painter calls this after an append, empty by design (the anchor is an item index, the tail only grows)
    public void OnModelGrew(int width) { }

    //internals: absolute row to item and row, converted with the LineIndex counts

    private static int HalfPage(int height) => System.Math.Max(1, height / 2);

    private int CurrentTop(int width, int height)
    {
        if (!_following) return AbsTop(width);
        var total = _index.TotalRows(width);
        return System.Math.Max(0, total - height);   //following, so the top is the top of the bottom-aligned frame
    }

    private int AbsTop(int width)
    {
        var cursor = 0;
        for (var i = 0; i < _model.Items.Count && i < _anchorItem; i++) cursor += _index.Count(_model.Items[i], width);
        return cursor + _anchorRow;
    }

    private void DetachTo(int width, int absRow)
    {
        _following = false;
        var total = _index.TotalRows(width);
        absRow = System.Math.Clamp(absRow, 0, System.Math.Max(0, total - 1));
        var cursor = 0;
        for (var i = 0; i < _model.Items.Count; i++)
        {
            var c = _index.Count(_model.Items[i], width);
            if (absRow < cursor + c) { _anchorItem = i; _anchorRow = absRow - cursor; return; }
            cursor += c;
        }
        //past the end, anchor the first row of the last item, the BottomOffset clamp keeps that at the content bottom
        _anchorItem = System.Math.Max(0, _model.Items.Count - 1);
        _anchorRow = 0;
    }

    //reveal a focused item, driven by the FocusController

    //scroll the item into view without passing its top, so a tall item below the view shows its top
    public void RevealItem(int itemIndex, int width, int height)
    {
        var (top, bottom) = ItemBounds(itemIndex, width);
        if (top < 0) return;
        var curTop = CurrentTop(width, height);
        if (top >= curTop && bottom <= curTop + height) return;                          //nothing to do, it's already fully visible
        if (top < curTop) { DetachTo(width, top); return; }                              //above the view, pin its top row
        DetachTo(width, System.Math.Min(top, System.Math.Max(0, bottom - height)));      //below the view, scroll just enough to show it, a tall item keeps its top
    }

    //scroll only when the item's top is above the view, so an expanding item clips downward and the view holds still
    public void EnsureTopVisible(int itemIndex, int width, int height)
    {
        var (top, _) = ItemBounds(itemIndex, width);
        if (top < 0) return;
        if (top < CurrentTop(width, height)) DetachTo(width, top);
    }

    private (int Top, int Bottom) ItemBounds(int itemIndex, int width)
    {
        if (itemIndex < 0 || itemIndex >= _model.Items.Count) return (-1, -1);
        var top = 0;
        for (var i = 0; i < itemIndex; i++) top += _index.Count(_model.Items[i], width);
        return (top, top + _index.Count(_model.Items[itemIndex], width));
    }
}
