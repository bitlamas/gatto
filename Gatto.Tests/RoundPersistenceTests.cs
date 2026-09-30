using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a one-shot run must flush each round, or a run killed by a time cap writes nothing
public class RoundPersistenceTests
{
    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static string ToolCallChunk(string id, string name, string args) =>
        Chunk($"{{\"tool_calls\":[{{\"index\":0,\"id\":\"{id}\",\"function\":{{\"name\":\"{name}\",\"arguments\":\"{args}\"}}}}]}}", finish: "tool_calls");

    //each flush is snapshotted. the property is when the flush happened, and a live reference shows only the end state.
    private sealed class RecordingSink
    {
        public List<List<string>> Flushes { get; } = new();
        public void Flush(Conversation c) =>
            Flushes.Add(c.Messages.Select(m => $"{m.Role}:{m.Content ?? "<null>"}").ToList());
    }

    private static (AgentLoop Loop, RecordingSink Sink, ToolRegistry Tools) Build(FakeOpenAiServer s)
    {
        var tools = new ToolRegistry();
        tools.Register(new EchoTool());
        var sink = new RecordingSink();
        var loop = new AgentLoop(
            new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl)),
            tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m")
        {
            OnRoundPersisted = sink.Flush,
        };
        return (loop, sink, tools);
    }

    //a flush must happen once the tool result is in the conversation, after the add rather than on the result hook
    [Fact]
    public async Task A_flush_lands_after_each_tool_round_carrying_that_rounds_result()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { ToolCallChunk("c1", "echo", "{}"), "data: [DONE]\n\n" }));
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"done\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var (loop, sink, _) = Build(s);
        var convo = new Conversation("sys");

        await loop.RunTurnAsync(convo, "go", new RecordingObserver(), default);

        //a tool round flushes twice. the first flush holds the asking record and the second the result.
        Assert.Equal(2, sink.Flushes.Count);
        Assert.Contains(sink.Flushes[0], r => r.StartsWith("assistant:", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Flushes[0], r => r.StartsWith("tool:", StringComparison.Ordinal));
        Assert.Contains(sink.Flushes[1], r => r.StartsWith("tool:", StringComparison.Ordinal));
    }

    //the asking record must be on disk, dated, while the tool runs. a hung tool then leaves its call in the session file.
    [Fact]
    public async Task THE_ASKING_RECORD_IS_ON_DISK_WHILE_ITS_TOOL_RUNS()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            await using var s = new FakeOpenAiServer();
            s.Enqueue(new FakeResponse(Frames: new[] { ToolCallChunk("c1", "probe", "{}"), "data: [DONE]\n\n" }));
            s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"done\"}", finish: "stop"), "data: [DONE]\n\n" }));
            string? seen = null;
            var tools = new ToolRegistry();
            tools.Register(new GuardProbeTool(() => seen = OnDisk(home, cwd)));
            var sessions = new SessionStore(home, cwd);
            var loop = new AgentLoop(
                new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl)),
                tools, new HookBus(), new TestToolContext(cwd), "m")
            {
                OnRoundPersisted = sessions.Save,
            };

            await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);

            AssertAskedAndUnanswered(seen, "c1");
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(cwd, true);
        }
    }

    //read the newest session file for this directory, or return null when none exists.
    internal static string? OnDisk(string home, string cwd) =>
        new SessionStore(home, cwd).LatestPathForCwd() is { } p ? File.ReadAllText(p) : null;

    //the asking record is on disk, dated, and its result is not there yet. the check first runs over a record ChatJson wrote, so a pattern that sees nothing fails
    internal static void AssertAskedAndUnanswered(string? file, string id)
    {
        var planted = new Conversation(null);
        planted.AddAssistant("", new[] { new ToolCall(id, "probe", "{}") });
        Assert.True(Asks(SessionStore.ChatJson(planted.Messages[0]), id), "the check cannot see a written record");

        Assert.NotNull(file);
        var lines = file!.Split('\n');
        Assert.Contains(lines, l => Asks(l, id));
        Assert.DoesNotContain(lines, l => l.Contains($"\"tool_call_id\":\"{id}\"", StringComparison.Ordinal));
    }

    private static bool Asks(string line, string id) =>
        line.Contains("\"role\":\"assistant\"", StringComparison.Ordinal)
        && line.Contains("\"tool_calls\"", StringComparison.Ordinal)
        && line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal)
        && line.Contains("\"ts\":\"", StringComparison.Ordinal);

    //a turn with no tool round flushes nothing, the caller's own end-of-turn save covers that tail
    [Fact]
    public async Task A_turn_with_no_tools_flushes_nothing()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"hi\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var (loop, sink, _) = Build(s);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);

        Assert.Empty(sink.Flushes);
    }

    //a failing flush warns once and later rounds stay quiet, the turn must still complete
    [Fact]
    public async Task A_failing_flush_says_so_once_and_then_stays_quiet()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { ToolCallChunk("c1", "echo", "{}"), "data: [DONE]\n\n" }));
        s.Enqueue(new FakeResponse(Frames: new[] { ToolCallChunk("c2", "echo", "{}"), "data: [DONE]\n\n" }));
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"done\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var tools = new ToolRegistry();
        tools.Register(new EchoTool());
        var loop = new AgentLoop(
            new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl)),
            tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m")
        {
            OnRoundPersisted = _ => throw new IOException("disk full"),
        };
        var obs = new RecordingObserver();

        var r = await loop.RunTurnAsync(new Conversation("sys"), "go", obs, default);

        Assert.Equal(TurnOutcome.Completed, r.Outcome);      //a failed flush must never fail the turn.
        var flushWarnings = obs.Warnings.Where(w => w.Contains("flush", StringComparison.Ordinal)).ToList();
        Assert.Single(flushWarnings);
        Assert.Contains("disk full", flushWarnings[0], StringComparison.Ordinal);
    }

    //the callback is null here, and a plain loop must still finish a tool round without throwing
    [Fact]
    public async Task The_callback_is_null_by_default()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { ToolCallChunk("c1", "echo", "{}"), "data: [DONE]\n\n" }));
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"done\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var tools = new ToolRegistry();
        tools.Register(new EchoTool());
        var loop = new AgentLoop(
            new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl)),
            tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m");

        Assert.Null(loop.OnRoundPersisted);

        var r = await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), default);
        Assert.Equal(TurnOutcome.Completed, r.Outcome);
    }
}
