using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

//capture redirects Console.Out, a process-global, so this class must not run beside another console test
[Collection("e2e")]
public class PlainRendererTests
{
    private static string Capture(Action<PlainRenderer> act) => Capture(ReasoningMode.Collapsed, act);

    private static string Capture(ReasoningMode reasoning, Action<PlainRenderer> act)
    {
        var sw = new StringWriter();
        var prior = Console.Out;
        Console.SetOut(sw);
        try { act(new PlainRenderer(reasoning)); } finally { Console.SetOut(prior); }
        return sw.ToString();
    }

    [Fact]
    public void Plain_collapsed_omits_reasoning()
    {
        var s = Capture(ReasoningMode.Collapsed, r => { r.BeginTurn(); r.OnReasoningDelta("secret thoughts\n"); r.OnTextDelta("answer"); });
        Assert.DoesNotContain("secret thoughts", s, StringComparison.Ordinal);
        Assert.Contains("answer", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_expanded_prints_reasoning()
    {
        var s = Capture(ReasoningMode.Expanded, r => { r.BeginTurn(); r.OnReasoningDelta("visible thoughts\n"); });
        Assert.Contains("visible thoughts", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_passthrough_prints_under_collapsed()
    {
        var s = Capture(ReasoningMode.Collapsed, r => { r.PassthroughReasoning = true; r.BeginTurn(); r.OnReasoningDelta("compact summary\n"); });
        Assert.Contains("compact summary", s, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolLines_MatchV1Format()
    {
        var call = new ToolCall("id1", "read_file", """{"path":"a.cs"}""");
        var ok = Capture(r => r.OnToolResult(call, new ToolResult("content")));
        Assert.Equal("[tool] read_file ✓" + Environment.NewLine, ok);
        var err = Capture(r => r.OnToolResult(call, new ToolResult("file not found: a.cs", IsError: true)));
        Assert.Equal("[tool] read_file ✗ file not found: a.cs" + Environment.NewLine, err);
    }

    [Fact]
    public void ToolStart_TruncatesArgsAt80()
    {
        var longArgs = new string('x', 100);
        var call = new ToolCall("id1", "shell", longArgs);
        var s = Capture(r => r.OnToolCallStart(call));
        //the leading break is a literal \n, because WriteLine appends Environment.NewLine at the end
        Assert.Equal($"\n[tool] shell {new string('x', 80)}…" + Environment.NewLine, s);
    }

    [Fact]
    public void ToolError_TruncatesTextAt120()
    {
        var call = new ToolCall("id1", "shell", "{}");
        var s = Capture(r => r.OnToolResult(call, new ToolResult(new string('e', 130), IsError: true)));
        //the cut is by cells including the ellipsis, so the whole line still fits inside 120 cells
        Assert.Equal($"[tool] shell ✗ {new string('e', 119)}…" + Environment.NewLine, s);
    }

    [Fact]
    public void A_shell_result_prints_the_shell_block_words()
    {
        var line = PlainRenderer.ResultLine(new ToolCall("c", "shell", "{}"),
            new ToolResult("out\r\n--- stderr ---\r\nbad\r\n(exit code 1)", IsError: true, Gloss: "exit 1"), GlyphSet.Unicode);
        Assert.StartsWith("[tool] shell \u2713 ran \u00b7 exit 1 \u00b7 bad", line, StringComparison.Ordinal);
        Assert.DoesNotContain("click", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exit_zero_shell_result_with_stdout_still_says_ran()
    {
        var line = PlainRenderer.ResultLine(new ToolCall("c", "shell", "{}"), new ToolResult("out", Gloss: "exit 0"), GlyphSet.Unicode);
        Assert.Equal("[tool] shell ✓ ran · out · ~0 tok", line);
    }

    [Fact]
    public void A_long_single_line_stays_out_of_the_plain_line()
    {
        var edge = new string('x', ShellBlockRender.PlainLineCells);
        Assert.Contains(edge, PlainRenderer.ResultLine(new ToolCall("c", "shell", "{}"), new ToolResult(edge, Gloss: "exit 0"), GlyphSet.Unicode), StringComparison.Ordinal);
        var over = PlainRenderer.ResultLine(new ToolCall("c", "shell", "{}"), new ToolResult(edge + "x", Gloss: "exit 0"), GlyphSet.Unicode);
        Assert.Equal("[tool] shell ✓ ran · ~30 tok", over);
    }

    [Fact]
    public void An_exit_zero_shell_result_with_stderr_only_still_says_ran()
    {
        var text = ShellTool.StderrMarker + "\r\nwarn";
        var line = PlainRenderer.ResultLine(new ToolCall("c", "shell", "{}"), new ToolResult(text, Gloss: "exit 0"), GlyphSet.Unicode);
        Assert.Equal("[tool] shell ✓ ran · stderr 1 · ~5 tok", line);
    }

    //the sanitizer keeps the escape's inert tail, so the tail shows the stderr line got through
    [Fact]
    public void An_escape_in_the_last_stderr_line_never_reaches_the_plain_line()
    {
        var text = "out\r\n" + ShellTool.StderrMarker + "\r\n\u001b[31mbad\u001b[0m\r\n(exit code 1)";
        var line = PlainRenderer.ResultLine(new ToolCall("c", "shell", "{}"), new ToolResult(text, IsError: true, Gloss: "exit 1"), GlyphSet.Unicode);
        Assert.DoesNotContain("\u001b", line, StringComparison.Ordinal);
        Assert.Contains("[31mbad[0m", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_tool_prints_a_sanitized_first_line()
    {
        var line = PlainRenderer.ResultLine(new ToolCall("c", "read_file", "{}"),
            new ToolResult("\u001b[31mboom\u001b[0m\nsecond", IsError: true), GlyphSet.Unicode);
        Assert.DoesNotContain("\u001b", line, StringComparison.Ordinal);
        Assert.DoesNotContain("second", line, StringComparison.Ordinal);
        Assert.Contains("boom", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWarning_HostileEscAndCr_Stripped()
    {
        //warning text comes from the endpoint, so the plain path strips the escape and leaves the rest as inert text
        var s = Capture(r => r.OnWarning("care\x1b[2Kful\r"));
        Assert.DoesNotContain('\x1b', s);
        Assert.Contains("care[2Kful", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_ContainsNoEscapeBytes()
    {
        var call = new ToolCall("id1", "grep", """{"pattern":"x"}""");
        var s = Capture(r =>
        {
            r.OnTextDelta("hello **not styled**");
            r.OnReasoningDelta("thinking");
            r.OnToolCallStart(call);
            r.OnWarning("careful");
        });
        Assert.DoesNotContain('\x1b', s);
    }
}
