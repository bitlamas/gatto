using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Render;

namespace Gatto.Tests;

file sealed class RecordingObserver : ITurnObserver
{
    public readonly List<string> Events = new();
    public void OnTextDelta(string t) => Events.Add("text:" + t);
    public void OnReasoningDelta(string t) => Events.Add("reason:" + t);
    public void OnToolCallStart(ToolCall c) => Events.Add("start:" + c.Name);
    public void OnToolResult(ToolCall c, ToolResult r) => Events.Add("result:" + c.Name + ":" + r.Text);
    public void OnWarning(string m) => Events.Add("warn:" + m);
    public void OnUsage(Usage u) { }
}

//redirecting Console.Out is process-wide, so the class runs serialized (in parallel another test's output shows up in this capture)
[Collection("e2e")]
public class ReplayTests
{
    [Fact]
    public void Replay_EchoesUser_TextsAssistant_PairsToolExchanges()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", "hello"),
            new("assistant", null, new[] { new ToolCall("c1", "read_file", "{\"path\":\"a\"}") }),
            new("tool", "file body", ToolCallId: "c1"),
            new("assistant", "done."),
        };
        var obs = new RecordingObserver();
        var users = new List<string>();
        Gatto.Repl.Repl.ReplayTranscript(messages, obs, users.Add);

        Assert.Equal(new[] { "hello" }, users);
        Assert.Equal(new[] { "start:read_file", "result:read_file:file body", "text:done.\n" }, obs.Events);
        //the system message is skipped, and a tool call and its result go out as a pair (a dangling call never shows alone)
    }

    [Fact]
    public void Replay_UnmatchedToolResult_SynthesizesACall()
    {
        var messages = new List<ChatMessage> { new("tool", "orphan", ToolCallId: "zz") };
        var obs = new RecordingObserver();
        Gatto.Repl.Repl.ReplayTranscript(messages, obs, _ => { });
        Assert.Equal(new[] { "result:(tool):orphan" }, obs.Events);
    }

    [Fact]
    public void Replay_AssistantText_GetsATrailingNewlineExactlyOnce()
    {
        var messages = new List<ChatMessage> { new("assistant", "line\n") };
        var obs = new RecordingObserver();
        Gatto.Repl.Repl.ReplayTranscript(messages, obs, _ => { });
        Assert.Equal(new[] { "text:line\n" }, obs.Events);
    }

    //the plain path must pass the gloss to the renderer, and a resumed failed shell reads with the block's words, never as a bare success
    [Fact]
    public void Replay_PlainPath_RendersFailedShell_WithItsExitWords()
    {
        var messages = new List<ChatMessage>
        {
            new("assistant", null, new[] { new ToolCall("c1", "shell", "{\"cmd\":\"false\"}") }),
            new("tool", "boom", ToolCallId: "c1", IsError: true, Gloss: "exit 1"),
        };
        var sw = new StringWriter();
        var prior = Console.Out;
        Console.SetOut(sw);
        try { Gatto.Repl.Repl.ReplayTranscript(messages, new PlainRenderer(), _ => { }); }
        finally { Console.SetOut(prior); }
        var output = sw.ToString();

        Assert.Contains("ran \u00b7 exit 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("click", output, StringComparison.Ordinal);   //the plain path has no click, so the link must not ride along
        Assert.DoesNotContain("[tool] shell \u2713" + Environment.NewLine, output, StringComparison.Ordinal);
    }
}
