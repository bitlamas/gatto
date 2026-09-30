using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class GrepRowsTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme T256 = new(new TermCaps(true, false));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly string Dash = ((char)0x2014).ToString();

    private const string TwoFiles =
        "Repl\\Render\\ChromePainter.cs:7:            n += Layout(Live(one), two);\r\n" +
        "Repl\\Render\\ChromePainter.cs:120:        var live = Layout(two);\r\n" +
        "Repl\\Render\\TranscriptItem.cs:15:    Layout(one);";

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static ToolBlockItem Block(string text, string pattern = "one|two", bool open = true, bool isError = false, Theme? theme = null, GlyphSet? g = null)
    {
        var r = new ToolResult(text, isError, Gloss: isError ? null : "3 matches");
        return new ToolBlockItem("grep", pattern, ItemRender.ToolGloss(r, theme ?? T, g ?? U), true, "coder")
        {
            Collapsed = !open, FullResult = text, Parts = ItemRender.ToolGlossParts(r, theme ?? T, g ?? U),
            GrepPattern = GrepRows.PatternOf("grep", JsonSerializer.Serialize(new { pattern })),
        };
    }

    private static List<string> Body(ToolBlockItem b, int width = 120, Theme? theme = null, GlyphSet? g = null) =>
        b.Render(width, theme ?? T, g ?? U).Skip(2).ToList();

    //the parse
    [Fact]
    public void The_parse_splits_path_line_and_text_and_counts_files()
    {
        var p = GrepRows.Parse(TwoFiles)!;
        Assert.Equal(3, p.Matches.Count);
        Assert.Equal(2, p.Files);
        Assert.Equal(("Repl\\Render\\ChromePainter.cs", 120, "       var live = Layout(two);"), (p.Matches[1].Path, p.Matches[1].Line, p.Matches[1].Text));
        Assert.Empty(p.Notes);
    }

    [Fact]
    public void A_line_holding_a_colon_or_a_line_marker_keeps_them_in_its_text()
    {
        var p = GrepRows.Parse("a.cs:3: x: y:12: z\nmy dir\\b c.cs:4: w\nD:\\far\\e.cs:9: v")!;
        Assert.Equal(("a.cs", 3, "x: y:12: z"), (p.Matches[0].Path, p.Matches[0].Line, p.Matches[0].Text));
        Assert.Equal("my dir\\b c.cs", p.Matches[1].Path);
        Assert.Equal(("D:\\far\\e.cs", 9), (p.Matches[2].Path, p.Matches[2].Line));
    }

    [Fact]
    public void Both_cap_notes_end_the_parse_as_notes()
    {
        var chars = $"[capped at 50000 chars {Dash} narrow the pattern or the glob]";
        var p = GrepRows.Parse("a.cs:1: x\r\n[capped at 200 matches]\r\n" + chars)!;
        Assert.Single(p.Matches);
        Assert.Equal(new[] { "[capped at 200 matches]", chars }, p.Notes);
    }

    [Fact]
    public void No_matches_parses_to_no_rows()
    {
        var p = GrepRows.Parse(Globbing.NoMatches)!;
        Assert.Empty(p.Matches);
        Assert.Equal(0, p.Files);
    }

    [Theory]
    [InlineData("a hook wrote this instead")]
    [InlineData("a.cs:1: x\nand then prose")]
    [InlineData("a.cs:1: x\n[capped at 200 matches]\na.cs:2: y")]
    [InlineData("")]
    public void Text_that_is_not_the_tools_does_not_parse(string text)
    {
        Assert.Null(GrepRows.Parse(text));
    }

    [Fact]
    public void The_pattern_comes_from_a_grep_call_only()
    {
        Assert.Equal("a|b", GrepRows.PatternOf("grep", """{"pattern":"a|b"}"""));
        Assert.Null(GrepRows.PatternOf("glob", """{"pattern":"a|b"}"""));
        Assert.Null(GrepRows.PatternOf("grep", "not json"));
    }

    //the rows
    [Fact]
    public void Each_path_shows_once_and_the_numbers_align_to_the_widest()
    {
        var rows = Body(Block(TwoFiles)).Select(Visible).ToList();
        Assert.Equal(new[]
        {
            $"{U.Box.Vertical} Repl\\Render\\ChromePainter.cs",
            $"{U.Box.Vertical}     7  n += Layout(Live(one), two);",
            $"{U.Box.Vertical}   120  var live = Layout(two);",
            $"{U.Box.Vertical} Repl\\Render\\TranscriptItem.cs",
            $"{U.Box.Vertical}    15  Layout(one);",
        }, rows);
    }

    [Fact]
    public void The_path_the_number_and_the_text_carry_their_inks()
    {
        var rows = Body(Block(TwoFiles, pattern: "zzz"));
        Assert.Contains(T.Paint("Repl\\Render\\ChromePainter.cs", Theme.ToolArgs), rows[0], StringComparison.Ordinal);
        Assert.Contains(T.Paint("  7", Theme.Dim), rows[1], StringComparison.Ordinal);
        Assert.Contains(T.Paint("n += Layout(Live(one), two);", Theme.CodeBlockFg), rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_row_counts_matches_and_files()
    {
        var row = Visible(Block(TwoFiles, open: false).Render(120, T, U)[1]);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} 3 matches in 2 files {U.Dot} {ShellBlockRender.ExpandWords} {U.Dot} ~", row, StringComparison.Ordinal);
        var one = Visible(Block("a.cs:1: one", open: false).Render(120, T, U)[1]);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} 1 match in 1 file {U.Dot}", one, StringComparison.Ordinal);
    }

    [Fact]
    public void No_match_reads_no_matches_and_opens_nothing()
    {
        var b = Block(Globbing.NoMatches);
        var rows = b.Render(120, T, U).Select(Visible).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal($"  {U.Elbow} {U.Ok} no matches", rows[1]);
    }

    [Fact]
    public void A_capped_result_says_so_on_the_row_and_ends_on_the_note()
    {
        var b = Block(TwoFiles + "\r\n[capped at 200 matches]");
        var result = b.Render(120, T, U)[1];
        Assert.Contains($"3 matches in 2 files {U.Dot} capped {U.Dot}", Visible(result), StringComparison.Ordinal);
        Assert.Contains(T.Paint("capped", Theme.Warn), result, StringComparison.Ordinal);
        b.View = ShellView.All;   //six rows, so the note shows above the footer in show all
        var last = Body(b)[^2];
        Assert.Equal($"{U.Box.Vertical} [capped at 200 matches]", Visible(last));
        Assert.Contains(T.Paint("[capped at 200 matches]", Theme.Dim), last, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_is_not_the_tools_opens_to_the_old_rows_whole()
    {
        const string hook = "a.cs:1: x\nand then prose";
        var b = Block(hook);
        var head = ItemRender.ToolRows("grep", "one|two", b.LinkLayout(120, T, U).Gloss, true, T, "coder", glyphs: U);
        var rows = b.Render(120, T, U);
        Assert.Equal(head, rows.Take(2));
        Assert.Equal(new[] { $"{U.Box.Vertical}   a.cs:1: x", $"{U.Box.Vertical}   and then prose" }, rows.Skip(2).Select(Visible));
        Assert.Contains(T.Paint("and then prose", Theme.CodeBlockFg), rows[3], StringComparison.Ordinal);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} 3 matches {U.Dot}", Visible(b.Render(120, T, U)[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_grep_keeps_todays_row()
    {
        var b = Block("invalid regex: bad", isError: true, open: false);
        Assert.Equal($"  {U.Elbow} {U.Bad} invalid regex: bad", Visible(b.Render(120, T, U)[1]));
    }

    [Fact]
    public void A_failed_call_whose_text_has_the_row_form_keeps_todays_rows()
    {
        var b = Block("a.cs:1: one\na.cs:2: two", isError: true);
        var rows = b.Render(120, T, U).Select(Visible).ToList();
        Assert.StartsWith($"  {U.Elbow} {U.Bad} a.cs:1: one", rows[1], StringComparison.Ordinal);
        Assert.Contains($"{U.Box.Vertical}   a.cs:2: two", rows);   //plain rows at the text column, not grouped
    }

    [Fact]
    public void Every_row_fits_keeps_the_rail_and_has_one_wrap()
    {
        var text = TwoFiles + "\r\nsrc\\deep\\folder\\with\\a\\long\\name\\File.cs:4: " + string.Join(' ', Enumerable.Range(0, 30).Select(i => $"word{i} two")) + "\r\n[capped at 200 matches]";
        foreach (var theme in new[] { T, T256 })
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        {
            var b = Block(text, theme: theme, g: g);
            for (var width = 30; width <= 120; width++)
            {
                var rows = b.Render(width, theme, g);
                Assert.Equal(rows.Count, b.RowWraps(width, theme, g).Count);
                foreach (var row in rows.Skip(2).Select(Visible))
                {
                    Assert.True(UnicodeWidth.Of(row) <= width, $"a row is wider than {width}: [{row}]");
                    Assert.StartsWith(g.Box.Vertical.ToString(), row, StringComparison.Ordinal);
                    if (ReferenceEquals(g, GlyphSet.Ascii)) Assert.True(row.All(c => c < 128), $"a row holds a glyph outside ASCII: [{row}]");
                }
            }
        }
    }

    [Fact]
    public void A_wrapped_match_hangs_under_its_text_with_copy_metadata()
    {
        var b = Block("a.cs:4: " + string.Join(' ', Enumerable.Range(0, 30).Select(i => $"word{i}")));
        b.View = ShellView.All;   //the window cuts a long row, show all wraps it above its footer
        var wraps = b.RowWraps(50, T, U).Skip(2).SkipLast(1).ToList();
        var rows = Body(b, 50).Select(Visible).SkipLast(1).ToList();
        Assert.True(rows.Count >= 3, "precondition: the match wrapped");
        Assert.All(rows.Skip(2), r => Assert.StartsWith($"{U.Box.Vertical}      ", r, StringComparison.Ordinal));
        Assert.Equal(7, wraps[1].LeadCells);
        Assert.All(wraps.Skip(2), w => Assert.True(w.Continuation && w.PrefixCells == 7));
    }

    //the marks
    [Fact]
    public void Each_span_the_pattern_matches_is_in_the_accent()
    {
        var rows = Body(Block(TwoFiles, pattern: "Layout|two"));
        Assert.Contains(T.Paint("Layout", Theme.Accent), rows[1], StringComparison.Ordinal);
        Assert.Contains(T.Paint("two", Theme.Accent), rows[1], StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rows[2], T.Paint("Layout", Theme.Accent)) + CountOf(rows[2], T.Paint("two", Theme.Accent)));
    }

    [Fact]
    public void The_marks_match_case_as_the_search_does()
    {
        var row = Body(Block("a.cs:1: Layout layout LAYOUT", pattern: "Layout"))[1];
        Assert.Equal(1, CountOf(row, T.Paint("Layout", Theme.Accent)));
        Assert.DoesNotContain(T.Paint("layout", Theme.Accent), row, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("LAYOUT", Theme.Accent), row, StringComparison.Ordinal);
    }

    private static int CountOf(string s, string needle) => (s.Length - s.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;

    [Fact]
    public void A_pattern_the_linear_engine_refuses_marks_nothing_and_the_rows_still_render()
    {
        var rows = Body(Block(TwoFiles, pattern: "(?<=Layout\\()two"));
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, r => r.Contains(T.Paint("two", Theme.Accent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pattern_that_backtracks_exponentially_still_renders_at_once()
    {
        var b = Block("a.cs:1: " + new string('a', 40) + "!", pattern: "(a+)+$");
        var render = Task.Run(() => b.Render(120, T, U));
        Assert.True(await Task.WhenAny(render, Task.Delay(TimeSpan.FromSeconds(5))) == render, "the render did not return");
    }

    //copy and replay
    [Fact]
    public void A_copy_of_two_match_rows_holds_the_text_only()
    {
        var s = new VtScreenSurface(120, 30);
        var model = new TranscriptModel("coder");
        model.Append(Block(TwoFiles));
        var p = new ChromePainter(s, T, new object(), model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        var y1 = s.Viewport.ToList().FindIndex(v => v.Contains("n += Layout", StringComparison.Ordinal));
        p.Mouse.Handle(new MouseEvent(2, y1, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y1 + 1, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y1 + 1, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal("n += Layout(Live(one), two);\nvar live = Layout(two);", p.Selection.CopyText(p.Width, T, null, U)?.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_live_block_and_its_replay_draw_the_same_rows()
    {
        var call = new ToolCall("c1", "grep", JsonSerializer.Serialize(new { pattern = "one|two" }));
        var result = new ToolResult(TwoFiles, Gloss: "3 matches");
        var s = new VtScreenSurface(120, 40);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var p = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
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
        convo.AddToolResult("c1", result);
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: U);
        var replay = rebuilt.Items.OfType<ToolBlockItem>().Single();

        live.Collapsed = false;
        replay.Collapsed = false;
        foreach (var width in new[] { 80, 120 })
        {
            var a = live.Render(width, T, U).Skip(live.LeadingBlank ? 1 : 0).ToList();
            Assert.Contains(a, row => row.Contains(T.Paint("two", Theme.Accent), StringComparison.Ordinal));
            Assert.Equal(a, replay.Render(width, T, U).Skip(replay.LeadingBlank ? 1 : 0));
        }
    }
}
