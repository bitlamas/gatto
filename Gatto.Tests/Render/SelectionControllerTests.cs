using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

//a selection survives pure-chrome repaints and dies on reflow or any mutation of a touched item. reset, item-removed and mid-drag invalidation kill it too
public sealed class SelectionControllerTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (TranscriptModel, SelectionController) Fresh(string role = "generalist")
    {
        var m = new TranscriptModel(role);
        return (m, new SelectionController(m));
    }

    [Fact]
    public void Bare_click_leaves_no_selection()   //press and release on the same cell must leave no phantom selection.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("hello world", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 3), DragKind.Char, 80);
        sel.EndDrag();
        Assert.Null(sel.Current);
    }

    [Fact]
    public void ExtendTo_after_a_mid_drag_invalidation_is_a_noop()
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("streaming", false);
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 4), 80);
        m.AppendOpenLine("grows", false);                //growing the open item bumps its revision mid-drag.
        sel.Clear();                                     //the next paint clears the current selection, the swept item is no longer valid
        sel.ExtendTo(new SelCell(0, 0, 8), 80);          //the next drag move must not resurrect an invalidated selection.
        Assert.Null(sel.Current);
    }

    [Fact]
    public void OnReset_clears_even_when_fresh_items_reuse_the_old_index()   //a stale selection over reused indices would copy the wrong text in silence.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("old text", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 3), 80);
        Assert.NotNull(sel.Current);
        m.Reset();                                  //after a reset, new items sit at the same indices as the old ones.
        Assert.Null(sel.Current);
    }

    [Fact]
    public void OnItemRemoved_clears()
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("real", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 3), 80);
        m.AppendOpenLine("   ", false); m.CloseOpen();   //appending whitespace-only text drops the item and fires the item-removed path.
        Assert.Null(sel.Current);
    }

    [Fact]
    public void Same_width_no_rev_change_repaint_keeps_the_span()   //a repaint from the activity ticker must keep the selection.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("stable scrollback line", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 5), 80);
        sel.OnWidthChanged(80);
        Assert.True(sel.StillValid(m.Items));            //a chrome repaint at the same width must not clear the span.
        Assert.NotNull(sel.Current);
    }

    [Fact]
    public void Width_change_clears()   //a width change reflows every row, so the selection must clear.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("x", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 1), 80);
        sel.OnWidthChanged(100);
        Assert.Null(sel.Current);
    }

    [Fact]
    public void Untouched_item_Rev_bump_does_not_invalidate_but_endpoint_does()
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("first", false); m.CloseOpen();
        m.AppendOpenLine("second", false);               //item one sits open, and the span over item zero does not touch it.
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 4), 80);
        m.AppendOpenLine("more", false);                 //growing an untouched item must keep the selection valid.
        Assert.True(sel.StillValid(m.Items));
        m.Items[0].Collapsed = !m.Items[0].Collapsed;    //bumping an endpoint's revision must invalidate the selection.
        Assert.False(sel.StillValid(m.Items));
    }

    [Fact]
    public void MIDDLE_touched_item_Rev_bump_invalidates()   //a revision bump on any touched item invalidates the span, middle items included
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("i0", false); m.CloseOpen();
        m.AppendOpenLine("i1", false); m.CloseOpen();
        m.AppendOpenLine("i2", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(2, 0, 1), 80);                    //the span runs from item zero to item two, so item one is touched in the middle.
        Assert.True(sel.StillValid(m.Items));
        m.Items[1].Collapsed = !m.Items[1].Collapsed;             //toggling a middle item mid-selection must invalidate the span.
        Assert.False(sel.StillValid(m.Items));
    }

    [Fact]
    public void StillValid_belt_rejects_out_of_range_index()
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("a", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(5, 0, 0), DragKind.Char, 80);   //an out-of-range index is rejected as a defensive check.
        sel.ExtendTo(new SelCell(5, 0, 1), 80);
        Assert.False(sel.StillValid(m.Items));
    }

    [Fact]
    public void CopyText_of_a_full_row_equals_its_visible_text()   //copy must equal the visible row text, marker glyph included.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("hello world", false); m.CloseOpen();
        var plain = TermText.StripAnsiForWidth(m.Items[0].Render(80, T, glyphs: GlyphSet.Unicode)[0]);
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 999), 80);
        Assert.Equal(plain, sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void CopyText_slices_a_mid_row_cell_range()
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("hello world", false); m.CloseOpen();
        //the marker takes cells zero and one, so a drag from cell two to cell six copies "hello"
        sel.BeginDrag(new SelCell(0, 0, 2), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 6), 80);
        Assert.Equal("hello", sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void CopyText_endpoints_are_inclusive_both_directions()   //the endpoint cell must be part of the copy in either drag direction.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("hello world", false); m.CloseOpen();
        sel.BeginDrag(new SelCell(0, 0, 2), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 4), 80);
        Assert.Equal("hel", sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode));
        sel.BeginDrag(new SelCell(0, 0, 4), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, 0, 2), 80);
        Assert.Equal("hel", sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void CopyText_of_a_collapsed_tool_is_its_summary_not_FullResult()
    {
        var (m, sel) = Fresh();
        m.Append(new ToolBlockItem("read_file", "x", "3 lines", true, "generalist")
        { Collapsed = true, FullResult = "SECRET FULL BODY" });
        var rows = m.Items[0].Render(80, T, glyphs: GlyphSet.Unicode);
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, rows.Count - 1, 999), 80);
        var text = sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode);
        Assert.NotNull(text);
        Assert.DoesNotContain("SECRET FULL BODY", text);
    }

    [Fact]
    public void CopyText_null_when_no_selection()
    {
        var (_, sel) = Fresh();
        Assert.Null(sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void CopyText_preserves_an_intra_item_blank_line()   //paragraphs must not jam together in the copy.
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("para one", false);
        m.AppendOpenLine("", false);          //an empty line between paragraphs renders as a blank row.
        m.AppendOpenLine("para two", false);
        m.CloseOpen();
        var rows = m.Items[0].Render(80, T, glyphs: GlyphSet.Unicode);
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(0, rows.Count - 1, 999), 80);
        var lines = sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode)!.Split('\n');
        Assert.Equal(rows.Count, lines.Length);                   //every rendered row must produce one copy line.
        Assert.Contains(lines, l => l.Length == 0);
    }

    [Fact]
    public void CopyText_preserves_the_inter_item_separator()
    {
        var (m, sel) = Fresh();
        m.AppendOpenLine("first", false); m.CloseOpen();
        m.AppendOpenLine("second", false); m.CloseOpen();   //the second item has a leading blank, so its render starts with an empty row.
        var rows1 = m.Items[1].Render(80, T, glyphs: GlyphSet.Unicode);
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(1, rows1.Count - 1, 999), 80);
        var lines = sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode)!.Split('\n');
        Assert.Contains(lines, l => l.Length == 0);
    }

    [Fact]
    public void CopyText_rejoins_a_soft_wrapped_line_with_one_space()
    {
        var (m, sel) = Fresh("coder");
        m.AppendOpenLine("alpha beta gamma delta epsilon zeta", false); m.CloseOpen();
        const int width = 18;   //eighteen cells force the wrap.
        var rows = m.Items[0].Render(width, T, glyphs: GlyphSet.Unicode);
        Assert.True(rows.Count >= 2, "precondition: the line wrapped");
        var lastRel = rows.Count - 1;
        var lastCells = UnicodeWidth.Of(TermText.StripAnsiForWidth(rows[lastRel])) - 1;
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Line, width);
        sel.ExtendTo(new SelCell(0, lastRel, System.Math.Max(0, lastCells)), width);
        var copied = sel.CopyText(width, T, null, glyphs: GlyphSet.Unicode);
        Assert.DoesNotContain('\n', copied!);
        Assert.Contains("gamma delta", copied);      //the wrap rejoins with exactly one space, and the gutter is dropped.
        Assert.DoesNotContain("gamma  ", copied);    //the continuation hang must not add extra spaces at the join.
    }

    [Fact]
    public void CopyText_rejoin_does_not_bleed_across_items()
    {
        var (m, sel) = Fresh("coder");
        m.AppendOpenLine("first", false); m.CloseOpen();
        m.AppendOpenLine("second", false); m.CloseOpen();
        var rows1 = m.Items[1].Render(80, T, glyphs: GlyphSet.Unicode);
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, 80);
        sel.ExtendTo(new SelCell(1, rows1.Count - 1, 999), 80);
        var copied = sel.CopyText(80, T, null, glyphs: GlyphSet.Unicode)!;
        Assert.Contains('\n', copied);   //row zero is never a continuation, so two items copy on separate lines
    }

    //drop the whole wrap at the rejoin, keeping the list indent leaves runs of extra spaces in pasted text
    [Fact]
    public void CopyText_rejoin_drops_a_list_continuation_indent()
    {
        var (m, sel) = Fresh("coder");
        m.AppendOpenLine("3. State machines or state transitions — you're thinking about when a system leaves one state", false);
        m.CloseOpen();
        const int width = 34;
        var rows = m.Items[0].Render(width, T, glyphs: GlyphSet.Unicode);
        Assert.True(rows.Count >= 2, "precondition: the list item wrapped");
        var lastRel = rows.Count - 1;
        var lastCells = UnicodeWidth.Of(TermText.StripAnsiForWidth(rows[lastRel])) - 1;
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Line, width);
        sel.ExtendTo(new SelCell(0, lastRel, System.Math.Max(0, lastCells)), width);
        var copied = sel.CopyText(width, T, null, glyphs: GlyphSet.Unicode)!;
        Assert.DoesNotContain('\n', copied);
        Assert.DoesNotContain("  ", copied);   //two spaces in a row would mean the list indent survived the rejoin
    }

    [Fact]
    public void CopyText_rejoin_head_in_the_gutter_adds_no_trailing_space()
    {
        var (m, sel) = Fresh("coder");
        m.AppendOpenLine("alpha beta gamma delta epsilon zeta", false); m.CloseOpen();
        const int width = 18;
        var rows = m.Items[0].Render(width, T, glyphs: GlyphSet.Unicode);
        Assert.True(rows.Count >= 2, "precondition: wrapped");
        //the drag head sits in the 2-cell hang, so the continuation row contributes nothing
        sel.BeginDrag(new SelCell(0, 0, 0), DragKind.Char, width);
        sel.ExtendTo(new SelCell(0, 1, 0), width);
        var copied = sel.CopyText(width, T, null, glyphs: GlyphSet.Unicode)!;
        Assert.DoesNotContain('\n', copied);
        Assert.False(copied.EndsWith(" "), "no trailing space when the head lands in the continuation gutter");
    }
}
