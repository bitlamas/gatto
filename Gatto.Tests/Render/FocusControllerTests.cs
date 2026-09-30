using System.Linq;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class FocusControllerTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (FocusController Fc, TranscriptModel Model, LineIndex Index, ScrollController Scroll)
        NewFocus(int height, params TranscriptItem[] items)
    {
        var model = new TranscriptModel("coder");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var scroll = new ScrollController(model, index);
        var surface = new RecordingSurface { Width = 80, Height = height };
        var compositor = new ViewportCompositor(surface, index, new object(), glyphs: GlyphSet.Unicode);
        var fc = new FocusController(model, index, scroll, compositor);
        foreach (var it in items) model.Append(it);
        return (fc, model, index, scroll);
    }

    private static AssistantBlockItem Prose(string text) => new(new[] { text }, "coder");
    private static ReasoningItem Reasoning(string text) => new(new[] { text }) { Collapsed = true };
    private static ToolBlockItem Tool(string name, string result = "output") =>
        new(name, "args", "✓", true, "coder") { Collapsed = true, FullResult = result };
    private static AssistantBlockItem TallProse(int n) =>
        new(Enumerable.Range(0, n).Select(i => $"line {i}").ToArray(), "coder");

    private static bool ItemInWindow(ScrollController scroll, LineIndex index, int itemIndex, int w, int h) =>
        index.Window(w, h, scroll.BottomOffset(w, h)).Any(x => x.ItemIndex == itemIndex);
    private static int? FirstRowWithin(ScrollController scroll, LineIndex index, int itemIndex, int w, int h)
    {
        foreach (var (_, first, idx) in index.Window(w, h, scroll.BottomOffset(w, h)))
            if (idx == itemIndex) return first;
        return null;
    }

    [Fact]
    public void Prev_from_no_focus_lands_on_most_recent_collapsible()
    {
        var (fc, _, _, _) = NewFocus(24, Prose("hi"), Reasoning("think"), Prose("answer"), Tool("read_file"));
        fc.Prev(80, 24);
        Assert.Equal(3, fc.Focused);   //the tool block is the most recent collapsible, nearest the composer.
    }

    [Fact]
    public void Nav_visits_only_collapsible_items()
    {
        var (fc, _, _, _) = NewFocus(24, Prose("a"), Reasoning("r"), Prose("b"), Tool("t"));
        fc.Prev(80, 24);               //the first step focuses the tool at index three.
        fc.Prev(80, 24);               //the next step skips prose and focuses the reasoning item.
        Assert.Equal(1, fc.Focused);
    }

    [Fact]
    public void Next_past_the_last_clears_focus()
    {
        var (fc, _, _, _) = NewFocus(24, Reasoning("r"), Tool("t"));
        fc.Prev(80, 24);               //focus starts on the tool at index one.
        fc.Next(80, 24);               //moving past the last collapsible clears focus.
        Assert.Null(fc.Focused);
    }

    [Fact]
    public void ToggleFocused_flips_collapsed()
    {
        var (fc, model, _, _) = NewFocus(24, Tool("t", "a\nb"));
        fc.Prev(80, 24);
        fc.ToggleFocused(80, 24);
        Assert.False(((ToolBlockItem)model.Items[0]).Collapsed);   //the first toggle expands the block.
        fc.ToggleFocused(80, 24);
        Assert.True(((ToolBlockItem)model.Items[0]).Collapsed);    //the second toggle collapses it again.
    }

    [Fact]
    public void Toggle_flips_the_open_streaming_reasoning_item()   //an open streaming thought must expand on toggle, so the user can follow it live.
    {
        var model = new TranscriptModel("coder") { ReasoningCollapseDefault = true };
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var scroll = new ScrollController(model, index);
        var compositor = new ViewportCompositor(new RecordingSurface { Width = 80, Height = 24 }, index, new object(), glyphs: GlyphSet.Unicode);
        var fc = new FocusController(model, index, scroll, compositor);
        model.AppendOpenLine("thinking...", reasoning: true);   //an open reasoning item starts collapsed.
        var ri = (ReasoningItem)model.Items[0];
        Assert.Same(ri, model.OpenItem);
        Assert.True(ri.Streaming);
        Assert.True(ri.Collapsed);      //the streaming thought shows only a capped preview.
        fc.Prev(80, 24);                //focus moves to the open reasoning item.
        fc.ToggleFocused(80, 24);
        Assert.False(ri.Collapsed);     //the toggle expands the open thought so the user can follow it live.
        fc.ToggleFocused(80, 24);
        Assert.True(ri.Collapsed);      //the second toggle caps the preview again.
    }

    //with nothing focused, Ctrl+R acts on a thought block only, and it opens no tool result and sets no focus

    [Fact]
    public void ToggleFocused_with_nothing_focused_skips_a_newer_TOOL_and_takes_the_latest_THOUGHT()
    {
        //the tool block sits nearer the composer, so a most-recent rule would open it. the thought block is what shows the hint.
        var (fc, model, _, _) = NewFocus(24, Prose("hi"), Reasoning("think"), Prose("answer"), Tool("read_file"));

        fc.ToggleFocused(80, 24);

        Assert.False(model.Items[1].Collapsed);   //the thought block opens.
        Assert.True(model.Items[3].Collapsed);    //the newer tool block stays closed.
    }

    [Fact]
    public void ToggleFocused_with_nothing_focused_and_no_thought_block_leaves_tools_alone()
    {
        //with only tool blocks present, the unfocused key does nothing, and a tool result opens only once it is focused
        var (fc, model, _, _) = NewFocus(24, Prose("hi"), Tool("read_file"), Tool("shell"));

        fc.ToggleFocused(80, 24);

        Assert.True(model.Items[1].Collapsed);
        Assert.True(model.Items[2].Collapsed);
        Assert.Null(fc.Focused);
    }

    [Fact]
    public void ToggleFocused_with_nothing_focused_takes_the_LATEST_thought_when_there_are_several()
    {
        var (fc, model, _, _) = NewFocus(24, Reasoning("older"), Prose("x"), Reasoning("newer"));

        fc.ToggleFocused(80, 24);

        Assert.False(model.Items[2].Collapsed);
        Assert.True(model.Items[0].Collapsed);
    }

    [Fact]
    public void ToggleFocused_with_nothing_focused_toggles_BACK_and_never_invents_a_focus()
    {
        var (fc, model, _, _) = NewFocus(24, Prose("hi"), Reasoning("think"));

        fc.ToggleFocused(80, 24);
        Assert.False(model.Items[1].Collapsed);
        Assert.Null(fc.Focused);

        fc.ToggleFocused(80, 24);
        Assert.True(model.Items[1].Collapsed);
        Assert.Null(fc.Focused);
    }

    [Fact]
    public void ToggleFocused_with_nothing_to_toggle_is_still_a_noop_not_a_throw()
    {
        var (fc, _, _, _) = NewFocus(24, Prose("no collapsible items here"));
        fc.ToggleFocused(80, 24);
        Assert.Null(fc.Focused);
    }

    [Fact]
    public void ToggleFocused_on_a_FOCUSED_tool_block_still_toggles_it()
    {
        //a focused tool block still toggles, the narrowing applies to the unfocused shortcut only
        var (fc, model, _, _) = NewFocus(24, Reasoning("think"), Tool("read_file", "a\nb"));
        fc.Focus(1, 80, 24);

        fc.ToggleFocused(80, 24);

        Assert.False(model.Items[1].Collapsed);
        Assert.True(model.Items[0].Collapsed);   //the thought block is left untouched.
    }

    [Fact]
    public void ToggleFocused_with_a_focus_still_toggles_THAT_one()
    {
        //an explicit focus wins over the most recent block.
        var (fc, model, _, _) = NewFocus(24, Reasoning("older"), Prose("x"), Tool("newer", "a\nb"));
        fc.Focus(0, 80, 24);

        fc.ToggleFocused(80, 24);

        Assert.False(model.Items[0].Collapsed);   //the focused item is the one that toggles.
        Assert.True(model.Items[2].Collapsed);    //the most recent tool block stays collapsed.
    }

    [Fact]
    public void Focus_sets_the_compositor_marker_and_clears_it()   //focus must set and clear the compositor marker, the first link of the chain that draws the reverse video.
    {
        var model = new TranscriptModel("coder");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var scroll = new ScrollController(model, index);
        var compositor = new ViewportCompositor(new RecordingSurface { Width = 80, Height = 24 }, index, new object(), glyphs: GlyphSet.Unicode);
        var fc = new FocusController(model, index, scroll, compositor);
        model.Append(Prose("a"));
        model.Append(Tool("t", "x"));
        fc.Prev(80, 24);
        Assert.Equal(1, compositor.FocusedItemIndex);   //the focus controller sets the marker that later renders as reverse video.
        fc.Clear();
        Assert.Null(compositor.FocusedItemIndex);
    }

    [Fact]
    public void Focus_clears_on_model_reset()   //focus must clear when the model resets.
    {
        var (fc, model, _, _) = NewFocus(24, Tool("t", "x"));
        fc.Prev(80, 24);
        Assert.NotNull(fc.Focused);
        model.Reset();
        Assert.Null(fc.Focused);
    }

    [Fact]
    public void An_empty_result_tool_is_not_a_focus_target()   //a tool block with an empty result must not become a focus target.
    {
        var (fc, _, _, _) = NewFocus(24, Tool("cancelled", ""), Reasoning("r"));
        fc.Prev(80, 24);               //navigation skips the empty tool and focuses the reasoning item.
        Assert.Equal(1, fc.Focused);
    }

    [Fact]
    public void Expanding_the_streaming_reasoning_block_follows_the_bottom()   //expanding the streaming thought keeps the view at the live bottom
    {
        var model = new TranscriptModel("coder") { ReasoningCollapseDefault = true };
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var scroll = new ScrollController(model, index);
        var compositor = new ViewportCompositor(new RecordingSurface { Width = 80, Height = 6 }, index, new object(), glyphs: GlyphSet.Unicode);
        var fc = new FocusController(model, index, scroll, compositor);
        model.Append(TallProse(20));
        for (var i = 0; i < 10; i++) model.AppendOpenLine($"reasoning {i}", reasoning: true);   //the appended reasoning stays open and streams.
        var ri = (ReasoningItem)model.OpenItem!;
        Assert.True(ri.Streaming && ri.Collapsed);

        scroll.Home(80, 6);              //scrolling to the top detaches the follow mode.
        Assert.False(scroll.Following);
        fc.Prev(80, 6);                  //focus the streaming reasoning item at index one.
        Assert.Equal(1, fc.Focused);
        fc.ToggleFocused(80, 6);         //expanding the thought follows it live.
        Assert.False(ri.Collapsed);
        Assert.True(scroll.Following);   //the view re-attaches to the live bottom
    }

    [Fact]
    public void Next_past_the_last_returns_the_view_to_the_bottom()   //moving focus past the last item must return the view to the bottom.
    {
        var (fc, _, _, scroll) = NewFocus(6, TallProse(20), Reasoning("r"), TallProse(20));
        fc.Prev(80, 6);                 //focusing the off-screen reasoning detaches the follow to reveal it.
        Assert.False(scroll.Following);
        fc.Next(80, 6);                 //moving past the last collapsible clears focus and re-attaches the view to the bottom.
        Assert.Null(fc.Focused);
        Assert.True(scroll.Following);
    }

    [Fact]
    public void Prev_to_an_offscreen_item_scrolls_it_into_view()   //focusing an off-screen item must scroll it into view.
    {
        var (fc, _, index, scroll) = NewFocus(6, TallProse(20), Reasoning("r"), TallProse(20), Tool("t"));
        Assert.True(scroll.Following);                 //the view starts at the bottom, with the reasoning above the screen.
        fc.Prev(80, 6);                                //the first step focuses the tool at index three, visible at the bottom.
        fc.Prev(80, 6);                                //the next step focuses the reasoning at index one, off-screen.
        Assert.Equal(1, fc.Focused);
        Assert.False(scroll.Following);                //the follow mode detaches to reveal the focused item.
        Assert.True(ItemInWindow(scroll, index, 1, 80, 6));
    }

    [Fact]
    public void Expand_keeps_the_focused_tool_top_visible()   //expanding a focused tool must keep its top row visible.
    {
        var big = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line {i}"));
        var (fc, model, index, scroll) = NewFocus(8, Tool("read_file", big));
        fc.Prev(80, 8);
        fc.ToggleFocused(80, 8);                       //expand the result of forty lines.
        Assert.False(((ToolBlockItem)model.Items[0]).Collapsed);
        Assert.Equal(0, FirstRowWithin(scroll, index, 0, 80, 8));   //the tool's top row stays at the window top.
    }

    [Fact]
    public void Click_focus_with_reveal_false_does_not_scroll()   //a click focus must never move the viewport.
    {
        var (fc, _, _, scroll) = NewFocus(6, TallProse(20), Reasoning("r"), TallProse(20));
        Assert.True(scroll.Following);                  //the reasoning at index one sits off-screen above.
        fc.Focus(1, 80, 6, reveal: false);
        Assert.Equal(1, fc.Focused);
        Assert.True(scroll.Following);                  //the view is not scrolled and still follows the bottom.
    }

    [Fact]
    public void Focus_reveal_true_scrolls_the_offscreen_item_into_view()
    {
        var (fc, _, index, scroll) = NewFocus(6, TallProse(20), Reasoning("r"), TallProse(20));
        fc.Focus(1, 80, 6, reveal: true);
        Assert.False(scroll.Following);                 //revealing the item detaches the follow mode.
        Assert.True(ItemInWindow(scroll, index, 1, 80, 6));
    }

    [Fact]
    public void IsCollapsible_int_distinguishes_targets()
    {
        var (fc, _, _, _) = NewFocus(24,
            Prose("hi"), Reasoning("r"), Tool("full", "out"),
            new ToolBlockItem("cancelled", "a", "g", true, "coder") { Collapsed = true, FullResult = "" });
        Assert.False(fc.IsCollapsible(0, 80));   //prose is not a collapsible target.
        Assert.True(fc.IsCollapsible(1, 80));    //reasoning items are collapsible.
        Assert.True(fc.IsCollapsible(2, 80));    //a tool with a result is collapsible.
        Assert.False(fc.IsCollapsible(3, 80));   //a tool with an empty result is not a target.
        Assert.False(fc.IsCollapsible(99, 80));  //an out-of-range index answers false.
        Assert.False(fc.IsCollapsible(-1, 80));
    }
}
