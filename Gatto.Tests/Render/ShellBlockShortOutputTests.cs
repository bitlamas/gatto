using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ShellBlockShortOutputTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private const string Script = "$s = @'\necho \"=== nightly job tree ===\"\nfind /srv/example/etl/jobs/nightly -name \"*.kjb\" | sort\nsystemctl is-active mariadb mysql 2>/dev/null\n'@; $s | ssh alpha \"bash -s\"";
    private const string Short = "python gate_ask.py";

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static string Lines(int n) => string.Join("\r\n", Enumerable.Range(1, n).Select(i => $"line {i}"));

    private static string WithStderr(string stdout, params string[] stderr) =>
        stdout + "\r\n" + ShellTool.StderrMarker + "\r\n" + string.Join("\r\n", stderr);

    private static ShellBlockInput Input(string command, string result, int exit = 0) =>
        new(command, ShellOutput.Parse(result), result.Length, $"exit {exit}", "painted", HasResult: true, "coder");

    private static ShellBlockLayout Closed(ShellBlockInput i, int width, GlyphSet? g = null) =>
        ShellBlockRender.Layout(i, ShellBlockState.Closed, width, T, g ?? U);

    private static ShellBlockLayout Open(ShellBlockInput i, int width, ShellView view = ShellView.Window) =>
        ShellBlockRender.Layout(i, new ShellBlockState(false, view, 0, 0), width, T, U);

    private static string ResultRow(ShellBlockLayout l) => Visible(l.Rows[l.ResultRow].Text);

    //one line of output
    [Fact]
    public void One_line_of_output_sits_on_the_result_row_with_no_link()
    {
        var l = Closed(Input(Short, "written"), 120);
        Assert.Equal(2, l.Rows.Count);
        Assert.Equal($"  {U.Elbow} {U.Ok} ran {U.Dot} written {U.Dot} ~1 tok", ResultRow(l));
        Assert.Equal(l.LinkStart, l.LinkEnd);
        Assert.Contains(T.Paint("written", Theme.CodeBlockFg), l.Rows[l.ResultRow].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_too_long_for_the_row_goes_back_behind_the_link()
    {
        var line = string.Join(' ', Enumerable.Range(0, 12).Select(i => $"word{i:00}"));
        var seen = new HashSet<bool>();
        for (var width = 30; width <= 140; width++)
        {
            var l = Closed(Input(Short, line), width);
            var row = ResultRow(l);
            Assert.True(UnicodeWidth.Of(row) <= width, $"row wider than {width}");
            var whole = row.Contains(line, StringComparison.Ordinal);
            seen.Add(whole);
            //the row holds the whole line or the link, a cut line would pass for the full output
            if (whole) Assert.Equal(l.LinkStart, l.LinkEnd);
            else Assert.DoesNotContain("word00", row, StringComparison.Ordinal);
            if (!whole && width >= 60) Assert.Equal(ShellBlockRender.ExpandWords, row[l.LinkStart..l.LinkEnd]);
        }
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void The_link_stays_beside_the_line_when_the_command_has_hidden_rows()
    {
        var l = Closed(Input(Script, "written"), 120);
        var row = ResultRow(l);
        Assert.Contains($"ran {U.Dot} written {U.Dot} {ShellBlockRender.ExpandWords} {U.Dot} ~", row, StringComparison.Ordinal);
        Assert.Equal(ShellBlockRender.ExpandWords, row[l.LinkStart..l.LinkEnd]);
    }

    [Fact]
    public void An_open_block_keeps_the_line_in_the_window_and_the_link_on_the_row()
    {
        var l = Open(Input(Short, "written"), 120);
        var row = ResultRow(l);
        Assert.DoesNotContain("written", row, StringComparison.Ordinal);
        Assert.Equal(ShellBlockRender.CollapseWords, row[l.LinkStart..l.LinkEnd]);
        Assert.Equal($"{U.Box.Vertical}   written", Visible(l.Rows[l.OutputFirst].Text));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Two_lines_or_more_stay_behind_the_link(int n)
    {
        var l = Closed(Input(Short, Lines(n)), 120);
        var row = ResultRow(l);
        Assert.DoesNotContain("line 1", row, StringComparison.Ordinal);
        Assert.Equal(ShellBlockRender.ExpandWords, row[l.LinkStart..l.LinkEnd]);
    }

    [Fact]
    public void A_failed_command_keeps_its_one_line_behind_the_link()
    {
        var l = Closed(Input(Short, "written\r\n(exit code 1)", 1), 120);
        Assert.DoesNotContain("written", ResultRow(l), StringComparison.Ordinal);
        Assert.Contains("exit 1", ResultRow(l), StringComparison.Ordinal);
    }

    [Fact]
    public void An_escape_in_the_one_line_never_reaches_the_row()
    {
        var red = (char)27 + "[31m";
        var l = Closed(Input(Short, "wri" + red + "tten"), 120);
        Assert.DoesNotContain(red, l.Rows[l.ResultRow].Text, StringComparison.Ordinal);
        Assert.Contains("tten", ResultRow(l), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_words_carry_the_one_line()
    {
        Assert.Equal($"{U.Ok} ran {U.Dot} written {U.Dot} ~1 tok", ShellBlockRender.PlainWords(ShellOutput.Parse("written"), 0, 7, U));
    }

    //the numbers
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Output_that_fits_the_window_has_no_numbers(int n)
    {
        var l = Open(Input(Short, Lines(n)), 120);
        var rows = l.Rows.Skip(l.OutputFirst).Take(l.OutputShown).ToList();
        Assert.Equal(n, rows.Count);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal($"{U.Box.Vertical}   line {i + 1}", Visible(rows[i].Text));
            Assert.Equal(4, rows[i].LeadCells);
        }
    }

    [Fact]
    public void Output_past_the_window_has_numbers_in_both_views()
    {
        var window = Open(Input(Short, Lines(6)), 120);
        Assert.Equal($"{U.Box.Vertical}   1  line 1", Visible(window.Rows[window.OutputFirst].Text));
        var all = Open(Input(Short, Lines(6)), 120, ShellView.All);
        Assert.Equal($"{U.Box.Vertical}   6  line 6", Visible(all.Rows[all.OutputFirst + 5].Text));
        Assert.Equal(7, all.Rows[all.OutputFirst].LeadCells);
    }

    [Fact]
    public void Show_all_of_a_short_output_has_no_numbers()
    {
        var l = Open(Input(Script, Lines(3)), 120, ShellView.All);
        Assert.Equal($"{U.Box.Vertical}   line 3", Visible(l.Rows[l.OutputFirst + 2].Text));
    }

    [Fact]
    public void A_number_is_painted_dimmer_than_its_line()
    {
        var l = Open(Input(Short, Lines(6)), 120);
        var row = l.Rows[l.OutputFirst].Text;
        Assert.Contains(T.Paint("1", Theme.Dim), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("line 1", Theme.CodeBlockFg), row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrapped_line_without_numbers_hangs_under_its_text()
    {
        var l = Open(Input(Script, new string('w', 150)), 80, ShellView.All);
        var rows = l.Rows.Skip(l.OutputFirst).Take(l.OutputShown).ToList();
        Assert.True(rows.Count >= 2, "precondition: the line wrapped");
        Assert.All(rows, r => Assert.StartsWith($"{U.Box.Vertical}   w", Visible(r.Text), StringComparison.Ordinal));
        Assert.All(rows.Skip(1), r => Assert.True(r.Continuation && r.PrefixCells == 4));
    }

    //stderr on a clean exit
    [Fact]
    public void A_clean_exit_with_stderr_counts_the_stderr_lines_in_amber()
    {
        var l = Closed(Input(Short, WithStderr(Lines(3), "first complaint", "second complaint")), 120);
        var painted = l.Rows[l.ResultRow].Text;
        var row = Visible(painted);
        Assert.Contains($"{U.Ok} ran {U.Dot} stderr 2 {U.Dot} {ShellBlockRender.ExpandWords} {U.Dot} ~", row, StringComparison.Ordinal);
        Assert.Contains(T.Paint(U.Ok, Theme.Ok), painted, StringComparison.Ordinal);
        Assert.Contains(T.Paint("stderr 2", Theme.Warn), painted, StringComparison.Ordinal);
        Assert.DoesNotContain("complaint", row, StringComparison.Ordinal);
        Assert.Equal(ShellBlockRender.ExpandWords, row[l.LinkStart..l.LinkEnd]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(14)]
    public void The_stderr_count_is_the_number_of_red_rows_the_block_shows(int n)
    {
        var input = Input(Short, WithStderr(Lines(2), Enumerable.Range(1, n).Select(i => $"complaint {i}").ToArray()));
        var all = Open(input, 120, ShellView.All);
        var red = all.Rows.Skip(all.OutputFirst).Take(all.OutputShown).Count(r => r.Text.Contains(T.Paint($"complaint", Theme.Err)[..^Ansi.Reset.Length], StringComparison.Ordinal));
        Assert.Equal(n, red);
        Assert.Contains($" stderr {n} ", ResultRow(Closed(input, 120)), StringComparison.Ordinal);
    }

    [Fact]
    public void One_stderr_line_alone_is_counted_and_not_quoted()
    {
        var l = Closed(Input(Short, WithStderr("", "only complaint")), 120);
        var row = ResultRow(l);
        Assert.Contains($"ran {U.Dot} stderr 1 {U.Dot} {ShellBlockRender.ExpandWords}", row, StringComparison.Ordinal);
        Assert.DoesNotContain("complaint", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_exit_with_no_stderr_has_no_count()
    {
        Assert.DoesNotContain("stderr", ResultRow(Closed(Input(Short, Lines(3)), 120)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_words_carry_the_stderr_count()
    {
        var words = ShellBlockRender.PlainWords(ShellOutput.Parse(WithStderr(Lines(2), "a", "b")), 0, 40, U);
        Assert.StartsWith($"{U.Ok} ran {U.Dot} stderr 2 {U.Dot} ~", words, StringComparison.Ordinal);
    }

    //the footer
    [Fact]
    public void The_footer_names_the_command_when_only_command_rows_are_hidden()
    {
        var l = Open(Input(Script, Lines(3)), 120);
        var footer = Visible(l.Rows[l.FooterRow].Text);
        Assert.Equal($"{U.Box.Vertical}   show full command", footer);
        Assert.Equal(ShellAction.ShowAll, l.FooterAction);
        Assert.Equal("show full command", footer[l.ActionStart..l.ActionEnd]);
    }

    [Fact]
    public void The_footer_says_show_all_when_output_is_hidden()
    {
        var l = Open(Input(Script, Lines(6)), 120);
        var footer = Visible(l.Rows[l.FooterRow].Text);
        Assert.EndsWith($"{U.Dot} show all", footer, StringComparison.Ordinal);
        Assert.Equal("show all", footer[l.ActionStart..l.ActionEnd]);
    }

    //every state at every width
    [Fact]
    public void Every_new_state_fits_every_width_with_both_glyph_sets()
    {
        var inputs = new[]
        {
            Input(Short, "written"), Input(Script, "written"), Input(Short, Lines(3)), Input(Script, Lines(3)),
            Input(Short, WithStderr(Lines(3), "a", "b")), Input(Short, WithStderr("", "a")), Input(Script, Lines(6)),
        };
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        foreach (var input in inputs)
        foreach (var state in new[] { ShellBlockState.Closed, new ShellBlockState(false, ShellView.Window, 0, 0), new ShellBlockState(false, ShellView.All, 0, 0) })
            for (var width = 30; width <= 120; width++)
            {
                var l = ShellBlockRender.Layout(input, state, width, T, g);
                Assert.All(l.Rows, r => Assert.True(UnicodeWidth.Of(Visible(r.Text)) <= width, $"a row is wider than {width}: [{Visible(r.Text)}]"));
                if (ReferenceEquals(g, GlyphSet.Ascii))
                    Assert.All(l.Rows, r => Assert.True(Visible(r.Text).All(c => c < 128), $"a row holds a glyph outside ASCII: [{Visible(r.Text)}]"));
            }
    }
}
