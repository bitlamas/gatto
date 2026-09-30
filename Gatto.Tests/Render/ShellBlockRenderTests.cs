using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ShellBlockRenderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme T256 = new(new TermCaps(true, false));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private const string Script = "$s = @'\necho \"=== nightly job tree ===\"\nfind /srv/example/etl/jobs/nightly -name \"*.kjb\" | sort\nsystemctl is-active mariadb mysql 2>/dev/null\n'@; $s | ssh alpha \"bash -s\"";
    private const string Short = "curl.exe -s https://checkip.amazonaws.com";

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static string Result(int stdout, string? stderr = null, int exit = 0)
    {
        var lines = Enumerable.Range(1, stdout).Select(i => $"line {i}").ToList();
        var text = string.Join("\r\n", lines);
        if (stderr is not null) text += "\r\n" + ShellTool.StderrMarker + "\r\n" + stderr;
        if (exit != 0) text += "\r\n(exit code " + exit + ")";
        return text;
    }

    private static ShellBlockInput Input(string command, string result, int exit, string? painted = null) =>
        new(command, ShellOutput.Parse(result), result.Length, $"exit {exit}", painted ?? "painted", HasResult: true, "coder");

    private static ShellBlockLayout Closed(ShellBlockInput i, int width, GlyphSet? g = null, Theme? t = null) =>
        ShellBlockRender.Layout(i, ShellBlockState.Closed, width, t ?? T, g ?? U);

    private static ShellBlockLayout Open(ShellBlockInput i, int width, ShellView view = ShellView.Window, int top = 0) =>
        ShellBlockRender.Layout(i, new ShellBlockState(false, view, top, 0), width, T, U);

    [Fact]
    public void A_closed_call_is_two_rows_for_a_one_row_command_and_at_most_five_otherwise()
    {
        for (var width = 60; width <= 120; width++)
        {
            Assert.Equal(2, Closed(Input(Short, Result(3), 0), width).Rows.Count);
            Assert.InRange(Closed(Input(Script, Result(3), 0), width).Rows.Count, 3, 5);
        }
    }

    [Fact]
    public void The_one_row_rule_is_measured_against_the_width()
    {
        var edge = 2 + "shell".Length + 1 + Short.Length;
        Assert.Equal(0, Closed(Input(Short, "x", 0), edge).CommandTotal);
        Assert.True(Closed(Input(Short, "x", 0), edge - 1).CommandTotal > 0);
        Assert.Equal(2, Closed(Input("a\nb", "x", 0), 200).CommandTotal);
    }

    [Fact]
    public void The_command_window_rests_on_the_last_three_rows()
    {
        var l = Closed(Input(Script, Result(3), 0), 120);
        Assert.Equal(3, l.CommandShown);
        var shown = l.Rows.Skip(l.CommandFirst).Take(3).Select(r => Visible(r.Text).Trim()).ToList();
        Assert.Equal("'@; $s | ssh alpha \"bash -s\"", shown[^1]);
        Assert.Equal("systemctl is-active mariadb mysql 2>/dev/null", shown[^2]);
    }

    [Fact]
    public void The_header_counts_the_command_lines_only_above_one()
    {
        Assert.Contains($"shell {U.Dot} 5 lines", Visible(Closed(Input(Script, "x", 0), 120).Rows[0].Text), StringComparison.Ordinal);
        Assert.DoesNotContain("line", Visible(Closed(Input(new string('x', 200), "x", 0), 120).Rows[0].Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_zero_with_output_reads_ran_and_offers_expand()
    {
        var l = Closed(Input(Short, Result(3), 0), 120);
        var row = Visible(l.Rows[l.ResultRow].Text);
        Assert.Contains($"{U.Ok} ran {U.Dot} {ShellBlockRender.ExpandWords} {U.Dot} ~", row, StringComparison.Ordinal);
        Assert.Equal(ShellBlockRender.ExpandWords, row[l.LinkStart..l.LinkEnd]);
    }

    [Fact]
    public void Exit_zero_with_no_output_says_so_and_offers_nothing()
    {
        var l = Closed(Input(Short, "", 0), 120);
        Assert.EndsWith($"{U.Ok} ran {U.Dot} no output", Visible(l.Rows[l.ResultRow].Text), StringComparison.Ordinal);
        Assert.Equal(l.LinkStart, l.LinkEnd);
    }

    [Fact]
    public void A_non_zero_exit_with_stdout_reads_amber_ran_with_the_exit_and_the_last_stderr_line()
    {
        var l = Closed(Input(Script, Result(85, "bash: line 13: /dev/null\r: Permission denied", 1), 1), 120);
        var painted = l.Rows[l.ResultRow].Text;
        Assert.Contains($"{U.Ok} ran {U.Dot} exit 1 {U.Dot} bash: line 13: /dev/null: Permission denied {U.Dot} {ShellBlockRender.ExpandWords}", Visible(painted), StringComparison.Ordinal);
        Assert.Contains(T.Paint(U.Ok, Theme.Warn), painted, StringComparison.Ordinal);
        Assert.Contains(T.Paint("exit 1", Theme.Warn), painted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_zero_exit_with_no_stdout_is_a_red_failure()
    {
        var l = Closed(Input(Short, Result(0, "ssh: connect to host alpha port 22: Connection timed out", 255), 255), 120);
        var row = Visible(l.Rows[l.ResultRow].Text);
        Assert.Contains($"{U.Bad} exit 255 {U.Dot} ssh: connect to host alpha port 22: Connection timed out", row, StringComparison.Ordinal);
        Assert.DoesNotContain(ShellBlockRender.ExpandWords, row, StringComparison.Ordinal);
    }

    //the cap can cut the exit line away, so only the gloss can say exit 1
    [Fact]
    public void The_exit_code_comes_from_the_gloss_when_the_cap_cut_the_exit_line()
    {
        var text = new string('x', 60_000);
        var capped = text[..50_000] + "\r\n" + ToolArgs.TruncatedMarker;
        var l = Closed(Input(Short, capped, 1), 120);
        Assert.Contains("exit 1", Visible(l.Rows[l.ResultRow].Text), StringComparison.Ordinal);
    }

    [Fact]
    public void No_exit_gloss_paints_the_given_gloss()
    {
        var i = Input(Short, "command timed out after 1500ms", 0) with { RawGloss = null, PaintedGloss = "GIVEN" };
        var l = Closed(i, 120);
        Assert.EndsWith("GIVEN", Visible(l.Rows[l.ResultRow].Text), StringComparison.Ordinal);
    }

    [Fact]
    public void The_stderr_line_is_cut_first_so_the_link_stays_visible()
    {
        var i = Input(Short, Result(3, new string('e', 300), 1), 1);
        foreach (var width in new[] { 60, 80, 100 })
        {
            var l = Closed(i, width);
            var row = Visible(l.Rows[l.ResultRow].Text);
            Assert.Contains(ShellBlockRender.ExpandWords, row, StringComparison.Ordinal);
            Assert.True(UnicodeWidth.Of(row) <= width, $"width {width}: {row}");
        }
    }

    [Fact]
    public void The_live_block_is_the_header_and_the_command_tail_with_no_result_row()
    {
        var l = ShellBlockRender.Layout(ShellBlockInput.Live(Script, "coder", null), ShellBlockState.Closed, 120, T, U);
        Assert.Equal(-1, l.ResultRow);
        Assert.Equal(4, l.Rows.Count);
    }

    [Fact]
    public void Plain_words_drop_the_link_and_keep_the_facts()
    {
        var words = ShellBlockRender.PlainWords(ShellOutput.Parse(Result(3, "bad", 1)), 1, 40, U);
        Assert.StartsWith($"{U.Ok} ran {U.Dot} exit 1 {U.Dot} bad {U.Dot} ~", words, StringComparison.Ordinal);
        Assert.EndsWith(" tok", words, StringComparison.Ordinal);
        Assert.DoesNotContain("click", words, StringComparison.Ordinal);
    }

    [Fact]
    public void The_open_window_shows_five_numbered_lines_and_counts_the_rest()
    {
        var l = Open(Input(Script, Result(86), 0), 120);
        Assert.Equal(5, l.OutputShown);
        Assert.Equal($"{U.Box.Vertical}    1  line 1", Visible(l.Rows[l.OutputFirst].Text));
        Assert.Equal($"{U.Box.Vertical}   {U.Ellipsis} 81 more {U.Dot} show all", Visible(l.Rows[l.FooterRow].Text));
        Assert.Equal(ShellAction.ShowAll, l.FooterAction);
        Assert.Equal("show all", Visible(l.Rows[l.FooterRow].Text)[l.ActionStart..l.ActionEnd]);
    }

    [Fact]
    public void A_scrolled_window_counts_above_and_below_and_the_end()
    {
        var mid = Open(Input(Script, Result(20), 0), 120, top: 7);
        Assert.Equal($"{U.Box.Vertical}   {U.Up} 7 above {U.Dot} {U.Ellipsis} 8 more {U.Dot} show all", Visible(mid.Rows[mid.FooterRow].Text));
        var end = Open(Input(Script, Result(20), 0), 120, top: 99);
        Assert.StartsWith($"{U.Box.Vertical}   {U.Up} 15 above {U.Dot} end of output", Visible(end.Rows[end.FooterRow].Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_hidden_means_no_footer()
    {
        var l = Open(Input(Short, Result(4), 0), 120);
        Assert.Equal(-1, l.FooterRow);
        Assert.False(l.Hides);
    }

    [Fact]
    public void Stderr_lines_follow_stdout_in_red_and_harness_lines_carry_no_number()
    {
        var text = Result(4, "boom", 1).Replace("\r\n(exit code 1)", "\r\n" + ShellTool.PipeHeldNote + "\r\n(exit code 1)", StringComparison.Ordinal);
        var l = Open(Input(Short, text, 1), 120, ShellView.All);
        var rows = l.Rows.Skip(l.OutputFirst).Take(l.OutputShown).ToList();
        //6 rows is one past the window, so the numbers show (one cell wide)
        Assert.Equal($"{U.Box.Vertical}   5  boom", Visible(rows[4].Text));
        Assert.Contains(T.Paint("boom", Theme.Err), rows[4].Text, StringComparison.Ordinal);
        Assert.Equal($"{U.Box.Vertical}      " + ShellTool.PipeHeldNote, Visible(rows[5].Text));
    }

    [Fact]
    public void Show_all_lifts_both_windows_and_wraps_long_lines_as_continuations()
    {
        var long1 = new string('w', 150);
        var l = Open(Input(Script, long1 + "\r\nshort", 0), 80, ShellView.All);
        Assert.Equal(l.CommandTotal, l.CommandShown);
        var outRows = l.Rows.Skip(l.OutputFirst).Take(l.OutputShown).ToList();
        Assert.True(outRows[1].Continuation);
        Assert.Equal(outRows[0].LeadCells, outRows[1].PrefixCells);
        Assert.Equal(ShellAction.ShowLess, l.FooterAction);
        Assert.StartsWith($"{U.Box.Vertical}   show less", Visible(l.Rows[l.FooterRow].Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Show_all_of_one_output_row_draws_show_less_only_where_the_window_would_cut_it()
    {
        var line = "out " + new string('x', 56);
        var seen = new HashSet<bool>();
        for (var width = 50; width <= 100; width++)
        {
            var input = Input(Short, line, 0);
            var window = Open(input, width);
            var all = Open(input, width, ShellView.All);
            var cut = Visible(window.Rows[window.OutputFirst].Text).EndsWith(U.Ellipsis, StringComparison.Ordinal);
            seen.Add(cut);
            Assert.Equal(cut || window.FooterRow >= 0, all.FooterRow >= 0);
            if (all.FooterRow >= 0)
            {
                Assert.Equal($"{U.Box.Vertical}   show less {U.Dot} 1 line", Visible(all.Rows[all.FooterRow].Text));
                Assert.Equal(ShellAction.ShowLess, all.FooterAction);
            }
            else
            {
                Assert.Equal(ShellAction.None, all.FooterAction);
                Assert.Equal(all.ActionStart, all.ActionEnd);
                Assert.Equal($"{U.Box.Vertical}   {line}", Visible(all.Rows[^1].Text));
            }
        }
        Assert.Equal(new[] { false, true }, seen.Order());
        Assert.Equal(-1, Open(Input(Short, line, 0), 120, ShellView.All).FooterRow);
        Assert.True(Open(Input(Short, line, 0), 60, ShellView.All).FooterRow >= 0);
    }

    [Fact]
    public void Show_all_keeps_show_less_while_the_command_is_hidden_outside_the_window()
    {
        for (var width = 50; width <= 120; width++)
        {
            var window = Open(Input(Script, "short", 0), width);
            var all = Open(Input(Script, "short", 0), width, ShellView.All);
            Assert.True(window.CommandTotal > window.CommandShown, $"precondition at {width}: the window hides command rows");
            Assert.Equal($"{U.Box.Vertical}   show full command", Visible(window.Rows[window.FooterRow].Text));
            Assert.Equal($"{U.Box.Vertical}   show less {U.Dot} 1 line", Visible(all.Rows[all.FooterRow].Text));
            Assert.Equal(ShellAction.ShowLess, all.FooterAction);
        }
    }

    [Fact]
    public void Every_state_fits_every_width_with_every_glyph_set_and_colour_depth()
    {
        var cjk = "echo \u65e5\u672c\u8a9e\u306e\u884c " + new string('\u5b57', 40);
        var inputs = new[] { Input(Script, Result(30, "err", 1), 1), Input(cjk, cjk + "\r\n" + cjk, 0), Input(Short, "", 0) };
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        foreach (var t in new[] { T, T256 })
        foreach (var input in inputs)
        foreach (var state in new[] { ShellBlockState.Closed, new ShellBlockState(false, ShellView.Window, 0, 0), new ShellBlockState(false, ShellView.All, 0, 0) })
        for (var width = 30; width <= 120; width++)
        {
            var l = ShellBlockRender.Layout(input, state, width, t, g);
            Assert.All(l.Rows, r => Assert.True(UnicodeWidth.Of(Visible(r.Text)) <= width, $"width {width}: {Visible(r.Text)}"));
            if (g == GlyphSet.Ascii)
                Assert.All(l.Rows, r => Assert.True(Visible(r.Text).All(c => c < 128 || c > 0x2E80), $"a unicode glyph under ascii: {Visible(r.Text)}"));
        }
    }

    [Fact]
    public void A_window_narrower_than_the_chrome_still_fits()
    {
        for (var width = 8; width <= 20; width++)
        {
            var l = Open(Input(Script, Result(30, "err", 1), 1), width);
            Assert.All(l.Rows, r => Assert.True(UnicodeWidth.Of(Visible(r.Text)) <= width, $"width {width}: {Visible(r.Text)}"));
        }
    }

    //the sanitizer keeps the escape's inert tail, and Visible would strip a leaked escape whole, so assert on the tail
    [Fact]
    public void An_escape_sequence_in_output_never_reaches_a_row()
    {
        var esc = (char)0x1B + "[31mred" + (char)0x1B + "[0m";
        var l = Open(Input(Short, esc + "\r\n" + ShellTool.StderrMarker + "\r\n" + esc, 1), 120, ShellView.All);
        foreach (var r in l.Rows.Skip(l.OutputFirst).Take(l.OutputShown))
        {
            Assert.Contains("[31mred[0m", Visible(r.Text), StringComparison.Ordinal);
            Assert.DoesNotContain(((char)0x1B).ToString() + "[31m", r.Text, StringComparison.Ordinal);
        }
    }

    //read the raw row as well, Visible would strip a leaked escape whole
    [Fact]
    public void An_escape_in_the_last_stderr_line_never_reaches_the_result_row()
    {
        var esc = (char)0x1B + "[31mbad" + (char)0x1B + "[0m";
        var l = Closed(Input(Short, Result(2, esc, 1), 1), 120);
        var row = l.Rows[l.ResultRow].Text;
        Assert.Contains("[31mbad[0m", Visible(row), StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x1B + "[31m", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_stderr_lines_put_the_last_on_the_result_row_and_both_in_the_window()
    {
        var l = Open(Input(Short, Result(2, "first err\r\nlast err", 1), 1), 120);
        var result = Visible(l.Rows[l.ResultRow].Text);
        Assert.Contains($"exit 1 {U.Dot} last err {U.Dot}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("first err", result, StringComparison.Ordinal);
        var rows = l.Rows.Skip(l.OutputFirst).Take(l.OutputShown).Select(r => Visible(r.Text)).ToList();
        Assert.Equal($"{U.Box.Vertical}   first err", rows[2]);
        Assert.Equal($"{U.Box.Vertical}   last err", rows[3]);
    }

    [Fact]
    public void A_rewritten_result_with_an_exit_gloss_renders_as_stdout()
    {
        var l = Open(Input(Short, "a hook wrote this\nand this", 1), 120);
        Assert.Equal(2, l.OutputTotal);
        Assert.Contains($"{U.Ok} ran {U.Dot} exit 1", Visible(l.Rows[l.ResultRow].Text), StringComparison.Ordinal);
    }
}
