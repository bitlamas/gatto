using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Render;
using Xunit;

namespace Gatto.Tests;

public sealed class EditPromptTests : IDisposable
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-prompt-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Probe(Action<int> probe, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        private int _n;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            probe(_n++);
            return _q.Dequeue();
        }
    }

    //the detail rows of the panel, raw and plain, taken between its two rules while the prompt is open, on a window tall enough for the full cap
    private static (List<string> Plain, List<string> Raw) Detail(PermissionRequest request, int width = 100, Theme? theme = null)
    {
        var th = theme ?? T;
        var surface = new RecordingSurface { Width = width, Height = 200 };
        var gate = new object();
        var painter = new ChromePainter(surface, th, gate)
        {
            Frame = new InputFrame(surface, th, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        var renderer = new StreamRenderer(painter, surface, th, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null);
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(surface, th,
            new Probe(n => { if (n == 0) live = painter.State.PanelRows; }, new[] { new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false) }),
            pump: null, chrome: new ChromeHandle { Painter = painter, Renderer = renderer });
        prompter.Ask(request);
        var raw = live!.ToList();
        var plain = raw.Select(r => TermText.StripAnsiForWidth(r).TrimEnd()).ToList();
        var rules = plain.Select((p, i) => (p, i)).Where(t => t.p.Length == width && t.p.Distinct().Count() == 1).Select(t => t.i).ToList();
        Assert.True(rules.Count >= 2, "the panel holds no ruled detail block");
        return (plain.Skip(rules[0] + 1).Take(rules[1] - rules[0] - 1).ToList(), raw.Skip(rules[0] + 1).Take(rules[1] - rules[0] - 1).ToList());
    }

    private static readonly EditView At14 = new(14, "", "", new[] { "line 11", "line 12", "line 13" }, new[] { "line 16", "line 17", "line 18" });

    private static PermissionRequest Edit(string oldS, string newS, EditView? view) =>
        new("edit_file", @"C:\proj\a.cs (10 bytes)", @"C:\proj", EditOld: oldS, EditNew: newS, View: view);

    private static string Lines(string prefix, int n, int from = 1) => string.Join("\n", Enumerable.Range(from, n).Select(i => $"{prefix} {i}"));

    [Fact]
    public void The_panel_of_a_two_line_edit()
    {
        foreach (var width in new[] { 60, 100, 160 })
        {
            var (plain, raw) = Detail(Edit("old a\nold b", "new a\nnew b", At14), width);
            Assert.Equal(new[]
            {
                "  11    line 11", "  12    line 12", "  13    line 13",
                "  14 -  old a", "  15 -  old b", "  14 +  new a", "  15 +  new b",
                "  16    line 16", "  17    line 17", "  18    line 18",
            }, plain);
            Assert.Equal(GroundWalker.Code(Theme.DiffRemovedBg, true), GroundWalker.Of(raw[3], width)[5]);
            Assert.Equal(GroundWalker.Code(Theme.DiffAddedBg, true), GroundWalker.Of(raw[5], width)[width - 1]);
            Assert.All(GroundWalker.Of(raw[0], width), c => Assert.Null(c));
        }
    }

    [Fact]
    public void Every_change_is_in_view_when_the_rows_fit()
    {
        var (plain, _) = Detail(Edit("a\nb\nc\nd", "A\nb\nC\nd", null));
        Assert.Equal(new[] { "  -  a", "  +  A", "     b", "  -  c", "  +  C", "     d" }, plain);
    }

    [Fact]
    public void Fourteen_lines_by_fourteen_others_shows_an_added_row()
    {
        var (plain, _) = Detail(Edit(Lines("old", 14), Lines("new", 14), null));
        Assert.Equal(14, plain.Count);
        Assert.Equal(Enumerable.Range(9, 6).Select(i => $"  -  old {i}").Concat(Enumerable.Range(1, 7).Select(i => $"  +  new {i}")), plain.Take(13));
        Assert.Equal($"  {U.Ellipsis} +7 added, +8 removed", plain[13]);
    }

    [Fact]
    public void Two_changes_forty_lines_apart_stand_in_file_order_with_a_gap_row()
    {
        var old = Lines("line", 42);
        var neu = string.Join("\n", Enumerable.Range(1, 42).Select(i => i is 2 or 41 ? $"line {i} changed" : $"line {i}"));
        var (plain, _) = Detail(Edit(old, neu, null));
        Assert.True(plain.Count <= 14, $"{plain.Count} rows");
        Assert.Equal(1, plain.Count(p => p == $"  {U.MidEllipsis}"));
        foreach (var row in new[] { "  -  line 2", "  +  line 2 changed", "  -  line 41", "  +  line 41 changed" })
            Assert.Contains(row, plain);
        Assert.True(plain.IndexOf("  +  line 2 changed") < plain.IndexOf($"  {U.MidEllipsis}"));
        Assert.True(plain.IndexOf($"  {U.MidEllipsis}") < plain.IndexOf("  -  line 41"));
        Assert.DoesNotContain(plain, p => p.Contains("added", StringComparison.Ordinal));
    }

    //five changes of two removed and two added rows, each after one line that stays
    private static (string Old, string New) FiveChanges()
    {
        var old = new List<string>();
        var neu = new List<string>();
        for (var k = 1; k <= 5; k++)
        {
            old.Add($"stay {k}"); neu.Add($"stay {k}");
            old.Add($"r{k}.1"); old.Add($"r{k}.2");
            neu.Add($"a{k}.1"); neu.Add($"a{k}.2");
        }
        old.Add("stay end"); neu.Add("stay end");
        return (string.Join("\n", old), string.Join("\n", neu));
    }

    [Fact]
    public void Five_changes_of_four_rows_show_every_change()
    {
        var (o, n) = FiveChanges();
        var (plain, _) = Detail(Edit(o, n, null));
        var gap = $"  {U.MidEllipsis}";
        Assert.Equal(new[]
        {
            "  -  r1.2", "  +  a1.1", gap, "  -  r2.2", "  +  a2.1", gap, "  -  r3.2", "  +  a3.1", gap,
            "  -  r4.2", "  +  a4.1", gap, "  +  a5.1", $"  {U.Ellipsis} +5 added, +6 removed",
        }, plain);
    }

    [Fact]
    public void The_panel_never_holds_more_than_14_detail_rows()
    {
        for (var changes = 1; changes <= 6; changes++)
            for (var rows = 1; rows <= 40; rows += 3)
            {
                var old = new List<string>();
                var neu = new List<string>();
                for (var k = 0; k < changes; k++)
                {
                    old.Add($"stay {k}"); neu.Add($"stay {k}");
                    for (var r = 0; r < rows; r++) { old.Add($"o{k}.{r}"); neu.Add($"n{k}.{r}"); }
                }
                var (plain, _) = Detail(Edit(string.Join("\n", old), string.Join("\n", neu), null));
                Assert.True(plain.Count <= 14, $"{changes} changes of {rows}: {plain.Count} rows");
            }
    }

    //the literal screen while the prompt is open, read on its first key and again after each resize the list holds
    private static List<List<string>> Screens(PermissionRequest request, int width, int height, params (int W, int H)[] resizes) =>
        Paints(request, width, height, resizes).Select(p => p.Screen).ToList();

    private static List<(List<string> Screen, int Room, int Panel)> Paints(PermissionRequest request, int width, int height, params (int W, int H)[] resizes)
    {
        var s = new VtScreenSurface(width, height);
        var gate = new object();
        var painter = new ChromePainter(s, T, gate)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        painter.AltScreen.Enter();
        painter.Repaint();
        var renderer = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null);
        var screens = new List<(List<string>, int, int)>();
        void Read()
        {
            painter.Repaint();
            screens.Add((s.Viewport.Select(r => r.TrimEnd()).ToList(), painter.PanelRowsAvailable(), painter.State.PanelRows!.Count));
        }
        var prompter = new RichPermissionPrompter(s, T,
            new Probe(n =>
            {
                if (n != 0) return;
                Read();
                foreach (var (w, h) in resizes)
                {
                    s.Resize(w, h);
                    Read();
                }
            }, new[] { new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false) }),
            pump: null, chrome: new ChromeHandle { Painter = painter, Renderer = renderer });
        prompter.Ask(request);
        return screens;
    }

    //60 rows for 60, each row longer than the window, so every detail row takes two rows as drawn
    private static PermissionRequest WideEdit(int width)
    {
        var tail = string.Concat(Enumerable.Repeat("lorem ", width / 5));
        return Edit(string.Join("\n", Enumerable.Range(1, 60).Select(i => $"removed {i:00} {tail}")),
            string.Join("\n", Enumerable.Range(1, 60).Select(i => $"added {i:00} {tail}")), null);
    }

    private static void AssertThePromptIsWhole(List<string> screen, string where)
    {
        var dump = where + "\n" + string.Join("\n", screen);
        Assert.True(screen.Any(r => r.StartsWith("  Edit file ", StringComparison.Ordinal)), "no title, " + dump);
        Assert.True(screen.Any(r => r.StartsWith("  Do you want to edit ", StringComparison.Ordinal)), "no question, " + dump);
        Assert.True(screen.Any(r => r.Contains("1. Yes", StringComparison.Ordinal)), "no first option, " + dump);
        Assert.True(screen.Any(r => r.Contains("Esc to cancel", StringComparison.Ordinal)), "no footer, " + dump);
        var removed = screen.Count(r => r.StartsWith("  -  removed ", StringComparison.Ordinal));
        var added = screen.Count(r => r.StartsWith("  +  added ", StringComparison.Ordinal));
        Assert.True(removed > 0 && added > 0, $"removed {removed}, added {added}, " + dump);
        var count = screen.Single(r => r.StartsWith($"  {U.Ellipsis} +", StringComparison.Ordinal));
        Assert.Equal($"  {U.Ellipsis} +{60 - added} added, +{60 - removed} removed", count);
    }

    [Fact]
    public void A_prompt_whose_rows_wrap_keeps_its_title_and_options_in_view()
    {
        foreach (var width in new[] { 60, 90, 128, 200 })
            foreach (var height in new[] { 24, 30, 40 })
                AssertThePromptIsWhole(Screens(WideEdit(width), width, height).Single(), $"{width}x{height}");
    }

    //one more detail row would take two rows as drawn, so a detail short of the full 13 leaves less than two rows of its room unused
    [Fact]
    public void The_detail_takes_all_the_room_that_is_left()
    {
        foreach (var width in new[] { 60, 128 })
        {
            var shown = new List<int>();
            for (var height = 22; height <= 50; height++)
            {
                var (screen, room, panel) = Paints(WideEdit(width), width, height).Single();
                AssertThePromptIsWhole(screen, $"{width}x{height}");
                var firsts = screen.Count(r => r.StartsWith("  -  removed ", StringComparison.Ordinal) || r.StartsWith("  +  added ", StringComparison.Ordinal));
                Assert.True(panel <= room, $"{width}x{height}: a panel of {panel} rows in a room of {room}");
                if (firsts < 13) Assert.True(room - panel < 2, $"{width}x{height}: {firsts} detail rows leave {room - panel} rows of {room} unused");
                shown.Add(firsts);
            }
            Assert.Equal(13, shown[^1]);
        }
    }

    private static void AssertTitleQuestionAndOptions(List<string> screen, string title, string question, string where)
    {
        var dump = where + "\n" + string.Join("\n", screen);
        Assert.True(screen.Any(r => r.StartsWith("  " + title, StringComparison.Ordinal)), "no title, " + dump);
        Assert.True(screen.Any(r => r.StartsWith("  " + question, StringComparison.Ordinal)), "no question, " + dump);
        Assert.True(screen.Any(r => r.Contains("1. Yes", StringComparison.Ordinal)), "no first option, " + dump);
        Assert.True(screen.Any(r => r.Contains("Esc to cancel", StringComparison.Ordinal)), "no footer, " + dump);
    }

    //every tool's prompt keeps its title, question and options on a short window, the detail gives up its rows and says how many lines it hides
    [Fact]
    public void Every_prompt_keeps_its_title_question_and_options_on_a_short_window()
    {
        var command = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Write-Output {i}"));
        var shell = new PermissionRequest("shell", command, null);
        var preview = Enumerable.Range(1, 5).Select(i => $"line {i}").ToList();
        var write = new PermissionRequest("write_file", @"C:\proj\a.txt (+40 lines)", @"C:\proj",
            PreviewLines: preview, PreviewTotalLines: 40, Existing: new ExistingFile(12, 200));
        //from 16 rows the panel's budget holds the title, the question and three options with no detail, and below that the budget decides
        for (var height = 16; height <= 30; height++)
        {
            var s = Screens(shell, 100, height).Single();
            AssertTitleQuestionAndOptions(s, "shell command", "Do you want to proceed?", $"shell 100x{height}");
            var shown = s.Count(r => r.StartsWith("  Write-Output ", StringComparison.Ordinal));
            if (shown is > 0 and < 40)
                Assert.Contains(s, r => r.StartsWith($"  {U.Ellipsis} +{40 - shown} lines", StringComparison.Ordinal));
            AssertTitleQuestionAndOptions(Screens(write, 100, height).Single(), "Overwrite file ", "Do you want to overwrite ", $"write 100x{height}");
            AssertTitleQuestionAndOptions(Screens(WideEdit(100), 100, height).Single(), "Edit file ", "Do you want to edit ", $"edit 100x{height}");
        }
    }

    //a shell command past the detail's line cap says how many lines it does not show, a command is never cut silently
    [Fact]
    public void A_long_shell_command_counts_the_lines_it_cuts()
    {
        var command = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"Write-Output {i}"));
        var (_, _, detail) = PromptTitles.For(new PermissionRequest("shell", command, null), null, @"C:\proj", U);
        Assert.Equal(21, detail.Count);
        Assert.Equal($"{U.Ellipsis} +5 lines", detail[^1].Text);
    }

    [Fact]
    public void A_tall_window_still_holds_the_prompt_to_14_detail_rows()
    {
        foreach (var width in new[] { 60, 128 })
        {
            var screen = Screens(WideEdit(width), width, 80).Single();
            AssertThePromptIsWhole(screen, $"{width}x80");
            var firsts = screen.Count(r => r.StartsWith("  -  removed ", StringComparison.Ordinal) || r.StartsWith("  +  added ", StringComparison.Ordinal));
            Assert.Equal(13, firsts);
        }
    }

    [Fact]
    public void A_resize_with_the_prompt_open_refits_the_detail()
    {
        var screens = Screens(WideEdit(128), 128, 80, (128, 30), (128, 80));
        for (var i = 0; i < screens.Count; i++) AssertThePromptIsWhole(screens[i], $"screen {i}");
        Assert.Equal(screens[0], screens[2]);
        Assert.True(screens[1].Count(r => r.StartsWith("  +  added ", StringComparison.Ordinal)) < screens[0].Count(r => r.StartsWith("  +  added ", StringComparison.Ordinal)));
    }

    //the gate
    private sealed class Capture : IPermissionPrompter
    {
        public List<PermissionRequest> Requests { get; } = new();
        public PermissionAnswer Ask(PermissionRequest request) { Requests.Add(request); return PermissionAnswer.Once; }
    }

    private (PermissionGate Gate, Capture Prompter, List<string> Reads) Gate(bool grant = false)
    {
        var store = PermissionStore.Load(_dir, out _);
        if (grant) store.GrantWriteDir(_dir, persist: false);
        var prompter = new Capture();
        var reads = new List<string>();
        var gate = new PermissionGate(store, prompter, autoYes: false) { ReadFile = p => { reads.Add(p); return File.ReadAllText(p); } };
        return (gate, prompter, reads);
    }

    private static HookPayload Call(string path, string oldS, string newS) =>
        new(Call: new ToolCall("c1", "edit_file", JsonSerializer.Serialize(new { path, old_string = oldS, new_string = newS })));

    //a file that is there but cannot be read still gets its prompt, the tool's own read decides
    [Fact]
    public async Task A_file_the_gate_cannot_read_gives_rows_with_no_number_and_a_prompt()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "locked.cs"), "old a\n");
        var store = PermissionStore.Load(_dir, out _);
        var prompter = new Capture();
        var gate = new PermissionGate(store, prompter, autoYes: false) { ReadFile = _ => throw new IOException("locked") };
        await gate.CheckAsync(Call("locked.cs", "old a", "new a"));
        var request = Assert.Single(prompter.Requests);
        Assert.Null(request.View);
        Assert.Equal(("old a", "new a"), (request.EditOld, request.EditNew));
        var (plain, _) = Detail(request);
        Assert.Equal(new[] { "  -  old a", "  +  new a" }, plain);
    }

    //an edit that cannot apply is refused before the prompt with the words the tool would have said, so nothing is asked that cannot happen
    [Fact]
    public async Task An_edit_that_cannot_apply_fails_with_no_prompt()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.cs"), "x\ntwice\ntwice\n");
        var full = Path.Combine(_dir, "a.cs");
        var missing = Path.Combine(_dir, "missing.cs");
        var (gate, prompter, _) = Gate();

        var absent = await Assert.ThrowsAsync<CannotApplyException>(() => gate.CheckAsync(Call("a.cs", "nowhere", "y")));
        var twice = await Assert.ThrowsAsync<CannotApplyException>(() => gate.CheckAsync(Call("a.cs", "twice", "y")));
        var empty = await Assert.ThrowsAsync<CannotApplyException>(() => gate.CheckAsync(Call("a.cs", "", "y")));
        var gone = await Assert.ThrowsAsync<CannotApplyException>(() => gate.CheckAsync(Call("missing.cs", "x", "y")));

        Assert.Empty(prompter.Requests);
        Assert.Equal($"old_string not found in {full}", absent.Message);
        Assert.Equal(await ToolError("a.cs", "nowhere"), absent.Message);
        Assert.Equal(await ToolError("a.cs", "twice"), twice.Message);
        Assert.Equal(await ToolError("a.cs", ""), empty.Message);
        Assert.Equal(await ToolError("missing.cs", "x"), gone.Message);
        Assert.Contains(missing, gone.Message, StringComparison.Ordinal);
    }

    //what the tool itself throws for the same call, the oracle the gate's refusal must match word for word
    private async Task<string> ToolError(string path, string oldS)
    {
        var args = JsonSerializer.SerializeToElement(new { path, old_string = oldS, new_string = "y" });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => new EditFileTool().ExecuteAsync(args, new TestToolContext(_dir), default));
        return ex.Message;
    }

    [Fact]
    public async Task A_granted_edit_reads_no_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.cs"), "x\nold a\ny\n");
        var (granted, grantedPrompter, grantedReads) = Gate(grant: true);
        await granted.CheckAsync(Call("a.cs", "old a", "new a"));
        Assert.Empty(grantedPrompter.Requests);
        Assert.Empty(grantedReads);

        var (asked, askedPrompter, askedReads) = Gate();
        await asked.CheckAsync(Call("a.cs", "old a", "new a"));
        Assert.Single(askedReads);
        Assert.Equal(2, Assert.Single(askedPrompter.Requests).View!.Start);
    }

    [Fact]
    public async Task A_file_that_changed_after_the_prompt()
    {
        var path = Path.Combine(_dir, "a.cs");
        await File.WriteAllTextAsync(path, Lines("line", 20) + "\n");
        var (gate, prompter, _) = Gate();
        await gate.CheckAsync(Call("a.cs", "line 14\n", "line fourteen\n"));
        Assert.Equal(14, prompter.Requests.Single().View!.Start);

        await File.WriteAllTextAsync(path, "inserted 1\ninserted 2\n" + Lines("line", 20) + "\n");
        var r = await new EditFileTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = "a.cs", old_string = "line 14\n", new_string = "line fourteen\n" }), new TestToolContext(_dir), default);
        Assert.Equal(16, r.View!.Start);
        var (plain, _) = Detail(prompter.Requests.Single());
        Assert.Contains("  14 -  line 14", plain);
    }

    [Fact]
    public void A_writes_panel_holds_no_ground()
    {
        var write = new PermissionRequest("write_file", @"C:\proj\sub\a.txt (+2 lines)", @"C:\proj", PreviewLines: new[] { "one", "two" }, PreviewTotalLines: 2);
        var (_, writeRaw) = Detail(write);
        Assert.All(writeRaw, r => Assert.All(GroundWalker.Of(r, 100), c => Assert.Null(c)));
        var (_, editRaw) = Detail(Edit("old a", "new a", null));
        Assert.Contains(editRaw, r => GroundWalker.Of(r, 100).Any(c => c is not null));
    }

    [Fact]
    public void A_ground_in_the_prompt_in_the_light_mode_and_below_truecolour()
    {
        var (_, light) = Detail(Edit("old a", "new a", null), 60, new Theme(new TermCaps(true, true), ThemeMode.Light));
        Assert.Equal("2;249;222;222", GroundWalker.Of(light[0], 60)[10]);
        Assert.Equal("2;221;242;221", GroundWalker.Of(light[1], 60)[10]);
        var (_, slots) = Detail(Edit("old a", "new a", null), 60, new Theme(new TermCaps(true, false)));
        Assert.Equal("5;52", GroundWalker.Of(slots[0], 60)[10]);
        Assert.Equal("5;22", GroundWalker.Of(slots[1], 60)[10]);
    }
}

[Collection("e2e")]
public sealed class EditPlainSurfacesTests
{
    private static string Capture(Action act, string input = "")
    {
        var sw = new StringWriter();
        var priorOut = Console.Out;
        var priorIn = Console.In;
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input));
        try { act(); } finally { Console.SetOut(priorOut); Console.SetIn(priorIn); }
        return sw.ToString();
    }

    [Fact]
    public void The_plain_prompter_is_unchanged()
    {
        var bare = new PermissionRequest("edit_file", @"C:\proj\a.cs (10 bytes)", @"C:\proj");
        var full = bare with { EditOld = "old a", EditNew = "new a", View = new EditView(3, "", "", new[] { "x" }, new[] { "y" }) };
        Assert.Equal(Capture(() => new PlainPermissionPrompter().Ask(bare), "1\n"), Capture(() => new PlainPermissionPrompter().Ask(full), "1\n"));
    }

    //the plain prompter names an overwrite with the rich prompt's words
    [Fact]
    public void The_plain_prompter_says_overwrite()
    {
        var request = new PermissionRequest("write_file", @"C:\proj\a.cs (+2 lines)", @"C:\proj", Existing: new ExistingFile(40, 900));
        var text = Capture(() => new PlainPermissionPrompter().Ask(request), "1\n");
        Assert.Contains("  Overwrite file, replaces 40 lines", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Overwrite", Capture(() => new PlainPermissionPrompter().Ask(request with { Existing = null }), "1\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_line_of_a_write_and_an_edit_is_unchanged()
    {
        var edit = new ToolCall("c1", "edit_file", JsonSerializer.Serialize(new { path = "a.cs", old_string = "a", new_string = "b" }));
        var write = new ToolCall("c2", "write_file", JsonSerializer.Serialize(new { path = "a.cs", content = "one\ntwo\n" }));
        var view = new EditView(3, "", "", new[] { "x" }, new[] { "y" });
        string Run(ToolCall call, ToolResult result) => Capture(() =>
        {
            var r = new PlainRenderer();
            r.OnToolCallStart(call);
            r.OnToolResult(call, result);
        });
        Assert.Equal(Run(edit, new ToolResult("edited a.cs", Gloss: "ok")), Run(edit, new ToolResult("edited a.cs", Gloss: "ok", View: view)));
        Assert.Contains("edit_file", Run(edit, new ToolResult("edited a.cs", Gloss: "ok", View: view)), StringComparison.Ordinal);
        Assert.Contains("write_file", Run(write, new ToolResult("wrote 8 bytes to a.cs", Gloss: "wrote 8 bytes")), StringComparison.Ordinal);
    }
}
