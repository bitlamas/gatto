using System.Linq;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class CompactorTests
{
    //an sse chunk in the exact shape that llama.cpp emits.
    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private const string Summary = "Task / goal: go.\n\nImmediate next step: go on.";
    private const string TextCall = "The file was truncated. Let me get the rest before writing the summary.\n\n<tool_call>\n<function=read_file>\n<parameter=path>\na.txt\n</parameter>\n</function>\n</tool_call>";

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
            Chunk("{\"content\":\"Immediate next step: SUMMARY TEXT\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var compactor = new Compactor(client, "m");

        var summary = await compactor.SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        Assert.Equal("Immediate next step: SUMMARY TEXT", summary);
        //the request must hold no tools key, a summarizer never calls tools
        Assert.False(server.LastRequestBody!.Value.TryGetProperty("tools", out _));
        //the request ends with the fixed template prompt as the final user message.
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
    }

    //the summary request starts as the loop's last request did, the same tools, overrides and shaped messages, so the server reuses its cache and reads only the new tail
    [Fact]
    public async Task The_summary_request_starts_with_the_loops_request_and_forbids_tool_calls()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"hi there\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"Immediate next step: SUMMARY\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        var body = JsonDocument.Parse("""{"chat_template_kwargs":{"enable_thinking":true}}""").RootElement.Clone();
        var loop = new AgentLoop(client, tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m",
            bodyOverrides: body, reasoningHistory: ReasoningHistory.None);
        var convo = new Conversation("sys");
        await loop.RunTurnAsync(convo, "hello", new RecordingObserver(), default);

        var summary = await new Compactor(client, "m", loop.RequestShape).SummarizeAsync(convo, new RecordingObserver(), default);

        Assert.Equal("Immediate next step: SUMMARY", summary);
        var turn = server.RequestBodies[0];
        var summarize = server.RequestBodies[1];
        Assert.Equal(turn.GetProperty("tools").GetRawText(), summarize.GetProperty("tools").GetRawText());
        Assert.Equal("none", summarize.GetProperty("tool_choice").GetString());
        Assert.Equal(turn.GetProperty("chat_template_kwargs").GetRawText(), summarize.GetProperty("chat_template_kwargs").GetRawText());
        var turnMessages = turn.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        var summaryMessages = summarize.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        Assert.Equal(turnMessages, summaryMessages.Take(turnMessages.Count));
    }

    //the loop's request as sent carries the role's thinking suffix on the turn's user message, so the summary request does too, or it diverges there
    [Fact]
    public async Task The_summary_request_keeps_the_thinking_suffix_the_loop_sent()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"hi there\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"Immediate next step: SUMMARY\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m",
            promptSuffix: "/think");
        var convo = new Conversation("sys");
        await loop.RunTurnAsync(convo, "hello", new RecordingObserver(), default);

        var summary = await new Compactor(client, "m", loop.RequestShape).SummarizeAsync(
            convo, new RecordingObserver(), default, windowTokens: 65_536);

        Assert.Equal("Immediate next step: SUMMARY", summary);
        var turnMessages = server.RequestBodies[0].GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        var summaryMessages = server.RequestBodies[1].GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        Assert.Contains("/think", turnMessages[^1]);
        Assert.Equal(turnMessages, summaryMessages.Take(turnMessages.Count));
        Assert.Contains("hi there", summaryMessages[turnMessages.Count]);
    }

    //an override that limits output would cut the summary, and it never touches the prompt, so the summary request drops it and keeps the rest
    [Fact]
    public async Task The_summary_request_drops_overrides_that_limit_output()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"Immediate next step: SUMMARY\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var body = JsonDocument.Parse("""
            {"max_tokens":50,"max_completion_tokens":50,"n_predict":50,"grammar":"root ::= \"x\"","json_schema":{},"response_format":{"type":"json_object"},"chat_template_kwargs":{"enable_thinking":true}}
            """).RootElement.Clone();
        var shape = new RequestShape(Array.Empty<ToolSpec>(), null, body, ReasoningHistory.All);

        await new Compactor(client, "m", shape).SummarizeAsync(SmallConvo(), new RecordingObserver(), default);

        var sent = server.RequestBodies[0];
        foreach (var key in new[] { "max_tokens", "max_completion_tokens", "n_predict", "grammar", "json_schema", "response_format" })
            Assert.False(sent.TryGetProperty(key, out _), key);
        Assert.True(sent.TryGetProperty("chat_template_kwargs", out _));
    }

    //a summary cut at the output limit beside the whole conversation is asked again from the shortened copy
    [Fact]
    public async Task A_summary_cut_beside_the_whole_conversation_falls_back_to_the_shortened_copy()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var sent = convo.Messages.ToArray();
        var shape = new RequestShape(Array.Empty<ToolSpec>(), null, null, ReasoningHistory.All, sent, sent);
        var client = new ScriptedClient((_, n) => n == 1
            ? new StreamEvent[] { new StreamEvent.TextDelta("half"), new StreamEvent.Finished("length", null) }
            : new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });
        var observer = new RecordingObserver();

        var summary = await new Compactor(client, "m", shape).SummarizeAsync(convo, observer, CancellationToken.None, windowTokens: 65_536);

        Assert.Equal(Summary, summary);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(observer.Warnings, w => w.Contains("shortened copy"));
    }

    //a reply with none of the headings is not a summary, so the shortened copy is asked again with no tools listed
    [Fact]
    public async Task A_reply_that_is_no_summary_beside_the_whole_conversation_is_asked_again_from_a_tool_free_shortened_copy()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var sent = convo.Messages.ToArray();
        var tools = new[] { new ToolSpec("read_file", "reads", JsonDocument.Parse("{\"type\":\"object\"}").RootElement) };
        var shape = new RequestShape(tools, null, null, ReasoningHistory.All, sent, sent);
        var client = new ScriptedClient((_, n) => n == 1
            ? new StreamEvent[] { new StreamEvent.TextDelta(TextCall), new StreamEvent.Finished("stop", null) }
            : new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });
        var observer = new RecordingObserver();

        var summary = await new Compactor(client, "m", shape).SummarizeAsync(convo, observer, CancellationToken.None, windowTokens: 65_536);

        Assert.Equal(Summary, summary);
        Assert.Equal(2, client.Requests.Count);
        Assert.Same(tools, client.Requests[0].Tools);
        Assert.Equal("none", client.Requests[0].ToolChoice);
        Assert.Null(client.Requests[1].Tools);
        Assert.Null(client.Requests[1].ToolChoice);
        Assert.Contains(observer.Warnings, w => w.Contains("shortened copy"));
    }

    //a call written as text on the shortened copy fails the compaction with its reason, which turns auto-compaction off for the session
    [Fact]
    public async Task A_reply_that_is_no_summary_on_the_shortened_copy_fails_and_names_why()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta(TextCall), new StreamEvent.Finished("stop", null) });
        var compactor = new Compactor(client, "m");

        Assert.Null(await compactor.SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536));
        Assert.Single(client.Requests);
        Assert.Equal("the reply holds none of the summary's headings, so it is not a summary", compactor.LastFailure);
    }

    //a heading is matched in any case and inside markdown, and the template lets a summary skip any heading, the next step included
    [Fact]
    public async Task A_summary_with_any_of_its_headings_in_another_case_or_in_bold_is_kept()
    {
        var noNextStep = "**Task / goal:** read the sessions.\n\n**Current state:** 7 read.\n\n**Memory candidates:**\n- none";
        foreach (var text in new[] { "Task: x.\n\n**Immediate Next Step:** y.", "## immediate next step\ny.", noNextStep, "## CURRENT STATE\nidle." })
        {
            var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null) });
            Assert.Equal(text, await new Compactor(client, "m").SummarizeAsync(SmallConvo(), new NullObserver(), CancellationToken.None));
        }
    }

    //the headings the check looks for are the template's own, so a reworded template cannot leave the check behind
    [Fact]
    public void Every_heading_the_check_looks_for_is_in_the_template()
    {
        foreach (var heading in Compactor.Headings)
            Assert.Contains(heading + ":", Compactor.TemplatePromptForTests, StringComparison.Ordinal);
    }

    //with too little room for the summary beside the conversation it goes to the shortened copy at once
    [Fact]
    public async Task Too_little_room_for_the_summary_goes_to_the_shortened_copy_at_once()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        for (var i = 0; i < 10; i++) { convo.AddAssistant("", new[] { Call($"c{i}") }); convo.AddToolResult($"c{i}", new Gatto.Core.Tools.ToolResult(new string('x', 25_800))); }
        var sent = convo.Messages.ToArray();
        var tools = new[] { new ToolSpec("read_file", "reads", JsonDocument.Parse("{\"type\":\"object\"}").RootElement) };
        var shape = new RequestShape(tools, null, null, ReasoningHistory.All, sent, sent);
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });

        var summary = await new Compactor(client, "m", shape).SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536);

        var used = ContextBudget.Estimate(convo.Messages);
        Assert.True(used + Compactor.SummaryRoomTokens > 65_536, $"fixture at {used} tokens");
        Assert.Equal(Summary, summary);
        Assert.Single(client.Requests);
        Assert.Null(client.Requests[0].Tools);
        Assert.True(ContextBudget.Estimate(client.Requests[0].Messages) <= 65_536 * Compactor.TargetFraction
            + Compactor.TemplatePromptLengthForTests / 4);
    }

    //a conversation at 91 percent with room for the summary is sent whole, no tool result stubbed
    [Fact]
    public async Task A_conversation_past_85_percent_with_room_is_sent_whole()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        for (var i = 0; i < 10; i++) { convo.AddAssistant("", new[] { Call($"c{i}") }); convo.AddToolResult($"c{i}", new Gatto.Core.Tools.ToolResult(new string('x', 23_000))); }
        var sent = convo.Messages.ToArray();
        var shape = new RequestShape(Array.Empty<ToolSpec>(), null, null, ReasoningHistory.All, sent, sent);
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });

        await new Compactor(client, "m", shape).SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536);

        var used = ContextBudget.Estimate(convo.Messages);
        Assert.True(used > 65_536 * Compactor.FitsFraction, $"fixture at {used} tokens");
        Assert.Equal(sent, client.Requests[0].Messages.Take(sent.Length));
    }

    [Fact]
    public async Task SummarizeAsync_SurfacesDeltasOnReasoningChannel()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"Immediate next step: abc\"}"), Chunk("{\"content\":\"def\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var obs = new ReasoningRecorder();

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), obs, default);

        Assert.Equal("Immediate next step: abcdef", summary);
        Assert.Equal("Immediate next step: abcdef", obs.Reasoning);   //deltas are rendered dimmed through the reasoning channel.
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
        var client = new ScriptedClient((_, _) => new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });
        var summary = await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536);
        Assert.Equal(Summary, summary);
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
            : new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });
        var summary = await new Compactor(client, "m").SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536);
        Assert.Equal(Summary, summary);
        Assert.Equal(2, client.Requests.Count);
    }

    //a summary that comes back null says why, so the line that turns auto-compaction off can name the reason
    [Fact]
    public async Task A_failed_summary_names_its_reason()
    {
        var convo = new Conversation("s");
        convo.AddUser("go");
        async Task<string?> ReasonFor(Func<ChatRequest, int, IEnumerable<StreamEvent>> script)
        {
            var compactor = new Compactor(new ScriptedClient(script), "m");
            Assert.Null(await compactor.SummarizeAsync(convo, new NullObserver(), CancellationToken.None, windowTokens: 65_536));
            return compactor.LastFailure;
        }

        Assert.Equal("the summary was cut at the output limit", await ReasonFor((_, _) =>
            new StreamEvent[] { new StreamEvent.TextDelta("half"), new StreamEvent.Finished("length", null) }));
        Assert.Equal("the summary came back empty", await ReasonFor((_, _) =>
            new StreamEvent[] { new StreamEvent.Finished("stop", null) }));
        Assert.Equal("the summary request did not fit the context window", await ReasonFor((_, _) =>
            throw new GattoContextOverflowException("over", null, null)));
        Assert.StartsWith("the server dropped the request: ", await ReasonFor((_, _) =>
            throw new GattoConnectionException("connection refused")));
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
            client.EnqueueTurn(new StreamEvent.TextDelta("SESSION SUMMARY\n\nImmediate next step: go on."), new StreamEvent.Finished("stop", null));
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
        client.EnqueueTurn(new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null));
        var convo = new Conversation("sys");
        convo.AddUser("q");
        convo.AddAssistant("a");

        var newConvo = await Gatto.Repl.Repl.CompactAsync(
            new Compactor(client, "m"), convo, sessions: null, () => Composed.Text("FRESH"), new RecordingObserver(), default);

        Assert.NotNull(newConvo);
        Assert.Contains(Summary, newConvo!.Messages[0].Content!);
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

    //the summarizer sees the head of every result it cannot fit, so the first line of a file can reach the summary
    [Fact]
    public void Shape_keeps_the_head_of_each_result_it_cuts()
    {
        var body = (string marker) => marker + "\n" + string.Join("\n", Enumerable.Range(0, 2_000).Select(i => $"filler line {i}"));
        var msgs = new[] { Sys("s"), User("go"),
            Asst("", Call("a")), Tool("a", body("ALPHA")),
            Asst("", Call("b")), Tool("b", body("BRAVO")) };
        var shaped = Compactor.ShapeForSummary(msgs, windowTokens: 4_000, ratio: 1.0);
        var results = shaped.Where(m => m.Role == "tool").Select(m => m.Content!).ToList();

        Assert.Equal(2, results.Count);
        Assert.StartsWith("ALPHA\nfiller line 0", results[0]);
        Assert.StartsWith("BRAVO\nfiller line 0", results[1]);
        Assert.All(results, r => Assert.Contains("[elided: tool result, ", r));
    }

    [Fact]
    public void Shape_ResultUnderTarget_On114PercentInput()
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
            new StreamEvent[] { new StreamEvent.TextDelta(Summary), new StreamEvent.Finished("stop", null) });
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
            Chunk("{\"content\":\"<|tool_calls_section_begin|>## Summary\\nfacts\\nImmediate next step: go.\"}"),
            Chunk("{}", "stop"), "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var observer = new RecordingObserver();

        var summary = await new Compactor(client, "m").SummarizeAsync(SmallConvo(), observer, default);

        Assert.Equal("## Summary\nfacts\nImmediate next step: go.", summary);
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
