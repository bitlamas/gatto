namespace Gatto.Repl.Render;

//the focus cursor over collapsible items, and the only place focus state is set. it marks the item in the compositor and asks the scroll to reveal it
public sealed class FocusController
{
    private readonly TranscriptModel _model;
    private readonly LineIndex _index;
    private readonly ScrollController _scroll;
    private readonly ViewportCompositor _compositor;
    private int? _focused;

    public FocusController(TranscriptModel model, LineIndex index, ScrollController scroll, ViewportCompositor compositor)
    {
        _model = model; _index = index; _scroll = scroll; _compositor = compositor;
        _model.OnReset += Clear;   //a rebuild leaves the focused index stale, so clear it on every reset
    }

    public int? Focused => _focused;

    //a focus stop has something to open at this width: a thought, or a tool block whose body holds more than its row. an open block that no longer does is skipped
    private bool IsStop(TranscriptItem it, int width) =>
        it is ReasoningItem || it is ToolBlockItem tb && tb.Opens(width, _index.Theme, _index.Glyphs);

    //the one collapsible test the mouse uses, so a click never repeats the rule. an open tool block can always close, and an out-of-range index is false
    public bool IsCollapsible(int itemIndex, int width) =>
        itemIndex >= 0 && itemIndex < _model.Items.Count
        && (_model.Items[itemIndex] is ToolBlockItem { Collapsed: false, HasBody: true } || IsStop(_model.Items[itemIndex], width));

    //the one place that sets focus, the marker and the reveal. null clears, and the mouse passes reveal false since a click is already under the pointer
    public void Focus(int? itemIndex, int width, int height, bool reveal = true)
    {
        _focused = itemIndex;
        _compositor.FocusedItemIndex = itemIndex;
        if (reveal && itemIndex is int i) _scroll.RevealItem(i, width, height);
    }

    public void Clear() => Focus(null, 0, 0);

    //move focus to the older collapsible item. with nothing focused the first press takes the newest one, and if nothing older exists the focus stays
    public void Prev(int width, int height)
    {
        var start = _focused ?? _model.Items.Count;
        for (var i = start - 1; i >= 0; i--)
            if (IsStop(_model.Items[i], width)) { Focus(i, width, height); return; }
    }

    //move focus to the newer collapsible item. past the last one it clears focus and re-attaches the view to the bottom, where the composer is
    public void Next(int width, int height)
    {
        if (_focused is not int start) return;   //with no focus there is nothing newer to move to
        for (var i = start + 1; i < _model.Items.Count; i++)
            if (IsStop(_model.Items[i], width)) { Focus(i, width, height); return; }
        Focus(null, width, height);              //past the last collapsible, so the focus clears
        _scroll.End();                           //go back to following the live bottom
    }

    //flip one item's Collapsed and keep its top visible, so it grows downward. the streaming reasoning item can be opened to follow along
    public void Toggle(int itemIndex, int width, int height)
    {
        if (itemIndex < 0 || itemIndex >= _model.Items.Count) return;
        var item = _model.Items[itemIndex];
        item.Collapsed = !item.Collapsed;
        if (item is ToolBlockItem { Collapsed: true } closed) closed.ResetView();   //a closed tool block opens next time at the top of its window
        if (item is ReasoningItem ri) ri.UserToggled = true;   //a manual toggle sticks, so the close leaves this block as the user left it
        _index.OnItemChanged(item, width);
        //expanding a streaming reasoning block re-attaches to the live bottom so new lines scroll in. any other toggle keeps the item's top visible
        if (item is ReasoningItem { Streaming: true } && !item.Collapsed)
            _scroll.End();
        else
            _scroll.EnsureTopVisible(itemIndex, width, height);   //the top stays visible either way, since RevealItem would yank a partly hidden item
    }

    //a press with nothing focused toggles the latest thought block, since that block is what advertises Ctrl+R. it sets no focus and leaves tool blocks alone
    public void ToggleFocused(int width, int height)
    {
        if (_focused is int i) { if (i < _model.Items.Count && _model.Items[i] is ToolBlockItem) Cycle(i, width, height); else Toggle(i, width, height); return; }
        for (var j = _model.Items.Count - 1; j >= 0; j--)
            if (_model.Items[j] is ReasoningItem) { Toggle(j, width, height); return; }
    }

    //closed, the window, show all when the window hides something, closed again
    public void Cycle(int itemIndex, int width, int height)
    {
        if (itemIndex < 0 || itemIndex >= _model.Items.Count || _model.Items[itemIndex] is not ToolBlockItem tb)
        {
            Toggle(itemIndex, width, height);
            return;
        }
        //a closed block opens only while its body holds more than its row, even under a focus kept across a resize
        if (tb.Collapsed) { if (!tb.Opens(width, _index.Theme, _index.Glyphs)) return; tb.Collapsed = false; }
        else if (tb.View == ShellView.Window && tb.Layout(width, _index.Theme, _index.Glyphs).Hides) tb.View = ShellView.All;
        else { tb.Collapsed = true; tb.ResetView(); }
        _index.OnItemChanged(tb, width);
        _scroll.EnsureTopVisible(itemIndex, width, height);
    }

    public void ShowAll(int itemIndex, bool all, int width, int height)
    {
        if (itemIndex < 0 || itemIndex >= _model.Items.Count || _model.Items[itemIndex] is not ToolBlockItem tb) return;
        tb.Collapsed = false;
        tb.View = all ? ShellView.All : ShellView.Window;
        if (!all) tb.ResetTop();   //show less returns to where the block rests
        _index.OnItemChanged(tb, width);
        _scroll.EnsureTopVisible(itemIndex, width, height);
    }
}
