using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Render;

public class RowContinuationsTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static IReadOnlyList<RowWrap> Wraps(TranscriptItem item, int width) =>
        item.RowWraps(width, T, glyphs: GlyphSet.Unicode);
    private static IReadOnlyList<bool> Conts(TranscriptItem item, int width) =>
        item.RowWraps(width, T, glyphs: GlyphSet.Unicode).Select(x => x.Continuation).ToList();

    private static IEnumerable<TranscriptItem> AllKinds() => new TranscriptItem[]
    {
        new AssistantBlockItem(new[] { "a short line", "another paragraph line that is quite long so it wraps at narrow widths for sure" }, "coder"),
        new ToolBlockItem("read_file", "path=x", "✓ ok", true, "coder"),
        new ToolBlockItem("read_file", "path=x", "✓ ok", true, "coder") { Collapsed = false, FullResult = "line one\nline two that is long enough to wrap somewhere" },
        new UserEchoItem(new[] { "hello world this is a fairly long user line that should wrap somewhere" }),
        new ReasoningItem(new[] { "thinking about a long thing that wraps at narrow widths for sure" }) { Streaming = false, Collapsed = false, Elapsed = System.TimeSpan.FromSeconds(3) },
        new SystemLineItem("a system warning long enough to wrap at a narrow width for sure", null),
        new SessionLeadItem("previous session summary that is long enough to wrap", null),
        new CompletionItem("purred for 3s", null),
        new CommandEchoItem(new[] { "allow? y/n" }, null),
    };

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void RowWraps_count_matches_Render_for_every_kind(int width)
    {
        foreach (var item in AllKinds())
            Assert.Equal(item.Render(width, T, glyphs: GlyphSet.Unicode).Count, item.RowWraps(width, T, glyphs: GlyphSet.Unicode).Count);
    }

    [Fact]
    public void LeadingBlank_row_is_false()
    {
        var item = new AssistantBlockItem(new[] { "x" }, "coder") { LeadingBlank = true };
        var flags = Conts(item, 80);
        Assert.Equal(item.Render(80, T, glyphs: GlyphSet.Unicode).Count, flags.Count);
        Assert.False(flags[0]);   //the leading separator row is not a continuation.
    }

    [Fact]
    public void AssistantBlock_tags_wrapped_prose_and_blank_paragraph()
    {
        var item = new AssistantBlockItem(new[] { "short", "", "word word word word word word word word word word" }, "coder");
        var rows = item.Render(30, T, glyphs: GlyphSet.Unicode);
        var flags = Conts(item, 30);
        Assert.Equal(rows.Count, flags.Count);
        Assert.False(flags[0]);
        Assert.False(flags.First());     //the first rendered row is never flagged as a continuation.
        Assert.Contains(true, flags);
    }

    //a wrapped list row needs the 2-cell hang plus the list indent, and reporting less pastes extra spaces into the rejoined text
    [Fact]
    public void Wrapped_list_item_reports_hang_plus_the_list_continuation_indent()
    {
        var item = new AssistantBlockItem(
            new[] { "3. State machines or state transitions — you're thinking about when a system leaves one state" }, "coder");
        var wraps = Wraps(item, 34);
        var cont = wraps.FirstOrDefault(w => w.Continuation);
        Assert.True(wraps.Any(w => w.Continuation), "precondition: the list item wrapped");
        //the prefix must exceed the bare 2-cell hang, the list indent is part of the chrome
        Assert.True(cont.PrefixCells > 2,
            $"a wrapped list continuation must report hang + list indent, got {cont.PrefixCells}");
    }

    [Fact]
    public void Non_wrapping_rows_report_zero_prefix()
    {
        var item = new AssistantBlockItem(new[] { "short" }, "coder");
        Assert.All(Wraps(item, 80), w => Assert.Equal(0, w.PrefixCells));
    }

    [Fact]
    public void SystemLine_tags_wrapped_line()
    {
        var item = new SystemLineItem("warn warn warn warn warn warn warn warn warn warn warn", null);
        var rows = item.Render(24, T, glyphs: GlyphSet.Unicode);
        var flags = Conts(item, 24);
        Assert.Equal(rows.Count, flags.Count);
        Assert.False(flags[0]);
        Assert.Contains(true, flags);
    }

    [Fact]
    public void Reasoning_expanded_header_false_body_wraps()
    {
        var item = new ReasoningItem(new[] { "long reasoning line that wraps at a narrow width here for sure" })
            { Streaming = false, Collapsed = false, Elapsed = System.TimeSpan.FromSeconds(5) };
        var rows = item.Render(24, T, glyphs: GlyphSet.Unicode);
        var flags = Conts(item, 24);
        Assert.Equal(rows.Count, flags.Count);
        Assert.False(flags[0]);
        Assert.Contains(true, flags);
    }

    [Fact]
    public void SessionLead_wraps()
    {
        var item = new SessionLeadItem("previous session summary long enough to wrap at a narrow width for sure", null);
        var rows = item.Render(24, T, glyphs: GlyphSet.Unicode);
        var flags = Conts(item, 24);
        Assert.Equal(rows.Count, flags.Count);
        Assert.False(flags[0]);
        Assert.Contains(true, flags);
    }

    [Fact]
    public void ToolBlock_collapsed_all_false_expanded_tags_result_lines()
    {
        var collapsed = new ToolBlockItem("shell", "x", "✓ ok", true, "coder");
        Assert.DoesNotContain(true, Conts(collapsed, 80));   //a collapsed block renders two single rows, so no row is flagged as a continuation.

        var expanded = new ToolBlockItem("shell", "x", "✓ ok", true, "coder")
            { Collapsed = false, View = ShellView.All, FullResult = "one\nthis result line is long enough to wrap at a narrow width for sure" };   //the window cuts a long row, show all wraps it
        var rows = expanded.Render(24, T, glyphs: GlyphSet.Unicode);
        var flags = Conts(expanded, 24);
        Assert.Equal(rows.Count, flags.Count);
        Assert.False(flags[0]);
        Assert.False(flags[1]);
        Assert.Contains(true, flags);
    }

    [Fact]
    public void UserEcho_prose_wraps_and_fenced_code_line_tags_continuations()
    {
        var prose = new UserEchoItem(new[] { "this user prose line is long enough to wrap at a narrow width for sure" });
        var pf = Conts(prose, 24);
        Assert.Equal(prose.Render(24, T, glyphs: GlyphSet.Unicode).Count, pf.Count);
        Assert.False(pf[0]);
        Assert.Contains(true, pf);

        var code = new UserEchoItem(new[] { "```", "a_very_long_code_line_that_should_wrap_at_a_narrow_width_here", "```" });
        var cw = Wraps(code, 24);
        Assert.Equal(code.Render(24, T, glyphs: GlyphSet.Unicode).Count, cw.Count);
        Assert.False(cw[0].Continuation);
        Assert.Contains(cw, w => w.Continuation);
        //a code continuation's prefix covers the hang plus the rail, and reporting less leaves rail residue in the rejoined text
        Assert.All(cw.Where(w => w.Continuation), w => Assert.True(w.PrefixCells >= 4));
    }
}
