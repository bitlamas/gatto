using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

//a fenced block in assistant prose soft-wraps like every other transcript surface, so a long line is not clipped at the terminal width
public sealed class FenceWrapTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static int Cells(string row) => UnicodeWidth.Of(TermText.StripAnsiForWidth(row));

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private const string LongCode =
        "A long paragraph that a model decided to put inside a fenced code block, which used to render " +
        "as a single physical row and get clipped by the compositor with an ellipsis at the right edge.";

    [Fact]
    public void Fence_content_wraps_and_no_row_exceeds_the_width()
    {
        var item = new AssistantBlockItem(new[] { "```", LongCode, "```" }, "coder");
        var rows = item.Render(60, T, glyphs: GlyphSet.Unicode);

        Assert.True(rows.Count > 1, "the long fenced line must wrap into several rows");
        Assert.All(rows, r => Assert.True(Cells(r) <= 60, $"row too wide ({Cells(r)}): {Visible(r)}"));
        Assert.DoesNotContain(rows, r => Visible(r).Contains('…'));
    }

    [Fact]
    public void Fence_content_survives_wrapping_whole()
    {
        var item = new AssistantBlockItem(new[] { "```", LongCode, "```" }, "coder");
        //skip the empty fence row, strip the two-cell gutter and join with spaces, a wrap drops the space at the break
        var joined = string.Join(" ", item.Render(60, T, glyphs: GlyphSet.Unicode).Skip(1).Select(r => Visible(r).TrimEnd()[2..]));
        Assert.Equal(LongCode, joined);
    }

    [Fact]
    public void Fence_wrap_rows_are_tagged_as_continuations()
    {
        var item = new AssistantBlockItem(new[] { "```", LongCode, "```" }, "coder");
        var rows = item.Render(60, T, glyphs: GlyphSet.Unicode);
        var wraps = item.RowWraps(60, T, glyphs: GlyphSet.Unicode);

        Assert.Equal(rows.Count, wraps.Count);
        Assert.False(wraps[0].Continuation);                       //the marker row is not a continuation.
        Assert.Contains(wraps, w => w.Continuation);
        //a fence continuation has only the two-cell hang, the prose path has no rail
        Assert.All(wraps.Where(w => w.Continuation), w => Assert.Equal(GutterWrap.Hang.Length, w.PrefixCells));
    }

    [Fact]
    public void Fence_lang_tag_and_short_code_still_render_one_row_each()
    {
        var item = new AssistantBlockItem(new[] { "```python", "print('hi')", "```" }, "coder");
        var rows = item.Render(60, T, glyphs: GlyphSet.Unicode);

        Assert.Equal(2, rows.Count);                               //the tag row and the code line stay one row each.
        Assert.Contains("python", Visible(rows[0]));
        Assert.Contains("print('hi')", Visible(rows[1]));
    }

    [Fact]
    public void A_long_fence_language_line_wraps_too()
    {
        //the info string comes from the model and has no length limit, so it must stay inside the width too.
        var lang = string.Join(" ", Enumerable.Repeat("verylongtoken", 12));
        var item = new AssistantBlockItem(new[] { "```" + lang, "x", "```" }, "coder");
        var rows = item.Render(40, T, glyphs: GlyphSet.Unicode);

        Assert.All(rows, r => Assert.True(Cells(r) <= 40, $"row too wide ({Cells(r)}): {Visible(r)}"));
    }

    [Fact]
    public void Fence_rows_stay_sgr_self_contained()
    {
        //every wrapped row opens and closes the code styling again, the compositor may clip any single row at the window top
        var item = new AssistantBlockItem(new[] { "```", LongCode, "```" }, "coder");
        Assert.All(item.Render(60, T, glyphs: GlyphSet.Unicode), r => Assert.EndsWith(Ansi.Reset, r, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Degenerate_width_still_renders_one_row_per_line()
    {
        //with width minus two at or below zero, soft-wrap disables itself rather than looping per cell, so each source line stays one row.
        var item = new AssistantBlockItem(new[] { "```", LongCode, "```" }, "coder");
        Assert.Equal(2, item.Render(2, T, glyphs: GlyphSet.Unicode).Count);
    }

    [Fact]
    public void Hr_still_renders_as_a_single_row()
    {
        var item = new AssistantBlockItem(new[] { "---" }, "coder");
        Assert.Single(item.Render(60, T, glyphs: GlyphSet.Unicode));
    }
}
