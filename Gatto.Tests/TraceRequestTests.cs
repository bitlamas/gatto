using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Roles;
using Gatto.Roles.Agents;
using Gatto.Tests.Fakes;
using Trace = Gatto.Core.Trace;

namespace Gatto.Tests;

//the request, shell.drain and run_agent child spans, each opened by the seam that owns it
public class TraceRequestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-trace-req-").FullName;
    private string File_ => Path.Combine(_dir, "s.trace.jsonl");

    public void Dispose() => Directory.Delete(_dir, true);

    private Trace NewTrace() => new(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => File_, () => "s", "b", 1);

    private List<JsonElement> Spans() =>
        File.ReadAllLines(File_).Skip(1).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static List<JsonElement> Named(List<JsonElement> spans, string name) =>
        spans.Where(s => s.GetProperty("s").GetString() == name).ToList();

    private static string Chunk(string deltaJson, string? finish = null, string? tail = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]{tail}}}\n\n";

    [Fact]
    public async Task The_request_span_carries_the_wire_and_the_server_numbers()
    {
        await using var s = new FakeOpenAiServer();
        var frames = new[]
        {
            Chunk("{\"reasoning_content\":\"hmm\"}"),
            Chunk("{\"content\":\"ok\"}"),
            Chunk("{}", finish: "stop",
                tail: ",\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2},\"timings\":{\"cache_n\":3,\"prompt_n\":2,\"prompt_ms\":12.5,\"predicted_ms\":40.25,\"other\":1}"),
            "data: [DONE]\n\n",
        };
        s.Enqueue(new FakeResponse(Frames: frames));
        using var trace = NewTrace();
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl), trace: trace);
        trace.MarkResume();

        await foreach (var _ in client.StreamAsync(new ChatRequest("m", [new ChatMessage("user", "hi")]), 9, default)) { }
        trace.Flush();

        var req = Named(Spans(), "request").Single();
        Assert.Equal(9, req.GetProperty("p").GetInt64());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(s.LastRequestRaw!), req.GetProperty("bytes_out").GetInt32());
        Assert.True(req.GetProperty("ttft_us").GetInt64() >= req.GetProperty("ttfb_us").GetInt64());
        Assert.Equal(frames.Length, req.GetProperty("chunks").GetInt32());
        Assert.True(req.GetProperty("bytes_in").GetInt64() > 0);
        Assert.Equal(5, req.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, req.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(3, req.GetProperty("cache_n").GetInt32());
        Assert.Equal(2, req.GetProperty("prompt_n").GetInt32());
        Assert.Equal(12.5, req.GetProperty("prompt_ms").GetDouble());
        Assert.Equal(40.25, req.GetProperty("predicted_ms").GetDouble());
        Assert.False(req.TryGetProperty("other", out _));
        Assert.Equal(1, req.GetProperty("after_resume").GetInt32());
    }

    [Fact]
    public async Task Only_the_first_request_after_a_resume_is_marked()
    {
        await using var s = new FakeOpenAiServer();
        for (var i = 0; i < 2; i++)
            s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}", finish: "stop"), "data: [DONE]\n\n" }));
        using var trace = NewTrace();
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl), trace: trace);
        trace.MarkResume();
        for (var i = 0; i < 2; i++)
            await foreach (var _ in client.StreamAsync(new ChatRequest("m", [new ChatMessage("user", "hi")]))) { }
        trace.Flush();

        var reqs = Named(Spans(), "request");
        Assert.Equal(2, reqs.Count);
        Assert.True(reqs[0].TryGetProperty("after_resume", out _));
        Assert.False(reqs[1].TryGetProperty("after_resume", out _));
    }

    [Fact]
    public async Task The_shell_drain_sits_under_the_shell_call()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "shell", "{\"command\":\"Write-Output hi\"}")),
            new StreamEvent.Finished("tool_calls", null, null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null, null));
        var reg = new ToolRegistry();
        reg.Register(new ShellTool());
        using var trace = NewTrace();
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(_dir), "m", trace: trace);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);
        trace.Flush();

        var spans = Spans();
        var exec = Named(spans, "tool.exec").Single();
        var drain = Named(spans, "shell.drain").Single();
        Assert.Equal(exec.GetProperty("id").GetInt64(), drain.GetProperty("p").GetInt64());
        Assert.Equal(0, drain.GetProperty("exit").GetInt32());
        Assert.Equal(0, drain.GetProperty("background_child").GetInt32());
        Assert.True(drain.GetProperty("drain_us").GetInt64() >= 0);
    }

    //a child's turn hangs under the parent's run_agent call and its requests under the child's turn, all in the parent's sidecar
    [Fact]
    public async Task A_run_agent_child_traces_into_the_parent_under_its_call()
    {
        var inner = new FakeChatClient();
        inner.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "run_agent", "{\"role\":\"helper\",\"task\":\"look\"}")),
            new StreamEvent.Finished("tool_calls", null, null));
        inner.EnqueueTurn(new StreamEvent.TextDelta("child done"), new StreamEvent.Finished("stop", null, null));
        inner.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null, null));
        var client = new ParentRecordingClient(inner);
        using var trace = NewTrace();
        var role = new RoleFile("helper", null, null, Array.Empty<string>(), false, "You are helper.", null, "helps",
            new[] { "read_file" }, 5, true, null, null, null, null);
        var ctx = new TestToolContext(_dir);
        var runAgent = new RunAgentTool(
            roles: () => [role],
            refuseRole: _ => null,
            composeChild: r => (RoleComposition.Compose(r, null, Array.Empty<(string, string)>(), null, cwd: null), "m"),
            subagentWiring: _ => (new ToolRegistry(), new HookBus()),
            client: client,
            requestSettings: () => ("m", null, null, null),
            parentCtx: ctx);
        var reg = new ToolRegistry();
        reg.Register(runAgent);
        var loop = new AgentLoop(client, reg, new HookBus(), ctx, "m", trace: trace);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);
        trace.Flush();

        var spans = Spans();
        var turns = Named(spans, "turn");
        var parent = turns.Single(t => !t.TryGetProperty("agent", out _));
        var child = turns.Single(t => t.TryGetProperty("agent", out _));
        Assert.Equal("helper", child.GetProperty("agent").GetString());
        Assert.False(child.TryGetProperty("seq", out _));
        var exec = Named(spans, "tool.exec").Single();
        Assert.Equal(exec.GetProperty("id").GetInt64(), child.GetProperty("p").GetInt64());
        Assert.Equal(parent.GetProperty("id").GetInt64(), Named(spans, "tool").Single().GetProperty("p").GetInt64());
        //the second request is the child's, and the counting client passed its turn on as the parent
        Assert.Equal(
            new[] { parent.GetProperty("id").GetInt64(), child.GetProperty("id").GetInt64(), parent.GetProperty("id").GetInt64() },
            client.Parents);
    }

    //the compact span sits under the turn that asked for it, and the summary request under the compact span
    [Fact]
    public async Task A_compaction_traces_its_reason_sizes_and_request_parent()
    {
        var inner = new FakeChatClient();
        inner.EnqueueTurn(new StreamEvent.TextDelta("Task / goal: test. Current state: done."), new StreamEvent.Finished("stop", null, null));
        var client = new ParentRecordingClient(inner);
        using var trace = NewTrace();
        var convo = new Conversation("sys");
        convo.AddUser("a long task");
        convo.AddAssistant("worked on it");

        var summary = await new Compactor(client, "m", null, trace).SummarizeAsync(
            convo, new RecordingObserver(), default, reason: "Proactive", traceParent: 5);
        trace.Flush();

        Assert.NotNull(summary);
        var compact = Named(Spans(), "compact").Single();
        Assert.Equal(5, compact.GetProperty("p").GetInt64());
        Assert.Equal("Proactive", compact.GetProperty("reason").GetString());
        Assert.True(compact.GetProperty("before_tokens").GetInt32() > 0);
        Assert.True(compact.GetProperty("summary_tokens").GetInt32() > 0);
        Assert.Equal(1, compact.GetProperty("ok").GetInt32());
        Assert.Equal(new[] { compact.GetProperty("id").GetInt64() }, client.Parents);
    }

    private sealed class ParentRecordingClient(IChatClient inner) : IChatClient
    {
        public List<long> Parents { get; } = new();
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
            StreamAsync(request, 0, ct);
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, long traceParent, CancellationToken ct)
        {
            Parents.Add(traceParent);
            return inner.StreamAsync(request, ct);
        }
    }
}
