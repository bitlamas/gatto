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

public sealed class EditBlockTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static ToolBlockItem Item(string name, ToolResult r, bool open = true, string? content = null, CodeLanguage language = CodeLanguage.None,
        string? oldS = null, string? newS = null, EditView? view = null) =>
        new(name, "a.cs", ItemRender.ToolGloss(r, T, U), true, "coder")
        {
            Collapsed = !open, FullResult = r.Text, RawGloss = r.Gloss, Parts = ItemRender.ToolGlossParts(r, T, U),
            WriteContent = content, FileLanguage = language, EditOld = oldS, EditNew = newS, EditAt = view, IsError = r.IsError,
        };

    private static ToolResult Wrote => new("wrote 10 bytes to a.cs", Gloss: "wrote 10 bytes");

    //a closed block's rows are cut to the window as they are composed, so no row leans on the frame's clip to fit
    [Fact]
    public void No_row_of_a_closed_block_is_wider_than_the_window()
    {
        var item = Item("read_file", new ToolResult(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}")), Gloss: "40 lines"), open: false) with { Args = "{\"path\":\"some\\\\long\\\\folder\\\\name\\\\file.cs\"}" };
        for (var width = 8; width <= 40; width++)
            Assert.All(item.Render(width, T, U), row => Assert.True(UnicodeWidth.Of(Visible(row)) <= width, $"{width}: {Visible(row)}"));
    }

    //the row takes its word from the tool's gloss, so a write over a file says so on the block
    [Fact]
    public void A_write_over_a_file_says_overwrote()
    {
        var rows = Item("write_file", new ToolResult("wrote 10 bytes to a.cs", Gloss: "overwrote 10 bytes"), content: "a\nb\n").Render(100, T, U);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} overwrote {U.Dot} 2 lines", Visible(rows[1]), StringComparison.Ordinal);
    }

    //the write
    [Fact]
    public void A_write_says_wrote_and_its_lines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 81).Select(i => $"line {i}")) + "\n";
        var rows = Item("write_file", Wrote, content: content).Render(100, T, U);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} wrote {U.Dot} 81 lines {U.Dot} {ShellBlockRender.CollapseWords}", Visible(rows[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("10 bytes", Visible(rows[1]), StringComparison.Ordinal);
        Assert.Equal($"{U.Box.Vertical}    1  line 1", Visible(rows[2]));
        Assert.Contains($"{U.Ellipsis} 76 more", Visible(rows[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_keeps_the_syntax_colour_of_its_language_on_every_row()
    {
        var content = "var s = @\"first\nsecond\";\nvar n = 2;";
        var rows = Item("write_file", Wrote, content: content, language: CodeLanguage.CSharp).Render(100, T, U);
        var second = rows.Single(r => Visible(r).Contains("second", StringComparison.Ordinal));
        Assert.Contains(Ansi.Fg(Theme.VsString, true) + "second", second, StringComparison.Ordinal);
        var third = rows.Single(r => Visible(r).Contains("var n", StringComparison.Ordinal));
        Assert.Contains(T.Paint("var", Theme.VsKeyword), third, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_write_has_no_body_and_no_link()
    {
        var b = Item("write_file", new ToolResult("wrote 0 bytes to a.cs", Gloss: "wrote 0 bytes"), content: "");
        Assert.False(b.HasBody);
        var rows = b.Render(100, T, U);
        Assert.Equal(2, rows.Count);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} wrote", Visible(rows[1]), StringComparison.Ordinal);
        var l = b.LinkLayout(100, T, U);
        Assert.Equal(l.LinkStart, l.LinkEnd);
    }

    //a call that did not happen
    [Fact]
    public void A_denied_write_opens_to_its_result_text_in_the_error_ink()
    {
        var b = Item("write_file", new ToolResult("The user declined this tool call.\nsecond line", IsError: true), content: "secret content\nmore");
        var rows = b.Render(100, T, U);
        Assert.Equal(new[] { $"{U.Box.Vertical}   The user declined this tool call.", $"{U.Box.Vertical}   second line" }, rows.Skip(2).Select(Visible));
        Assert.Contains(T.Paint("second line", Theme.Err), rows[3], StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => Visible(r).Contains("secret content", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_edit_opens_to_its_error_in_the_error_ink()
    {
        var b = Item("edit_file", new ToolResult("old_string not found in a.cs", IsError: true), oldS: "x", newS: "y");
        var rows = b.Render(100, T, U);
        Assert.Contains(T.Paint("old_string not found in a.cs", Theme.Err), rows[^1], StringComparison.Ordinal);
        Assert.True(b.HasBody);
    }

    //at rest
    private static (StreamRenderer R, TranscriptModel Model) Live()
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
        return (r, model);
    }

    public static IEnumerable<object[]> Calls() => new[]
    {
        new object[] { "write_file", JsonSerializer.Serialize(new { path = "a.cs", content = "x\ny\n" }), false, false },
        new object[] { "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = "x", new_string = "y" }), false, false },
        new object[] { "read_file", JsonSerializer.Serialize(new { path = "a.cs" }), false, true },
        new object[] { "write_file", JsonSerializer.Serialize(new { path = "a.cs", content = "x" }), true, true },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public void A_successful_write_and_edit_commit_open_and_every_other_block_closed(string name, string args, bool error, bool closed)
    {
        var call = new ToolCall("c1", name, args);
        var result = error ? new ToolResult("The user declined this tool call.", IsError: true) : new ToolResult("done\nsecond", Gloss: "ok");
        var (r, model) = Live();
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, result);
        r.EndTurn();
        Assert.Equal(closed, model.Items.OfType<ToolBlockItem>().Single().Collapsed);

        var convo = new Gatto.Core.Loop.Conversation("sys");
        convo.AddUser("go");
        convo.AddAssistant("", new[] { call });
        convo.AddToolResult(call.Id, result);
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: U);
        Assert.Equal(closed, rebuilt.Items.OfType<ToolBlockItem>().Single().Collapsed);
    }

    //the edit
    private static readonly EditView At14 = new(14, "", "", new[] { "line 11", "line 12", "line 13" }, new[] { "line 15", "line 16", "line 17" });

    private static ToolResult Edited(EditView? view = null) => new("edited a.cs", Gloss: "ok", View: view);

    private static ToolBlockItem OneLine(bool open = true) => Item("edit_file", Edited(At14), open, oldS: "old line 14", newS: "new line 14", view: At14);

    private static string Fourteen => string.Join("\n", Enumerable.Range(1, 14).Select(i => $"line {i}"));

    //lines 2, 4, 6, 8, 10 and 12 changed, each between two lines that stay: 20 diff rows, changes at 1-2, 4-5, 7-8, 10-11, 13-14, 16-17
    private static string SixChanged => string.Join("\n", Enumerable.Range(1, 14).Select(i => i % 2 == 0 && i <= 12 ? $"line {i} changed" : $"line {i}"));

    private static ToolBlockItem Six(bool open = true) => Item("edit_file", Edited(), open, oldS: Fourteen, newS: SixChanged);

    private static (int Above, int More) Counts(string footer)
    {
        var above = System.Text.RegularExpressions.Regex.Match(footer, @"(\d+) above");
        var more = System.Text.RegularExpressions.Regex.Match(footer, @"(\d+) more");
        return (above.Success ? int.Parse(above.Groups[1].Value) : 0, more.Success ? int.Parse(more.Groups[1].Value) : 0);
    }

    [Fact]
    public void A_one_line_edit_rests_open_on_its_pair_with_the_files_numbers()
    {
        var rows = OneLine().Render(100, T, U).Select(Visible).ToList();
        Assert.Equal(new[]
        {
            $"{U.Box.Vertical}   13    line 13",
            $"{U.Box.Vertical}   14 -  old line 14",
            $"{U.Box.Vertical}   14 +  new line 14",
            $"{U.Box.Vertical}   15    line 15",
            $"{U.Box.Vertical}   16    line 16",
        }, rows.Skip(2).Take(5).Select(r => r.TrimEnd()));
        Assert.Equal($"{U.Box.Vertical}   {U.Up} 2 above {U.Dot} {U.Ellipsis} 1 more {U.Dot} show all", rows[^1]);
    }

    [Fact]
    public void The_row_says_edited_and_the_counts()
    {
        var row = Visible(OneLine().Render(100, T, U)[1]);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} edited {U.Dot} +1 -1 {U.Dot} {ShellBlockRender.CollapseWords}", row, StringComparison.Ordinal);
        Assert.DoesNotContain(" ok", row, StringComparison.Ordinal);
    }

    [Fact]
    public void K_changes_shows_above_1()
    {
        var row = Visible(Six().Render(100, T, U)[1]);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} edited {U.Dot} +6 -6 {U.Dot} 6 changes {U.Dot} {ShellBlockRender.CollapseWords}", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_footer_counts_the_other_changes_and_a_change_cut_by_the_edge_is_one()
    {
        var rows = Six().Render(100, T, U).Select(Visible).ToList();
        Assert.Equal($"{U.Box.Vertical}   {U.Ellipsis} 15 more {U.Dot} 5 other changes {U.Dot} show all", rows[^1]);
        var one = OneLine().Render(100, T, U).Select(Visible).ToList();
        Assert.DoesNotContain("other change", one[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrow_edit_footer_drops_the_other_changes_and_keeps_show_all_whole()
    {
        var full = $"{U.Box.Vertical}   {U.Ellipsis} 15 more {U.Dot} 5 other changes {U.Dot} show all";
        var bare = $"{U.Box.Vertical}   {U.Ellipsis} 15 more {U.Dot} show all";
        for (var width = UnicodeWidth.Of(bare); width <= 100; width++)
            Assert.Equal(UnicodeWidth.Of(full) <= width ? full : bare, Visible(Six().Render(width, T, U)[^1]));
    }

    [Fact]
    public void A_narrow_shell_footer_drops_the_rows_above_and_keeps_show_all_whole()
    {
        var shell = new ToolBlockItem("shell", "cmd", "✓", true, "coder")
        {
            FullArgs = "cmd", FullResult = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"out {i}")), RawGloss = "exit 0", Collapsed = false, OutputTop = 10,
        };
        var full = $"{U.Box.Vertical}   {U.Up} 10 above {U.Dot} {U.Ellipsis} 15 more {U.Dot} show all";
        var bare = $"{U.Box.Vertical}   {U.Ellipsis} 15 more {U.Dot} show all";
        for (var width = UnicodeWidth.Of(bare); width <= 100; width++)
            Assert.Equal(UnicodeWidth.Of(full) <= width ? full : bare, Visible(shell.Render(width, T, U)[^1]));
    }

    [Fact]
    public void Above_plus_shown_plus_more_is_the_total()
    {
        var b = Six();
        var max = ToolWindow.MaxTop(ToolBody.Edit(LineDiff.Of(Fourteen.Split('\n'), SixChanged.Split('\n')), null));
        for (var top = 0; top <= max; top++)
        {
            b.OutputTop = top;
            var (above, more) = Counts(Visible(b.Render(100, T, U)[^1]));
            Assert.Equal(20, above + ToolWindow.Height + more);
        }
    }

    private static (FocusController Fc, int Index) Focus(ToolBlockItem b)
    {
        var model = new TranscriptModel("coder");
        var index = new LineIndex(model, T, glyphs: U);
        var scroll = new ScrollController(model, index);
        var compositor = new ViewportCompositor(new RecordingSurface { Width = 100, Height = 30 }, index, new object(), glyphs: U);
        var fc = new FocusController(model, index, scroll, compositor);
        model.Append(b);
        return (fc, 0);
    }

    [Fact]
    public void Closing_and_opening_returns_to_the_rest_position()
    {
        var b = OneLine();
        Assert.Equal(2, b.OutputTop);
        var (fc, i) = Focus(b);
        b.OutputTop = 3;
        fc.Toggle(i, 100, 30);
        fc.Toggle(i, 100, 30);
        Assert.Equal((false, 2), (b.Collapsed, b.OutputTop));
        b.OutputTop = 3;
        fc.Focus(i, 100, 30);
        fc.ToggleFocused(100, 30);   //window to show all
        fc.ToggleFocused(100, 30);   //show all to closed
        fc.ToggleFocused(100, 30);   //closed to the window
        Assert.Equal((false, ShellView.Window, 2), (b.Collapsed, b.View, b.OutputTop));
    }

    [Fact]
    public void Show_all_then_show_less_returns_to_the_rest_position()
    {
        var b = OneLine();
        var (fc, i) = Focus(b);
        b.OutputTop = 3;
        fc.ShowAll(i, true, 100, 30);
        fc.ShowAll(i, false, 100, 30);
        Assert.Equal((ShellView.Window, 2), (b.View, b.OutputTop));
    }

    //sixty rows of prose sit above the block, so a notch the window hands on has a transcript to scroll
    private static (ChromePainter P, VtScreenSurface S, ToolBlockItem B) Painted(ToolCall call, ToolResult result)
    {
        var s = new VtScreenSurface(120, 40);
        var gate = new object();
        var model = new TranscriptModel("coder");
        model.Append(new AssistantBlockItem(Enumerable.Range(0, 60).Select(i => $"prose {i}").ToArray(), "coder"));
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
        p.Repaint();
        return (p, s, model.Items.OfType<ToolBlockItem>().Single());
    }

    private static ToolCall SixCall => new("c1", "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = Fourteen, new_string = SixChanged }));

    private static int RowOf(VtScreenSurface s, string needle)
    {
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(needle, StringComparison.Ordinal));
        Assert.True(y >= 0, $"no row holds {needle}:\n{string.Join("\n", s.Viewport.Select(v => v.TrimEnd()))}");
        return y;
    }

    [Fact]
    public void The_wheel_moves_the_window_of_a_focused_edit_block()
    {
        var (p, s, b) = Painted(SixCall, Edited());
        Assert.Equal(0, b.OutputTop);
        p.Focus.Focus(p.Model.Items.ToList().IndexOf(b), p.Width, p.ViewportRows(), reveal: false);
        p.Repaint();
        p.Mouse.Handle(new MouseEvent(10, RowOf(s, "line 2 changed"), MouseKind.Wheel, MouseButton.None, -120, 0), p.Width, p.ViewportRows());
        p.Repaint();
        Assert.Equal(3, b.OutputTop);
    }

    [Fact]
    public void A_press_on_show_all_lands_on_the_action_with_the_part_before_it()
    {
        var (p, s, b) = Painted(SixCall, Edited());
        var y = RowOf(s, "other changes");
        var x = s.Viewport[y].IndexOf("show all", StringComparison.Ordinal);
        p.Mouse.Handle(new MouseEvent(x - 2, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x - 2, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal(ShellView.Window, b.View);
        p.Mouse.Handle(new MouseEvent(x + 2, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x + 2, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal(ShellView.All, b.View);
    }

    [Fact]
    public void A_copy_of_changed_rows_holds_their_text_only()
    {
        var call = new ToolCall("c1", "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = "old line 14", new_string = "new line 14" }));
        var (p, s, _) = Painted(call, Edited(At14));
        var y1 = RowOf(s, "old line 14");
        var y2 = RowOf(s, "new line 14");
        p.Mouse.Handle(new MouseEvent(0, y1, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y2, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y2, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal("old line 14\nnew line 14", p.Selection.CopyText(p.Width, T, null, U)?.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_denied_edit_shows_todays_row()
    {
        var call = new ToolCall("c1", "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = "x", new_string = "y" }));
        var (r, model) = Live();
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.NotePermissionOutcome(PermissionOutcomeKind.Denied);
        r.OnToolResult(call, new ToolResult("The user declined this tool call.", IsError: true));
        r.EndTurn();
        var b = model.Items.OfType<ToolBlockItem>().Single();
        Assert.True(b.Collapsed);
        Assert.Equal($"  {U.Elbow} {U.Bad} denied", Visible(b.Render(100, T, U)[1]).TrimEnd());
    }

    public static IEnumerable<object?[]> Refusals() => new[]
    {
        new object?[] { PermissionOutcomeKind.Denied, null, "blocked: " + Gatto.Core.Loop.Permissions.PermissionGate.DenyNudge },
        new object?[] { PermissionOutcomeKind.Denied, "use read_file", "blocked: " + Gatto.Core.Loop.Permissions.PermissionGate.DenyWithReasonPrefix + "use read_file" },
        new object?[] { PermissionOutcomeKind.Cancelled, null, "blocked: " + Gatto.Core.Loop.Permissions.PermissionGate.CancelMessage },
    };

    //a refusal restored from the session file draws the row it drew live, read back from the gate's own words in the record
    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refused_call_restores_as_it_drew_live(PermissionOutcomeKind outcome, string? reason, string text)
    {
        var call = new ToolCall("c1", "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = "x", new_string = "y" }));
        var result = new ToolResult(text, IsError: true);
        var (r, model) = Live();
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.NotePermissionOutcome(outcome, reason);
        r.OnToolResult(call, result);
        r.EndTurn();
        var live = model.Items.OfType<ToolBlockItem>().Single();

        var convo = new Gatto.Core.Loop.Conversation("sys");
        convo.AddUser("go");
        convo.AddAssistant("", new[] { call });
        convo.AddToolResult(call.Id, result);
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: U);
        var restored = rebuilt.Items.OfType<ToolBlockItem>().Single();
        live.LeadingBlank = restored.LeadingBlank = false;

        Assert.Equal(live.Render(100, T, U), restored.Render(100, T, U));
        Assert.True(restored.Refused);
    }

    //a result hook that appends a paragraph to a refusal, as the grounding nudge does, leaves the refusal readable on restore
    [Fact]
    public void A_refusal_with_an_appended_paragraph_still_reads_as_refused()
    {
        var gate = "blocked: " + Gatto.Core.Loop.Permissions.PermissionGate.DenyWithReasonPrefix + "use read_file";
        Assert.Equal((PermissionOutcomeKind.Denied, "use read_file"), Refusal.Of(new ToolResult(gate + "\n\nground first", IsError: true)));
        Assert.Equal((PermissionOutcomeKind.Cancelled, (string?)null),
            Refusal.Of(new ToolResult("blocked: " + Gatto.Core.Loop.Permissions.PermissionGate.CancelMessage + "\n\nground first", IsError: true)));
    }

    //a call the stopped turn never reached restores as cancelled, not as a plain error
    [Fact]
    public void A_call_a_stop_never_reached_restores_as_cancelled()
    {
        Assert.Equal((PermissionOutcomeKind.Cancelled, (string?)null), Refusal.Of(new ToolResult(Gatto.Core.Loop.LoopErrors.Cancelled, IsError: true)));
        Assert.Equal(((PermissionOutcomeKind?)null, (string?)null), Refusal.Of(new ToolResult("cancelled", IsError: false)));
    }

    public static IEnumerable<object?[]> Views() => new[] { new object?[] { At14 }, new object?[] { null } };

    [Theory]
    [MemberData(nameof(Views))]
    public void A_live_edit_and_its_replay_draw_the_same_rows(EditView? view)
    {
        var call = new ToolCall("c1", "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = "old line 14", new_string = "new line 14" }));
        var (r, model) = Live();
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, Edited(view));
        r.EndTurn();
        var live = model.Items.OfType<ToolBlockItem>().Single();

        var convo = new Gatto.Core.Loop.Conversation("sys");
        convo.AddUser("go");
        convo.AddAssistant("", new[] { call });
        convo.AddToolResult(call.Id, Edited(view));
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: U);
        var replay = rebuilt.Items.OfType<ToolBlockItem>().Single();
        live.LeadingBlank = replay.LeadingBlank = false;
        foreach (var w in new[] { 80, 120 })
            Assert.Equal(live.Render(w, T, U), replay.Render(w, T, U));
        Assert.Equal(view is null ? "-  old line 14" : "14 -  old line 14",
            live.Render(120, T, U).Select(Visible).First(v => v.Contains("old line 14", StringComparison.Ordinal))[4..].TrimEnd().TrimStart());
    }

    [Fact]
    public void A_block_with_no_view_draws_from_the_arguments()
    {
        var args = JsonSerializer.Serialize(new { path = "a.cs", old_string = "foo(1)", new_string = "foo(2)" });
        var lines = new[]
        {
            """{"role":"system","content":"sys"}""",
            """{"role":"user","content":"go"}""",
            "{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"c1\",\"name\":\"edit_file\",\"arguments\":" + JsonSerializer.Serialize(args) + "}]}",
            """{"role":"tool","content":"edited a.cs","tool_call_id":"c1","gloss":"ok"}""",
        };
        var (_, model) = TranscriptStore.Rebuild(lines, T, "coder", glyphs: U);
        var rows = model.Items.OfType<ToolBlockItem>().Single().Render(100, T, U).Select(Visible).ToList();
        Assert.Contains($"{U.Box.Vertical}   -  foo(1)", rows.Select(r => r.TrimEnd()));
        Assert.Contains($"{U.Box.Vertical}   +  foo(2)", rows.Select(r => r.TrimEnd()));
    }

    [Fact]
    public void A_copy_with_another_view_draws_its_own_rows()
    {
        var b = OneLine();
        var first = b.Render(100, T, U);
        var moved = b with { EditAt = At14 with { Start = 40, Before = new[] { "a", "b", "c" } } };
        Assert.NotEqual(first, moved.Render(100, T, U));
        Assert.Contains(moved.Render(100, T, U).Select(Visible), r => r.Contains("40 -  old line 14", StringComparison.Ordinal));
    }
}
