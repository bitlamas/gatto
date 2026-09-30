using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//a resumed session must send the request the live session would have sent, so the server finds its prefix
[Collection("e2e")]
public sealed class ResumeEqualityTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-resume-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-resume-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public ResumeEqualityTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{\"model\":\"test-model\"}");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private void Home(FakeOpenAiServer server, string reasoningHistory = "all", string thinking = "")
    {
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{"files":[{"path":"m.gguf","active":true}],"port":1235,"context":8192,"reasoning_history":"{{reasoningHistory}}"{{thinking}}}""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model"}""");
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
    }

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static async Task Run(FakeOpenAiServer server, params string[] args)
    {
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"answer\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(args));
    }

    private static async Task<(string Out, string Err)> RunCapture(FakeOpenAiServer server, params string[] args)
    {
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"answer\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(args));
        return (stdout.ToString(), stderr.ToString());
    }

    //the line follows the session path on stderr, and stdout keeps the model's text alone
    [Fact]
    public async Task The_p_path_writes_the_line_to_stderr_after_the_session_path()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "PLANTED CONTEXT 7c1e\n");
        var (stdout, stderr) = await RunCapture(server, "-p", "second", "--continue");
        Assert.Equal("answer" + Environment.NewLine, stdout);
        var lines = stderr.Split(Environment.NewLine).ToList();
        var session = lines.FindIndex(l => l.StartsWith("gatto: session ", StringComparison.Ordinal));
        Assert.True(session >= 0, stderr);
        Assert.Equal("told the model what changed: GATTO.md", lines[session + 1]);
        var (_, exact) = await RunCapture(server, "-p", "third", "--continue");
        Assert.DoesNotContain("told the model", exact, StringComparison.Ordinal);
        Assert.DoesNotContain("sent again", exact, StringComparison.Ordinal);
    }

    //the messages of the earlier request must open the later request byte for byte, and the tools must match
    internal static void AssertPrefix(JsonElement earlier, JsonElement later)
    {
        var a = earlier.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        var b = later.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        Assert.True(b.Count > a.Count, $"later request holds {b.Count} messages, earlier {a.Count}");
        for (var i = 0; i < a.Count; i++)
            Assert.True(a[i] == b[i], $"message {i} differs:\n  was: {a[i]}\n  now: {b[i]}");
        Assert.Equal(earlier.GetProperty("tools").GetRawText(), later.GetProperty("tools").GetRawText());
    }

    [Fact]
    public async Task A_resume_with_nothing_changed_sends_the_same_prefix()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        await Run(server, "-p", "second", "--continue");
        AssertPrefix(server.RequestBodies[0], server.RequestBodies[1]);
    }

    [Fact]
    public async Task A_resume_after_a_context_file_edit_sends_the_same_prefix()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "PLANTED CONTEXT 7c1e\n");
        await Run(server, "-p", "second", "--continue");
        AssertPrefix(server.RequestBodies[0], server.RequestBodies[1]);
    }

    [Fact]
    public async Task A_resume_after_a_memory_write_sends_the_same_prefix()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        MemoryDir.Write(MemoryDir.FindProjectRoot(_cwd), "planted-fact", "a fact written between the two runs\n");
        await Run(server, "-p", "second", "--continue");
        AssertPrefix(server.RequestBodies[0], server.RequestBodies[1]);
    }

    //the update rides the resumed message, the next request keeps it byte for byte, and a resume with nothing new adds none
    [Fact]
    public async Task A_resume_after_a_change_carries_the_update_and_the_next_resume_keeps_the_prefix()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "PLANTED CONTEXT 7c1e\n");
        await Run(server, "-p", "second", "--continue");
        await Run(server, "-p", "third", "--continue");
        string LastUser(JsonElement body) => body.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        var carried = LastUser(server.RequestBodies[1]);
        Assert.StartsWith(ResumeUpdate.FirstLine + "\n", carried, StringComparison.Ordinal);
        Assert.Contains("Context file added: " + Path.Combine(_cwd, "GATTO.md") + ".", carried, StringComparison.Ordinal);
        Assert.EndsWith("\n\nsecond", carried, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANTED CONTEXT 7c1e", carried, StringComparison.Ordinal);
        AssertPrefix(server.RequestBodies[0], server.RequestBodies[1]);
        AssertPrefix(server.RequestBodies[1], server.RequestBodies[2]);
        Assert.Equal("third", LastUser(server.RequestBodies[2]));
    }

    [Fact]
    public async Task A_replay_with_no_effort_flag_keeps_the_thinking_the_session_was_sent_with()
    {
        await using var server = new FakeOpenAiServer();
        Home(server, thinking: ""","thinking":{"low":{"reasoning_effort":"low"},"high":{"reasoning_effort":"high"}}""");
        await Run(server, "-p", "first", "--effort", "high");
        Assert.Equal("high", server.RequestBodies[0].GetProperty("reasoning_effort").GetString());
        await Run(server, "-p", "second", "--continue");
        Assert.Equal("high", server.RequestBodies[1].GetProperty("reasoning_effort").GetString());
        Assert.Equal("high", Environment.GetEnvironmentVariable("GATTO_EFFORT"));
        AssertPrefix(server.RequestBodies[0], server.RequestBodies[1]);
    }

    //a replay would keep the stored text without the planted context, so its presence says the resume reset
    [Fact]
    public async Task A_changed_tool_list_resets()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", "planted_tool.csx"),
            """Gatto.Register("planted_tool", "a planted tool", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "PLANTED CONTEXT 7c1e\n");
        await Run(server, "-p", "second", "--continue");
        var second = server.RequestBodies[1];
        Assert.Contains("PLANTED CONTEXT 7c1e", second.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains(second.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "planted_tool");
        var files = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(2, SessionStore.LoadBaseline(files[^1])!.Seq);
    }

    [Fact]
    public async Task A_session_saved_by_the_launch_holds_its_baseline()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await Run(server, "-p", "first");
        var file = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal));
        var b = SessionStore.LoadBaseline(file);
        Assert.NotNull(b);
        Assert.Equal((1, "generalist", "test-model", "all"), (b!.Seq, b.Role, b.Model, b.ReasoningHistory));
        var sent = server.RequestBodies[0].GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(sent.Select(t => t.GetProperty("function").GetProperty("name").GetString()), b.Tools.Select(t => t.Name));
        Assert.Equal(sent.Select(t => BaselineMarks.Sha256Hex(t.GetRawText())), b.Tools.Select(t => t.Sha256));
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), b.Sources.Date);
    }

    private sealed class FailingTool : ITool
    {
        public string Name => "fail";
        public string Description => "fails with a gloss";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct) =>
            Task.FromResult(new ToolResult("it did not work", IsError: true, Gloss: "failed badly"));
    }

    //every record kind the loop writes, seeded by the real loop and saved by the real store
    private async Task<Conversation> Seed()
    {
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        reg.Register(new FailingTool());
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(_cwd), "test-model");
        var convo = new Conversation("BASE SYSTEM\n\n" + Compactor.BuildContext("THE OLD SUMMARY LINE", "old.jsonl", null));
        client.EnqueueTurn(new StreamEvent.ReasoningDelta("thinking it over"),
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c2", "fail", "{}")), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("the answer<|tool_call_end|>"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "first question", new RecordingObserver(), default);
        client.EnqueueTurn(new StreamEvent.TextDelta("<|tool_call_end|>"),
            new StreamEvent.ToolCallReady(new ToolCall("c3", "echo", "{\"msg\":\"again\"}")), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "second question", new RecordingObserver(), default);
        new SessionStore(_home, _cwd).Save(convo);
        return convo;
    }

    //the records after the system message, the part the store round-trips, sent as the request writes them
    [Theory]
    [InlineData("all")]
    [InlineData("none")]
    public async Task A_resume_holds_every_record_kind(string reasoningHistory)
    {
        await using var server = new FakeOpenAiServer();
        Home(server, reasoningHistory);
        var seeded = await Seed();
        Assert.Contains(seeded.Messages, m => m.ReasoningContent is { Length: > 0 });
        Assert.Contains(seeded.Messages, m => m.Role == "tool" && m.IsError && m.Gloss is not null);
        Assert.Contains(seeded.Messages, m => m.Stripped > 0 && m.ToolCalls is { Count: > 0 });
        await Run(server, "-p", "next", "--continue");
        var policy = reasoningHistory == "none" ? ReasoningHistory.None : ReasoningHistory.All;
        var expected = ContextBudget.ShapeReasoning(seeded.Messages, policy).Skip(1).Select(m => RequestJson.SerialiseMessage(m)).ToList();
        var sent = server.RequestBodies[^1].GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        Assert.Equal(expected.Count + 2, sent.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(expected[i] == sent[i + 1], $"record {i + 1} differs:\n  saved: {expected[i]}\n  sent:  {sent[i + 1]}");
        Assert.Contains("\"next\"", sent[^1], StringComparison.Ordinal);
    }
}
