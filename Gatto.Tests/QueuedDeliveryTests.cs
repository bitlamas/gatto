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
        public List<ChatMessage> Images = new();
        public void OnImagesDelivered(ChatMessage record) => Images.Add(record);
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

    private static BackgroundEnd End(int n) =>
        new(n, 0, $"background task {n} ended · exit 0 · 1s\nbuilt {n}", "exit 0", false, BackgroundTasks.Exit, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AN_END_QUEUED_DURING_A_ROUND_SITS_AFTER_ITS_RESULTS_AS_A_BACKGROUND_RECORD()
    {
        var (loop, client, convo, obs, sent) = Setup();
        TwoRounds(client);
        var end = End(3);
        IReadOnlyList<QueuedInput>? left = [new(end.Text, null, end), new("typed")];

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: () => { var t = left ?? []; left = null; return t; });

        Assert.Equal(["system", "user", "assistant", "tool", "user", "user", "assistant"], convo.Messages.Select(m => m.Role));
        var record = convo.Messages[4];
        Assert.Equal((3, "exit 0", false, (string?)null), (record.Background, record.Gloss, record.IsError, record.Watch));
        Assert.Equal(end.Text, record.Content);
        Assert.Null(convo.Messages[5].Background);
        Assert.Equal([record, convo.Messages[5]], obs.Delivered);
        Assert.Same(record, sent[1][4]);
    }

    [Fact]
    public async Task THE_REQUEST_AFTER_A_DELIVERED_END_BEGINS_WITH_THE_PREVIOUS_ONE_BYTE_FOR_BYTE()
    {
        var (loop, client, convo, obs, sent) = Setup();
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")), Calls);
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c2", "echo", "{\"msg\":\"again\"}")), Calls);
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), Stop);
        var end = End(1);
        var rounds = 0;

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: () => ++rounds == 2 ? [new(end.Text, null, end)] : []);

        for (var r = 1; r < sent.Count; r++)
        {
            var before = sent[r - 1].Select(Json).ToList();
            var after = sent[r].Select(Json).ToList();
            Assert.Equal(before, after.Take(before.Count));
        }
        Assert.Equal(1, sent[^1][^1].Background);
    }

    //rebuilds through the real rebuild and keeps what the compaction was told
    private sealed class Rebuilding(Conversation convo) : ICompactionHandler
    {
        public List<TurnSoFar> Told = new();
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct) =>
            throw new InvalidOperationException("the loop asked with the prompt alone");
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, TurnSoFar turn, ITurnObserver observer, long traceParent, CancellationToken ct)
        {
            Told.Add(turn);
            var (result, _) = Compactor.RebuildMidTurn("summary", convo, turn, observer, null, () => new ComposedSystem("sys", null), "·");
            return Task.FromResult<CompactionResult?>(result);
        }
    }

    [Fact]
    public async Task A_COMPACTION_AFTER_A_DELIVERED_END_CARRIES_ITS_HEAD_LINE_AND_NOT_ITS_OUTPUT()
    {
        var convo = new Conversation("sys");
        var handler = new Rebuilding(convo);
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(Path.GetTempPath()), "m", compaction: handler);
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")), Calls);
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), Stop);
        var end = End(2);
        IReadOnlyList<QueuedInput>? left = [new(end.Text, null, end)];

        await loop.RunTurnAsync(convo, "go", new DeliveryObserver(), default, takeQueued: () => { var t = left ?? []; left = null; return t; });

        Assert.Equal(["background task 2 ended · exit 0 · 1s"], Assert.Single(handler.Told).Delivered);
        Assert.DoesNotContain("built 2", convo.Messages[1].Content!, StringComparison.Ordinal);
    }

    //read_file's image shape without the disk sniff: each call returns the named file's ref, and onCall runs inside the call
    private sealed class ImagesTool(string dir, Action<int>? onCall = null) : ITool
    {
        private int _calls;
        public string Name => "read_file";
        public string Description => "reads";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            onCall?.Invoke(++_calls);
            var path = Path.Combine(dir, args.GetProperty("path").GetString()!);
            var img = ImageRef.FromFile(path);
            return Task.FromResult(new ToolResult($"image: {img.Name}, {img.Size}", Images: [img]));
        }
    }

    private static string Picture(string dir, string name, byte seed)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [.. ImageFixtures.Png1x1, seed]);
        return path;
    }

    private static StreamEvent Read(string id, string name) =>
        new StreamEvent.ToolCallReady(new ToolCall(id, "read_file", $"{{\"path\":\"{name}\"}}"));

    private static StreamEvent.Finished Calls => new("tool_calls", null);
    private static StreamEvent.Finished Stop => new("stop", null);

    private sealed class Pictures : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory("gatto-readimage-").FullName;
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task TWO_READS_IN_ONE_ROUND_GIVE_ONE_IMAGE_RECORD_AFTER_THE_LAST_RESULT_AND_BEFORE_A_QUEUED_MESSAGE()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        Picture(p.Dir, "b.png", 2);
        var (loop, client, convo, obs, sent) = Setup(new ImagesTool(p.Dir));
        client.EnqueueTurn(Read("c1", "a.png"), Read("c2", "b.png"), Calls);
        client.EnqueueTurn(new StreamEvent.TextDelta("seen"), Stop);

        await loop.RunTurnAsync(convo, "go", obs, default, takeQueued: new Once("also this").Take);

        Assert.Equal(["system", "user", "assistant", "tool", "tool", "user", "user", "assistant"], convo.Messages.Select(m => m.Role));
        var record = convo.Messages[5];
        Assert.Equal("read_file", record.By);
        Assert.Equal(["a.png", "b.png"], record.Images!.Select(i => i.Name));
        Assert.Equal($"images read this round: a.png ({record.Images![0].Size}), b.png ({record.Images[1].Size})", record.Content);
        Assert.Equal("also this", convo.Messages[6].Content);
        Assert.Same(record, sent[1][5]);
    }

    [Fact]
    public async Task THE_SAME_FILE_TWICE_IN_A_ROUND_GIVES_ONE_REF_AND_ONE_ALREADY_SHOWN_NAME()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        var (loop, client, convo, obs, _) = Setup(new ImagesTool(p.Dir));
        client.EnqueueTurn(Read("c1", "a.png"), Read("c2", "a.png"), Calls);
        client.EnqueueTurn(Stop);

        await loop.RunTurnAsync(convo, "go", obs, default);

        var record = Assert.Single(convo.Messages, m => m.By is not null);
        Assert.Single(record.Images!);
        Assert.EndsWith("; already shown: a.png", record.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_SHA_THE_CONVERSATION_ALREADY_CARRIES_IS_NAMED_AND_NOT_SENT_AGAIN()
    {
        using var p = new Pictures();
        var path = Picture(p.Dir, "a.png", 1);
        var (loop, client, convo, obs, _) = Setup(new ImagesTool(p.Dir));
        convo.AddUser("look at this", [ImageRef.FromFile(path)]);
        client.EnqueueTurn(Read("c1", "a.png"), Calls);
        client.EnqueueTurn(Stop);

        await loop.RunTurnAsync(convo, "and again", obs, default);

        var record = Assert.Single(convo.Messages, m => m.By is not null);
        Assert.Null(record.Images);
        Assert.Equal("already shown: a.png", record.Content);
    }

    [Fact]
    public async Task A_HOOK_THAT_REPLACES_THE_TEXT_KEEPS_THE_TOOLS_IMAGES()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(new ImagesTool(p.Dir));
        var hooks = new HookBus();
        hooks.OnToolResult(_ => Task.FromResult<ToolResult?>(new ToolResult("rewritten")));
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(p.Dir), "m");
        var convo = new Conversation("sys");
        client.EnqueueTurn(Read("c1", "a.png"), Calls);
        client.EnqueueTurn(Stop);

        await loop.RunTurnAsync(convo, "go", new DeliveryObserver(), default);

        Assert.Equal("rewritten", convo.Messages[3].Content);
        Assert.Equal("a.png", Assert.Single(Assert.Single(convo.Messages, m => m.By is not null).Images!).Name);
    }

    [Fact]
    public async Task A_HOOK_THAT_RETURNS_AN_ERROR_DELIVERS_NOTHING()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(new ImagesTool(p.Dir));
        var hooks = new HookBus();
        hooks.OnToolResult(h => Task.FromResult<ToolResult?>(h.Result! with { IsError = true }));
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(p.Dir), "m");
        var convo = new Conversation("sys");
        client.EnqueueTurn(Read("c1", "a.png"), Calls);
        client.EnqueueTurn(Stop);

        await loop.RunTurnAsync(convo, "go", new DeliveryObserver(), default);

        Assert.DoesNotContain(convo.Messages, m => m.By is not null);
    }

    [Fact]
    public async Task A_CANCEL_BETWEEN_TWO_CALLS_DELIVERS_THE_FIRST_IMAGE_AFTER_THE_CANCELLED_RESULT()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        Picture(p.Dir, "b.png", 2);
        using var cts = new CancellationTokenSource();
        var (loop, client, convo, obs, _) = Setup(new ImagesTool(p.Dir, n => { if (n == 1) cts.Cancel(); }));
        client.EnqueueTurn(Read("c1", "a.png"), Read("c2", "b.png"), Calls);

        var result = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal(["system", "user", "assistant", "tool", "tool", "user"], convo.Messages.Select(m => m.Role));
        Assert.Equal(LoopErrors.Cancelled, convo.Messages[4].Content);
        Assert.Equal("a.png", Assert.Single(convo.Messages[5].Images!).Name);
    }

    [Fact]
    public async Task A_CANCEL_AFTER_THE_LAST_CALL_DELIVERS_BOTH_IMAGES()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        Picture(p.Dir, "b.png", 2);
        using var cts = new CancellationTokenSource();
        var (loop, client, convo, obs, _) = Setup(new ImagesTool(p.Dir, n => { if (n == 2) cts.Cancel(); }));
        client.EnqueueTurn(Read("c1", "a.png"), Read("c2", "b.png"), Calls);

        var result = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal("user", convo.Messages[^1].Role);
        Assert.Equal(["a.png", "b.png"], convo.Messages[^1].Images!.Select(i => i.Name));
    }

    [Fact]
    public async Task A_ROUND_PAST_THE_CEILING_DROPS_THE_REF_THAT_WOULD_PASS_IT_AND_NAMES_IT()
    {
        using var p = new Pictures();
        var convo = new Conversation(null);
        var a = new ImageRef(Path.Combine(p.Dir, "a.png"), ImageRef.MaxBytes - 10, "aa", "image/png");
        var b = new ImageRef(Path.Combine(p.Dir, "b.png"), 20, "bb", "image/png");
        var c = new ImageRef(Path.Combine(p.Dir, "c.png"), 10, "cc", "image/png");

        var record = Gatto.Core.Loop.ImageDelivery.Build(convo, [a, b, c], []);

        Assert.Equal(["a.png", "c.png"], record.Images!.Select(i => i.Name));
        Assert.EndsWith("; not shown, over the round's 20 MB: b.png", record.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task A_REFUSAL_AFTER_THE_RECORD_REMOVES_IT_ALONE_AND_KEEPS_THE_QUEUED_MESSAGE(int status)
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        var (loop, client, convo, obs, _) = Setup(new ImagesTool(p.Dir));
        client.EnqueueTurn(Read("c1", "a.png"), Calls);
        client.EnqueueThrow(new GattoHttpStatusException("refused", status, "{}"));

        await Assert.ThrowsAsync<GattoHttpStatusException>(() =>
            loop.RunTurnAsync(convo, "go", obs, default, takeQueued: new Once("also this").Take));

        Assert.DoesNotContain(convo.Messages, m => m.By is not null);
        Assert.Equal(["system", "user", "assistant", "tool", "user"], convo.Messages.Select(m => m.Role));
        Assert.Equal("also this", convo.Messages[^1].Content);
        Assert.Contains("the server refused the request after a.png; image withdrawn", obs.Warnings);
    }

    [Fact]
    public async Task A_BUSY_SERVER_OR_AN_OVERFLOW_WITHDRAWS_NOTHING()
    {
        foreach (var refusal in new GattoHttpStatusException[]
                 { new("busy", 429, ""), new("loading", 503, ""), new GattoContextOverflowException("full", 9, 8) })
        {
            using var p = new Pictures();
            Picture(p.Dir, "a.png", 1);
            var (loop, client, convo, obs, _) = Setup(new ImagesTool(p.Dir));
            client.EnqueueTurn(Read("c1", "a.png"), Calls);
            client.EnqueueThrow(refusal);

            await Assert.ThrowsAnyAsync<GattoHttpStatusException>(() => loop.RunTurnAsync(convo, "go", obs, default));

            Assert.Single(convo.Messages, m => m.By is not null);
        }
    }

    [Fact]
    public async Task THE_REQUEST_AFTER_AN_IMAGE_RECORD_BEGINS_WITH_THE_PREVIOUS_ONE_BYTE_FOR_BYTE()
    {
        using var p = new Pictures();
        Picture(p.Dir, "a.png", 1);
        var client = new FakeChatClient();
        var sent = new List<ChatMessage[]>();
        client.OnRequest = () => sent.Add([.. client.Requests[^1].Messages]);
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        reg.Register(new ImagesTool(p.Dir));
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(p.Dir), "m");
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")), Calls);
        client.EnqueueTurn(Read("c2", "a.png"), Calls);
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), Stop);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new DeliveryObserver(), default);

        var cache = new ImageCache();
        string J(ChatMessage m) => RequestJson.SerialiseMessage(m, cache);
        var before = sent[1].Select(J).ToList();
        var after = sent[2].Select(J).ToList();
        Assert.Equal("read_file", sent[2][^1].By);
        Assert.Equal(before, after.Take(before.Count));
    }
}
