using System.Runtime.CompilerServices;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a failed turn throws when the iterator is enumerated, the way a real connect failure surfaces through await foreach
public sealed class FailableChatClient : IChatClient
{
    private readonly Queue<StreamEvent[]?> _turns = new();   //a null entry marks a failed turn.
    public int CallCount { get; private set; }
    public void EnqueueTurn(params StreamEvent[] events) => _turns.Enqueue(events);
    public void EnqueueFailure() => _turns.Enqueue(null);

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        CallCount++;
        var script = _turns.Dequeue();
        if (script is null) throw new GattoConnectionException("server down — run: gatto serve start m");
        foreach (var e in script) { ct.ThrowIfCancellationRequested(); yield return e; }
        await Task.CompletedTask;
    }
}

public sealed class GuardProbeTool(Action onExecute) : ITool
{
    public string Name => "probe";
    public string Description => "records execution";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        onExecute();
        return Task.FromResult(new ToolResult("probed"));
    }
}

public class ReplTurnGuardTests
{
    private static (AgentLoop, FailableChatClient, Conversation, RecordingObserver) Setup(params ITool[] tools)
    {
        var client = new FailableChatClient();
        var reg = new ToolRegistry();
        foreach (var t in tools) reg.Register(t);
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        return (loop, client, new Conversation(null), new RecordingObserver());
    }

    [Fact]
    public async Task ConnectFailureRound1_RollsBackUserMessage()
    {
        var (loop, client, convo, obs) = Setup();
        client.EnqueueFailure();
        var mark = convo.Count;

        await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "hello", obs, null, default);

        Assert.Equal(mark, convo.Count);
        Assert.Single(obs.Warnings);
    }

    //a lost connection to a server gatto served says that server is gone in place of the client's error, so the start command shows once
    [Fact]
    public async Task A_connection_failure_to_a_dead_server_says_so_in_one_line()
    {
        var (loop, client, convo, obs) = Setup();
        client.EnqueueFailure();
        client.EnqueueFailure();

        await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "hello", obs, null, default, serverGone: () => "llama-server for m is not running");
        Assert.Equal(["llama-server for m is not running"], obs.Warnings);

        var quiet = new RecordingObserver();
        await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "hello", quiet, null, default, serverGone: () => null);
        Assert.Equal(["server down — run: gatto serve start m"], quiet.Warnings);
    }

    [Fact]
    public async Task ConnectionFailureRound2_KeepsExecutedToolExchange()
    {
        var executed = 0;
        var (loop, client, convo, obs) = Setup(new GuardProbeTool(() => executed++));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueFailure();

        await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "go", obs, null, default);

        Assert.Equal(1, executed);
        Assert.Contains(convo.Messages, m => m.Role == "user" && m.Content == "go");
        Assert.Contains(convo.Messages, m => m.Role == "assistant" && m.ToolCalls is { Count: 1 } tc && tc[0].Id == "c1");
        Assert.Contains(convo.Messages, m => m.Role == "tool" && m.ToolCallId == "c1");
        Assert.Single(obs.Warnings);
    }

    [Fact]
    public async Task ConnectionFailureRound2_PersistsPartialTurn()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            var (loop, client, convo, obs) = Setup(new GuardProbeTool(() => { }));
            client.EnqueueTurn(
                new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
                new StreamEvent.Finished("tool_calls", null));
            client.EnqueueFailure();
            var sessions = new SessionStore(home, cwd);

            await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "go", obs, sessions, default);

            var loaded = new SessionStore(home, cwd).LoadLatestForCwd();
            Assert.NotNull(loaded);
            Assert.Contains(loaded!, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content == "probed");
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(cwd, true);
        }
    }

    //the asking record must be on disk before its tool runs (a freeze in between would leave a session with no trace of it)
    [Fact]
    public async Task THE_REPL_PUTS_THE_ASKING_RECORD_ON_DISK_BEFORE_ITS_TOOL_RUNS()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            string? seen = null;
            var (loop, client, convo, obs) =
                Setup(new GuardProbeTool(() => seen = RoundPersistenceTests.OnDisk(home, cwd)));
            client.EnqueueTurn(
                new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
                new StreamEvent.Finished("tool_calls", null));
            client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

            await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "go", obs, new SessionStore(home, cwd), default);

            RoundPersistenceTests.AssertAskedAndUnanswered(seen, "c1");
            Assert.Null(loop.OnRoundPersisted);   //the per-turn hook must clear when the turn ends.
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(cwd, true);
        }
    }

    //passing a display model routes the save through TranscriptStore, which is what writes the display events to the file
    [Fact]
    public async Task AModelRoutesTheSaveThroughTranscriptStore_EventsPersist()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            var (loop, client, convo, obs) = Setup();
            client.EnqueueTurn(new StreamEvent.TextDelta("hello"), new StreamEvent.Finished("stop", null));
            var sessions = new SessionStore(home, cwd);
            var model = new TranscriptModel("generalist");
            model.Append(new CommandEchoItem(new[] { "gatto · generalist" }, null));   //the banner is appended before the turn runs.

            await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "hi", obs, sessions, default, model);

            var path = new SessionStore(home, cwd).LatestPathForCwd();
            Assert.NotNull(path);
            var text = File.ReadAllText(path!);
            Assert.Contains("\"event\":\"command\"", text, StringComparison.Ordinal);   //the display event must reach the file.
            Assert.Contains("\"content\":\"hi\"", text, StringComparison.Ordinal);       //chat records persist alongside the display event.
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(cwd, true);
        }
    }

    //cancels the turn's token mid-rescue and returns null, the shape a real summarize produces when ctrl+c arrives during that call
    private sealed class CancellingCompactionHandler(CancellationTokenSource cts) : ICompactionHandler
    {
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt,
            ITurnObserver observer, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromResult<CompactionResult?>(null);
        }
    }

    //a ctrl+c during rescue surfaces as an OperationCanceledException, the guard must absorb it or the run dies at the app backstop
    [Fact]
    public async Task CancelledDuringRescue_SessionSurvives_DanglingUserRolledBack()
    {
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m");
        var cts = new CancellationTokenSource();
        loop.EnableAutoCompact(new CancellingCompactionHandler(cts), new ContextUsageState(), null);
        var convo = new Conversation("s");
        var obs = new RecordingObserver();
        var mark = convo.Count;

        //the guard must absorb this exception, the app backstop would kill the run
        await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "hello", obs, null, cts.Token);

        Assert.Equal(mark, convo.Count);                                    //the rolled-back conversation leaves no dangling user message.
        Assert.Contains(obs.Warnings, w => w.Contains("cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Round1Failure_DoesNotSave()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            var (loop, client, convo, obs) = Setup();
            client.EnqueueFailure();
            var sessions = new SessionStore(home, cwd);

            await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, "hello", obs, sessions, default);

            //the rollback branch never saves, so no file exists.
            Assert.Null(new SessionStore(home, cwd).LoadLatestForCwd());
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(cwd, true);
        }
    }
}
