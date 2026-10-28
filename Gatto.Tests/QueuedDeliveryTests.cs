using System.Text;
using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a message queued during a round reaches the model at the round's boundary, as a user record after the round's results and before the next request
public class QueuedDeliveryTests
{
    private sealed class DeliveryObserver : ITurnObserver
    {
        public List<ChatMessage> Delivered = new();
        public List<string> Warnings = new();
        public void OnTextDelta(string text) { }
        public void OnReasoningDelta(string text) { }
        public void OnToolCallStart(ToolCall call) { }
        public void OnToolResult(ToolCall call, ToolResult result) { }
        public void OnWarning(string message) => Warnings.Add(message);
        public void OnUsage(Usage usage) { }
        //must match ITurnObserver.OnQueuedDelivered exactly, otherwise the interface's default body runs and nothing is recorded
        public void OnQueuedDelivered(ChatMessage record) => Delivered.Add(record);
    }

    //the fake keeps the request's list, which is the live conversation when nothing was shaped, so sent copies each request as it goes out
    private static (AgentLoop Loop, FakeChatClient Client, Conversation Convo, DeliveryObserver Obs, List<ChatMessage[]> Sent) Setup(ITool? tool = null, Trace? trace = null)
    {
        var client = new FakeChatClient();
        var sent = new List<ChatMessage[]>();
        client.OnRequest = () => sent.Add([.. client.Requests[^1].Messages]);
        var reg = new ToolRegistry();
        reg.Register(tool ?? new EchoTool());
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(Path.GetTempPath()), "m", trace: trace);
        return (loop, client, new Conversation("sys"), new DeliveryObserver(), sent);
    }

    private static void TwoRounds(FakeChatClient client)
    {
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
    }

    //hands over the given texts once, at the first boundary, then nothing, and counts its calls
    private sealed class Once(params string[] texts)
    {
        private string[]? _left = texts;
        public int Calls;
        public IReadOnlyList<QueuedInput> Take()
        {
            Calls++;
            var t = (_left ?? []).Select(x => new QueuedInput(x)).ToList();
            _left = null;
            return t;
        }
    }

    private static string[] Shape(Conversation convo) => [.. convo.Messages.Select(m => m.Role + ":" + (m.Content ?? ""))];

    [Fact]
    public async Task A_MESSAGE_QUEUED_IN_ROUND_ONE_SITS_AFTER_ITS_RESULTS_AND_BEFORE_ROUND_TWO()
    {
        var (loop, client, convo, obs, sent) = Setup();
        TwoRounds(client);
        var take = new Once("also this");

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: take.Take);

        Assert.Equal(["system:sys", "user:go", "assistant:", "tool:echo: yo", "user:also this", "assistant:done"], Shape(convo));
        Assert.Equal("also this", sent[1][^1].Content);
        Assert.Equal("user", sent[1][^1].Role);
        Assert.Same(convo.Messages[4], Assert.Single(obs.Delivered));
    }

    [Fact]
    public async Task A_DELIVERED_LETTER_CARRIES_ITS_PATH_IN_THE_WATCH_FIELD_AND_A_TYPED_MESSAGE_NONE()
    {
        var (loop, client, convo, obs, _) = Setup();
        TwoRounds(client);
        IReadOnlyList<QueuedInput>? left = [new("C:\\box\\a.md\n\nletter a", "C:\\box\\a.md"), new("typed")];

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: () => { var t = left ?? []; left = null; return t; });

        Assert.Equal(["C:\\box\\a.md", null], obs.Delivered.Select(r => r.Watch));
        Assert.Null(convo.Messages[1].Watch);
    }

    [Fact]
    public async Task TWO_MESSAGES_TAKEN_AT_ONE_BOUNDARY_GO_IN_ORDER_AS_TWO_RECORDS()
    {
        var (loop, client, convo, obs, sent) = Setup();
        TwoRounds(client);

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: new Once("first", "second").Take);

        Assert.Equal(["user:first", "user:second"], Shape(convo)[4..6]);
        Assert.Equal(["first", "second"], obs.Delivered.Select(r => r.Content));
    }

    [Fact]
    public async Task A_TRACED_TURN_DELIVERS_TOO()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-queued-trace-").FullName;
        try
        {
            using var trace = new Trace(System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => Path.Combine(dir, "t.jsonl"), () => "s", "b", 1);
            var (loop, client, convo, obs, sent) = Setup(trace: trace);
            TwoRounds(client);

            await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: new Once("also this").Take);

            Assert.Equal("user:also this", Shape(convo)[4]);
            Assert.Single(obs.Delivered);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task A_ROUND_WITH_NO_CALLS_ENDS_THE_TURN_AND_THE_TAKE_IS_NEVER_CALLED()
    {
        var (loop, client, convo, obs, sent) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("hi"), new StreamEvent.Finished("stop", null));
        var take = new Once("for the next turn");

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: take.Take);

        Assert.Equal(0, take.Calls);
        Assert.Empty(obs.Delivered);
    }

    [Fact]
    public async Task A_CANCEL_AFTER_THE_LAST_CALL_DELIVERS_NOTHING_AND_THE_TAKE_IS_NEVER_CALLED()
    {
        using var cts = new CancellationTokenSource();
        var (loop, client, convo, obs, sent) = Setup(new ProbeTool(() => cts.Cancel()));
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")), new StreamEvent.Finished("tool_calls", null));
        var take = new Once("still queued");

        var result = await loop.RunTurnAsync(convo, "go", obs, cts.Token, takeQueued: take.Take);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, take.Calls);
        Assert.DoesNotContain(convo.Messages, m => m.Content == "still queued");
    }

    [Fact]
    public async Task NO_TAKE_CHANGES_NO_REQUEST()
    {
        var (loop, client, convo, obs, sent) = Setup();
        TwoRounds(client);

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(["system:sys", "user:go", "assistant:", "tool:echo: yo", "assistant:done"], Shape(convo));
        Assert.Equal("tool", sent[1][^1].Role);
        Assert.Empty(obs.Delivered);
    }

    [Fact]
    public async Task A_BACKGROUND_END_TURN_DELIVERS_A_MESSAGE_TYPED_DURING_IT()
    {
        var (loop, client, convo, obs, sent) = Setup();
        TwoRounds(client);
        var end = convo.BackgroundRecord(2, "background task 2 ended · exit 0 · 1s\nbuilt", "exit 0", isError: false);

        await loop.RunTurnAsync(convo, end, obs, default, takeQueued: new Once("and run the tests").Take);

        Assert.Equal("user:and run the tests", Shape(convo)[4]);
        Assert.Null(convo.Messages[4].Background);
    }

    [Fact]
    public async Task A_DELIVERED_RECORD_IS_SAVED_BEFORE_THE_RENDERER_HEARS_OF_IT()
    {
        var (loop, client, convo, obs, sent) = Setup();
        TwoRounds(client);
        var saved = new List<int>();
        loop.OnRoundPersisted = c => saved.Add(c.Count);

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: new Once("also this").Take);

        Assert.Contains(5, saved);   //system, user, assistant, tool and the delivered record, flushed before OnQueuedDelivered
    }

    private static string Json(ChatMessage m)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) RequestJson.WriteMessage(w, m, new ImageCache(), null);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task ROUND_TWO_HOLDS_ROUND_ONE_BYTE_FOR_BYTE_WITH_A_DELIVERY_BETWEEN()
    {
        var (loop, client, convo, obs, sent) = Setup();
        client.EnqueueTurn(
            new StreamEvent.ReasoningDelta("think once"),
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c2", "echo", "{\"msg\":\"again\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: new Once("also this").Take);

        for (var r = 1; r < sent.Count; r++)
        {
            var before = sent[r - 1].Select(Json).ToList();
            var after = sent[r].Select(Json).ToList();
            Assert.True(after.Count > before.Count);
            Assert.Equal(before, after.Take(before.Count));
        }
    }
}
