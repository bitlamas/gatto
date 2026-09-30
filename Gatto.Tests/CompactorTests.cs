using System.Linq;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class CompactorTests
{
    //an sse chunk in the exact shape that llama.cpp emits.
    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static Conversation SmallConvo()
    {
        var c = new Conversation("sys");
        c.AddUser("hello");
        c.AddAssistant("hi there");
        return c;
    }

    [Fact]
    public async Task SummarizeAsync_StripsTools_AndReturnsScriptedSummary()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"SUMMARY TEXT\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var compactor = new Compactor(client, "m");

        var summary = await compactor.SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        Assert.Equal("SUMMARY TEXT", summary);
        //the request must hold no tools key, a summarizer never calls tools
        Assert.False(server.LastRequestBody!.Value.TryGetProperty("tools", out _));
        //the request ends with the fixed template prompt as the final user message.
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task SummarizeAsync_SurfacesDeltasOnReasoningChannel()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"abc\"}"), Chunk("{\"content\":\"def\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var obs = new ReasoningRecorder();

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), obs, default);

        Assert.Equal("abcdef", summary);
        Assert.Equal("abcdef", obs.Reasoning);   //deltas are rendered dimmed through the reasoning channel.
        Assert.Equal("", obs.Text);               //nothing reaches the plain text channel.
    }

    [Fact]
    public async Task SummarizeAsync_Truncation_ReturnsNull()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"half\"}"), Chunk("{}", "length"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        Assert.Null(summary);
    }

    [Fact]
    public async Task SummarizeAsync_StreamDrop_ReturnsNull()
    {
        await using var server = new FakeOpenAiServer();
        //frames with no finish_reason make the client yield truncated.
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"partial\"}"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        Assert.Null(summary);
    }

    [Fact]
    public async Task SummarizeAsync_Cancellation_ReturnsNull()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("x"), new StreamEvent.Finished("stop", null));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), new RecordingObserver(), cts.Token);

        Assert.Null(summary);
    }

    [Fact]
    public async Task SummarizeAsync_ConnectionFailure_ReturnsNull()
    {
        var client = new FailableChatClient();
        client.EnqueueFailure();

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        Assert.Null(summary);
    }

    private sealed class ScriptedClient(Func<ChatRequest, int, IEnumerable<StreamEvent>> script) : IChatClient
    {
        public readonly List<ChatRequest> Requests = new();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var ev in script(request, Requests.Count)) { yield return ev; await Task.Yield(); }
        }
    }

    private sealed class NullObserver : ITurnObserver
    {
        public void OnTextDelta(string t) { }
        public void OnReasoningDelta(string t) { }
        public void OnToolCallStart(ToolCall c) { }
        public void OnToolResult(ToolCall c, Gatto.Core.Tools.ToolResult r) { }
        public void OnWarning(string m) { }
        public void OnUsage(Usage u) { }
    }

    [Fact]
    public async Task Summarize_OverLimitConversation_SendsShapedRequest()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        for (var i = 0; i < 10; i++) { convo.AddAssistant("", new[] { Call($"c{i}") }); convo.AddToolResult($"c{i}", new Gatto.Core.Tools.ToolResult(new string('x', 30_000))); }
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta("summary"), new StreamEvent.Finished("stop", null) });
        var summary = await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536);
        Assert.Equal("summary", summary);
        Assert.True(ContextBudget.Estimate(client.Requests[0].Messages) <= 65_536 * Compactor.TargetFraction
            + Compactor.TemplatePromptLengthForTests / 4);
    }

    [Fact]
    public async Task Summarize_OwnOverflow_RetriesOnceHarsher_ThenSucceeds()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, n) => n == 1
            ? throw new GattoContextOverflowException("over", 70_000, 65_536)
            : new StreamEvent[] { new StreamEvent.TextDelta("summary"), new StreamEvent.Finished("stop", null) });
        var summary = await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536);
        Assert.Equal("summary", summary);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Summarize_TwoOverflows_ReturnsNull_SessionUntouchedContract()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, _) => throw new GattoContextOverflowException("over", null, null));
        Assert.Null(await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536));
    }

    [Fact]
    public async Task Summarize_MidTurn_TemplateGainsInFlightHeading()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta("x"), new StreamEvent.Finished("stop", null) });
        await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, midTurn: true);
        Assert.Contains("In-flight task", client.Requests[0].Messages[^1].Content);
        await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, midTurn: false);
        Assert.DoesNotContain("In-flight task", client.Requests[1].Messages[^1].Content);
    }

    [Fact]
    public async Task Summarize_TemplateIsFindingsAwareWithRaisedCap()
    {
        //this pins the template text only, the fake scripts the summary and a real check needs a live model
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta("x"), new StreamEvent.Finished("stop", null) });
        await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None);
        var template = client.Requests[0].Messages[^1].Content;
        Assert.Contains("Findings & facts learned", template);
        Assert.Contains("Weight the headings to the work", template);
        Assert.Contains("under 1200 words", template);
        Assert.DoesNotContain("under 600 words", template);
    }

    [Fact]
    public async Task Summarize_NeverSendsToolSpecs()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta("x"), new StreamEvent.Finished("stop", null) });
        await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 1); //a tiny window forces the harshest shaping.
        Assert.All(client.Requests, r => Assert.Null(r.Tools));
    }

    [Fact]
    public void Continuation_keeps_the_users_words_verbatim()
    {
        //the original prompt is the only trustworthy statement of intent, so it must appear verbatim
        const string prompt = "The repo is here: C:\\x. Also please read CLAUDE.md inside that "
            + "directory, then go investigate the codebase to understand how colors work.";

        Assert.Contains(prompt, Compactor.BuildContinuationPrompt(prompt), StringComparison.Ordinal);
    }

    [Fact]
    public void Continuation_labels_before_the_prompt_and_directs_after_it()
    {
        //the neutralising label must precede the prompt and the directive must come last, or the model obeys the first imperative it sees
        var built = Compactor.BuildContinuationPrompt("go investigate the codebase");

        var label = built.IndexOf("Original request", StringComparison.Ordinal);
        var prompt = built.IndexOf("go investigate the codebase", StringComparison.Ordinal);
        var directive = built.IndexOf(Compactor.ContinuationDirective, StringComparison.Ordinal);

        Assert.InRange(label, 0, prompt);
        Assert.InRange(prompt, label, directive);
        Assert.EndsWith(Compactor.ContinuationDirective, built, StringComparison.Ordinal);
        Assert.Contains("do NOT start it over", built, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuation_directive_points_at_the_heading_and_forbids_re_reading()
    {
        //the summary is the only authority, so the directive points at the heading and forbids re-reading files
        var d = Compactor.ContinuationDirective;

        Assert.Contains("Immediate next step", d, StringComparison.Ordinal);
        Assert.Contains("do not re-read files", d, StringComparison.Ordinal);
        Assert.Contains("authoritative", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_asks_for_environment_gotchas()
    {
        //the template must ask for environment gotchas, else lessons learned mid-session die at every compaction
        Assert.Contains("Environment gotchas learned", Compactor.TemplatePromptForTests, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatePrompt_ContainsMemoryCandidatesHeading_AsLastSection()
    {
        //the memory section must come last, the extractor takes everything after the heading
        var t = Compactor.TemplatePromptForTests;

        Assert.Contains(Gatto.Core.Memory.MemoryPiggyback.Heading, t, StringComparison.Ordinal);
        Assert.True(
            t.IndexOf(Gatto.Core.Memory.MemoryPiggyback.Heading, StringComparison.Ordinal)
                > t.IndexOf("Immediate next step", StringComparison.Ordinal),
            "the Memory candidates heading must come after Immediate next step");
        Assert.Contains("This must be the last section.", t, StringComparison.Ordinal);
        //no heading may follow the memory heading, the closing paragraph is guidance
        Assert.DoesNotContain("Current state:", t[t.IndexOf(Gatto.Core.Memory.MemoryPiggyback.Heading, StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContext_WithoutLastExchange_ExactShape()
    {
        var block = Compactor.BuildContext("the summary", "sess-abc.jsonl", null);
        Assert.Equal(
            "## Previous session summary (compacted from sess-abc.jsonl)\n\nthe summary",
            block);
    }

    [Fact]
    public void BuildContext_WithLastExchange_ExactShape()
    {
        var block = Compactor.BuildContext("the summary", "sess-abc.jsonl", ("what is 2+2?", "4"));
        Assert.Equal(
            "## Previous session summary (compacted from sess-abc.jsonl)\n\nthe summary\n\n" +
            "## Last exchange (verbatim)\n\nUser: what is 2+2?\n\nAssistant: 4",
            block);
    }

    [Fact]
    public void LastTextExchange_SkipsTrailingToolCall()
    {
        var c = new Conversation("sys");
        c.AddUser("u1");
        c.AddAssistant("a1");
        c.AddUser("u2");
        c.AddAssistant("", new[] { new ToolCall("t1", "echo", "{}") });   //the assistant turn holds only a tool call.
        c.AddToolResult("t1", new Gatto.Core.Tools.ToolResult("res"));

        var ex = Gatto.Repl.Repl.LastTextExchange(c);

        //the last text exchange is u1 to a1, the trailing tool-call turn has no text answer
        Assert.Equal(("u1", "a1"), ex);
    }

    [Fact]
    public void LastTextExchange_NullWhenNoAssistantText()
    {
        var c = new Conversation("sys");
        c.AddUser("u");
        c.AddAssistant("", new[] { new ToolCall("t1", "echo", "{}") });

        Assert.Null(Gatto.Repl.Repl.LastTextExchange(c));
    }

    [Fact]
    public void LastTextExchange_NullOnEmptyConversation()
    {
        Assert.Null(Gatto.Repl.Repl.LastTextExchange(new Conversation("sys")));
    }

    [Fact]
    public void ShouldOfferCompact_FiresOnce_AtOrAboveThreshold()
    {
        Assert.True(Gatto.Repl.Repl.ShouldOfferCompact(85, 100, alreadyOffered: false));    //exactly the threshold fires.
        Assert.True(Gatto.Repl.Repl.ShouldOfferCompact(90, 100, alreadyOffered: false));
        Assert.False(Gatto.Repl.Repl.ShouldOfferCompact(90, 100, alreadyOffered: true));    //it never fires a second time in one session.
        Assert.False(Gatto.Repl.Repl.ShouldOfferCompact(84, 100, alreadyOffered: false));   //just below the threshold it stays false.
    }

    [Fact]
    public void ShouldOfferCompact_NullOrZeroBudget_NeverFires()
    {
        Assert.False(Gatto.Repl.Repl.ShouldOfferCompact(1_000_000, null, alreadyOffered: false));
        Assert.False(Gatto.Repl.Repl.ShouldOfferCompact(1_000_000, 0, alreadyOffered: false));
    }

    [Fact]
    public void CompactOfferLine_states_what_will_actually_happen()
    {
        //the offer line states the threshold in force and no variant names 90%, elision is age-based
        var armed = Gatto.Repl.Repl.CompactOfferLineFor(0.8);
        Assert.Contains("auto-compacts at 80%", armed);
        Assert.DoesNotContain("90%", armed);

        var off = Gatto.Repl.Repl.CompactOfferLineFor(null);
        Assert.Contains("/compact", off);
        Assert.Contains("auto-compact is off", off);
        Assert.DoesNotContain("90%", off);
    }

    [Fact]
    public async Task CompactAsync_Rotation_OldFileIntact_NewFileSaved_SystemHasSummaryAndWalk()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            var sessions = new SessionStore(home, cwd);

            var convo = new Conversation("sys");
            convo.AddUser("build the widget");
            convo.AddAssistant("done, widget built");
            sessions.Save(convo);                                   //save first, CurrentPath is null until the store has written a session file
            var oldPath = sessions.CurrentPath!;
            var oldBytes = File.ReadAllBytes(oldPath);

            var client = new FakeChatClient();
            client.EnqueueTurn(new StreamEvent.TextDelta("SESSION SUMMARY"), new StreamEvent.Finished("stop", null));
            var compactor = new Compactor(client, "m");

            var newConvo = await Gatto.Repl.Repl.CompactAsync(
                compactor, convo, sessions, () => Composed.Text("FRESH_WALK"), new RecordingObserver(), default);

            Assert.NotNull(newConvo);
            var system = newConvo!.Messages[0].Content!;
            Assert.Equal("system", newConvo.Messages[0].Role);
            Assert.Contains("FRESH_WALK", system);                                     //the recompose callback supplies the new system prompt
            Assert.Contains("SESSION SUMMARY", system);
            Assert.Contains($"## Previous session summary (compacted from {oldPath})", system);
            Assert.Contains("## Last exchange (verbatim)", system);
            Assert.Contains("User: build the widget", system);
            Assert.Contains("Assistant: done, widget built", system);

            Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
            //two session files exist now
            Assert.Equal(2, Directory.GetFiles(Path.Combine(home, "sessions"), "*.jsonl").Length);
            Assert.NotEqual(oldPath, sessions.CurrentPath);
        }
        finally { Directory.Delete(home, true); Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task CompactAsync_Failure_LeavesConvoAndFileUnchanged()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            var sessions = new SessionStore(home, cwd);

            var convo = new Conversation("sys");
            convo.AddUser("q");
            convo.AddAssistant("a");
            sessions.Save(convo);
            var oldPath = sessions.CurrentPath!;
            var oldBytes = File.ReadAllBytes(oldPath);
            var countBefore = convo.Count;

            var client = new FakeChatClient();
            client.EnqueueTurn(new StreamEvent.TextDelta("half"), new StreamEvent.Truncated(false));   //a truncated turn makes the summary null.
            var obs = new RecordingObserver();

            var newConvo = await Gatto.Repl.Repl.CompactAsync(
                compactor: new Compactor(client, "m"),
                convo: convo, sessions: sessions, recomposeSystem: () => Composed.Text("FRESH"), observer: obs, ct: default);

            Assert.Null(newConvo);                                        //the null tells the caller to abort and keep the current session.
            Assert.Contains(obs.Warnings, w => w.Contains("compact failed"));
            Assert.Equal(countBefore, convo.Count);
            Assert.Equal(oldPath, sessions.CurrentPath);                  //the path still names the old file, StartNew never ran
            Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
            Assert.Single(Directory.GetFiles(Path.Combine(home, "sessions"), "*.jsonl"));
        }
        finally { Directory.Delete(home, true); Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task CompactAsync_NullSessions_StillComposesNewConversation()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("S"), new StreamEvent.Finished("stop", null));
        var convo = new Conversation("sys");
        convo.AddUser("q");
        convo.AddAssistant("a");

        var newConvo = await Gatto.Repl.Repl.CompactAsync(
            new Compactor(client, "m"), convo, sessions: null, () => Composed.Text("FRESH"), new RecordingObserver(), default);

        Assert.NotNull(newConvo);
        Assert.Contains("S", newConvo!.Messages[0].Content!);
    }

    private static ChatMessage Sys(string t) => new("system", t);
    private static ChatMessage User(string t) => new("user", t);
    private static ChatMessage Asst(string t, params ToolCall[] calls) =>
        new("assistant", t, calls.Length > 0 ? calls : null);
    private static ChatMessage Tool(string id, string t) => new("tool", t, ToolCallId: id);
    private static ToolCall Call(string id) => new(id, "read_file", "{}");

    [Fact]
    public void Shape_NoOp_WhenUnderFitsThreshold()
    {
        var msgs = new[] { Sys("s"), User("hello") };                       //a conversation that easily fits the window.
        var shaped = Compactor.ShapeForSummary(msgs, windowTokens: 65536, ratio: 1.0);
        Assert.Same(msgs, shaped);                                          //the same reference returns, so nothing is reallocated.
    }

    [Fact]
    public void Shape_ElidesCurrentTurnToolResults_NoProtectedRegion()
    {
        //results from the current turn are elided too, there is no protected region
        var big = new string('x', 40_000);
        var msgs = new[] { Sys("s"), User("go"),
            Asst("", Call("a")), Tool("a", big),
            Asst("", Call("b")), Tool("b", big) };
        var shaped = Compactor.ShapeForSummary(msgs, windowTokens: 10_000, ratio: 1.0);
        Assert.All(shaped.Where(m => m.Role == "tool"),
            m => Assert.Contains("[elided:", m.Content));
    }

    [Fact]
    public void Shape_ResultUnderTarget_On106PercentInput()
    {
        var big = new string('x', 30_000);
        var msgs = Enumerable.Range(0, 10)
            .SelectMany(i => new[] { Asst("", Call($"c{i}")), Tool($"c{i}", big) })
            .Prepend(User("go")).Prepend(Sys("s")).ToArray();               //the input is about 75k tokens against a 65k window
        var shaped = Compactor.ShapeForSummary(msgs, 65_536, 1.0);
        Assert.True(ContextBudget.Estimate(shaped) <= 65_536 * Compactor.TargetFraction);
    }

    [Fact]
    public void SuppressionAddendum_WrapsIndexText_AndIsNullWhenThereIsNothingToFeed()
    {
        Assert.Null(Compactor.SuppressionAddendum(new MemoryIndex.LoadResult(null, 0)));
        //an empty index means no whole line fit the cap, the addendum would be about nothing
        Assert.Null(Compactor.SuppressionAddendum(new MemoryIndex.LoadResult("", 3)));

        var a = Compactor.SuppressionAddendum(new MemoryIndex.LoadResult("- nginx binds 127.0.0.1", 0));
        Assert.NotNull(a);
        Assert.Contains("already saved in project memory", a);
        Assert.Contains("- nginx binds 127.0.0.1", a);
        Assert.Contains(MemoryPiggyback.Heading, a);   //the section is named through the constant, so it cannot drift.
    }

    [Fact]
    public async Task Summarize_AppendsDoNotRepeat_AfterTheTemplate()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, _) =>
            new StreamEvent[] { new StreamEvent.TextDelta("summary"), new StreamEvent.Finished("stop", null) });
        var addendum = Compactor.SuppressionAddendum(new MemoryIndex.LoadResult("- fact one", 0))!;

        await new Compactor(client, "m").SummarizeAsync(
            convo, new NullObserver(), CancellationToken.None, doNotRepeat: addendum);

        var prompt = client.Requests[0].Messages[^1].Content!;
        Assert.StartsWith(Compactor.TemplatePromptForTests, prompt, StringComparison.Ordinal);  //the template stays unchanged at the prompt's start.
        Assert.EndsWith(addendum, prompt, StringComparison.Ordinal);                            //and the addendum comes last.
    }

    [Fact]
    public void Shape_CountsTheRealPromptLength_NotJustTheTemplate()
    {
        //a template-only count under-fires shaping and the request overflows onto the harsh retry
        const int window = 8_000;
        var msgTokens = (int)(window * Compactor.FitsFraction) - Compactor.TemplatePromptLengthForTests / 4 - 10;
        var msgs = new[] { Sys("s"), User(new string('x', msgTokens * 4)) };

        Assert.Same(msgs, Compactor.ShapeForSummary(msgs, window, ratio: 1.0,
            promptChars: Compactor.TemplatePromptLengthForTests));
        Assert.NotSame(msgs, Compactor.ShapeForSummary(msgs, window, ratio: 1.0,
            promptChars: Compactor.TemplatePromptLengthForTests + 4_000));
    }

    [Fact]
    public void HarshShape_CountsTheRealPromptLength_NotJustTheTemplate()
    {
        //the retry composes the same prompt, a template-only measure overflows here too
        const int window = 8_000;
        var perMsgTokens = (int)(window * Compactor.HarshFraction) / 2;
        var msgs = new[] { Sys("s"), User(new string('x', perMsgTokens * 4)), User("keep me") };

        var atTemplate = Compactor.HarshShape(msgs, window, ratio: 1.0,
            promptChars: Compactor.TemplatePromptLengthForTests);
        var withAddendum = Compactor.HarshShape(msgs, window, ratio: 1.0,
            promptChars: Compactor.TemplatePromptLengthForTests + 20_000);
        Assert.True(withAddendum.Count <= atTemplate.Count);   //a longer prompt can only tighten the kept suffix.
        Assert.True(withAddendum.Count < atTemplate.Count);    //at this size the tightening must actually happen.
    }

    [Fact]
    public void Shape_RatioScalesTheMeasure()
    {
        //the estimate fits at ratio 1.0 and needs shaping at 2.0.
        var msgs = new[] { Sys("s"), User("go"), Asst("", Call("a")), Tool("a", new string('x', 120_000)) };
        Assert.Same(msgs, Compactor.ShapeForSummary(msgs, 65_536, ratio: 1.0));
        Assert.NotSame(msgs, Compactor.ShapeForSummary(msgs, 65_536, ratio: 2.0));
    }

    [Fact]
    public void Shape_DropsWholeMessages_OnlyAfterAllToolResultsElided_AdjacencyKept()
    {
        //when elision alone is not enough, the oldest whole messages drop. a tool call and its results always drop together
        var wall = new string('x', 60_000);
        var msgs = new[] { Sys("s"),
            User(wall), Asst(wall), User(wall), Asst("", Call("a")), Tool("a", wall),
            User("latest question") };
        var shaped = Compactor.ShapeForSummary(msgs, 20_000, 1.0);
        Assert.True(ContextBudget.Estimate(shaped) <= 20_000 * Compactor.TargetFraction);
        AssertAdjacency(shaped);
        Assert.Equal("system", shaped[0].Role);
    }

    [Fact]
    public void Shape_PreservesToolCallNameAndPath_WhenShrinkingWriteFileArgs()
    {
        //a write_file body sits in the tool-call arguments, where result elision never looks. the shrink pass stubs the body and keeps the name and path
        var writeArgs = "{\"path\":\"foo.ps1\",\"content\":\"" + new string('x', 6000) + "\"}";
        var msgs = new[]
        {
            Sys("s"),
            User("create a script"),
            Asst("", new ToolCall("w", "write_file", writeArgs)),
            Tool("w", "wrote foo.ps1"),
            User("latest question"),
        };
        var shaped = Compactor.ShapeForSummary(msgs, windowTokens: 2_000, ratio: 1.0);

        var writeCall = shaped.SelectMany(m => m.ToolCalls ?? Array.Empty<ToolCall>())
            .FirstOrDefault(c => c.Name == "write_file");
        Assert.NotNull(writeCall);                              //the tool call itself survives.
        Assert.Contains("foo.ps1", writeCall!.ArgumentsJson);    //the file path remains.
        Assert.Contains("[elided:", writeCall.ArgumentsJson);    //the body is replaced by a stub
        Assert.True(ContextBudget.Estimate(shaped) <= 2_000 * Compactor.TargetFraction);
        AssertAdjacency(shaped);
    }

    //the cut backs off one char when a surrogate pair straddles it (a lone high surrogate breaks the serialized request)
    [Fact]
    public void Shape_HeadTruncate_NeverSplitsASurrogatePair()
    {
        //a json string is not an object, ShrinkArgs head-truncates it. the emoji pair straddles the 200-char cut exactly
        var args = "\"" + new string('a', 198) + "🐱" + new string('b', 6000) + "\"";
        var msgs = new[]
        {
            Sys("s"),
            User("run it"),
            Asst("", new ToolCall("c", "custom_tool", args)),
            Tool("c", "ok"),
            User("latest question"),
        };
        var shaped = Compactor.ShapeForSummary(msgs, windowTokens: 2_000, ratio: 1.0);

        var call = shaped.SelectMany(m => m.ToolCalls ?? Array.Empty<ToolCall>())
            .FirstOrDefault(c => c.Name == "custom_tool");
        Assert.NotNull(call);
        Assert.All(call!.ArgumentsJson, ch => Assert.False(char.IsSurrogate(ch)));
    }

    [Fact]
    public void HarshShape_KeepsSystemAndLastUser_Under25Percent()
    {
        var wall = new string('x', 40_000);
        var msgs = new[] { Sys("s"), User(wall), Asst(wall), User("the actual task"),
            Asst("", Call("a")), Tool("a", wall) };
        var shaped = Compactor.HarshShape(msgs, 20_000, 1.0);
        Assert.True(ContextBudget.Estimate(shaped) <= 20_000 * Compactor.HarshFraction
            + 64); //the 64 slack covers the elision stubs that shaping adds.
        Assert.Equal("system", shaped[0].Role);
        Assert.Contains(shaped, m => m.Role == "user" && m.Content == "the actual task");
        Assert.NotEqual("tool", shaped.SkipWhile(m => m.Role == "system").First().Role);
        AssertAdjacency(shaped);
    }

    [Fact]
    public void HarshShape_NoSystemMessage_SingleNonUserMessage_NotEmpty()
    {
        //index 0 must stay reachable when no system message exists, or a solo non-user message shapes to empty.
        var msgs = new ChatMessage[] { new("assistant", "solo reply") };
        var shaped = Compactor.HarshShape(msgs, 20_000, 1.0);
        Assert.NotEmpty(shaped);
        Assert.Equal("solo reply", shaped[0].Content);
    }

    [Fact]
    public void HarshShape_AsymmetricBoundary_DropsAssistantAndToolTogether()
    {
        //the tool result fits alone but its assistant is oversized, and the pair drops together
        var toolSmall = new string('x', 4_000);
        var assistantHuge = new string('x', 60_000);
        var msgs = new[] { Sys("s"), User("the actual task"), Asst(assistantHuge, Call("a")), Tool("a", toolSmall) };
        var shaped = Compactor.HarshShape(msgs, 20_000, 1.0);
        AssertAdjacency(shaped);
        Assert.DoesNotContain(shaped, m => m.Role == "tool");
        Assert.DoesNotContain(shaped, m => m.ToolCalls is not null);
        Assert.Contains(shaped, m => m.Role == "user" && m.Content == "the actual task");
    }

    [Fact]
    public void HarshShape_RatioScalesTheMeasure()
    {
        //the same messages survive at ratio 1.0 and need trimming at ratio 2.0
        var msgs = new[] { Sys("s"), User("go"), Asst("", Call("a")), Tool("a", new string('x', 15_000)) };
        var full = Compactor.HarshShape(msgs, 20_000, ratio: 1.0);
        var shapedHigh = Compactor.HarshShape(msgs, 20_000, ratio: 2.0);
        Assert.Equal(msgs.Length, full.Count);
        Assert.True(shapedHigh.Count < msgs.Length);
    }

    [Fact]
    public void HarshShape_NonPositiveWindow_ReturnsSystemAndLastUserOnly()
    {
        var msgs = new[] { Sys("s"), User("u1"), Asst("a1"), User("last user") };
        var shaped = Compactor.HarshShape(msgs, windowTokens: 0, ratio: 1.0);
        Assert.Equal(2, shaped.Count);
        Assert.Equal("system", shaped[0].Role);
        Assert.Equal("user", shaped[1].Role);
        Assert.Equal("last user", shaped[1].Content);
    }

    //each surviving tool result needs its tool_calls message earlier, and each tool_calls needs every id answered later
    private static void AssertAdjacency(IReadOnlyList<ChatMessage> msgs)
    {
        for (var i = 0; i < msgs.Count; i++)
        {
            if (msgs[i].Role == "tool")
            {
                Assert.True(i > 0, "tool result at index 0");
                var prev = msgs.Take(i).LastOrDefault(m => m.ToolCalls is not null);
                Assert.NotNull(prev);
                Assert.Contains(prev!.ToolCalls!, c => c.Id == msgs[i].ToolCallId);
            }
            if (msgs[i].ToolCalls is { } calls)
            {
                var later = msgs.Skip(i + 1).ToList();
                foreach (var c in calls)
                    Assert.Contains(later, m => m.Role == "tool" && m.ToolCallId == c.Id);
            }
        }
    }

    [Fact]
    public async Task Summary_is_stripped_before_it_enters_the_prefix()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"<|tool_calls_section_begin|>## Summary\\nfacts\"}"),
            Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var observer = new RecordingObserver();

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), observer, default);

        Assert.Equal("## Summary\nfacts", summary);
        Assert.Contains(observer.Warnings, w => w.Contains("compaction summary"));
    }

    [Fact]
    public async Task All_pollution_summary_aborts_via_the_existing_null_path()
    {
        //a summary of only delimiters is not a summary. it takes the existing null abort, loop and Repl state stay untouched
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"<|tool_calls_section_end|>\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        Assert.Null(summary);
    }

}

//records each delta by observer channel, the summary text streams on the reasoning channel
public sealed class ReasoningRecorder : ITurnObserver
{
    public string Reasoning = "";
    public string Text = "";
    public void OnTextDelta(string t) => Text += t;
    public void OnReasoningDelta(string t) => Reasoning += t;
    public void OnToolCallStart(ToolCall c) { }
    public void OnToolResult(ToolCall c, Gatto.Core.Tools.ToolResult r) { }
    public void OnWarning(string m) { }
    public void OnUsage(Usage u) { }
}
