using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class TranscriptItemTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    //a row is self-contained when no styling stays active at its end, so a clipped item inherits no colour from a clipped-off predecessor
    private static bool SgrSelfContained(string row)
    {
        var active = false;
        var i = 0;
        while ((i = row.IndexOf('\x1b', i)) >= 0)
        {
            if (i + 1 < row.Length && row[i + 1] == '[')
            {
                var k = i + 2;
                while (k < row.Length && !char.IsLetter(row[k])) k++;
                if (k < row.Length)
                {
                    var final = row[k];
                    var body = row.Substring(i + 2, k - (i + 2));
                    if (final == 'm')
                        active = !(body.Length == 0 || System.Array.Exists(body.Split(';'), p => p == "0"));
                    i = k + 1;
                    continue;
                }
            }
            i++;
        }
        return !active;
    }

    private static bool Contains(string s, string needle) => s.Contains(needle, System.StringComparison.Ordinal);

    [Fact]
    public void Reasoning_closed_and_collapsed_renders_one_body_row_with_duration()
    {
        //a bare item has no leading blank, so the collapsed render is a single row
        var it = new ReasoningItem(new[] { "line one", "line two", "line three" })
            { Collapsed = true, Streaming = false, Elapsed = System.TimeSpan.FromSeconds(12) };
        var rows = it.Render(80, T, glyphs: GlyphSet.Unicode);
        Assert.Single(rows);
        Assert.True(Contains(rows[0], "thought for 12s"));
        Assert.True(SgrSelfContained(rows[0]));
    }

    [Fact]
    public void Reasoning_streaming_and_collapsed_renders_the_capped_first_rows_preview()
    {
        var many = System.Linq.Enumerable.Range(0, 10).Select(i => $"reasoning line {i}").ToArray();
        var it = new ReasoningItem(many) { Collapsed = true, Streaming = true };
        var rows = it.Render(80, T, glyphs: GlyphSet.Unicode);
        Assert.Equal(ItemRender.ReasoningCap + 1, rows.Count);         //the cap of content rows plus the affordance row fixes the total.
        Assert.True(Contains(rows[0], "reasoning line 0"));
        Assert.DoesNotContain(rows, r => Contains(r, "reasoning line 9"));
        Assert.True(Contains(rows[^1], "Ctrl+R or click to expand"));
        Assert.DoesNotContain("click here", rows[^1], StringComparison.Ordinal);
        Assert.True(Contains(rows[^1], "10 lines"));                  //the live line count tells the reader the stream is still running.
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void Reasoning_expanded_renders_full_lines()
    {
        var it = new ReasoningItem(new[] { "line one", "line two" }) { Collapsed = false };
        Assert.True(it.Render(80, T, glyphs: GlyphSet.Unicode).Count >= 2);
    }

    //the hint names the row it sits on, a click on the body starts a selection so only the header row toggles

    [Fact]
    public void The_collapsed_summary_points_at_ITSELF_as_the_click_target()
    {
        var it = new ReasoningItem(new[] { "t" }) { Collapsed = true, Streaming = false, Elapsed = TimeSpan.FromSeconds(3), ClickHint = true };
        var row = Assert.Single(it.Render(80, T, glyphs: GlyphSet.Unicode));
        Assert.True(Contains(row, "click to expand"));
        Assert.DoesNotContain("click here", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_expanded_header_points_at_ITSELF_as_the_click_target()
    {
        var it = new ReasoningItem(new[] { "t" }) { Collapsed = false, Streaming = false, Elapsed = TimeSpan.FromSeconds(3), ClickHint = true };
        Assert.True(Contains(it.Render(80, T, glyphs: GlyphSet.Unicode)[0], "click to collapse"));
        Assert.DoesNotContain("click here", it.Render(80, T, glyphs: GlyphSet.Unicode)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_collapsed_is_two_rows_expanded_appends_full_result()
    {
        var collapsed = new ToolBlockItem("read_file", "path=x", "✓ read 2 lines", true, "coder")
            { Collapsed = true, FullResult = "alpha\nbeta" };
        Assert.Equal(2, collapsed.Render(80, T, glyphs: GlyphSet.Unicode).Count);

        var expanded = collapsed with { Collapsed = false };
        var rows = expanded.Render(80, T, glyphs: GlyphSet.Unicode);
        Assert.True(rows.Count > 2);
        Assert.True(Contains(rows[2], "alpha"));
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void Tool_expanded_with_empty_result_stays_two_rows()
    {
        var it = new ToolBlockItem("bash", "x", "✗ cancelled", true, "coder") { Collapsed = false, FullResult = "" };
        Assert.Equal(2, it.Render(80, T, glyphs: GlyphSet.Unicode).Count);   //an empty result gives nothing to expand into, so the block stays two rows.
    }

    [Theory]   //the LineIndex count must match the render count with wide glyphs, cjk text and fences
    [InlineData(true)]
    [InlineData(false)]
    public void LineIndex_count_matches_render_count_at_both_collapse_states(bool collapsed)
    {
        var model = new TranscriptModel("coder");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var reasoning = new ReasoningItem(new[] { "短い行です — CJKと英語", "plain" })
            { Collapsed = collapsed, Elapsed = System.TimeSpan.FromSeconds(5) };
        var tool = new ToolBlockItem("read_file", "path=x", "✓", true, "coder")
            { Collapsed = collapsed, FullResult = "```cs\nvar 変数 = \"日本語テキストが折り返す長さまで伸ばした行\";\n```" };
        model.Append(reasoning);
        model.Append(tool);
        foreach (var width in new[] { 20, 80 })
            foreach (var it in model.Items)
                Assert.Equal(it.Render(width, index.Theme, glyphs: GlyphSet.Unicode).Count, index.Count(it, width));
    }

    //the markers differ on purpose, so an edit that unifies them must fail here. the tool marker keeps the role tint, dimming it would demote every tool row.
    [Fact]
    public void Prose_is_the_filled_marker_and_a_tool_call_is_the_hollow_one()
    {
        var prose = new AssistantBlockItem(new[] { "here is the answer" }, "coder").Render(80, T, glyphs: GlyphSet.Unicode)[0];
        var tool = new ToolBlockItem("read_file", "path=a", "✓ ok", HasResult: true, "coder").Render(80, T, glyphs: GlyphSet.Unicode)[0];

        Assert.Contains(ItemRender.ProseMarkerOf(glyphs: GlyphSet.Unicode), prose, StringComparison.Ordinal);
        Assert.DoesNotContain(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), prose, StringComparison.Ordinal);

        Assert.Contains(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), tool, StringComparison.Ordinal);
        Assert.DoesNotContain(ItemRender.ProseMarkerOf(glyphs: GlyphSet.Unicode), tool, StringComparison.Ordinal);

        Assert.NotEqual(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), ItemRender.ProseMarkerOf(glyphs: GlyphSet.Unicode));
        var tint = Ansi.Fg(T.RoleTint("coder"), T.TrueColor);
        Assert.StartsWith(tint, prose, StringComparison.Ordinal);
        Assert.StartsWith(tint, tool, StringComparison.Ordinal);
    }

    private static ToolBlockItem Write(string content, string gloss = "✓ ok")
    {
        return new ToolBlockItem("write_file", "a.md", gloss, HasResult: true, "coder")
            { Collapsed = false, FullResult = "wrote it", WriteContent = content };
    }

    [Fact]
    public void A_write_shows_five_numbered_lines_and_counts_what_it_hides()
    {
        var content = string.Join("\n", Enumerable.Range(1, 232).Select(n => $"line {n}"));
        var rows = Write(content).Render(80, T, glyphs: GlyphSet.Unicode);

        //the count is the bullet and the gloss row, the window's five rows and its footer
        Assert.Equal(2 + 5 + 1, rows.Count);
        for (var n = 1; n <= 5; n++)
            Assert.True(Contains(rows[1 + n], $"line {n}"), $"row for line {n} missing");
        Assert.True(Contains(rows[^1], "227 more"));   //the footer counts hidden lines, so 232 total minus the 5 shown.
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    //the count comes from FilePreview.CountLines, which ends a line at the trailing newline. a split would invent a line and disagree with the prompt.
    [Fact]
    public void A_trailing_newline_does_not_invent_a_line()
    {
        var rows = Write("a\nb\nc\nd\ne\nf\n").Render(80, T, glyphs: GlyphSet.Unicode);   //six real lines, with a trailing newline.
        Assert.True(Contains(rows[^1], " 1 more"));
        Assert.False(Contains(rows[^1], "2 more"));
    }

    [Fact]
    public void A_write_shorter_than_the_cap_shows_no_hint_row()
    {
        var rows = Write("only\ntwo").Render(80, T, glyphs: GlyphSet.Unicode);
        Assert.Equal(2 + 2, rows.Count);
        Assert.False(Contains(rows[^1], "…"));
    }

    [Fact]
    public void A_non_write_tool_gets_no_preview()
    {
        var rows = new ToolBlockItem("read_file", "a.md", "✓ ok", HasResult: true, "coder")
            { Collapsed = true, FullResult = "body" }.Render(80, T, glyphs: GlyphSet.Unicode);
        Assert.Equal(2, rows.Count);
    }

    //the wide line must truncate, wrapping it would push the next tool call off screen. 40 is the width where a wrapping build blows past the row cap.
    [Fact]
    public void Preview_rows_truncate_rather_than_wrap()
    {
        var rows = Write(new string('x', 400) + "\nsecond\nthird").Render(40, T, glyphs: GlyphSet.Unicode);
        Assert.Equal(2 + 3 + 1, rows.Count);   //each previewed line must take exactly one row, even a very wide one, and the cut one brings the footer
        Assert.EndsWith("show all", Gatto.Terminal.TermText.StripAnsiForWidth(rows[^1]), StringComparison.Ordinal);
        Assert.All(rows, r => Assert.True(Gatto.Terminal.UnicodeWidth.Of(
            Gatto.Terminal.TermText.StripAnsiForWidth(r)) <= 40, $"row overflows 40 cells: {r}"));
    }

    //the row count has to grow with the preview rows, a mismatch misplaces every mouse click and selection below the block
    [Fact]
    public void RowWraps_still_matches_the_rendered_row_count_with_a_preview()
    {
        var item = Write(string.Join("\n", Enumerable.Range(1, 40).Select(n => $"line {n}")));
        foreach (var width in new[] { 40, 80 })
            Assert.Equal(item.Render(width, T, glyphs: GlyphSet.Unicode).Count, item.RowWraps(width, T, glyphs: GlyphSet.Unicode).Count);
    }

    [Fact]
    public void Prose_run_marks_the_first_row_and_hangs_the_rest_one_dot_per_run()
    {
        var item = new AssistantBlockItem(new[] { "first line", "", "second paragraph" }, "generalist");
        var rows = item.Render(40, T, glyphs: GlyphSet.Unicode);

        Assert.Contains("●", rows[0]);
        Assert.Equal("", rows[1]);
        Assert.StartsWith(GutterWrap.Hang, rows[2]);
        Assert.DoesNotContain("●", rows[2]);
        Assert.All(rows, r => Assert.True(SgrSelfContained(r), $"unbalanced SGR: {r}"));
    }

    [Fact]
    public void Prose_wraps_a_long_line_into_hang_indented_continuation_rows()
    {
        var item = new AssistantBlockItem(new[] { "one two three four five six seven eight nine ten" }, "generalist");
        var rows = item.Render(20, T, glyphs: GlyphSet.Unicode);   //twenty cells leave eighteen for text, which forces several rows.

        Assert.True(rows.Count > 1, "a long line must wrap");
        Assert.Contains("●", rows[0]);
        Assert.StartsWith(GutterWrap.Hang, rows[1]);
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void Cjk_line_renders_and_stays_self_contained()
    {
        var item = new AssistantBlockItem(new[] { "日本語のテキストは全角です" }, "generalist");
        var rows = item.Render(16, T, glyphs: GlyphSet.Unicode);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void Tool_block_renders_bullet_plus_gloss_and_omits_gloss_when_dangling()
    {
        var withResult = new ToolBlockItem("read_file", "path=a", "✓ ok", HasResult: true, "generalist").Render(60, T, glyphs: GlyphSet.Unicode);
        Assert.Equal(2, withResult.Count);
        Assert.Contains("○", withResult[0]);
        Assert.Contains("read_file", withResult[0]);
        Assert.Contains("⎿", withResult[1]);

        var dangling = new ToolBlockItem("shell", "", "", HasResult: false, "generalist").Render(60, T, glyphs: GlyphSet.Unicode);
        Assert.Single(dangling);                              //a call with no result yet must not render a gloss row.
        Assert.All(withResult, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void User_echo_bands_the_first_row_with_the_prompt_glyph()
    {
        var rows = new UserEchoItem(new[] { "hello there" }).Render(40, T, glyphs: GlyphSet.Unicode);
        Assert.Single(rows);
        Assert.Contains("❯", rows[0]);
        Assert.EndsWith(Ansi.Reset, rows[0], StringComparison.Ordinal);                 //the band must close at the row end.
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void User_echo_renders_fenced_code_as_literal_gutter_block()
    {
        var rows = new UserEchoItem(new[]
        {
            "look:",
            "```python",
            "keep **these** stars",
            "```",
            "done",
        }).Render(60, T, glyphs: GlyphSet.Unicode);
        var visible = rows.Select(TermText.StripAnsiForWidth).ToList();

        Assert.DoesNotContain(visible, v => v.Contains("```"));
        //a language tag renders as its own dim row under the gutter
        Assert.Contains(visible, v => v.Contains("│ python"));
        //code lines render literally under a gutter, so **these** keeps its asterisks and no inline markdown runs inside a fence
        var code = Assert.Single(visible, v => v.Contains("these"));
        Assert.Contains("│", code);
        Assert.Contains("**these**", code);
        //prose outside the fence keeps its normal band, the first row shows ❯ and none shows the gutter
        Assert.Contains(visible, v => v.Contains("❯") && v.Contains("look:"));
        Assert.Contains(visible, v => v.Contains("done") && !v.Contains("│"));
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void User_echo_fence_containing_a_lang_example_does_not_close_early()
    {
        //a fence line with a language tag inside an open block is content, a closer has no info string. only a bare fence line closes the block.
        var rows = new UserEchoItem(new[]
        {
            "```md",
            "example:",
            "```bash",          //this line is fence content and must not close the outer block.
            "echo hi",
            "```",              //the bare fence line is the real closer.
            "after",
        }).Render(60, T, glyphs: GlyphSet.Unicode);
        var visible = rows.Select(TermText.StripAnsiForWidth).ToList();

        //the bash fence line renders as content with literal backticks, so the block stayed open
        Assert.Contains(visible, v => v.Contains("│") && v.Contains("```bash"));
        //the echo hi line is still inside the block, so it renders as guttered code
        Assert.Contains(visible, v => v.Contains("│") && v.Contains("echo hi"));
        //the bare closer is consumed, so the after line bands as prose with no gutter
        Assert.Contains(visible, v => v.Contains("after") && !v.Contains("│"));
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void User_echo_bare_fence_has_no_language_tag_and_still_gutters_code()
    {
        var rows = new UserEchoItem(new[] { "```", "code line", "```" }).Render(40, T, glyphs: GlyphSet.Unicode);
        var visible = rows.Select(TermText.StripAnsiForWidth).ToList();
        Assert.DoesNotContain(visible, v => v.Contains("```"));
        //with no language named there is no tag row, so the only rendered row is the guttered code line.
        var code = Assert.Single(visible);
        Assert.Contains("│", code);
        Assert.Contains("code line", code);
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void Assistant_output_bold_and_headings_follow_the_role_tint()
    {
        var oracle = new AssistantBlockItem(new[] { "# Title", "a **strong** word" }, "oracle").Render(60, T, glyphs: GlyphSet.Unicode);
        var joined = string.Concat(oracle);
        Assert.Contains(Ansi.Fg(T.Map(Theme.RoleOracle), T.TrueColor), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Fg(T.Map(Theme.Accent), T.TrueColor), joined, StringComparison.Ordinal);
        Assert.All(oracle, r => Assert.True(SgrSelfContained(r)));

        //the generalist tint is the accent, so generalist output keeps the accent colour.
        var generalist = string.Concat(new AssistantBlockItem(new[] { "a **strong** word" }, "generalist").Render(60, T, glyphs: GlyphSet.Unicode));
        Assert.Contains(Ansi.Fg(T.Map(Theme.Accent), T.TrueColor), generalist, StringComparison.Ordinal);
    }

    [Fact]
    public void User_echo_bold_stays_legacy_bright_not_role_tinted()
    {
        //the role tint applies to output only, a user message keeps the legacy bright foreground
        var rows = string.Concat(new UserEchoItem(new[] { "my **strong** word" }).Render(60, T, glyphs: GlyphSet.Unicode));
        Assert.DoesNotContain(Ansi.Fg(T.Map(Theme.RoleOracle), T.TrueColor), rows, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Fg(T.Map(Theme.RoleCoder), T.TrueColor), rows, StringComparison.Ordinal);
    }

    [Fact]
    public void System_line_carries_the_hash_marker()
    {
        var rows = new SystemLineItem("context 85% full", After: null).Render(40, T, glyphs: GlyphSet.Unicode);
        Assert.Contains("♯", rows[0]);
        Assert.All(rows, r => Assert.True(SgrSelfContained(r)));
    }

    [Fact]
    public void Completion_is_a_single_dim_row_truncated_to_width()
    {
        var rows = new CompletionItem("⟨＾･ω･＾⟩ purred for 1m 27s · ↓ 42", After: null).Render(24, T, glyphs: GlyphSet.Unicode);
        Assert.Single(rows);
        Assert.True(SgrSelfContained(rows[0]));
        Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(rows[0])) <= 24);
    }

    [Fact]
    public void A_completion_with_a_wide_time_of_day_stays_one_row_at_every_width()
    {
        var item = new CompletionItem("(^· ·^)⟆ purred for 1h 2m 46s (오후 2:21) · ↓ 46.1k · Σ 85.6k", After: null);   //a locale can put two-cell characters in the time, so the cut must count cells
        for (var width = 10; width <= 100; width++)
        {
            var rows = item.Render(width, T, glyphs: GlyphSet.Unicode);
            Assert.Single(rows);
            Assert.True(SgrSelfContained(rows[0]));
            Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(rows[0])) <= width, $"width {width}");
        }
    }

    [Fact]
    public void Leading_blank_prepends_exactly_one_separator_row()
    {
        var item = new AssistantBlockItem(new[] { "content" }, "generalist") { LeadingBlank = true };
        var rows = item.Render(40, T, glyphs: GlyphSet.Unicode);
        Assert.Equal("", rows[0]);
        Assert.Contains("content", rows[1]);
    }

    [Fact]
    public void System_line_render_strips_hostile_control_bytes()
    {
        //raw-text kinds sanitize in render, replay rebuilds them from raw records
        var joined = string.Concat(new SystemLineItem("evil\x1b]0;pwned\x07 tail", After: null).Render(60, T, glyphs: GlyphSet.Unicode));
        //check characters rather than string literals, so the check can't re-arm a greedy escape parse in the code under test
        Assert.False(joined.Contains('\x07'), "BEL stripped ⇒ the C0 filter ran ⇒ the injection's ESC is gone too");
        Assert.False(TermText.StripAnsiForWidth(joined).Contains('\x1b'), "no raw ESC remains in the visible text");
        Assert.Contains("evil", joined);
    }

    [Fact]
    public void Event_items_carry_a_reference_anchor_not_an_index()
    {
        var msg = new Gatto.Core.Client.ChatMessage("tool", "ok", ToolCallId: "c1");
        var echo = new CommandEchoItem(new[] { "  allowed shell" }, After: msg);
        Assert.Same(msg, echo.After);
        Assert.Equal(new[] { "  allowed shell" }, echo.Render(80, T, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void Error_gloss_shows_the_error_not_the_stderr_separator()
    {
        //an error result with empty stdout opens with the stderr marker, so the gloss must skip past it to the message
        var shellResult = "\r\n" + Gatto.Core.Tools.ShellTool.StderrMarker
            + "\r\nThe token '&&' is not a valid statement separator in this version.";

        var gloss = Gatto.Terminal.TermText.StripAnsiForWidth(
            ItemRender.ToolGloss(new Gatto.Core.Tools.ToolResult(shellResult, IsError: true), T, glyphs: GlyphSet.Unicode));

        Assert.Contains("'&&' is not a valid statement separator", gloss);
        Assert.DoesNotContain("--- stderr ---", gloss);
    }
}
