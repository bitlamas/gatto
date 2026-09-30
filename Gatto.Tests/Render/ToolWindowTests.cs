using System.Text.RegularExpressions;
using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ToolWindowTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme T256 = new(new TermCaps(true, false));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static string Visible(RenderedRow row) => Visible(row.Text);

    private static List<ToolBodyRow> Body(int n, string text = "row") =>
        Enumerable.Range(1, n).Select(i => new ToolBodyRow($"{text} {i}", BodyKind.Numbered, BodyInk.Text, i)).ToList();

    private static WindowLayout Layout(IReadOnlyList<ToolBodyRow> body, int top = 0, ShellView view = ShellView.Window, int width = 100,
        string unit = "line", bool numbersAlways = false, bool hidesOutside = false, string? outside = null, GlyphSet? g = null, Theme? t = null) =>
        ToolWindow.Layout(new WindowInput(body, numbersAlways, unit, hidesOutside, outside), new WindowState(view, top), width, t ?? T, g ?? U);

    private static (int Above, int More) Counts(WindowLayout l)
    {
        var footer = Visible(l.Rows[l.FooterRow]);
        var above = Regex.Match(footer, @"(\d+) above");
        var more = Regex.Match(footer, @"(\d+) more");
        return (above.Success ? int.Parse(above.Groups[1].Value) : 0, more.Success ? int.Parse(more.Groups[1].Value) : 0);
    }

    [Fact]
    public void A_body_of_three_and_of_five_rows_shows_them_and_no_footer()
    {
        foreach (var n in new[] { 3, 5 })
        {
            var l = Layout(Body(n));
            Assert.Equal(n, l.Rows.Count);
            Assert.Equal(n, l.Shown);
            Assert.Equal(-1, l.FooterRow);
            Assert.False(l.Hides);
            for (var i = 0; i < n; i++) Assert.EndsWith($"row {i + 1}", Visible(l.Rows[i]), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_row_cut_at_the_windows_edge_shows_the_footer_with_show_all_alone()
    {
        foreach (var width in new[] { 30, 60, 100, 160 })
            foreach (var n in new[] { 1, 3, 5 })
            {
                var body = Body(n);
                body[n - 1] = body[n - 1] with { Text = "long " + new string('x', width) };
                var l = Layout(body, width: width);
                Assert.True(l.Hides, $"{width} cols, {n} rows");
                Assert.Equal(n, l.FooterRow);
                Assert.Equal($"{U.Box.Vertical}   show all", Visible(l.Rows[l.FooterRow]));
                Assert.Equal(ShellAction.ShowAll, l.Action);
                Assert.Equal("show all", Visible(l.Rows[l.FooterRow])[l.ActionStart..l.ActionEnd]);
                var all = Layout(body, view: ShellView.All, width: width);
                Assert.Contains(all.Rows, r => Visible(r).EndsWith("x", StringComparison.Ordinal) && !Visible(r).Contains(U.Ellipsis, StringComparison.Ordinal));
                Assert.StartsWith($"{U.Box.Vertical}   show less", Visible(all.Rows[all.FooterRow]), StringComparison.Ordinal);
            }
    }

    [Fact]
    public void A_cut_row_offers_show_all_and_a_hidden_command_keeps_its_own_words()
    {
        var body = Body(2);
        body[1] = body[1] with { Text = "long " + new string('x', 100) };
        Assert.Equal($"{U.Box.Vertical}   show all", Visible(Layout(body, width: 60, outside: "show full command").Rows[^1]));
        Assert.Equal($"{U.Box.Vertical}   show full command", Visible(Layout(body, width: 60, hidesOutside: true, outside: "show full command").Rows[^1]));
    }

    [Fact]
    public void Short_rows_that_fit_the_width_show_no_footer_at_any_width()
    {
        foreach (var width in new[] { 30, 60, 100, 160 })
            foreach (var n in new[] { 1, 3, 5 })
            {
                var l = Layout(Body(n), width: width);
                Assert.False(l.Hides, $"{width} cols, {n} rows");
                Assert.Equal(-1, l.FooterRow);
            }
    }

    [Fact]
    public void A_body_of_six_rows_shows_five_and_counts_one_more()
    {
        var l = Layout(Body(6));
        Assert.Equal(6, l.Rows.Count);
        Assert.Equal(5, l.Shown);
        Assert.Equal(5, l.FooterRow);
        Assert.True(l.Hides);
        Assert.Equal($"{U.Box.Vertical}   1  row 1", Visible(l.Rows[0]));
        Assert.Equal($"{U.Box.Vertical}   {U.Ellipsis} 1 more {U.Dot} show all", Visible(l.Rows[5]));
        Assert.Equal(ShellAction.ShowAll, l.Action);
        Assert.Equal("show all", Visible(l.Rows[5])[l.ActionStart..l.ActionEnd]);
    }

    //the widest of the three footers that fits is the one drawn, the other changes going first and the rows above next
    [Fact]
    public void A_narrow_footer_drops_the_other_changes_then_the_rows_above_and_keeps_show_all_whole()
    {
        var input = new WindowInput(Body(205), NumbersAlways: false, "line", FooterPart: (_, _) => "3 other changes");
        var v = U.Box.Vertical;
        var forms = new[]
        {
            $"{v}   {U.Up} 100 above {U.Dot} {U.Ellipsis} 100 more {U.Dot} 3 other changes {U.Dot} show all",
            $"{v}   {U.Up} 100 above {U.Dot} {U.Ellipsis} 100 more {U.Dot} show all",
            $"{v}   {U.Ellipsis} 100 more {U.Dot} show all",
        };
        for (var width = 20; width <= 100; width++)
        {
            if (forms.All(f => UnicodeWidth.Of(f) > width)) continue;
            var l = ToolWindow.Layout(input, new WindowState(ShellView.Window, 100), width, T, U);
            var footer = Visible(l.Rows[l.FooterRow]);
            Assert.Equal(forms.First(f => UnicodeWidth.Of(f) <= width), footer);
            Assert.Equal("show all", footer[l.ActionStart..l.ActionEnd]);
        }
        Assert.Equal(forms[0], Visible(ToolWindow.Layout(input, new WindowState(ShellView.Window, 100), 0, T, U).Rows[^1]));
    }

    [Fact]
    public void A_body_of_205_rows_counts_above_and_more_at_the_top_the_middle_and_the_end()
    {
        foreach (var (top, above, more) in new[] { (0, 0, 200), (100, 100, 100), (200, 200, 0), (500, 200, 0) })
        {
            var l = Layout(Body(205), top);
            Assert.Equal(above, l.Top);
            Assert.Equal((above, more), Counts(l));
            Assert.Equal(205, Counts(l).Above + l.Shown + Counts(l).More);
            Assert.EndsWith($"row {above + 1}", Visible(l.Rows[0]), StringComparison.Ordinal);
        }
        Assert.Contains("end of output", Visible(Layout(Body(205), 200).Rows[^1]), StringComparison.Ordinal);
        Assert.DoesNotContain("end of output", Visible(Layout(Body(205), 199).Rows[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Show_all_wraps_every_row_and_ends_on_show_less_with_the_total()
    {
        var body = Body(7);
        body[2] = body[2] with { Text = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"word{i}")) };
        var l = Layout(body, view: ShellView.All, width: 40);
        Assert.True(l.Rows.Count > 8);
        Assert.Contains(l.Rows, r => r.Continuation);
        Assert.All(l.Rows.Where(r => r.Continuation), r => Assert.Equal(0, r.LeadCells));
        Assert.Equal($"{U.Box.Vertical}   show less {U.Dot} 7 lines", Visible(l.Rows[^1]));
        Assert.Equal(ShellAction.ShowLess, l.Action);
        Assert.Equal(l.Rows.Count - 1, l.FooterRow);
        Assert.Equal(l.Rows.Count - 1, l.Shown);
    }

    [Fact]
    public void A_window_row_is_cut_and_never_wrapped()
    {
        var body = Body(6);
        body[1] = body[1] with { Text = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"word{i}")) };
        var l = Layout(body, width: 40);
        Assert.Equal(6, l.Rows.Count);
        Assert.DoesNotContain(l.Rows, r => r.Continuation);
        Assert.EndsWith(U.Ellipsis, Visible(l.Rows[1]), StringComparison.Ordinal);
        Assert.Equal(40, UnicodeWidth.Of(Visible(l.Rows[1])));
    }

    [Fact]
    public void No_row_is_wider_than_the_window()
    {
        var body = Body(8);
        body[3] = body[3] with { Text = new string((char)0x6F22, 40) + " " + new string('x', 150) };
        body.Add(new ToolBodyRow("[capped at 200 matches]", BodyKind.Note, BodyInk.Note));
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
            foreach (var t in new[] { T, T256 })
                foreach (var view in new[] { ShellView.Window, ShellView.All })
                    for (var w = 30; w <= 120; w++)
                        foreach (var r in Layout(body, view: view, width: w, g: g, t: t).Rows)
                            Assert.True(UnicodeWidth.Of(Visible(r.Text)) <= w, $"{w}: {Visible(r.Text)}");
    }

    [Fact]
    public void A_window_narrower_than_the_chrome_still_fits()
    {
        var body = Body(120);
        for (var w = 8; w <= 20; w++)
            foreach (var view in new[] { ShellView.Window, ShellView.All })
                foreach (var r in Layout(body, top: 50, view: view, width: w).Rows)
                    Assert.True(UnicodeWidth.Of(Visible(r.Text)) <= w, $"{w}: {Visible(r.Text)}");
    }

    [Fact]
    public void Rows_hidden_outside_the_window_bring_the_footer_and_their_own_action()
    {
        var l = Layout(Body(3), hidesOutside: true, outside: "show full command");
        Assert.True(l.Hides);
        Assert.Equal(3, l.FooterRow);
        Assert.Equal($"{U.Box.Vertical}   show full command", Visible(l.Rows[3]));
        Assert.Equal(ShellAction.ShowAll, l.Action);
        Assert.Equal("show full command", Visible(l.Rows[3])[l.ActionStart..l.ActionEnd]);
    }

    [Fact]
    public void The_unit_word_is_the_one_the_caller_passed()
    {
        Assert.EndsWith($"{U.Dot} 7 rows", Visible(Layout(Body(7), view: ShellView.All, unit: "row").Rows[^1]), StringComparison.Ordinal);
        Assert.EndsWith($"{U.Dot} 7 lines", Visible(Layout(Body(7), view: ShellView.All, unit: "line").Rows[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_numbered_body_that_fits_shows_numbers_only_when_the_caller_asks()
    {
        Assert.Equal($"{U.Box.Vertical}   row 1", Visible(Layout(Body(3)).Rows[0]));
        Assert.Equal($"{U.Box.Vertical}   1  row 1", Visible(Layout(Body(3), numbersAlways: true).Rows[0]));
        var plain = new[] { new ToolBodyRow("text", BodyKind.Plain, BodyInk.Text) };
        Assert.Equal($"{U.Box.Vertical}   text", Visible(Layout(plain, numbersAlways: true).Rows[0]));
    }

    [Fact]
    public void A_marked_cut_row_shows_what_the_cut_shows_at_every_small_room()
    {
        var body = new[] { new ToolBodyRow("abcdefghij", BodyKind.Numbered, BodyInk.Text, 1) };
        var marks = GrepRowsRender.Painter(new System.Text.RegularExpressions.Regex("c"), T);
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        {
            var col = UnicodeWidth.Of(g.Box.Vertical) + 3;
            for (var room = 1; room <= 6; room++)
            {
                var l = ToolWindow.Layout(new WindowInput(body, false, "line", Marks: marks), new WindowState(ShellView.Window, 0), col + room, T, g);
                Assert.Equal(TermText.TruncateCells("abcdefghij", room, g), Visible(l.Rows[0])[col..]);
            }
        }
    }

    //the sticky heading and the positions

    //a grep body of files with a match count each, every match text unique so a row maps back to its body index
    private static IReadOnlyList<ToolBodyRow> Files(params (string Path, int Matches)[] files) => Files(files, Array.Empty<string>());

    private static IReadOnlyList<ToolBodyRow> Files((string Path, int Matches)[] files, string[] notes, Func<string, int, int>? line = null)
    {
        var lines = files.SelectMany(f => Enumerable.Range(1, f.Matches).Select(i => $"{f.Path}:{(line ?? ((_, n) => n * 10))(f.Path, i)}: {f.Path}-m{i}"));
        return ToolBody.Grep(GrepRows.Parse(string.Join("\n", lines.Concat(notes)))!);
    }

    private static WindowLayout Grep(IReadOnlyList<ToolBodyRow> body, int top, ShellView view = ShellView.Window) =>
        Layout(body, top, view, unit: "row", numbersAlways: true);

    //the window's rows without the footer
    private static List<string> WindowRows(WindowLayout l) =>
        l.Rows.Take(l.FooterRow >= 0 ? l.FooterRow : l.Rows.Count).Select(Visible).ToList();

    //every position the wheel can rest on, from the top down
    private static List<int> Positions(IReadOnlyList<ToolBodyRow> body)
    {
        var list = new List<int> { 0 };
        while (ToolWindow.Step(body, list[^1], -1) is { Used: -1 } s)
        {
            Assert.True(s.Top > list[^1], $"a notch down from {list[^1]} went to {s.Top}");   //a notch that does not move would loop here
            list.Add(s.Top);
        }
        return list;
    }

    //the body index a window row shows, by its unique text, or -1 for the empty row
    private static int IndexOf(IReadOnlyList<ToolBodyRow> body, string row)
    {
        var text = row.TrimEnd();
        if (text == U.Box.Vertical) return -1;
        var i = body.ToList().FindIndex(r => text.EndsWith(" " + r.Text, StringComparison.Ordinal));
        Assert.True(i >= 0, $"no body row for {row}");
        return i;
    }

    private static IReadOnlyList<ToolBodyRow> ThreeFiles => Files(("a.cs", 4), ("b.cs", 1), ("c.cs", 6));

    [Fact]
    public void The_first_row_is_a_heading_or_a_row_with_no_heading_above_it_at_every_position()
    {
        var body = ThreeFiles;
        for (var t = 0; t <= body.Count + 2; t++)
        {
            var first = IndexOf(body, WindowRows(Grep(body, t))[0]);
            Assert.True(body[first].Kind == BodyKind.Heading || body[first].Heading < 0, $"top {t}: {body[first].Text}");
        }
    }

    [Fact]
    public void The_sticky_heading_is_the_one_the_first_visible_match_belongs_to()
    {
        var body = Files(new[] { ("a.cs", 6), ("b.cs", 6) }, Array.Empty<string>(), (_, n) => n);
        foreach (var t in Positions(body))
        {
            var l = Grep(body, t);
            if (body[l.Top].Kind != BodyKind.Numbered) continue;
            var rows = WindowRows(l);
            Assert.Equal(body[body[l.Top].Heading].Text, body[IndexOf(body, rows[0])].Text);
            Assert.Equal(l.Top, IndexOf(body, rows[1]));
        }
    }

    [Fact]
    public void A_file_with_one_match_between_two_files_keeps_its_heading_over_its_own_match()
    {
        var body = Files(("a.cs", 3), ("b.cs", 1), ("c.cs", 3));
        for (var t = 0; t <= body.Count; t++)
        {
            var idx = WindowRows(Grep(body, t)).Select(r => IndexOf(body, r)).Where(i => i >= 0).ToList();
            for (var k = 1; k < idx.Count; k++)
                Assert.False(body[idx[k - 1]].Kind == BodyKind.Heading && body[idx[k]].Kind == BodyKind.Heading, $"top {t}: two headings adjacent");
        }
    }

    [Fact]
    public void Above_plus_shown_plus_more_is_the_total_at_every_position_the_last_included()
    {
        foreach (var body in new[] { ThreeFiles, Files(new[] { ("a.cs", 2), ("b.cs", 3) }, new[] { "[capped at 200 matches]" }), Body(12) })
            foreach (var t in Positions(body))
            {
                var l = Grep(body, t);
                var (above, more) = Counts(l);
                var shown = WindowRows(l).Select(r => IndexOf(body, r)).Where(i => i >= above).ToList();
                Assert.Equal(Enumerable.Range(above, shown.Count), shown);
                Assert.Equal(body.Count, above + shown.Count + more);
            }
    }

    [Fact]
    public void The_window_is_five_screen_rows_at_every_position()
    {
        foreach (var body in new[] { ThreeFiles, Files(("a.cs", 3), ("b.cs", 1), ("c.cs", 3)), Files(("a.cs", 5), ("b.cs", 3)) })
            foreach (var t in Positions(body))
                Assert.Equal(5, WindowRows(Grep(body, t)).Count);
    }

    [Fact]
    public void The_last_position_ends_on_the_bodys_last_row_and_says_end_of_output()
    {
        var body = ThreeFiles;
        var positions = Positions(body);
        Assert.Equal(ToolWindow.MaxTop(body), positions[^1]);
        foreach (var t in positions)
        {
            var l = Grep(body, t);
            var last = WindowRows(l).Select(r => IndexOf(body, r)).Max();
            Assert.Equal(t == positions[^1], last == body.Count - 1);
            Assert.Equal(t == positions[^1], Visible(l.Rows[l.FooterRow]).Contains("end of output", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_notch_never_lands_on_a_position_that_draws_the_same_rows()
    {
        var body = ThreeFiles;
        var down = Positions(body);
        for (var k = 1; k < down.Count; k++)
            Assert.NotEqual(WindowRows(Grep(body, down[k - 1])), WindowRows(Grep(body, down[k])));
        var up = new List<int> { down[^1] };
        while (ToolWindow.Step(body, up[^1], 1) is { Used: 1 } s)
        {
            Assert.True(s.Top < up[^1], $"a notch up from {up[^1]} went to {s.Top}");
            up.Add(s.Top);
        }
        Assert.Equal(down.AsEnumerable().Reverse(), up);
    }

    [Fact]
    public void A_tail_of_four_rows_under_a_new_heading_still_draws_five_screen_rows()
    {
        foreach (var body in new[] { Files(("a.cs", 4), ("b.cs", 3)), Files(new[] { ("a.cs", 4), ("b.cs", 1) }, new[] { "[capped at 200 matches]", "[capped at 50000 chars]" }) })
        {
            var top = ToolWindow.MaxTop(body);
            Assert.Equal(body.Count - 4, top);
            var l = Grep(body, top);
            var rows = WindowRows(l);
            Assert.Equal(5, rows.Count);
            Assert.Equal(U.Box.Vertical, rows[^1].TrimEnd());
            Assert.Equal(5, l.Shown);
            Assert.Contains("end of output", Visible(l.Rows[l.FooterRow]), StringComparison.Ordinal);
            Assert.Equal((top, 0), Counts(l));
        }
    }

    [Fact]
    public void A_step_across_a_heading_uses_one_unit()
    {
        var body = Files(("a.cs", 3), ("b.cs", 8));
        Assert.Equal((4, -1), ToolWindow.Step(body, 3, -1));
        Assert.Equal((6, -1), ToolWindow.Step(body, 4, -1));
        Assert.Equal((4, 1), ToolWindow.Step(body, 6, 1));
        Assert.Equal((8, -3), ToolWindow.Step(body, 4, -3));
        Assert.Equal((4, 3), ToolWindow.Step(body, 8, 3));
    }

    [Fact]
    public void A_body_with_no_heading_steps_as_a_clamp()
    {
        var body = Body(20);
        Assert.Equal(15, ToolWindow.MaxTop(body));
        Assert.Equal((3, -3), ToolWindow.Step(body, 0, -3));
        Assert.Equal((15, -1), ToolWindow.Step(body, 14, -3));
        Assert.Equal((0, 1), ToolWindow.Step(body, 1, 3));
        Assert.Equal((0, 0), ToolWindow.Step(body, 0, 3));
        Assert.Equal(0, ToolWindow.MaxTop(Body(5)));
    }

    [Fact]
    public void A_top_past_the_end_is_clamped_at_paint()
    {
        var body = ThreeFiles;
        Assert.Equal(ToolWindow.MaxTop(body), Grep(body, 999).Top);
        Assert.Equal(0, Grep(body, -4).Top);
        Assert.Equal(0, Grep(body, 1).Top);
        Assert.Equal(WindowRows(Grep(body, 0)), WindowRows(Grep(body, 1)));
    }

    [Fact]
    public void A_body_of_five_and_of_six_rows_with_a_heading_first()
    {
        var five = Files(("a.cs", 4));
        var l5 = Grep(five, 0);
        Assert.Equal((5, -1), (l5.Rows.Count, l5.FooterRow));

        var six = Files(("a.cs", 5));
        Assert.Equal(2, ToolWindow.MaxTop(six));
        Assert.Equal(new[] { 0, 2 }, Positions(six));
        var top = Grep(six, 0);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, WindowRows(top).Select(r => IndexOf(six, r)));
        Assert.Contains($"{U.Ellipsis} 1 more", Visible(top.Rows[top.FooterRow]), StringComparison.Ordinal);
        var end = Grep(six, 2);
        Assert.Equal(new[] { 0, 2, 3, 4, 5 }, WindowRows(end).Select(r => IndexOf(six, r)));
        Assert.Contains("end of output", Visible(end.Rows[end.FooterRow]), StringComparison.Ordinal);
    }
}
