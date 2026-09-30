using System.Text.Json;
using Gatto.Core.Tools;
using Xunit;

namespace Gatto.Tests;

public sealed class EditLocateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-edit-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Lines(int n, string nl = "\n") => string.Join(nl, Enumerable.Range(1, n).Select(i => $"line {i} of the file")) + nl;

    //the edit through the real tool, and the view it returned
    private async Task<(EditView? View, string Before, string After)> Edit(string text, string oldS, string newS)
    {
        var path = Path.Combine(_dir, "f.cs");
        await File.WriteAllTextAsync(path, text);
        var r = await new EditFileTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = "f.cs", old_string = oldS, new_string = newS }), new TestToolContext(_dir), default);
        return (r.View, text, await File.ReadAllTextAsync(path));
    }

    //a second reader of the line number, counting the newlines before the text itself
    private static int LineOf(string text, string needle) => text[..text.IndexOf(needle, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    [Fact]
    public async Task A_one_line_edit_at_line_14_starts_at_14()
    {
        var (view, before, _) = await Edit(Lines(30), "line 14 of the file\n", "line 14 changed\n");
        Assert.NotNull(view);
        Assert.Equal(LineOf(before, "line 14 of the file"), view!.Start);
        Assert.Equal(14, view.Start);
        Assert.Equal(new[] { "line 11 of the file", "line 12 of the file", "line 13 of the file" }, view.Before);
        Assert.Equal(new[] { "line 15 of the file", "line 16 of the file", "line 17 of the file" }, view.After);
    }

    [Fact]
    public async Task An_old_string_inside_a_line_gives_the_head_and_the_tail_of_that_line()
    {
        var (view, _, _) = await Edit("a\n    var x = foo(1);\nb\n", "foo(1)", "foo(2)");
        Assert.Equal((2, "    var x = ", ";"), (view!.Start, view.Head, view.Tail));
        Assert.Equal(new[] { "a" }, view.Before);
        Assert.Equal(new[] { "b" }, view.After);
    }

    [Fact]
    public async Task An_old_string_that_ends_in_a_newline_has_an_empty_tail()
    {
        var (view, _, _) = await Edit("a\nbb\ncc\nd\n", "bb\ncc\n", "BB\n");
        Assert.Equal((2, "", ""), (view!.Start, view.Head, view.Tail));
        Assert.Equal(new[] { "d" }, view.After);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_at_the_first_line_has_no_line_before(bool finalNewline)
    {
        var text = "first\nsecond\nthird" + (finalNewline ? "\n" : "");
        var (view, _, _) = await Edit(text, "first", "FIRST");
        Assert.Equal(1, view!.Start);
        Assert.Empty(view.Before);
        Assert.Equal(new[] { "second", "third" }, view.After);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_at_the_last_line_has_no_line_after(bool finalNewline)
    {
        var text = "first\nsecond\nthird" + (finalNewline ? "\n" : "");
        var (view, _, _) = await Edit(text, "third", "THIRD");
        Assert.Equal(3, view!.Start);
        Assert.Empty(view.After);
        Assert.Equal(new[] { "first", "second" }, view.Before);
    }

    [Fact]
    public async Task A_CRLF_file_edited_with_LF_strings_is_found_and_its_lines_hold_no_CR()
    {
        var (view, before, after) = await Edit(Lines(20, "\r\n"), "line 9 of the file\nline 10 of the file", "line 9 of the file\nline ten");
        Assert.NotNull(view);
        Assert.Equal(9, view!.Start);
        Assert.DoesNotContain(view.Before.Concat(view.After).Append(view.Head).Append(view.Tail), l => l.Contains('\r'));
        Assert.Contains("line ten\r\n", after, StringComparison.Ordinal);
        var (o, n) = EditLocate.Lines("line 9 of the file\nline 10 of the file", "line 9 of the file\nline ten", view);
        var d = LineDiff.Of(o, n);
        Assert.Equal((1, 1, 1), (d.Added, d.Removed, d.Changes));
        Assert.DoesNotContain(o.Concat(n), l => l.Contains('\r'));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task A_string_that_occurs_twice_gives_no_view_and_todays_error()
    {
        Assert.Equal((null, 2), (EditLocate.Find("x\ny\nx\n", "x", "z", default).Match, EditLocate.Find("x\ny\nx\n", "x", "z", default).Count));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Edit("x\ny\nx\n", "x", "z"));
    }

    [Fact]
    public async Task A_string_that_does_not_occur_gives_no_view_and_todays_error()
    {
        Assert.Equal((null, 0), (EditLocate.Find("x\ny\n", "absent", "z", default).Match, EditLocate.Find("x\ny\n", "absent", "z", default).Count));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Edit("x\ny\n", "absent", "z"));
    }

    [Fact]
    public async Task A_neighbour_longer_than_300_characters_is_cut_and_keeps_its_surrogate_pairs()
    {
        var pair = char.ConvertFromUtf32(0x1F600);
        var longLine = new string('a', 299) + pair + new string('b', 20);
        var (view, _, _) = await Edit(longLine + "\ntarget\n" + longLine + "\n", "target", "TARGET");
        foreach (var l in view!.Before.Concat(view.After))
        {
            Assert.True(l.Length <= EditLocate.LineCells, $"{l.Length} chars");
            Assert.False(l.Length > 0 && char.IsHighSurrogate(l[^1]), "a cut split a surrogate pair");
        }
        Assert.Equal(new string('a', 299), view.Before[0]);
    }

    [Fact]
    public async Task The_whole_lines_of_a_fragment_edit_are_the_files_lines_before_and_after()
    {
        var (view, before, after) = await Edit("a\n    var x = foo(1) + foo2;\nb\n", "foo(1)", "foo(2)");
        var (o, n) = EditLocate.Lines("foo(1)", "foo(2)", view);
        Assert.Equal(new[] { before.Split('\n')[1] }, o);
        Assert.Equal(new[] { after.Split('\n')[1] }, n);
        var (fo, fn) = EditLocate.Lines("foo(1)", "foo(2)", null);
        Assert.Equal(new[] { "foo(1)" }, fo);
        Assert.Equal(new[] { "foo(2)" }, fn);
    }

    //the rows an edit block draws on the new side, number and text, against the file read after the edit
    private async Task AssertNewSideIsTheFile(string text, string oldS, string newS)
    {
        var (view, _, after) = await Edit(text, oldS, newS);
        var (o, n) = EditLocate.Lines(oldS, newS, view);
        var rows = ToolBody.Edit(LineDiff.Of(o, n), view).Where(r => r.Kind != BodyKind.Removed).ToList();
        var file = after.Split('\n');
        Assert.NotEmpty(rows);
        foreach (var r in rows)
            Assert.Equal(file[r.Number!.Value - 1], r.Text);
        Assert.Equal(Enumerable.Range(rows[0].Number!.Value, rows.Count), rows.Select(r => r.Number!.Value));
    }

    [Fact]
    public async Task An_old_string_that_ends_in_a_newline_and_a_new_string_that_does_not_draw_the_joined_line() =>
        await AssertNewSideIsTheFile("a\nb\nc\nd\n", "b\n", "B");

    [Fact]
    public async Task A_deletion_of_whole_lines_adds_no_row_and_shows_the_next_line()
    {
        await AssertNewSideIsTheFile("a\nx\ny\nz\n", "x\n", "");
        var (view, _, _) = await Edit("a\nx\ny\nz\n", "x\n", "");
        var (o, n) = EditLocate.Lines("x\n", "", view);
        Assert.DoesNotContain(ToolBody.Edit(LineDiff.Of(o, n), view), r => r.Kind == BodyKind.Added);
    }

    [Fact]
    public async Task Both_strings_ending_in_a_newline_keep_whole_lines() =>
        await AssertNewSideIsTheFile("a\nb\nc\nd\n", "b\n", "B\n");
}
