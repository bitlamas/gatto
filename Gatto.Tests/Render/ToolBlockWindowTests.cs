using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Census;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ToolBlockWindowTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme T256 = new(new TermCaps(true, false));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static string Matches(int n, string path = "src\\Deep\\Sample.cs", int from = 1) =>
        string.Join("\r\n", Enumerable.Range(from, n).Select(i => $"{path}:{i * 3}:     var one = Two({i});"));

    private static string Lines(int from, int n) => string.Join("\r\n", Enumerable.Range(from, n).Select(i => $"text of line {i}"));

    private static ToolBlockItem Block(string name, string argsJson, ToolResult r, bool open = true, Theme? theme = null, GlyphSet? g = null, string? followUp = null) =>
        new(name, "args", ItemRender.ToolGloss(r, theme ?? T, g ?? U), true, "coder")
        {
            Collapsed = !open, FullResult = r.Text, RawGloss = r.Gloss, Parts = ItemRender.ToolGlossParts(r, theme ?? T, g ?? U),
            FollowUp = followUp, GrepPattern = GrepRows.PatternOf(name, argsJson), BodyStart = ToolBody.StartOf(name, argsJson),
        };

    private static ToolBlockItem Grep(string text, string pattern = "one|Two", bool open = true, Theme? theme = null, GlyphSet? g = null) =>
        Block("grep", JsonSerializer.Serialize(new { pattern }), new ToolResult(text, Gloss: "matches"), open, theme, g);

    private static ToolBlockItem Read(int offset, int n) =>
        Block("read_file", JsonSerializer.Serialize(new { path = "a.cs", offset, limit = n }), new ToolResult(Lines(offset, n), Gloss: $"{n} lines"));

    private static List<string> Rows(ToolBlockItem b, int width = 120, Theme? theme = null, GlyphSet? g = null) =>
        b.Render(width, theme ?? T, g ?? U).ToList();

    [Fact]
    public void An_open_grep_block_of_201_rows_draws_the_head_five_rows_and_a_footer()
    {
        var b = Grep(Matches(200));
        var rows = Rows(b);
        Assert.Equal(2 + 5 + 1, rows.Count);
        Assert.Equal($"{U.Box.Vertical} src\\Deep\\Sample.cs", Visible(rows[2]));
        Assert.Equal($"{U.Box.Vertical}     3  var one = Two(1);", Visible(rows[3]));
        Assert.Equal($"{U.Box.Vertical}   {U.Ellipsis} 196 more {U.Dot} show all", Visible(rows[7]));
        var l = b.Layout(120, T, U);
        Assert.Equal((2, 5, 201, 7), (l.OutputFirst, l.OutputShown, l.OutputTotal, l.FooterRow));
        Assert.True(l.Hides);
    }

    [Fact]
    public void An_open_block_whose_body_fits_draws_its_rows_and_no_footer()
    {
        var rows = Rows(Grep(Matches(3)));
        Assert.Equal(2 + 4, rows.Count);
        Assert.EndsWith("var one = Two(3);", Visible(rows[^1]), StringComparison.Ordinal);
        Assert.Equal(-1, Grep(Matches(3)).Layout(120, T, U).FooterRow);
    }

    [Fact]
    public void A_read_with_an_offset_shows_the_files_line_numbers()
    {
        var rows = Rows(Read(218, 28));
        Assert.Equal($"{U.Box.Vertical}   218  text of line 218", Visible(rows[2]));
        Assert.Equal($"{U.Box.Vertical}   222  text of line 222", Visible(rows[6]));
    }

    [Fact]
    public void A_short_read_still_shows_its_line_numbers() =>
        Assert.Equal($"{U.Box.Vertical}   10  text of line 10", Visible(Rows(Read(10, 3))[2]));

    [Fact]
    public void Any_other_tool_draws_plain_rows_with_no_numbers()
    {
        var rows = Rows(Block("list_dir", "{}", new ToolResult("alpha\nbeta", Gloss: "2 entries")));
        Assert.Equal(new[] { $"{U.Box.Vertical}   alpha", $"{U.Box.Vertical}   beta" }, rows.Skip(2).Select(Visible));
    }

    [Fact]
    public void A_hooks_text_for_a_grep_call_draws_plain_rows_whole()
    {
        var rows = Rows(Grep("a.cs:1: one\nthe hook replaced this line"));
        Assert.Equal(new[] { $"{U.Box.Vertical}   a.cs:1: one", $"{U.Box.Vertical}   the hook replaced this line" }, rows.Skip(2).Select(Visible));
    }

    [Fact]
    public void An_error_result_draws_its_rows_in_the_error_ink()
    {
        var rows = Rows(Block("read_file", "{}", new ToolResult("file not found: a.cs\nsecond line", IsError: true)));
        Assert.Contains(T.Paint("file not found: a.cs", Theme.Err), rows[2], StringComparison.Ordinal);
        Assert.Contains(T.Paint("second line", Theme.Err), rows[3], StringComparison.Ordinal);
    }

    [Fact]
    public void The_body_text_is_in_the_code_ink_the_heading_in_the_args_ink_the_note_dim()
    {
        var rows = Rows(Grep("a.cs:4: plain text\n[capped at 200 matches]", pattern: "zzz"));
        Assert.Contains(T.Paint("a.cs", Theme.ToolArgs), rows[2], StringComparison.Ordinal);
        Assert.Contains(T.Paint("plain text", Theme.CodeBlockFg), rows[3], StringComparison.Ordinal);
        Assert.Contains(T.Paint("[capped at 200 matches]", Theme.Dim), rows[4], StringComparison.Ordinal);
        var plain = Rows(Block("list_dir", "{}", new ToolResult("alpha", Gloss: "1 entry")));
        Assert.Contains(T.Paint("alpha", Theme.CodeBlockFg), plain[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_row_keeps_the_marks_before_the_cut()
    {
        var text = "a.cs:9: one " + string.Join(' ', Enumerable.Range(0, 40).Select(i => $"word{i}"));
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
            foreach (var t in new[] { T, T256 })
            {
                var row = Rows(Grep(text, pattern: "one", theme: t, g: g), 60, t, g)[3];
                Assert.Contains(t.Paint("one", Theme.Accent), row, StringComparison.Ordinal);
                Assert.EndsWith(g.Ellipsis, Visible(row), StringComparison.Ordinal);
                Assert.Equal(60, UnicodeWidth.Of(Visible(row)));
            }
    }

    [Fact]
    public void Show_all_keeps_the_marks_on_every_piece_of_a_wrapped_row()
    {
        var text = "a.cs:9: one " + string.Join(' ', Enumerable.Range(0, 8).Select(i => $"filler{i}")) + " tail";
        var b = Grep(text, pattern: "one|filler2 filler3 filler4 filler5 filler6|tail");
        b.View = ShellView.All;
        var rows = Rows(b, 40);
        var accent = T.Paint("X", Theme.Accent).Split('X')[0];
        var first = rows.FindIndex(r => Visible(r).Contains("one", StringComparison.Ordinal));
        var start = rows.FindIndex(r => Visible(r).Contains("filler2", StringComparison.Ordinal));
        var end = rows.FindIndex(r => Visible(r).Contains("filler6", StringComparison.Ordinal));
        var last = rows.FindIndex(r => Visible(r).Contains("tail", StringComparison.Ordinal));
        Assert.True(end > start, "precondition: the wrap splits the long mark");
        Assert.True(last > first, "precondition: the row wrapped");
        Assert.Contains(T.Paint("one", Theme.Accent), rows[first], StringComparison.Ordinal);
        Assert.Contains(T.Paint("tail", Theme.Accent), rows[last], StringComparison.Ordinal);
        Assert.Contains(accent + "filler2", rows[start], StringComparison.Ordinal);
        var cont = Visible(rows[end]).Trim().TrimStart(U.Box.Vertical[0]).Trim().Split(' ')[0];
        Assert.Contains(accent + cont, rows[end], StringComparison.Ordinal);
    }

    [Fact]
    public void A_grep_footer_counts_rows_and_a_read_footer_counts_lines()
    {
        var grep = Grep(Matches(200));
        var read = Read(1, 28);
        Assert.Contains("show all", Visible(Rows(grep)[^1]), StringComparison.Ordinal);
        grep.View = ShellView.All;
        read.View = ShellView.All;
        Assert.EndsWith($"show less {U.Dot} 201 rows", Visible(Rows(grep)[^1]), StringComparison.Ordinal);
        Assert.EndsWith($"show less {U.Dot} 28 lines", Visible(Rows(read)[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Show_all_of_one_read_row_draws_show_less_only_where_the_window_would_cut_it()
    {
        var line = "missing " + new string('x', 52);
        var b = Block("read_file", JsonSerializer.Serialize(new { path = "a.cs" }), new ToolResult(line, Gloss: "1 line"));
        var seen = new HashSet<bool>();
        for (var width = 50; width <= 100; width++)
        {
            b.View = ShellView.Window;
            var window = b.Layout(width, T, U);
            b.View = ShellView.All;
            var all = b.Layout(width, T, U);
            var cut = Visible(window.Rows[window.OutputFirst].Text).EndsWith(U.Ellipsis, StringComparison.Ordinal);
            seen.Add(cut);
            Assert.Equal(cut, window.FooterRow >= 0);
            Assert.Equal(cut, all.FooterRow >= 0);
            if (cut)
            {
                Assert.Equal($"{U.Box.Vertical}   show less {U.Dot} 1 line", Visible(all.Rows[all.FooterRow].Text));
                Assert.Equal(ShellAction.ShowLess, all.FooterAction);
            }
            else
            {
                Assert.Equal(ShellAction.None, all.FooterAction);
                Assert.Equal(all.ActionStart, all.ActionEnd);
                Assert.EndsWith(line, Visible(all.Rows[^1].Text), StringComparison.Ordinal);
                Assert.Equal(all.OutputFirst + 1, all.Rows.Count);
            }
        }
        Assert.Equal(new[] { false, true }, seen.Order());
    }

    [Fact]
    public void Show_all_of_a_body_taller_than_the_window_keeps_show_less_at_every_width()
    {
        var read = Read(1, 28);
        read.View = ShellView.All;
        for (var width = 30; width <= 120; width++)
        {
            var l = read.Layout(width, T, U);
            Assert.Equal(l.Rows.Count - 1, l.FooterRow);
            Assert.Equal($"{U.Box.Vertical}   show less {U.Dot} 28 lines", Visible(l.Rows[l.FooterRow].Text));
            Assert.Equal(ShellAction.ShowLess, l.FooterAction);
        }
    }

    [Fact]
    public void A_follow_up_row_moves_the_body_down_by_one_in_the_map()
    {
        var r = new ToolResult(Lines(1, 8), Gloss: "8 lines");
        var plain = Block("read_file", "{}", r);
        var follow = Block("read_file", "{}", r, followUp: "auto-approved: reads under src");
        Assert.Equal(2, plain.Layout(120, T, U).OutputFirst);
        Assert.Equal(3, follow.Layout(120, T, U).OutputFirst);
        Assert.Equal(8, follow.Layout(120, T, U).FooterRow);
        Assert.Contains("more", Visible(Rows(follow)[8]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_with_no_match_is_no_focus_stop()
    {
        var grep = Block("grep", "{\"pattern\":\"x\"}", new ToolResult(Globbing.NoMatches, Gloss: "0 matches"));
        var glob = Block("glob", "{\"pattern\":\"*.x\"}", new ToolResult(Globbing.NoMatches, Gloss: "0 files"));
        var some = Block("glob", "{\"pattern\":\"*.cs\"}", new ToolResult("a.cs", Gloss: "1 file"));
        var model = new TranscriptModel("coder");
        var index = new LineIndex(model, T, glyphs: U);
        var scroll = new ScrollController(model, index);
        var compositor = new ViewportCompositor(new RecordingSurface { Width = 80, Height = 24 }, index, new object(), glyphs: U);
        var fc = new FocusController(model, index, scroll, compositor);
        model.Append(grep);
        model.Append(glob);
        model.Append(some);
        Assert.False(fc.IsCollapsible(0, 80));
        Assert.False(fc.IsCollapsible(1, 80));
        Assert.True(fc.IsCollapsible(2, 80));
        Assert.Equal(2, Rows(grep).Count);
    }

    [Fact]
    public void An_empty_result_has_no_body_and_no_link()
    {
        var b = Block("list_dir", "{}", new ToolResult("", Gloss: "0 entries"));
        Assert.False(b.HasBody);
        Assert.Equal(2, Rows(b).Count);
        var l = b.Layout(120, T, U);
        Assert.Equal((-1, -1, 0, 0), (l.OutputFirst, l.FooterRow, l.LinkStart, l.LinkEnd));
    }

    [Fact]
    public void Rows_and_wraps_have_one_entry_each_at_every_width()
    {
        var blocks = new[] { Grep(Matches(200) + "\r\n[capped at 200 matches]"), Read(218, 28), Block("list_dir", "{}", new ToolResult(Lines(1, 9) + " " + new string('x', 200), Gloss: "9")) };
        foreach (var b in blocks)
            foreach (var view in new[] { ShellView.Window, ShellView.All })
            {
                b.View = view;
                for (var w = 30; w <= 120; w++)
                {
                    var rows = b.Render(w, T, U);
                    Assert.Equal(rows.Count, b.RowWraps(w, T, U).Count);
                    Assert.All(rows.Skip(Math.Max(0, b.Layout(w, T, U).OutputFirst)), r => Assert.True(UnicodeWidth.Of(Visible(r)) <= w));   //the head is the call's rows, the window's own rows fit
                }
            }
    }

    [Fact]
    public void A_live_block_and_its_replay_draw_the_same_rows()
    {
        var calls = new[]
        {
            (new ToolCall("c1", "grep", JsonSerializer.Serialize(new { pattern = "one|Two" })), new ToolResult(Matches(12), Gloss: "12 matches")),
            (new ToolCall("c2", "read_file", JsonSerializer.Serialize(new { path = "a.cs", offset = 218, limit = 9 })), new ToolResult(Lines(218, 9), Gloss: "9 lines")),
            (new ToolCall("c3", "glob", JsonSerializer.Serialize(new { pattern = "*.cs" })), new ToolResult("a.cs\r\nb.cs\r\n[capped at 200 matches]", Gloss: "2 files")),
        };
        foreach (var (call, result) in calls)
        {
            var s = new VtScreenSurface(120, 40);
            var gate = new object();
            var model = new TranscriptModel("coder");
            var p = new ChromePainter(s, T, gate, model)
            {
                Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
                RoleForTint = "coder",
            };
            p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
            var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
            p.AltScreen.Enter();
            r.BeginTurn();
            r.OnToolCallStart(call);
            r.OnToolResult(call, result);
            r.EndTurn();
            var live = model.Items.OfType<ToolBlockItem>().Single();

            var convo = new Gatto.Core.Loop.Conversation("sys");
            convo.AddUser("go");
            convo.SetGattoRole("coder");
            convo.AddAssistant("", new[] { call });
            convo.AddToolResult(call.Id, result);
            var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: U);
            var replay = rebuilt.Items.OfType<ToolBlockItem>().Single();

            live.Collapsed = false;
            replay.Collapsed = false;
            live.LeadingBlank = replay.LeadingBlank = false;
            Assert.True(live.HasBody);
            foreach (var w in new[] { 80, 120 })
                Assert.Equal(live.Render(w, T, U), replay.Render(w, T, U));
            Assert.Contains(live.Render(120, T, U), row => Visible(row).Contains("a.cs", StringComparison.Ordinal) || Visible(row).Contains("Sample.cs", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_shell_output_and_a_body_of_the_same_lines_count_the_same()
    {
        var text = Lines(1, 30);
        var shell = new ToolBlockItem("shell", "ls", "painted", true, "coder") { Collapsed = false, FullResult = text, FullArgs = "ls", RawGloss = "exit 0" };
        var other = Block("list_dir", "{}", new ToolResult(text, Gloss: "30"));
        foreach (var top in new[] { 0, 7, 25, 90 })
        {
            shell.OutputTop = top;
            other.OutputTop = top;
            var a = shell.Layout(120, T, U);
            var b = other.Layout(120, T, U);
            Assert.Equal(Visible(a.Rows[a.FooterRow].Text), Visible(b.Rows[b.FooterRow].Text));
            Assert.Equal((a.OutputShown, a.OutputTotal), (b.OutputShown, b.OutputTotal));
        }
    }
}
