using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class EditBlockRowsTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme T256 = new(new TermCaps(true, false));
    private static readonly Theme TL = new(new TermCaps(true, true), ThemeMode.Light);
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly string Esc = ((char)0x1B).ToString();

    private static string Visible(RenderedRow row) => TermText.StripAnsiForWidth(row.Text);

    private static readonly EditView At14 = new(14, "", "", new[] { "line 11", "line 12", "line 13" }, new[] { "line 15", "line 16", "line 17" });

    private static IReadOnlyList<ToolBodyRow> Body(string oldS, string newS, EditView? view)
    {
        var (o, n) = EditLocate.Lines(oldS, newS, view);
        return ToolBody.Edit(LineDiff.Of(o, n), view);
    }

    //three neighbours, the old line, the new line, three neighbours: 8 rows, the window from row 0 shows the pair at 3 and 4
    private static IReadOnlyList<ToolBodyRow> OneLine => Body("old line 14", "new line 14", At14);

    private static WindowLayout Win(IReadOnlyList<ToolBodyRow> body, int width = 80, Theme? theme = null, GlyphSet? g = null,
        ShellView view = ShellView.Window, int top = 0, Func<int, int, string?>? footerPart = null) =>
        ToolWindow.Layout(new WindowInput(body, true, "line", FooterPart: footerPart), new WindowState(view, top), width, theme ?? T, g ?? U);

    //the walker
    [Fact]
    public void The_walker_reads_the_background_of_every_cell()
    {
        var on = GroundWalker.Of($"{Esc}[48;2;1;2;3mab{Esc}[38;2;9;9;9mcd{Esc}[0m");
        Assert.Equal(new[] { "2;1;2;3", "2;1;2;3", "2;1;2;3", "2;1;2;3" }, on);
        var off = GroundWalker.Of($"{Esc}[48;2;1;2;3mab{Esc}[0mcd");
        Assert.Equal(new string?[] { "2;1;2;3", "2;1;2;3", null, null }, off);
        Assert.Equal(new string?[] { "5;22", null }, GroundWalker.Of($"{Esc}[48;5;22mx{Esc}[49my"));
        Assert.Equal(new string?[] { null, "5;52" }, GroundWalker.Of($"x{Esc}[0;48;5;52my"));
        Assert.Equal(2, GroundWalker.Of(((char)0x6F22).ToString()).Count);
        Assert.Equal(new[] { "5;22", "5;22", "5;22" }, GroundWalker.Of($"{Esc}[48;5;22m{Esc}[Kx", 3));
        Assert.Equal(new string?[] { null, "5;22", "5;22" }, GroundWalker.Of($"{Esc}[48;5;22m{Esc}[K{Esc}[0mx", 3));
    }

    //the ground
    [Fact]
    public void The_ground_runs_from_the_number_to_the_windows_edge()
    {
        foreach (var theme in new[] { T, T256, TL })
            foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
                for (var width = 30; width <= 120; width++)
                {
                    var rows = Win(OneLine, width, theme, g).Rows;
                    var col = UnicodeWidth.Of(g.Box.Vertical) + 3;
                    foreach (var (row, bg) in new[] { (rows[3], Theme.DiffRemovedBg), (rows[4], Theme.DiffAddedBg) })
                    {
                        var cells = GroundWalker.Of(row.Text, width);
                        Assert.Equal(width, cells.Count);
                        Assert.All(cells.Take(col), c => Assert.Null(c));
                        var code = GroundWalker.Code(theme.Map(bg), theme.TrueColor);
                        Assert.All(cells.Skip(col), c => Assert.Equal(code, c));
                    }
                }
    }

    [Fact]
    public void A_row_with_no_change_has_no_ground()
    {
        var rows = Win(OneLine).Rows;
        foreach (var i in new[] { 0, 1, 2 })
            Assert.All(GroundWalker.Of(rows[i].Text), c => Assert.Null(c));
    }

    [Fact]
    public void An_edits_rows_carry_no_syntax_colour()
    {
        var body = Body("var x = 1; // old", "var x = 2; // new \"text\"", At14);
        var allowed = new[] { Theme.Dim, Theme.Ok, Theme.Err, Theme.CodeBlockFg }.Select(c => $"38;2;{c.R};{c.G};{c.B}").ToHashSet();
        foreach (var row in Win(body, 100).Rows.Take(5))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(row.Text, @"38;2;\d+;\d+;\d+"))
                Assert.Contains(m.Value, allowed);
    }

    [Fact]
    public void The_sign_stands_after_the_number_in_both_glyph_sets()
    {
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        {
            var rows = Win(OneLine, 80, T, g).Rows;
            var rail = g.Box.Vertical;
            Assert.Equal($"{rail}   11    line 11", Visible(rows[0]).TrimEnd());
            Assert.Equal($"{rail}   14 -  old line 14", Visible(rows[3]).TrimEnd());
            Assert.Equal($"{rail}   14 +  new line 14", Visible(rows[4]).TrimEnd());
            Assert.Contains(T.Paint("-", Theme.Err), rows[3].Text, StringComparison.Ordinal);
            Assert.Contains(T.Paint("+", Theme.Ok), rows[4].Text, StringComparison.Ordinal);
        }
    }

    //the numbers
    [Fact]
    public void A_removed_row_carries_the_old_number_and_an_added_row_the_new()
    {
        var view = At14 with { After = new[] { "after 1", "after 2", "after 3" } };
        var body = Body("a1\na2\na3", "b1\nb2\nb3\nb4\nb5", view);
        Assert.Equal(new int?[] { 11, 12, 13, 14, 15, 16, 14, 15, 16, 17, 18, 19, 20, 21 }, body.Select(r => r.Number));
        Assert.Equal(3, body.Count(r => r.Kind == BodyKind.Removed));
        Assert.Equal(5, body.Count(r => r.Kind == BodyKind.Added));
    }

    [Fact]
    public void Without_a_view_no_row_has_a_number_and_no_neighbour_shows()
    {
        var body = Body("foo(1)", "foo(2)", null);
        Assert.Equal(new[] { BodyKind.Removed, BodyKind.Added }, body.Select(r => r.Kind));
        Assert.All(body, r => Assert.Null(r.Number));
        var rows = Win(body).Rows;
        Assert.Equal($"{U.Box.Vertical}   -  foo(1)", Visible(rows[0]).TrimEnd());
    }

    //the width
    [Fact]
    public void No_row_is_wider_than_the_window()
    {
        var body = Body("old " + new string('x', 150), "new " + new string((char)0x6F22, 80), At14);
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
            foreach (var view in new[] { ShellView.Window, ShellView.All })
                for (var width = 30; width <= 120; width++)
                    foreach (var row in Win(body, width, T, g, view).Rows)
                        Assert.True(UnicodeWidth.Of(Visible(row)) <= width, $"{width}: {Visible(row)}");
    }

    [Fact]
    public void A_window_narrower_than_the_chrome_still_fits()
    {
        for (var width = 8; width <= 20; width++)
            foreach (var view in new[] { ShellView.Window, ShellView.All })
                foreach (var row in Win(OneLine, width, T, U, view).Rows)
                    Assert.True(UnicodeWidth.Of(Visible(row)) <= width, $"{width}: {Visible(row)}");
    }

    [Fact]
    public void A_wrapped_changed_row_keeps_its_ground_on_every_piece()
    {
        var body = Body("old", string.Join(' ', Enumerable.Range(0, 20).Select(i => $"word{i}")), At14);
        var layout = Win(body, 40, T, U, ShellView.All);
        var added = layout.Rows.Select((r, i) => (r, i)).Where(p => Visible(p.r).Contains("word", StringComparison.Ordinal)).ToList();
        Assert.True(added.Count >= 3, "precondition: the added row wrapped");
        var code = GroundWalker.Code(Theme.DiffAddedBg, true);
        foreach (var (row, _) in added)
        {
            var cells = GroundWalker.Of(row.Text, 40);
            Assert.Equal(40, cells.Count);
            Assert.All(cells.Skip(4), c => Assert.Equal(code, c));
        }
    }

    [Fact]
    public void Below_truecolour_the_grounds_are_slots_22_and_52()
    {
        var rows = Win(OneLine, 60, T256).Rows;
        Assert.Equal("5;52", GroundWalker.Of(rows[3].Text)[10]);
        Assert.Equal("5;22", GroundWalker.Of(rows[4].Text)[10]);
    }

    [Fact]
    public void The_light_mode_paints_the_light_grounds()
    {
        var rows = Win(OneLine, 60, TL).Rows;
        Assert.Equal("2;249;222;222", GroundWalker.Of(rows[3].Text)[10]);
        Assert.Equal("2;221;242;221", GroundWalker.Of(rows[4].Text)[10]);
        var slots = Win(OneLine, 60, new Theme(new TermCaps(true, false), ThemeMode.Light)).Rows;
        Assert.Equal("5;224", GroundWalker.Of(slots[3].Text)[10]);
        Assert.Equal("5;194", GroundWalker.Of(slots[4].Text)[10]);
    }

    //the selection and the copy
    [Fact]
    public void A_selection_over_a_ground_shows_and_the_ground_returns()
    {
        var row = Win(OneLine, 60).Rows[4].Text;
        var selected = GroundWalker.Of(TermText.HighlightCells(row, 12, 18, T.SelectionBgOn), 60);
        var sel = GroundWalker.Code(Theme.SelectionBg, true);
        var ground = GroundWalker.Code(Theme.DiffAddedBg, true);
        Assert.All(selected.Skip(4).Take(8), c => Assert.Equal(ground, c));
        Assert.All(selected.Skip(12).Take(6), c => Assert.Equal(sel, c));
        Assert.All(selected.Skip(18), c => Assert.Equal(ground, c));
    }

    [Fact]
    public void A_copy_of_changed_rows_holds_their_text_only()
    {
        var rows = Win(OneLine, 60).Rows;
        foreach (var i in new[] { 0, 3, 4 })
            Assert.Equal(i == 0 ? "line 11" : i == 3 ? "old line 14" : "new line 14", Visible(rows[i])[rows[i].LeadCells..].TrimEnd());
        Assert.Equal(rows[3].LeadCells, rows[0].LeadCells);
    }

    //the footer
    [Fact]
    public void The_footers_part_stands_before_show_all_and_the_action_keeps_its_columns()
    {
        var l = Win(OneLine, 80, footerPart: (top, shown) => top == 0 && shown == 5 ? "2 other changes" : null);
        var footer = Visible(l.Rows[l.FooterRow]);
        Assert.Equal($"{U.Box.Vertical}   {U.Ellipsis} 3 more {U.Dot} 2 other changes {U.Dot} show all", footer);
        Assert.Equal("show all", footer[l.ActionStart..l.ActionEnd]);
        var none = Win(OneLine, 80, footerPart: (_, _) => null);
        Assert.DoesNotContain("other", Visible(none.Rows[none.FooterRow]), StringComparison.Ordinal);
    }

    //the rest position
    private static ToolBodyRow N => new("n", BodyKind.Numbered, BodyInk.Text, 1);
    private static ToolBodyRow R => new("r", BodyKind.Removed, BodyInk.Text, 1);
    private static ToolBodyRow A => new("a", BodyKind.Added, BodyInk.Text, 1);
    private static ToolBodyRow[] Rows(string shape) => shape.Select(c => c switch { 'n' => N, 'r' => R, _ => A }).ToArray();

    [Theory]
    [InlineData("nnnranNN", 2)]
    [InlineData("nnnaannn", 2)]
    [InlineData("nnnraannn", 2)]
    [InlineData("nnnrraaannn", 5)]
    [InlineData("nnnrraaaannn", 5)]
    [InlineData("nnnrrnnn", 3)]
    [InlineData("nrnnannnnn", 2)]
    [InlineData("nnnnnnnn", 0)]
    [InlineData("nnnnnnnnnnra", 7)]
    public void The_rest_position(string shape, int top) =>
        Assert.Equal(top, ToolBody.RestTop(Rows(shape.ToLowerInvariant()), ToolWindow.Height));

    [Fact]
    public void A_write_body_numbers_from_1_and_drops_the_empty_last_line()
    {
        Assert.Equal(new int?[] { 1, 2, 3 }, ToolBody.Write("a\nb\nc\n").Select(r => r.Number));
        Assert.Equal(new[] { "a", "b" }, ToolBody.Write("a\r\nb").Select(r => r.Text));
        Assert.Empty(ToolBody.Write(""));
        Assert.All(ToolBody.Write("a\nb"), r => Assert.Equal((BodyKind.Numbered, BodyInk.Text), (r.Kind, r.Ink)));
    }
}
