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
using Xunit;

namespace Gatto.Tests.Render;

//the shell block opens to its whole command, the running row streams its tail, and a permission prompt colours the code it asks about
public sealed class ToolInputTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private const string LongCommand =
        "Get-ChildItem -Path . -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch 'node_modules' } | Select-Object -First 20 | Format-Table Name, Length";

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static ToolCall ShellCall(string command, string id = "c1") =>
        new(id, "shell", JsonSerializer.Serialize(new { command }));

    private static ToolBlockItem ShellBlock(string command, string result, bool collapsed) =>
        new("shell", TermText.Sanitize(command), "ok", true, "coder")
        {
            Collapsed = collapsed,
            FullResult = result,
            FullArgs = command,
        };

    private static (StreamRenderer R, ChromePainter P, VtScreenSurface S) Wire(int width, int height)
    {
        var s = new VtScreenSurface(width, height);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var ticker = new ChromeTicker(painter, gate);
        var renderer = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: model, convoTail: () => null);
        painter.AltScreen.Enter();
        painter.Repaint();
        return (renderer, painter, s);
    }

    [Fact]
    public void An_open_shell_block_wraps_its_whole_command_at_every_width()
    {
        for (var width = 30; width <= 120; width++)
        {
            var block = ShellBlock(LongCommand, "done", collapsed: false);
            block.View = ShellView.All;
            var rows = block.Render(width, T, GlyphSet.Unicode);
            var l = block.ShellLayout(width, T, GlyphSet.Unicode);
            Assert.True(l.CommandShown > 1, $"width {width}: show all did not lift the command window");
            Assert.Contains(rows, r => Visible(r).Contains($"{GlyphSet.Unicode.Elbow}", StringComparison.Ordinal));
            Assert.All(rows, r => Assert.True(UnicodeWidth.Of(Visible(r)) <= width, $"width {width}: a row is wider than the window: {Visible(r)}"));

            //each wrap dropped exactly one space and the rejoin returns it, so the command survives whole
            var command = rows.Skip(l.CommandFirst).Take(l.CommandShown).Select(r => Visible(r).Trim()).ToList();
            Assert.Equal(LongCommand, string.Join(" ", command));
        }
    }

    [Fact]
    public void An_open_shell_block_paints_every_row_of_its_command_in_the_powershell_colours()
    {
        var block = ShellBlock(LongCommand, "done", collapsed: false);
        block.View = ShellView.All;
        var rows = block.Render(50, T, GlyphSet.Unicode);
        var tail = rows.Single(r => Visible(r).Contains("Format-Table", StringComparison.Ordinal));
        Assert.DoesNotContain("shell", Visible(tail), StringComparison.Ordinal);
        Assert.Contains(T.Paint("Format-Table", Theme.CampbellBrightYellow), tail, StringComparison.Ordinal);

        var all = string.Concat(rows);
        Assert.Contains(T.Paint("Get-ChildItem", Theme.CampbellBrightYellow), all, StringComparison.Ordinal);
        Assert.Contains(T.Paint("-Recurse", Theme.CampbellBrightBlack), all, StringComparison.Ordinal);
        Assert.Contains(T.Paint("-notmatch", Theme.CampbellBrightBlack), all, StringComparison.Ordinal);
        Assert.Contains(T.Paint("'node_modules'", Theme.CampbellCyan), all, StringComparison.Ordinal);
        Assert.Contains(T.Paint("$_", Theme.CampbellBrightGreen), all, StringComparison.Ordinal);
        Assert.Contains(T.Paint("20", Theme.CampbellBrightWhite), all, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_line_of_a_multi_line_command_starts_its_own_row()
    {
        const string command = "$files = Get-ChildItem\nforeach ($f in $files) {\n    Write-Output $f.Name\n}";
        var block = ShellBlock(command, "a\nb", collapsed: false);
        block.View = ShellView.All;
        foreach (var width in new[] { 40, 80 })
        {
            var rows = block.Render(width, T, GlyphSet.Unicode).Select(Visible).ToList();
            var wraps = block.RowWraps(width, T, GlyphSet.Unicode);
            Assert.Equal(rows.Count, wraps.Count);

            var head = rows.FindIndex(r => r.Contains("shell", StringComparison.Ordinal));
            Assert.True(head >= 0, $"width {width}: no header row");
            Assert.Contains("4 lines", rows[head], StringComparison.Ordinal);
            var first = head + 1;   //the command rows start below the header
            Assert.Equal("$files = Get-ChildItem", rows[first].Trim());
            Assert.Equal("foreach ($f in $files) {", rows[first + 1].Trim());
            Assert.Equal("    Write-Output $f.Name", rows[first + 2][GutterWrap.Hang.Length..]);
            Assert.Equal("}", rows[first + 3].Trim());
            Assert.All(Enumerable.Range(first, 4), k => Assert.False(wraps[k].Continuation, $"width {width}: row {k} continues the row above it"));
        }
    }

    [Fact]
    public void A_cancelled_shell_block_opens_to_its_command_without_a_result_rail()
    {
        var (r, p, _) = Wire(80, 20);
        r.BeginTurn();
        r.OnToolCallStart(ShellCall(LongCommand));
        r.EndTurn();

        var index = p.Model.Items.ToList().FindIndex(it => it is ToolBlockItem);
        var item = (ToolBlockItem)p.Model.Items[index];
        Assert.Equal(LongCommand, item.FullArgs);
        Assert.True(p.Focus.IsCollapsible(index, p.Width));

        p.Focus.Focus(index, p.Width, p.ViewportRows(), reveal: false);
        p.Focus.Toggle(index, p.Width, p.ViewportRows());
        Assert.False(item.Collapsed);
        var rows = item.Render(80, T, GlyphSet.Unicode).Select(Visible).ToList();
        Assert.Contains(rows, row => row.Contains("Format-Table", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains($"{GlyphSet.Unicode.Box.Vertical} ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_live_path_gives_a_shell_block_its_full_command_and_leaves_it_closed()
    {
        const string command = "Get-Date\nGet-Location";
        var (r, p, _) = Wire(80, 20);
        var call = ShellCall(command);
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("done"));
        r.EndTurn();

        var item = Assert.Single(p.Model.Items.OfType<ToolBlockItem>());
        Assert.Equal(command, item.FullArgs);
        Assert.True(item.Collapsed);
    }

    [Fact]
    public void Replay_gives_a_shell_block_its_full_command()
    {
        var convo = new Conversation("sys");
        convo.AddUser("run it");
        convo.Load(new[]
        {
            new ChatMessage("assistant", null, new[] { ShellCall(LongCommand) }),
            new ChatMessage("tool", "done", ToolCallId: "c1"),
        });

        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: GlyphSet.Unicode);

        var item = Assert.Single(rebuilt.Items.OfType<ToolBlockItem>());
        Assert.Equal(LongCommand, item.FullArgs);
        Assert.True(item.Collapsed);
    }

    [Fact]
    public void A_full_command_keeps_its_line_breaks_and_reads_paths_from_the_profile()
    {
        var json = JsonSerializer.Serialize(new { command = "cd C:\\Users\\user\\src\nGet-ChildItem C:\\Users\\user -Force", timeout_ms = 500 });
        Assert.Equal("cd ~\\src\nGet-ChildItem ~ -Force", ItemRender.FullArgsOf("shell", json, @"C:\proj", @"C:\Users\user"));
    }

    [Theory]
    [InlineData("read_file", "{\"path\":\"a.txt\",\"command\":\"x\"}")]
    [InlineData("shell", "{not json")]
    [InlineData("shell", "{\"timeout_ms\":5}")]
    [InlineData("shell", "[\"Get-Date\"]")]
    public void Only_a_shell_call_that_names_a_command_has_a_full_command(string tool, string json)
        => Assert.Equal("", ItemRender.FullArgsOf(tool, json, @"C:\proj", @"C:\Users\user"));

    //the one-row rule keeps a short command on the header row live and committed, so it never reflows at commit
    [Fact]
    public void A_shell_row_runs_to_the_window_edge_while_it_runs_and_after_it_commits()
    {
        const string command = "git status --short";
        var full = UnicodeWidth.Of("\u25cb shell " + command);
        foreach (var width in new[] { full, full + 1, full + 40, 120 })
        {
            var (r, p, s) = Wire(width, 20);
            var call = ShellCall(command);
            r.BeginTurn();
            r.OnToolCallStart(call);
            Assert.Contains(s.Viewport, row => row.Contains($"shell {command}", StringComparison.Ordinal));
            r.OnToolResult(call, new ToolResult("done"));
            r.EndTurn();
            var item = Assert.Single(p.Model.Items.OfType<ToolBlockItem>());
            var rows = item.Render(width, T, GlyphSet.Unicode).Select(Visible).ToList();
            Assert.Equal(2, rows.Count);   //header and result, the one-row rule
            Assert.Contains($"shell {command}", rows[0], StringComparison.Ordinal);
        }
    }

    //the tap is compared with the screen, since comparing it with the item's own render would be the same call twice
    [Fact]
    public void The_committed_rows_are_the_rows_the_screen_shows_for_a_result_and_for_a_cancel()
    {
        var (r, p, s) = Wire(120, 30);
        var tapped = new List<string>();
        r.CommitTap = batch => tapped.AddRange(batch);
        r.BeginTurn();
        var call = ShellCall("echo one\necho two");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("one\r\ntwo", Gloss: "exit 0"));
        p.Repaint();
        var screen = s.Viewport.Select(v => v.TrimEnd()).Where(v => v.Length > 0).ToList();
        foreach (var row in tapped.Select(t => Visible(t).TrimEnd()).Where(t => t.Length > 0))
            Assert.Contains(row, screen);

        tapped.Clear();
        r.OnToolCallStart(ShellCall("sleep 9", "c2"));
        r.EndTurn();
        p.Repaint();
        screen = s.Viewport.Select(v => v.TrimEnd()).Where(v => v.Length > 0).ToList();
        Assert.Contains(tapped.Select(t => Visible(t).TrimEnd()), row => row.Contains("cancelled", StringComparison.Ordinal));
        foreach (var row in tapped.Select(t => Visible(t).TrimEnd()).Where(t => t.Length > 0))
            Assert.Contains(row, screen);
    }

    //wider than the surface, so the screen wraps it and a tap rendered without the width won't match
    [Fact]
    public void The_committed_rows_of_a_command_wider_than_the_surface_are_the_rows_the_screen_shows()
    {
        var (r, p, s) = Wire(80, 30);
        var tapped = new List<string>();
        r.CommitTap = batch => tapped.AddRange(batch);
        r.BeginTurn();
        var call = ShellCall(LongCommand);
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("one\r\ntwo", Gloss: "exit 0"));
        p.Repaint();
        var screen = s.Viewport.Select(v => v.TrimEnd()).Where(v => v.Length > 0).ToList();
        var rows = tapped.Select(t => Visible(t).TrimEnd()).Where(t => t.Length > 0).ToList();
        Assert.True(rows.Count >= 3, string.Join("\n", rows));
        foreach (var row in rows)
            Assert.Contains(row, screen);
    }

    [Fact]
    public void The_shell_prompt_classifies_its_command_once_across_every_row()
    {
        var (_, _, detail) = PromptTitles.For(new PermissionRequest("shell", "Write-Output 'first\nsecond' | Out-Null", null), null, @"C:\proj");

        Assert.Equal(2, detail.Count);
        Assert.All(detail, d => Assert.Equal(CodeLanguage.PowerShell, d.Language));
        Assert.All(detail, d => Assert.NotNull(d.Roles));
        Assert.Equal(SpanRole.Function, detail[0].Roles![0]);
        Assert.All(detail[1].Roles!.Take("second'".Length), role => Assert.Equal(SpanRole.String, role));
        Assert.Equal(SpanRole.Function, detail[1].Roles![detail[1].Text.IndexOf("Out-Null", StringComparison.Ordinal)]);
    }

    [Theory]
    [InlineData(@"C:\proj\a.cs (+2 lines)", CodeLanguage.CSharp)]
    [InlineData(@"C:\proj\a.ps1 (+2 lines)", CodeLanguage.PowerShell)]
    [InlineData(@"C:\proj\a.txt (+2 lines)", CodeLanguage.None)]
    public void A_write_prompt_takes_its_preview_language_from_the_path(string summary, CodeLanguage expected)
    {
        var (_, _, detail) = PromptTitles.For(new PermissionRequest("write_file", summary, @"C:\proj",
            PreviewLines: new[] { "var total = 42;", "// done" }, PreviewTotalLines: 2), null, @"C:\proj");

        Assert.Equal(2, detail.Count);
        Assert.All(detail, d => Assert.Equal(expected, d.Language));
        Assert.All(detail, d => Assert.Equal(expected != CodeLanguage.None, d.Roles is not null));
        if (expected == CodeLanguage.CSharp) Assert.Equal(SpanRole.Keyword, detail[0].Roles![0]);
    }

    private sealed class OneKey : IKeySource
    {
        private bool _read;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            if (_read) throw new InvalidOperationException("the prompt read a second key");
            _read = true;
            return new ConsoleKeyInfo((char)0, ConsoleKey.Enter, false, false, false);
        }
    }

    [Fact]
    public void The_prompt_paints_the_command_in_the_powershell_colours_at_every_width()
    {
        const string command = "Get-ChildItem -Recurse -Filter '*.cs' | Where-Object { $_.Length -gt 1000 } | Select-Object -First 20";
        var (_, _, detail) = PromptTitles.For(new PermissionRequest("shell", command, null), null, @"C:\proj");
        for (var width = 32; width <= 120; width += 4)
        {
            var surface = new RecordingSurface { Width = width };
            new SelectPrompt(surface, T, new OneKey()).Show(new SelectSpec(["shell command"], "Do you want to proceed?", [new SelectOption("Yes")], DetailRows: detail));

            var text = surface.Text;
            Assert.Contains(T.Paint("Get-ChildItem", Theme.CampbellBrightYellow), text, StringComparison.Ordinal);
            Assert.Contains(T.Paint("-Recurse", Theme.CampbellBrightBlack), text, StringComparison.Ordinal);
            Assert.Contains(T.Paint("'*.cs'", Theme.CampbellCyan), text, StringComparison.Ordinal);
            Assert.Contains(T.Paint("$_", Theme.CampbellBrightGreen), text, StringComparison.Ordinal);
            Assert.Contains(T.Paint("Select-Object", Theme.CampbellBrightYellow), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_prompt_paints_a_code_preview_in_the_colours_of_its_language()
    {
        var (_, _, detail) = PromptTitles.For(new PermissionRequest("write_file", @"C:\proj\a.cs (+2 lines)", @"C:\proj",
            PreviewLines: new[] { "var total = 42;", "// done" }, PreviewTotalLines: 2), null, @"C:\proj");
        var surface = new RecordingSurface();
        new SelectPrompt(surface, T, new OneKey()).Show(new SelectSpec(["Write new file a.cs"], "Proceed?", [new SelectOption("Yes")], DetailRows: detail));

        Assert.Contains(T.Paint("1  ", Theme.Dim) + T.Paint("var", Theme.VsKeyword), surface.Text, StringComparison.Ordinal);
        Assert.Contains(T.Paint("42", Theme.VsNumber), surface.Text, StringComparison.Ordinal);
        Assert.Contains(T.Paint("// done", Theme.VsComment), surface.Text, StringComparison.Ordinal);
    }
}
