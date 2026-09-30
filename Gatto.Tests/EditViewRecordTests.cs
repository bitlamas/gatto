using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests;

public sealed class EditViewRecordTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-view-").FullName;
    //best effort, a loaded extension keeps its compiled assembly open until the process ends
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (UnauthorizedAccessException) { } catch (IOException) { }
    }

    private static readonly EditView View = new(14, "    var x = ", ";", new[] { "a", "b", "c" }, new[] { "d" });

    private static ChatMessage Tool(EditView? view) =>
        new("tool", "edited f.cs", ToolCallId: "c1", Gloss: "ok", View: view);

    private static void AssertSame(EditView expected, EditView? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Start, expected.Head, expected.Tail), (actual!.Start, actual.Head, actual.Tail));
        Assert.Equal(expected.Before, actual.Before);
        Assert.Equal(expected.After, actual.After);
    }

    private static ChatMessage Read(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return SessionStore.ParseChatElement(doc.RootElement);
    }

    [Fact]
    public void A_result_with_a_view_survives_a_save_and_a_load()
    {
        var line = SessionStore.ChatJson(Tool(View));
        Assert.Contains("\"view\":{\"start\":14,", line, StringComparison.Ordinal);
        AssertSame(View, Read(line).View);
        var empty = View with { Before = Array.Empty<string>(), After = Array.Empty<string>(), Head = "", Tail = "" };
        AssertSame(empty, Read(SessionStore.ChatJson(Tool(empty))).View);
    }

    [Fact]
    public void A_record_with_no_view_loads()
    {
        var line = SessionStore.ChatJson(Tool(null));
        Assert.DoesNotContain("\"view\"", line, StringComparison.Ordinal);
        Assert.Null(Read(line).View);
        Assert.Null(Read("""{"role":"tool","content":"x","tool_call_id":"c1","view":"not an object"}""").View);
    }

    [Fact]
    public void Both_writers_write_the_view()
    {
        var convo = new Conversation("sys");
        convo.AddUser("go");
        convo.AddAssistant("", new[] { new ToolCall("c1", "edit_file", "{}") });
        convo.AddToolResult("c1", new ToolResult("edited f.cs", Gloss: "ok", View: View));
        var store = new SessionStore(_dir, _dir);
        store.Save(convo);
        var saved = File.ReadAllLines(store.CurrentPath!).Single(l => l.Contains("\"role\":\"tool\"", StringComparison.Ordinal));
        var built = TranscriptStore.BuildLines(new TranscriptModel("coder"), convo, store.Cwd).Single(l => l.Contains("\"role\":\"tool\"", StringComparison.Ordinal));
        Assert.Contains("\"view\":", saved, StringComparison.Ordinal);
        Assert.Equal(saved, built);
    }

    [Fact]
    public void A_request_holds_no_view()
    {
        var bytes = RequestJson.SerialiseMessage(Tool(View));
        Assert.Contains("\"tool_call_id\"", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view\"", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gloss\"", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("var x = ", bytes, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resume_through_the_replay_keeps_the_view()
    {
        var call = new ToolCall("c1", "edit_file", "{}");
        var messages = new List<ChatMessage>
        {
            new("assistant", null, new[] { call }),
            Tool(View),
            new("tool", "orphan", ToolCallId: "c9", View: View),
        };
        var obs = new RecordingObserver();
        Gatto.Repl.Repl.ReplayTranscript(messages, obs, _ => { });
        Assert.Equal(2, obs.Results.Count);
        Assert.All(obs.Results, r => AssertSame(View, r.Item2.View));
    }

    [Fact]
    public async Task A_hook_that_builds_a_new_result_drops_the_view_and_nothing_throws()
    {
        var hooks = new HookBus();
        hooks.OnToolResult(p => Task.FromResult<ToolResult?>(new ToolResult(p.Result!.Text + " (rewritten)")));
        var result = await hooks.EmitToolResultAsync(new HookPayload(Call: new ToolCall("c1", "edit_file", "{}"), Result: new ToolResult("edited", Gloss: "ok", View: View)));
        Assert.Null(result.View);
        Assert.Equal("edited (rewritten)", result.Text);

        var keeps = new HookBus();
        keeps.OnToolResult(p => Task.FromResult<ToolResult?>(p.Result! with { Text = "nudged" }));
        var kept = await keeps.EmitToolResultAsync(new HookPayload(Call: new ToolCall("c1", "edit_file", "{}"), Result: new ToolResult("edited", Gloss: "ok", View: View)));
        AssertSame(View, kept.View);
    }
}
