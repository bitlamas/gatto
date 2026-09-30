using System.Text.Json;
using Gatto.Core.Client;
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

//a tool block opens only when its result row does not hold what the body would show, at the width it is drawn at
public sealed class ToolBlockOpensTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly int[] Widths = { 40, 60, 100, 160 };

    //78 characters, so the row holds it whole at 100 and 160 columns and cuts it at 40 and 60
    private const string Missing = "cannot read C:\\proj\\missing-file-with-a-long-name.txt: the file does not exist";

    private static (ChromePainter P, VtScreenSurface S, ToolBlockItem B) Committed(int width, ToolCall call, ToolResult result, PermissionOutcomeKind? outcome = null)
    {
        var s = new VtScreenSurface(width, 40);
        var gate = new object();
        var model = new TranscriptModel("coder");
        model.Append(new AssistantBlockItem(new[] { "prose" }, "coder"));
        var p = new ChromePainter(s, T, gate, model, 3)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
        p.AltScreen.Enter();
        r.BeginTurn();
        r.OnToolCallStart(call);
        if (outcome is { } o) r.NotePermissionOutcome(o);
        r.OnToolResult(call, result);
        r.EndTurn();
        p.Repaint();
        return (p, s, model.Items.OfType<ToolBlockItem>().Single());
    }

    private static ToolCall Call(string name, object args) => new("c1", name, JsonSerializer.Serialize(args));

    private static ToolCall Read() => Call("read_file", new { path = "missing-file-with-a-long-name.txt" });

    private static ToolCall Shell(string command) => Call("shell", new { command });

    private static ToolCall EditCall() => Call("edit_file", new { path = "a.cs", old_string = "old a", new_string = "new a" });

    private static string Screen(VtScreenSurface s) => string.Join("\n", s.Viewport.Select(v => v.TrimEnd()));

    private static int HeaderRow(VtScreenSurface s, string tool)
    {
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(tool, StringComparison.Ordinal));
        Assert.True(y >= 0, $"no row holds {tool}:\n{Screen(s)}");
        return y;
    }

    private static void PressHeader(ChromePainter p, VtScreenSurface s, string tool)
    {
        var y = HeaderRow(s, tool);
        p.Mouse.Handle(new MouseEvent(4, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(4, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    //the block reached by the keys, the header press and the link, or by none of them
    private static void AssertOpens(bool opens, ChromePainter p, VtScreenSurface s, ToolBlockItem b, string tool, string where)
    {
        var index = p.Model.Items.ToList().IndexOf(b);
        Assert.True(opens == s.Viewport.Any(v => v.Contains(ShellBlockRender.ExpandWords, StringComparison.Ordinal)), $"{where}: link\n{Screen(s)}");
        p.Focus.Prev(p.Width, p.ViewportRows());
        Assert.True(opens == (p.Focus.Focused == index), $"{where}: focus stop");
        p.Focus.Clear();
        PressHeader(p, s, tool);
        Assert.True(opens == !b.Collapsed, $"{where}: header press\n{Screen(s)}");
    }

    [Fact]
    public void A_missing_file_opens_only_where_its_row_cuts_the_error()
    {
        foreach (var w in Widths)
        {
            var (p, s, b) = Committed(w, Read(), new ToolResult(Missing, IsError: true));
            AssertOpens(w < 100, p, s, b, "read_file", $"{w} cols");
        }
    }

    [Theory]
    [InlineData(PermissionOutcomeKind.Denied)]
    [InlineData(PermissionOutcomeKind.Cancelled)]
    public void A_refused_call_has_no_link_and_does_not_open_for_any_tool(PermissionOutcomeKind outcome)
    {
        var text = outcome == PermissionOutcomeKind.Denied ? PermissionGate.DenyNudge : PermissionGate.CancelMessage;
        foreach (var w in Widths)
            foreach (var (call, tool) in new[] { (Read(), "read_file"), (EditCall(), "edit_file"), (Shell("Get-ChildItem -Recurse C:\\proj | Select-Object -First 400"), "shell") })
            {
                var (p, s, b) = Committed(w, call, new ToolResult(text, IsError: true), outcome);
                Assert.True(b.Refused, $"{outcome} {tool} {w}");
                AssertOpens(false, p, s, b, tool, $"{outcome} {tool} {w} cols");
            }
    }

    [Fact]
    public void A_shell_that_shows_all_it_has_on_its_row_does_not_open()
    {
        foreach (var w in Widths)
        {
            var (p, s, b) = Committed(w, Shell("dir"), new ToolResult("", Gloss: "exit 0"));
            AssertOpens(false, p, s, b, "shell", $"no output, {w} cols");
            (p, s, b) = Committed(w, Shell("echo hi"), new ToolResult("hi", Gloss: "exit 0"));
            AssertOpens(false, p, s, b, "shell", $"one line, {w} cols");
        }
    }

    [Fact]
    public void A_shell_whose_command_does_not_fit_its_window_still_opens()
    {
        var command = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Write-Output {i}"));
        foreach (var w in Widths)
        {
            var (p, s, b) = Committed(w, Shell(command), new ToolResult("", Gloss: "exit 0"));
            Assert.True(b.Opens(w, T, GlyphSet.Unicode), $"{w} cols\n{Screen(s)}");
            p.Focus.Prev(p.Width, p.ViewportRows());
            Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
        }
    }

    [Fact]
    public void A_write_and_an_edit_that_happened_still_open()
    {
        foreach (var w in Widths)
        {
            var (p, s, b) = Committed(w, EditCall(), new ToolResult("edited a.cs", Gloss: "ok"));
            p.Focus.Prev(p.Width, p.ViewportRows());
            Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
            (p, s, b) = Committed(w, Call("write_file", new { path = "a.txt", content = "one\ntwo\n" }), new ToolResult("wrote 8 bytes", Gloss: "wrote 8 bytes"));
            p.Focus.Prev(p.Width, p.ViewportRows());
            Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
        }
    }

    [Fact]
    public void A_row_whose_link_left_for_room_still_opens_by_its_header()
    {
        var (p, s, b) = Committed(24, Read(), new ToolResult(string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line {i}")), Gloss: "8 lines"));
        Assert.DoesNotContain(s.Viewport, v => v.Contains(ShellBlockRender.ExpandWords, StringComparison.Ordinal));
        PressHeader(p, s, "read_file");
        Assert.False(b.Collapsed, Screen(s));
    }

    [Fact]
    public void An_open_block_that_a_resize_makes_whole_stays_open_and_the_focus_skips_it()
    {
        var (p, s, b) = Committed(60, Read(), new ToolResult(Missing, IsError: true));
        PressHeader(p, s, "read_file");
        Assert.False(b.Collapsed);
        s.Resize(160, 40);
        p.Repaint();
        Assert.False(b.Collapsed);
        Assert.False(b.Opens(160, T, GlyphSet.Unicode));
        p.Focus.Clear();
        p.Focus.Prev(p.Width, p.ViewportRows());
        Assert.Null(p.Focus.Focused);
        PressHeader(p, s, "read_file");
        Assert.True(b.Collapsed, Screen(s));
        PressHeader(p, s, "read_file");
        Assert.True(b.Collapsed, Screen(s));
    }

    [Fact]
    public void Ctrl_R_does_not_open_a_focused_closed_block_that_a_resize_made_whole()
    {
        var (p, s, b) = Committed(60, Read(), new ToolResult(Missing, IsError: true));
        p.Focus.Prev(p.Width, p.ViewportRows());
        var index = p.Model.Items.ToList().IndexOf(b);
        Assert.Equal(index, p.Focus.Focused);
        s.Resize(160, 40);
        p.Repaint();
        p.Focus.ToggleFocused(160, 40);
        p.Repaint();
        Assert.True(b.Collapsed, Screen(s));
        Assert.Equal(index, p.Focus.Focused);
    }

    [Fact]
    public void Ctrl_R_does_not_reopen_a_block_the_header_press_closed_after_a_resize()
    {
        var (p, s, b) = Committed(60, Read(), new ToolResult(Missing, IsError: true));
        PressHeader(p, s, "read_file");
        Assert.False(b.Collapsed);
        s.Resize(160, 40);
        p.Repaint();
        PressHeader(p, s, "read_file");
        Assert.True(b.Collapsed, Screen(s));
        p.Focus.ToggleFocused(160, 40);
        p.Repaint();
        Assert.True(b.Collapsed, Screen(s));
    }

    [Fact]
    public void An_open_shell_whose_one_line_a_resize_shows_whole_no_longer_opens()
    {
        var line = "output " + new string('o', 50);
        var (p, s, b) = Committed(40, Shell("echo o"), new ToolResult(line, Gloss: "exit 0"));
        Assert.True(b.Opens(40, T, GlyphSet.Unicode), Screen(s));
        PressHeader(p, s, "shell");
        Assert.False(b.Collapsed, Screen(s));
        s.Resize(160, 40);
        p.Repaint();
        Assert.False(b.Opens(160, T, GlyphSet.Unicode));
        p.Focus.Clear();
        p.Focus.Prev(p.Width, p.ViewportRows());
        Assert.Null(p.Focus.Focused);
    }

    //the refusal is read from the red bullet, so only the two refused outcomes may paint it
    [Fact]
    public void Only_a_denied_and_a_cancelled_call_read_as_refused()
    {
        var cases = new (string Name, ToolCall Call, ToolResult Result, PermissionOutcomeKind? Outcome, bool Refused)[]
        {
            ("ran", Read(), new ToolResult("line 1", Gloss: "1 line"), null, false),
            ("failed", Read(), new ToolResult(Missing, IsError: true), null, false),
            ("auto-approved", Read(), new ToolResult("line 1", Gloss: "1 line"), PermissionOutcomeKind.AutoApproved, false),
            ("allowed", Read(), new ToolResult("line 1", Gloss: "1 line"), PermissionOutcomeKind.Allowed, false),
            ("shell exit 1", Shell("exit 1"), new ToolResult("boom", IsError: true, Gloss: "exit 1"), null, false),
            ("denied", Read(), new ToolResult(PermissionGate.DenyNudge, IsError: true), PermissionOutcomeKind.Denied, true),
            ("cancelled", Read(), new ToolResult(PermissionGate.CancelMessage, IsError: true), PermissionOutcomeKind.Cancelled, true),
        };
        foreach (var c in cases)
            Assert.True(c.Refused == Committed(100, c.Call, c.Result, c.Outcome).B.Refused, c.Name);
    }
}
