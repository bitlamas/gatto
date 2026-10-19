using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;
using Trace = Gatto.Core.Trace;

namespace Gatto.Tests;

//the loop opens the turn, shape and tool spans, and each child names its parent's id
public class TraceLoopTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-trace-loop-").FullName;
    private string File_ => Path.Combine(_dir, "s.trace.jsonl");

    public void Dispose() => Directory.Delete(_dir, true);

    private Trace NewTrace() => new(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => File_, () => "s", "b", 1);

    private List<JsonElement> Spans() =>
        File.ReadAllLines(File_).Skip(1).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static JsonElement One(List<JsonElement> spans, string name) => spans.Single(s => s.GetProperty("s").GetString() == name);

    private static FakeChatClient ToolThenText(string tool, string args)
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", tool, args)),
            new StreamEvent.Finished("tool_calls", null, null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null, null));
        return client;
    }

    [Fact]
    public async Task A_tool_round_writes_every_loop_span_under_its_parent()
    {
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        var hooks = new HookBus();
        using var trace = NewTrace();
        hooks.Trace = trace;
        hooks.On(HookEvent.ToolCall, _ => Task.CompletedTask);
        hooks.On(HookEvent.MessageEnd, _ => Task.CompletedTask, "ext1");
        var loop = new AgentLoop(ToolThenText("echo", "{\"msg\":\"yo\"}"), reg, hooks, new TestToolContext(_dir), "m", trace: trace) { TraceRole = "coder" };
        var convo = new Conversation("sys");

        var r = await loop.RunTurnAsync(convo, "go", new RecordingObserver(), default);
        trace.Flush();

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        var spans = Spans();
        var turn = One(spans, "turn");
        var id = turn.GetProperty("id").GetInt64();
        Assert.Equal(0, turn.GetProperty("p").GetInt64());
        Assert.Equal(1, turn.GetProperty("seq").GetInt32());   //system 0, the user record 1
        Assert.Equal(2, turn.GetProperty("rounds").GetInt32());
        Assert.Equal("Completed", turn.GetProperty("result").GetString());
        Assert.Equal("coder", turn.GetProperty("role").GetString());
        Assert.Equal("m", turn.GetProperty("model").GetString());

        var shapes = spans.Where(s => s.GetProperty("s").GetString() == "shape").ToList();
        Assert.Equal(2, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(id, s.GetProperty("p").GetInt64()));
        Assert.Equal(2, shapes[0].GetProperty("msgs").GetInt32());
        Assert.Equal(0, shapes[0].GetProperty("still_over").GetInt32());

        var tool = One(spans, "tool");
        var toolId = tool.GetProperty("id").GetInt64();
        Assert.Equal(id, tool.GetProperty("p").GetInt64());
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Equal(3, tool.GetProperty("seq").GetInt32());   //after the assistant record at 2
        Assert.Equal("tool", convo.Messages[3].Role);
        Assert.Equal(0, tool.GetProperty("error").GetInt32());
        Assert.Equal("echo: yo".Length, tool.GetProperty("result_bytes").GetInt32());

        var gate = One(spans, "tool.gate");
        Assert.Equal(toolId, gate.GetProperty("p").GetInt64());
        Assert.Equal(1, gate.GetProperty("handlers").GetInt32());
        Assert.Equal(0, gate.GetProperty("blocked").GetInt32());
        Assert.Equal(toolId, One(spans, "tool.exec").GetProperty("p").GetInt64());
        var result = One(spans, "tool.result");
        Assert.Equal(toolId, result.GetProperty("p").GetInt64());
        Assert.Equal(0, result.GetProperty("replaced").GetInt32());

        var hook = One(spans, "hook");
        Assert.Equal(id, hook.GetProperty("p").GetInt64());
        Assert.Equal("message_end", hook.GetProperty("event").GetString());
        Assert.Equal("ext1", hook.GetProperty("extension").GetString());
        Assert.Equal(0, hook.GetProperty("threw").GetInt32());
    }

    [Fact]
    public async Task A_blocked_call_and_a_replaced_result_are_marked()
    {
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        var hooks = new HookBus();
        using var trace = NewTrace();
        hooks.On(HookEvent.ToolCall, _ => throw new InvalidOperationException("no"));
        hooks.OnToolResult(p => Task.FromResult<ToolResult?>(new ToolResult("rewritten", IsError: true)));
        var loop = new AgentLoop(ToolThenText("echo", "{\"msg\":\"yo\"}"), reg, hooks, new TestToolContext(_dir), "m", trace: trace);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);
        trace.Flush();

        var spans = Spans();
        Assert.Equal(1, One(spans, "tool.gate").GetProperty("blocked").GetInt32());
        Assert.DoesNotContain(spans, s => s.GetProperty("s").GetString() == "tool.exec");
        Assert.Equal(1, One(spans, "tool.result").GetProperty("replaced").GetInt32());
        Assert.Equal(1, One(spans, "tool").GetProperty("error").GetInt32());
    }

    [Fact]
    public async Task A_turn_that_throws_is_written_as_an_error()
    {
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoConnectionException("down"));
        using var trace = NewTrace();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_dir), "m", trace: trace);

        await Assert.ThrowsAsync<GattoConnectionException>(() => loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default));
        trace.Flush();

        var turn = One(Spans(), "turn");
        Assert.Equal("Error", turn.GetProperty("result").GetString());
        Assert.Equal(1, turn.GetProperty("rounds").GetInt32());
    }

    //with no trace the loop hands the tool the session's own context, so an untraced run is the code that ran before
    [Fact]
    public async Task Untraced_the_tool_sees_the_session_context_itself()
    {
        IToolContext? seen = null;
        var reg = new ToolRegistry();
        reg.Register(new ContextProbe(c => seen = c));
        var ctx = new TestToolContext(_dir);
        var loop = new AgentLoop(ToolThenText("ctxprobe", "{}"), reg, new HookBus(), ctx, "m");
        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);
        Assert.Same(ctx, seen);
    }

    private sealed class ContextProbe(Action<IToolContext> seen) : ITool
    {
        public string Name => "ctxprobe";
        public string Description => "records the context";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            seen(ctx);
            return Task.FromResult(new ToolResult("ok"));
        }
    }
}
