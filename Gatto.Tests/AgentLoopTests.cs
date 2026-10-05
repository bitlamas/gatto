using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public sealed class RecordingObserver : ITurnObserver
{
    public string Text = "";
    public List<(ToolCall, ToolResult)> Results = new();
    public List<string> Warnings = new();
    public List<Usage> Usages = new();
    public List<string> Events = new();
    public void OnTextDelta(string t) => Text += t;
    public void OnReasoningDelta(string t) { }
    //must match ITurnObserver.OnToolCallDelta exactly, otherwise the interface's default body runs and nothing is recorded
    public void OnToolCallDelta(string? name = null, string? partialArguments = null) =>
        Events.Add("tool_call_delta" + (name is null ? "" : ":" + name));
    //must match ITurnObserver.OnPromptProgress exactly, otherwise the interface's default body runs and nothing is recorded
    public void OnPromptProgress(long total, long processed) => Events.Add($"prompt_progress:{processed}/{total}");
    public void OnToolCallStart(ToolCall c) { }
    public void OnToolResult(ToolCall c, ToolResult r) => Results.Add((c, r));
    public void OnWarning(string m) => Warnings.Add(m);
    public void OnUsage(Usage u) => Usages.Add(u);
}

public sealed class EchoTool : ITool
{
    public string Name => "echo";
    public string Description => "echoes";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult("echo: " + args.GetProperty("msg").GetString()));
}

public sealed class ThrowingTool : ITool
{
    public string Name => "bomb";
    public string Description => "throws";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("kaboom");
}

public sealed class ProbeTool(Action onExecute) : ITool
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

public sealed class CancellingTool(CancellationTokenSource cts) : ITool
{
    public string Name => "cancelling";
    public string Description => "cancels the turn mid-execution";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        cts.Cancel();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolResult("unreachable"));
    }
}

public sealed class ThrowingOceTool : ITool
{
    public string Name => "oce_bomb";
    public string Description => "throws OperationCanceledException without the turn's ct being cancelled";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => throw new OperationCanceledException("tool-internal cancel, not the user's");
}

//a tool result holding ChatML control tokens (the shape read_file gives for a Jinja template) must survive the strip verbatim
public sealed class TemplateEchoTool : ITool
{
    public string Name => "template_echo";
    public string Description => "returns a chat template fragment";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult("<|im_start|>user<|im_end|>"));
}

public class AgentLoopTests
{
    private static (AgentLoop, FakeChatClient, HookBus, Conversation, RecordingObserver) Setup(params ITool[] tools)
    {
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        foreach (var t in tools) reg.Register(t);
        var hooks = new HookBus();
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(Path.GetTempPath()), "m");
        return (loop, client, hooks, new Conversation(null), new RecordingObserver());
    }

    [Fact]
    public async Task Plain_text_turn_completes()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("hi"), new StreamEvent.Finished("stop", null));
        var outcome = await loop.RunTurnAsync(convo, "hello", obs, default);
        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        Assert.Equal("hi", obs.Text);
        Assert.Equal("assistant", convo.Messages[^1].Role);
    }

    [Fact]
    public async Task A_prompt_progress_event_reaches_the_observer()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.PromptProgress(15063, 4138),
            new StreamEvent.ToolCallDelta("probe"),
            new StreamEvent.TextDelta("hi"),
            new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "hello", obs, default);

        Assert.Equal(["prompt_progress:4138/15063", "tool_call_delta:probe"], obs.Events);
    }

    [Fact]
    public async Task Assistant_message_captures_reasoning_content()
    {
        //streamed reasoning_content must accumulate onto the assistant message (history resends it, and the model stops reasoning without it)
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.ReasoningDelta("let me think. "),
            new StreamEvent.ReasoningDelta("2+2=4."),
            new StreamEvent.TextDelta("The answer is 4."),
            new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "what is 2+2?", obs, default);

        var asst = convo.Messages.Single(m => m.Role == "assistant");
        Assert.Equal("The answer is 4.", asst.Content);
        Assert.Equal("let me think. 2+2=4.", asst.ReasoningContent);
    }

    [Fact]
    public async Task Empty_turn_records_no_assistant_message_warns_and_completes()
    {
        //a turn with no text and no tool calls records no assistant message (an empty record makes llama.cpp's chat template reject the replay)
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        var outcome = await loop.RunTurnAsync(convo, "hello", obs, default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        Assert.DoesNotContain(convo.Messages, m => m.Role == "assistant");
        Assert.Contains(obs.Warnings, w => w.Contains("empty turn"));
    }

    [Fact]
    public async Task PromptSuffix_AppendedToRequestCopy_ButConvoAndJsonlStayPure()
    {
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        var loop = new AgentLoop(client, reg, new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", promptSuffix: "/no_think");
        client.EnqueueTurn(new StreamEvent.TextDelta("hi"), new StreamEvent.Finished("stop", null));

        var convo = new Conversation(null);
        var outcome = await loop.RunTurnAsync(convo, "hello", new RecordingObserver(), default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        //the suffix goes on the request copy, one space after the text
        var sentUser = client.Requests[0].Messages.Single(m => m.Role == "user");
        Assert.Equal("hello /no_think", sentUser.Content);
        //the stored Conversation and the JSONL keep the message without the suffix
        var storedUser = convo.Messages.Single(m => m.Role == "user");
        Assert.Equal("hello", storedUser.Content);
    }

    [Fact]
    public async Task PromptSuffix_Null_LeavesUserMessageUnchanged()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("hi"), new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "hello", obs, default);

        Assert.Equal("hello", client.Requests[0].Messages.Single(m => m.Role == "user").Content);
    }

    [Fact]
    public async Task UpdateOverrides_ChangesModel_OnTheNextTurn_NotTheOneInFlight()
    {
        //the /role command swaps overrides through this mutator, the loop is never rebuilt mid-session (a rebuild would drop the ToolRegistry, HookBus and IToolContext wiring)
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("first"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "hello", obs, default);
        Assert.Equal("m", client.Requests[0].Model);

        loop.UpdateOverrides("m2", null, null);

        client.EnqueueTurn(new StreamEvent.TextDelta("second"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "hello again", obs, default);
        Assert.Equal("m2", client.Requests[1].Model);
    }

    [Fact]
    public async Task UpdateOverrides_ArmsPromptSuffix_OnTheNextTurn()
    {
        //the /role command sets the model, sampling and thinking body through UpdateOverrides, and the prompt suffix goes through the same mutator
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("first"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "hello", obs, default);
        Assert.Equal("hello", client.Requests[0].Messages.Single(m => m.Role == "user").Content);

        loop.UpdateOverrides("m", null, null, "/think");

        client.EnqueueTurn(new StreamEvent.TextDelta("second"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "hello again", obs, default);
        var sentUser = client.Requests[1].Messages.Where(m => m.Role == "user").Last();
        Assert.Equal("hello again /think", sentUser.Content);
    }

    [Fact]
    public async Task Tool_call_executes_and_loop_continues_until_no_calls()
    {
        var (loop, client, _, convo, obs) = Setup(new EchoTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

        var outcome = await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        Assert.Equal("echo: yo", obs.Results.Single().Item2.Text);
        Assert.Equal(2, client.Requests.Count);
        //the second request to the server must include the tool result message.
        Assert.Contains(client.Requests[1].Messages, m => m.Role == "tool" && m.Content == "echo: yo");
    }

    [Fact]
    public async Task ToolCallDelta_ReachesObserver()
    {
        var (loop, client, _, convo, obs) = Setup(new EchoTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallDelta(),
            new StreamEvent.ToolCallDelta("echo", "{\"msg\":"),
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Contains("tool_call_delta", obs.Events);          //a delta with no name must still reach the observer.
        Assert.Contains("tool_call_delta:echo", obs.Events);      //the event must include the tool name once the model streams it.
    }

    [Fact]
    public async Task Tool_throw_becomes_error_result_with_message_only()
    {
        var (loop, client, _, convo, obs) = Setup(new ThrowingTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "bomb", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal("kaboom", result.Text);      //the error text must be the exception message only, with no stack trace.
    }

    [Fact]
    public async Task Unknown_tool_and_malformed_args_become_error_results()
    {
        var (loop, client, _, convo, obs) = Setup(new EchoTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "ghost", "{}")),
            new StreamEvent.ToolCallReady(new ToolCall("c2", "echo", "{not json")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Contains("unknown tool: ghost", obs.Results[0].Item2.Text);
        Assert.True(obs.Results[0].Item2.IsError);
        Assert.Contains("malformed tool arguments", obs.Results[1].Item2.Text);
        Assert.True(obs.Results[1].Item2.IsError);
    }

    [Fact]
    public async Task Blocked_tool_call_is_not_executed()
    {
        var executed = false;
        var (loop, client, hooks, convo, obs) = Setup(new ProbeTool(() => executed = true));
        hooks.On(HookEvent.ToolCall, _ => throw new InvalidOperationException("gate refuses"));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Contains("blocked: gate refuses", result.Text);
        Assert.False(executed);   //when the gate refuses, the tool must not execute.
    }

    [Fact]
    public async Task Cancel_during_tool_execution_balances_transcript_and_survives()
    {
        using var cts = new CancellationTokenSource();
        var (loop, client, _, convo, obs) = Setup(new CancellingTool(cts), new EchoTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "cancelling", "{}")),
            new StreamEvent.ToolCallReady(new ToolCall("c2", "echo", "{\"msg\":\"never\"}")),
            new StreamEvent.Finished("tool_calls", null));

        var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

        Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);          //a cancellation becomes a Cancelled outcome and the exception never escapes the loop
        var toolMsgs = convo.Messages.Where(m => m.Role == "tool").ToList();
        Assert.Equal(2, toolMsgs.Count);                       //every tool call id needs an answer message even after cancellation.
        Assert.All(toolMsgs, m => Assert.Contains("cancelled", m.Content));
    }

    [Fact]
    public async Task Cancel_during_stream_reports_turn_cancelled_not_truncated()
    {
        //a cancel mid-stream warns turn cancelled
        using var cts = new CancellationTokenSource();
        var (loop, client, _, convo, obs) = Setup(new EchoTool());
        client.EnqueueTurn(new StreamEvent.TextDelta("part"), new StreamEvent.Finished("stop", null));
        cts.Cancel();

        var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

        Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);
        Assert.Contains(obs.Warnings, w => w.Contains("turn cancelled"));
        Assert.DoesNotContain(obs.Warnings, w => w.Contains("turn truncated"));
    }

    [Fact]
    public async Task Truncated_stream_discards_pending_calls_and_marks_text()
    {
        var (loop, client, _, convo, obs) = Setup(new EchoTool());
        client.EnqueueTurn(
            new StreamEvent.TextDelta("part"),
            new StreamEvent.Truncated(DroppedInFlightToolCall: true));

        var outcome = await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(TurnOutcome.Truncated, outcome.Outcome);
        Assert.Empty(obs.Results);                       //a truncated turn must execute no tool.
        Assert.Contains("[truncated]", convo.Messages[^1].Content);
        Assert.Contains(obs.Warnings, w => w.Contains("tool call"));
    }

    [Fact]
    public async Task FinishReasonLength_EmitsWarning()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("half an answe"), new StreamEvent.Finished("length", null));

        var outcome = await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        Assert.Contains(obs.Warnings, w => w.Contains("token limit"));
    }

    [Fact]
    public async Task FinishReason_NonLength_ReportsUsageWithoutWarning()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("hi"), new StreamEvent.Finished("stop", new Usage(1, 2)));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.DoesNotContain(obs.Warnings, w => w.Contains("token limit"));
    }

    //usage reports on every finish reason, so a length-truncated response still fires the usage and the token-limit warning
    [Fact]
    public async Task FinishReasonLength_WithUsage_FiresBothUsageAndWarning()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("half an answe"), new StreamEvent.Finished("length", new Usage(10, 20)));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Contains(obs.Warnings, w => w.Contains("token limit"));
        Assert.Contains(obs.Usages, u => u.PromptTokens == 10 && u.CompletionTokens == 20);
    }

    //usage and timings must reach the stored assistant record (this proves the wiring end to end)
    [Fact]
    public async Task Completed_turn_stamps_usage_and_timings_onto_the_convo_record()
    {
        var (loop, client, _, convo, obs) = Setup();
        var timings = "{\"predicted_per_second\":12.3}";
        client.EnqueueTurn(new StreamEvent.TextDelta("hi"),
            new StreamEvent.Finished("stop", new Usage(3, 4), timings));

        await loop.RunTurnAsync(convo, "go", obs, default);

        var last = convo.Messages[^1];
        Assert.Equal(new Usage(3, 4), last.Usage);
        Assert.Equal(timings, last.Timings);
    }

    [Fact]
    public async Task HookOce_WithoutUserCancel_IsNotReportedCancelled()
    {
        var (loop, client, hooks, convo, obs) = Setup(new EchoTool());
        hooks.On(HookEvent.ToolCall, _ => throw new OperationCanceledException());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        var outcome = await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.NotEqual(TurnOutcome.Cancelled, outcome.Outcome);
        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);              //a hook cancel blocks the tool and the result is an error
        //a hook's cancellation is a genuine block, so the blocked: prefix stays reserved for hook throws
        Assert.StartsWith("blocked: ", result.Text);
    }

    [Fact]
    public async Task ToolOce_WithoutUserCancel_IsNotReportedCancelled()
    {
        var (loop, client, _, convo, obs) = Setup(new ThrowingOceTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "oce_bomb", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        var outcome = await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.NotEqual(TurnOutcome.Cancelled, outcome.Outcome);
        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        //a tool's own cancellation is an ordinary tool error, the blocked: prefix stays for hook throws only
        Assert.Equal("tool-internal cancel, not the user's", result.Text);
    }

    [Fact]
    public async Task MaxRounds_StopsWithoutExecutingPendingCalls_AndReturnsTruncated()
    {
        //at the round cap the pending tool call is dropped and the turn ends Truncated (the sub-agent turn budget)
        var executed = false;
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(new ProbeTool(() => executed = true));
        var loop = new AgentLoop(client, reg, new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", maxRounds: 1);
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
            new StreamEvent.Finished("tool_calls", null));

        var outcome = await loop.RunTurnAsync(new Conversation(null), "go", new RecordingObserver(), default);

        Assert.Equal(TurnOutcome.Truncated, outcome.Outcome);
        Assert.False(executed);            //the loop must not execute a call left over from a capped round.
        Assert.Single(client.Requests);    //the loop must send no request after the cap.
    }

    [Fact]
    public async Task MaxRounds_TurnThatFinishesWithTextBeforeCap_Completes()
    {
        //a turn that finishes with text before the cap completes, the cap only stops a turn still asking for tools
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        var loop = new AgentLoop(client, reg, new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", maxRounds: 2);
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"yo\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

        var outcome = await loop.RunTurnAsync(new Conversation(null), "go", new RecordingObserver(), default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        Assert.Equal(2, client.Requests.Count);
    }

    //the elision tests drive the real client over the in-process fake server, so the assertion checks the JSON the server received

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static Conversation ConvoWithBigPriorToolResult(string big) =>
        Load(new Conversation(null), new[]
        {
            new ChatMessage("user", "earlier"),
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "shell", "{}") }),
            new ChatMessage("tool", big, ToolCallId: "c1"),
        });

    private static Conversation Load(Conversation c, ChatMessage[] msgs) { c.Load(msgs); return c; }

    [Fact]
    public async Task Budget_ElidesOldestToolResult_InRequestOnly_AndWarnsOnce()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", budgetTokens: 1000);

        var big = new string('x', 5000);
        var convo = ConvoWithBigPriorToolResult(big);
        var obs = new RecordingObserver();

        await loop.RunTurnAsync(convo, "now", obs, default);

        //exactly one elision warning must fire, with the exact message text.
        Assert.Equal(1, obs.Warnings.Count(w => w.Contains("elided")));
        Assert.Contains("context: elided 1 oldest tool results", obs.Warnings);
        //the request holds the stub in place of the full tool result
        var toolMsg = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal(new string('x', 300) + "\n[elided: shell result, 5000 chars, the first 300 shown above — re-run if needed]", toolMsg.GetProperty("content").GetString());
        //the stored Conversation and the JSONL keep the full result
        Assert.Equal(big, convo.Messages.Single(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task Budget_ElisionWarning_FiresOncePerTurn_AcrossRoundTrips()
    {
        await using var server = new FakeOpenAiServer();
        //both round-trips re-elide the prior result, the warning still fires once
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"t1\",\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"msg\\\":\\\"yo\\\"}\"}}]}"),
            Chunk("{}", "tool_calls"), "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"done\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        var loop = new AgentLoop(client, reg, new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", budgetTokens: 1000);

        var big = new string('x', 5000);
        var convo = ConvoWithBigPriorToolResult(big);
        var obs = new RecordingObserver();

        var outcome = await loop.RunTurnAsync(convo, "now", obs, default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        //the echo result in the transcript proves both round-trips ran.
        Assert.Contains(convo.Messages, m => m.Role == "tool" && m.Content == "echo: yo");
        //the elision warning must fire exactly once for the whole turn.
        Assert.Equal(1, obs.Warnings.Count(w => w.Contains("elided")));
        //the second request must also send the elided prior result.
        Assert.Contains(server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray(), m =>
            m.GetProperty("role").GetString() == "tool" &&
            m.GetProperty("content").GetString() == new string('x', 300) + "\n[elided: shell result, 5000 chars, the first 300 shown above — re-run if needed]");
        //the stored Conversation and the JSONL keep the full prior result
        Assert.Equal(big, convo.Messages.First(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task Budget_StillOverWarning_FiresOncePerTurn_AcrossRoundTrips()
    {
        await using var server = new FakeOpenAiServer();
        //both round-trips stay over the window, the warning still fires once
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"t1\",\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"msg\\\":\\\"yo\\\"}\"}}]}"),
            Chunk("{}", "tool_calls"), "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"done\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var reg = new ToolRegistry();
        reg.Register(new EchoTool());
        var loop = new AgentLoop(client, reg, new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", budgetTokens: 100);

        //the current user message is never elided, so it alone keeps every round-trip over the 100-token window
        var convo = new Conversation(null);
        var obs = new RecordingObserver();
        var outcome = await loop.RunTurnAsync(convo, new string('u', 4000), obs, default);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
        //the echo result in the transcript proves both round-trips ran.
        Assert.Contains(convo.Messages, m => m.Role == "tool" && m.Content == "echo: yo");
        //the over-window warning must fire exactly once for the whole turn.
        Assert.Equal(1, obs.Warnings.Count(w => w.Contains("exceeds the window")));
    }

    [Fact]
    public async Task UpdateContextBudget_ShrinkingBudget_ElidesInRequestButNotConversation()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok2\"}"), Chunk("{}", "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        //a large budget fits the big prior tool result, the first turn sends it unstubbed
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", budgetTokens: 1_000_000);

        var big = new string('x', 5000);
        var convo = ConvoWithBigPriorToolResult(big);
        var obs = new RecordingObserver();

        await loop.RunTurnAsync(convo, "first", obs, default);
        //under the huge budget the big tool result goes out unstubbed.
        var firstToolMsg = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal(big, firstToolMsg.GetProperty("content").GetString());

        //after the window shrinks, the next request must elide the same old tool result.
        loop.UpdateContextBudget(200);
        await loop.RunTurnAsync(convo, "second", obs, default);

        var secondToolMsg = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal(new string('x', 300) + "\n[elided: shell result, 5000 chars, the first 300 shown above — re-run if needed]", secondToolMsg.GetProperty("content").GetString());
        //the stored conversation must keep the full result.
        Assert.Contains(convo.Messages, m => m.Role == "tool");
        Assert.Equal(big, convo.Messages.Single(m => m.Role == "tool").Content);
    }

    [Fact]
    public void UpdateContextBudget_Null_DisablesEnforcement()
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m", budgetTokens: 10);

        var exception = Record.Exception(() => loop.UpdateContextBudget(null));   //a null budget disables enforcement, the call must not throw

        Assert.Null(exception);
    }

    //maps a turn outcome to the string a headless -p caller reads on exit (tested without launching a process)

    [Fact]
    public void OutcomeString_CompletedWithLengthFinish_MapsToTruncatedLength()
    {
        //the hit-context-length signal on a -p exit is a length finish on an otherwise completed turn
        var r = new TurnResult(TurnOutcome.Completed, "length", null, 1, null);
        Assert.Equal("truncated_length", GattoApp.OutcomeString(r));
    }

    [Fact]
    public void OutcomeString_TruncatedStream_MapsToTruncatedStream()
    {
        var r = new TurnResult(TurnOutcome.Truncated, null, TruncationKind.Stream, 1, null);
        Assert.Equal("truncated_stream", GattoApp.OutcomeString(r));
    }

    [Fact]
    public void OutcomeString_TruncatedRoundCap_MapsToTruncatedLength()
    {
        //only run_agent hits the round cap, and that path sets TruncationKind.Length, which maps to truncated_length
        var r = new TurnResult(TurnOutcome.Truncated, null, TruncationKind.Length, 1, null);
        Assert.Equal("truncated_length", GattoApp.OutcomeString(r));
    }

    [Fact]
    public void OutcomeString_Cancelled_MapsToCancelled()
    {
        var r = new TurnResult(TurnOutcome.Cancelled, null, null, 1, null);
        Assert.Equal("cancelled", GattoApp.OutcomeString(r));
    }

    [Fact]
    public void OutcomeString_CompletedWithNullFinish_MapsToCompleted()
    {
        var r = new TurnResult(TurnOutcome.Completed, null, null, 1, null);
        Assert.Equal("completed", GattoApp.OutcomeString(r));
    }

    private static AgentLoop NewLoop(FakeChatClient client, ICompactionHandler? compaction = null,
        ContextUsageState? usageState = null, double? autoCompactAt = null, int? budgetTokens = null) =>
        new(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m",
            budgetTokens: budgetTokens, compaction: compaction, usageState: usageState, autoCompactAt: autoCompactAt);

    private sealed class FakeCompactionHandler(CompactionResult? result) : ICompactionHandler
    {
        public int Calls; public CompactionReason LastReason; public string? LastUserPrompt;
        public string? Failure { get; init; }
        public string? LastFailure => Failure;
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt,
            ITurnObserver observer, CancellationToken ct)
        {
            Calls++; LastReason = reason; LastUserPrompt = currentUserPrompt;
            return Task.FromResult(result);
        }
    }

    //snapshot the observer's warnings when compaction starts, the notice must reach the observer before a wait of tens of minutes
    private sealed class NoticeCapturingHandler(CompactionResult? result) : ICompactionHandler
    {
        public int Calls;
        public List<string> WarningsAtEntry = new();
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt,
            ITurnObserver observer, CancellationToken ct)
        {
            Calls++;
            if (observer is RecordingObserver ro) WarningsAtEntry = new List<string>(ro.Warnings);
            return Task.FromResult(result);
        }
    }

    //a failed proactive attempt must not consume the reactive rescue allowance (the handler answers first, then second)
    private sealed class SequencedHandler(CompactionResult? first, CompactionResult? second) : ICompactionHandler
    {
        public int Calls; public CompactionReason LastReason;
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt,
            ITurnObserver observer, CancellationToken ct)
        {
            Calls++; LastReason = reason;
            return Task.FromResult(Calls == 1 ? first : second);
        }
    }

    [Fact]
    public async Task Overflow_WithHandler_CompactsAndRetriesOnce()
    {
        var handler = new FakeCompactionHandler(new CompactionResult(
            "compacted system", new[] { new ChatMessage("user", Compactor.BuildContinuationPrompt("original prompt")) }, null));
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", 69_716, 65_536));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler);
        var convo = new Conversation("s");

        var result = await loop.RunTurnAsync(convo, "original prompt", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(CompactionReason.Overflow, handler.LastReason);
        Assert.Equal("original prompt", handler.LastUserPrompt);
        Assert.Equal("compacted system", convo.Messages[0].Content);          //compaction must replace the stored conversation in place.
        Assert.Equal("done", convo.Messages[^1].Content);                     //the retry must run on the rebuilt conversation.
        Assert.Single(convo.Messages, m => m.Role == "user");                 //the rebuilt user message must be sent exactly once.
    }

    [Fact]
    public async Task SecondOverflowInSameTurn_Surfaces()
    {
        //a second overflow with no successful round in between surfaces, the rescue allowance only comes back after a round streams
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        client.EnqueueThrow(new GattoContextOverflowException("over again", null, null));
        var loop = NewLoop(client, compaction: handler);

        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(new Conversation("s"), "u", new RecordingObserver(), CancellationToken.None));
        Assert.Equal(1, handler.Calls);   //the loop must attempt a rescue only once without progress.
    }

    [Fact]
    public async Task MultipleReactiveRescues_AfterProgress_AllRescueAndComplete()
    {
        //a round that streams successfully gives the rescue back, a later overflow is rescued again
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over1", 70_000, 65_536));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("id", "read_file", "{}")),
            new StreamEvent.Finished("stop", new Usage(20_000, 100)));
        client.EnqueueThrow(new GattoContextOverflowException("over2", 70_000, 65_536));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler);
        var convo = new Conversation("s");

        var result = await loop.RunTurnAsync(convo, "u", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(2, handler.Calls);                     //both overflows are rescued, the successful round in between gives the rescue back
        Assert.Equal("done", convo.Messages[^1].Content);
    }

    [Fact]
    public async Task Overflow_NoHandler_PropagatesUnchanged()   //with auto_compact: false the loop gets no handler
    {
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        var loop = NewLoop(client, compaction: null);
        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(new Conversation("s"), "u", new RecordingObserver(), CancellationToken.None));
    }

    [Fact]
    public async Task Overflow_HandlerFails_Surfaces_StateUntouched()
    {
        var handler = new FakeCompactionHandler(result: null);   //a null result models a failed compaction.
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        var loop = NewLoop(client, compaction: handler);
        var convo = new Conversation("old system");

        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(convo, "u", new RecordingObserver(), CancellationToken.None));
        Assert.Equal("old system", convo.Messages[0].Content);   //a failed compaction must leave the stored conversation untouched.
    }

    //cancels the turn's token source mid-rescue and returns null, so a cancel during compaction surfaces as OperationCanceledException
    private sealed class CancellingCompactionHandler(CancellationTokenSource cts) : ICompactionHandler
    {
        public int Calls;
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt,
            ITurnObserver observer, CancellationToken ct)
        {
            Calls++;
            cts.Cancel();
            return Task.FromResult<CompactionResult?>(null);
        }
    }

    [Fact]
    public async Task Overflow_CancelledDuringRescue_ThrowsOperationCanceled_NotOverflow()
    {
        var cts = new CancellationTokenSource();
        var handler = new CancellingCompactionHandler(cts);
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        var loop = NewLoop(client, compaction: handler);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => loop.RunTurnAsync(new Conversation("s"), "u", new RecordingObserver(), cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Proactive_FiresAtRoundTop_WhenLastUsageOverThreshold()
    {
        //proactive compaction must fire at the top of the round, before the next request goes out.
        var usage = new ContextUsageState();
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(55_000, 200)));   //55k tokens exceed 80% of the 65_536 window.
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);
        var convo = new Conversation("s");

        var result = await loop.RunTurnAsync(convo, "explore", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(CompactionReason.Proactive, handler.LastReason);
        Assert.Null(usage.LastPromptTokens);        //the latch clears until fresh usage arrives, compaction cannot spin
        Assert.Single(convo.Messages, m => m.Role == "user");   //the rebuilt user message must be sent exactly once.
    }

    //a loop armed with nothing, as a subagent's is, still measures the server's count, elides on it and keeps the trim after the ratio falls
    [Fact]
    public async Task An_unarmed_loop_elides_on_the_server_count_and_keeps_the_trim()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c2", "nosuch", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(5_000, 10)));   //far above the estimate, so the ratio is large
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c3", "nosuch", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(1, 10)));       //below the estimate, so the ratio falls back to 1
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, budgetTokens: 1_000);
        var convo = new Conversation("s");
        convo.Load(new[]
        {
            new ChatMessage("user", "old"),
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "shell", "{}") }),
            new ChatMessage("tool", new string('a', 3_000), ToolCallId: "c1"),
            new ChatMessage("assistant", "ok"),
        });

        await loop.RunTurnAsync(convo, "next", new RecordingObserver(), CancellationToken.None);

        string? Old(int request) => client.Requests[request].Messages[3].Content;
        Assert.Equal(new string('a', 3_000), Old(0));   //about 760 estimated against a limit of 900, the first request goes whole
        Assert.Contains("[elided: shell result, 3000 chars", Old(1));
        Assert.Contains("[elided: shell result, 3000 chars", Old(2));
    }

    //a loaded conversation of shell rounds with 300-char results, about 75 tokens a round, ending on a plain answer
    private static Conversation Grown(int rounds)
    {
        var list = new List<ChatMessage> { new("user", "old") };
        for (var i = 0; i < rounds; i++)
        {
            list.Add(new ChatMessage("assistant", null, new[] { new ToolCall($"c{i}", "shell", "{}") }));
            list.Add(new ChatMessage("tool", new string('a', 300), ToolCallId: $"c{i}"));
        }
        list.Add(new ChatMessage("assistant", "ok"));
        var convo = new Conversation("s");
        convo.Load(list);
        return convo;
    }

    private static ContextUsageState ReadAt(Conversation convo, double ratio)
    {
        var usage = new ContextUsageState();
        var estimate = ContextBudget.Estimate(convo.Messages);
        usage.Record((int)(estimate * ratio), estimate, convo.Count);
        return usage;
    }

    //while auto-compaction is armed nothing is cut, a session over the elision line sends its results whole
    [Fact]
    public async Task An_armed_loop_sends_the_conversation_uncut_past_the_elision_line()
    {
        var convo = Grown(12);   //about 91% of 1000
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: new FakeCompactionHandler(null), usageState: ReadAt(convo, 1.0),
            autoCompactAt: 0.99, budgetTokens: 1_000);

        await loop.RunTurnAsync(convo, "next", new RecordingObserver(), CancellationToken.None);

        Assert.DoesNotContain(client.Requests[0].Messages, m => m.Content?.StartsWith("[elided:") == true);
    }

    //the trigger reads the uncut conversation, so a threshold above the elision line still compacts
    [Fact]
    public async Task An_armed_loop_compacts_on_the_uncut_count()
    {
        var convo = Grown(13);   //about 98% of 1000, a cut copy would read under 90%
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: ReadAt(convo, 1.0), autoCompactAt: 0.95, budgetTokens: 1_000);

        await loop.RunTurnAsync(convo, "next", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    //an armed loop keeps what an earlier request cut, so the prefix the server holds survives, and cuts nothing new
    [Fact]
    public async Task An_armed_loop_recuts_below_a_stored_frontier_and_nothing_beyond()
    {
        var convo = Grown(12);   //0 system, 1 old, then an assistant and a tool per round: tools at 3, 5, 7 and on
        var usage = ReadAt(convo, 1.0);
        usage.Seed(6, 1.0);
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: new FakeCompactionHandler(null), usageState: usage, autoCompactAt: 0.99, budgetTokens: 1_000);

        await loop.RunTurnAsync(convo, "next", new RecordingObserver(), CancellationToken.None);

        var sent = client.Requests[0].Messages;
        Assert.StartsWith("[elided:", sent[3].Content);
        Assert.StartsWith("[elided:", sent[5].Content);
        Assert.Equal(new string('a', 300), sent[7].Content);
    }

    //the elision state rides the session file, so the first request after a resume repeats the cut prefix of the last one before it
    [Fact]
    public async Task A_resumed_session_shapes_its_first_request_as_the_last_one_was_shaped()
    {
        var convo = Grown(13);
        var before = new FakeChatClient();
        before.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        //the server counts 1.3 tokens for each estimated one, so this session cuts more than a ratio of 1 would
        await NewLoop(before, usageState: ReadAt(convo, 1.3), budgetTokens: 1_000)
            .RunTurnAsync(convo, "next", new RecordingObserver(), CancellationToken.None);
        var last = before.Requests[^1].Messages;

        var lines = convo.Messages.Select(m => Gatto.Core.Home.SessionStore.ChatJson(m)).ToList();
        var restored = lines.Select(l => Gatto.Core.Home.SessionStore.ParseChatElement(JsonDocument.Parse(l).RootElement)).ToList();
        var resumed = new Conversation("s");
        resumed.Load(restored.Where(m => m.Role != "system"));
        var usage = new ContextUsageState();
        usage.SeedFrom(resumed.Messages);
        var frontier = usage.ElidedThrough;
        Assert.True(frontier > 0, "the session before the resume cut nothing, so the test proves nothing");

        var after = new FakeChatClient();
        after.EnqueueTurn(new StreamEvent.TextDelta("again"), new StreamEvent.Finished("stop", null));
        await NewLoop(after, usageState: usage, budgetTokens: 1_000)
            .RunTurnAsync(resumed, "more", new RecordingObserver(), CancellationToken.None);
        var first = after.Requests[0].Messages;

        //what the server receives of each message, the record's own timestamp never goes out
        static string Wire(ChatMessage m) =>
            $"{m.Role}|{m.Content}|{m.ToolCallId}|{string.Join(";", m.ToolCalls?.Select(c => c.Id + c.Name + c.ArgumentsJson) ?? [])}";
        for (var i = 0; i < frontier; i++)
            Assert.Equal(Wire(last[i]), Wire(first[i]));
    }

    private sealed class WideTool : ITool
    {
        public string Name => "wide";
        public string Description => new('d', 8_000);
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult("ok"));
    }

    //the tool definitions are a fixed cost on every request, counted in the estimate the ratio is not inflated by them
    [Fact]
    public async Task The_ratio_counts_the_tool_definitions()
    {
        var tools = new ToolRegistry();
        tools.Register(new WideTool());
        var usage = new ContextUsageState();
        var client = new FakeChatClient();
        var user = new string('u', 400);
        var asked = ContextBudget.Estimate([new ChatMessage("system", "s"), new ChatMessage("user", user)]) + ContextBudget.EstimateTools(tools.Specs());
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", new Usage(asked * 2, 10)));
        var loop = new AgentLoop(client, tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m", budgetTokens: 65_536, usageState: usage);

        await loop.RunTurnAsync(new Conversation("s"), user, new RecordingObserver(), CancellationToken.None);

        Assert.InRange(usage.Ratio, 1.9, 2.1);   //about 21 when the 2,000 tokens of definitions count as message density
    }

    //with compaction armed it decides on the uncut conversation and nothing is cut, where elision used to go first and keep compaction waiting
    [Fact]
    public async Task An_armed_compaction_runs_where_elision_used_to_go_first()
    {
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c2", "nosuch", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(950, 10)));   //95 percent of the window, over the 90 percent threshold
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: new ContextUsageState(), autoCompactAt: 0.9, budgetTokens: 1_000);
        var convo = new Conversation("s");
        convo.Load(new[]
        {
            new ChatMessage("user", "old"),
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "shell", "{}") }),
            new ChatMessage("tool", new string('a', 3_000), ToolCallId: "c1"),
            new ChatMessage("assistant", "ok"),
        });

        await loop.RunTurnAsync(convo, "next", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(client.Requests.SelectMany(r => r.Messages), m => m.Content?.StartsWith("[elided:") == true);
    }

    [Fact]
    public async Task Proactive_DoesNotFire_WithoutRecordedUsage()
    {
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: new ContextUsageState(), autoCompactAt: 0.8, budgetTokens: 65_536);
        await loop.RunTurnAsync(new Conversation("s"), "hi", new RecordingObserver(), CancellationToken.None);
        Assert.Equal(0, handler.Calls);             //with no recorded usage reading, proactive compaction must not fire.
    }

    [Fact]
    public async Task Proactive_UnderThreshold_DoesNotFire()
    {
        var usage = new ContextUsageState();
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(20_000, 200)));   //20k tokens stay under the 80% threshold.
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);
        await loop.RunTurnAsync(new Conversation("s"), "hi", new RecordingObserver(), CancellationToken.None);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Proactive_FailedAttempt_OncePerTurn_AndKeepsGoing()
    {
        var usage = new ContextUsageState();
        var handler = new FakeCompactionHandler(result: null);                          //a null result models a compaction that always fails.
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(55_000, 200)));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c2", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(56_000, 200)));   //56k tokens still exceed the threshold.
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);
        var observer = new RecordingObserver();

        var outcome = await loop.RunTurnAsync(new Conversation("s"), "hi", observer, CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, outcome.Outcome);                                  //a failed proactive compaction must not abort the turn.
        Assert.Equal(1, handler.Calls);                                                 //the latch must stop a second attempt after one failure.
        Assert.Contains(observer.Warnings, w => w.Contains("auto-compact failed"));
    }

    //after a failed auto-compaction an overflow is not rescued by another one, it surfaces and /compact is the user's
    [Fact]
    public async Task Proactive_Failed_then_an_overflow_surfaces_without_a_second_compaction()
    {
        var usage = new ContextUsageState();
        var handler = new SequencedHandler(first: null,
            second: new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(55_000, 200)));
        client.EnqueueThrow(new GattoContextOverflowException("over", 70_000, 65_536));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);

        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(new Conversation("s"), "hi", new RecordingObserver(), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    //a failed auto-compaction stays off for the session: the next turn over the threshold sends without trying again
    [Fact]
    public async Task A_failed_auto_compaction_is_not_tried_again_in_the_session()
    {
        var usage = new ContextUsageState();
        var handler = new FakeCompactionHandler(result: null) { Failure = "the summary was cut at the output limit" };
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(55_000, 200)));
        client.EnqueueTurn(new StreamEvent.TextDelta("one"), new StreamEvent.Finished("stop", new Usage(56_000, 10)));
        client.EnqueueTurn(new StreamEvent.TextDelta("two"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);
        var convo = new Conversation("s");
        var observer = new RecordingObserver();

        await loop.RunTurnAsync(convo, "first", observer, CancellationToken.None);
        var second = await loop.RunTurnAsync(convo, "second", observer, CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, second.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.Null(loop.ArmedAutoCompactAt);
        var line = Assert.Single(observer.Warnings, w => w.Contains("auto-compact failed"));
        Assert.Contains("the summary was cut at the output limit", line);
        Assert.Contains("/compact", line);

        loop.ResetAutoCompact();
        Assert.Equal(0.8, loop.ArmedAutoCompactAt);
    }

    //a failed overflow rescue stays off too: the next overflow surfaces without calling the handler
    [Fact]
    public async Task A_failed_overflow_rescue_is_not_tried_again_in_the_session()
    {
        var handler = new FakeCompactionHandler(result: null);
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        client.EnqueueThrow(new GattoContextOverflowException("over again", null, null));
        var loop = NewLoop(client, compaction: handler);
        var convo = new Conversation("s");

        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(convo, "u", new RecordingObserver(), CancellationToken.None));
        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(convo, "u2", new RecordingObserver(), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    //a compaction the user stopped stays off for the session like a failed one
    [Fact]
    public async Task A_stopped_auto_compaction_is_not_tried_again_in_the_session()
    {
        var cts = new CancellationTokenSource();
        var handler = new CancellingCompactionHandler(cts);
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", null, null));
        client.EnqueueThrow(new GattoContextOverflowException("over again", null, null));
        var loop = NewLoop(client, compaction: handler);
        var convo = new Conversation("s");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => loop.RunTurnAsync(convo, "u", new RecordingObserver(), cts.Token));
        await Assert.ThrowsAsync<GattoContextOverflowException>(
            () => loop.RunTurnAsync(convo, "u2", new RecordingObserver(), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LargeToolResult_TriggersNextRoundCheckpoint()
    {
        //a huge run_agent result only crosses the threshold at the next usage reading, which fires the checkpoint
        var usage = new ContextUsageState();
        var handler = new FakeCompactionHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "run_agent", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(10_000, 500)));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c2", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(60_000, 200)));   //the large tool result pushed usage past the threshold.
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);
        await loop.RunTurnAsync(new Conversation("s"), "delegate", new RecordingObserver(), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(CompactionReason.Proactive, handler.LastReason);
    }

    [Fact]
    public async Task Proactive_compaction_announces_itself_before_it_starts()
    {
        var usage = new ContextUsageState();
        var handler = new NoticeCapturingHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(55_000, 200)));   //55k tokens exceed 80% of the 65_536 window.
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, usageState: usage, autoCompactAt: 0.8, budgetTokens: 65_536);
        var obs = new RecordingObserver();

        await loop.RunTurnAsync(new Conversation("s"), "explore", obs, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        //the notice must reach the user before the summarize call begins (the order is the contract)
        Assert.Contains(handler.WarningsAtEntry, w => w.Contains("auto-compacting"));
        Assert.Contains(obs.Warnings, w => w.Contains("auto-compacted at"));   //the completion line must still fire after the start notice.
    }

    [Fact]
    public async Task Reactive_rescue_announces_itself_before_it_starts()
    {
        var handler = new NoticeCapturingHandler(new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, null));
        var client = new FakeChatClient();
        client.EnqueueThrow(new GattoContextOverflowException("over", 69_716, 65_536));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var loop = NewLoop(client, compaction: handler, budgetTokens: 65_536);
        var obs = new RecordingObserver();

        await loop.RunTurnAsync(new Conversation("s"), "hi", obs, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Contains(handler.WarningsAtEntry, w => w.Contains("context overflowed"));
    }

    [Fact]
    public async Task Poisoned_deltas_split_across_chunks_are_stripped_from_the_record()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.TextDelta("<|tool_"),             //a control token split across deltas must still be stripped.
            new StreamEvent.TextDelta("call_begin|>real answer"),
            new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "hi", obs, default);

        var last = convo.Messages[^1];
        Assert.Equal("real answer", last.Content);
        Assert.Equal(1, last.Stripped);
        Assert.Contains(obs.Warnings, w => w.Contains("control tokens stripped"));
    }

    [Fact]
    public async Task All_pollution_no_calls_records_nothing_and_does_not_wedge()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.TextDelta("<|tool_calls_section_end|>"),
            new StreamEvent.Finished("stop", null));
        var before = convo.Count;

        var result = await loop.RunTurnAsync(convo, "hi", obs, default);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        //a record with neither content nor tool_calls breaks every later replay. the count alone can't catch it, the user message adds one
        Assert.DoesNotContain(convo.Messages, m => m.Role == "assistant");
        Assert.Equal(before + 1, convo.Count);
        Assert.Contains(obs.Warnings, w => w.Contains("empty turn"));
    }

    [Fact]
    public async Task All_pollution_with_tool_calls_keeps_the_record()
    {
        var (loop, client, _, convo, obs) = Setup(new EchoTool());
        client.EnqueueTurn(
            new StreamEvent.TextDelta("<|tool_call_begin|>"),
            new StreamEvent.ToolCallReady(new ToolCall("c1", "echo", "{\"msg\":\"x\"}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "hi", obs, default);

        var assistant = convo.Messages.First(m => m.Role == "assistant");
        Assert.Null(assistant.Content);                       //content that strips to empty must persist as null.
        Assert.NotNull(assistant.ToolCalls);                  //tool calls keep the record valid even with null content.
        Assert.Equal(1, assistant.Stripped);
    }

    [Fact]
    public async Task Reasoning_channel_is_stripped_and_counted()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.ReasoningDelta("<|tool_call_begin|>thinking"),
            new StreamEvent.TextDelta("answer"),
            new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "hi", obs, default);

        var last = convo.Messages[^1];
        Assert.Equal("thinking", last.ReasoningContent);
        Assert.Equal(1, last.Stripped);
    }

    [Fact]
    public async Task Truncated_stream_still_strips_before_the_marker()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.TextDelta("<|tool_call_begin|>partial"),
            new StreamEvent.Truncated(false));

        await loop.RunTurnAsync(convo, "hi", obs, default);

        Assert.Equal("partial [truncated]", convo.Messages[^1].Content);
        Assert.Equal(1, convo.Messages[^1].Stripped);
    }

    [Fact]
    public async Task Tool_results_and_user_text_pass_through_verbatim()
    {
        //a tool result holding ChatML tokens must reach the record untouched (the shape read_file gives for a template)
        var (loop, client, _, convo, obs) = Setup(new TemplateEchoTool());
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "template_echo", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "read the template <|tool_call_begin|> please", obs, default);

        Assert.Equal("read the template <|tool_call_begin|> please",
            convo.Messages.First(m => m.Role == "user").Content);           //user text must pass through the loop verbatim.
        Assert.Equal("<|im_start|>user<|im_end|>",
            convo.Messages.First(m => m.Role == "tool").Content);           //tool text must pass through the loop verbatim.
    }

    [Fact]
    public async Task Strip_warning_fires_once_per_session_but_counts_stamp_every_record()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("<|tool_call_begin|>a"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "one", obs, default);
        client.EnqueueTurn(new StreamEvent.TextDelta("<|tool_call_end|>b"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "two", obs, default);

        Assert.Single(obs.Warnings, w => w.Contains("control tokens stripped"));
        Assert.Equal(1, convo.Messages[^1].Stripped);         //the second record must still hold its strip count.
    }

    [Fact]
    public async Task ResetStripWarning_re_arms_the_notice_for_a_new_session()
    {
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(new StreamEvent.TextDelta("<|tool_call_begin|>a"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(convo, "one", obs, default);

        loop.ResetStripWarning();                             //the /new handlers call this method

        var freshConvo = new Conversation(null);
        client.EnqueueTurn(new StreamEvent.TextDelta("<|tool_call_end|>b"), new StreamEvent.Finished("stop", null));
        await loop.RunTurnAsync(freshConvo, "two", obs, default);

        Assert.Equal(2, obs.Warnings.Count(w => w.Contains("control tokens stripped")));
    }

    [Fact]
    public async Task Strip_notice_does_not_clobber_the_turn_error()
    {
        //a turn truncated by the token limit keeps that diagnostic in LastError, the strip notice goes to the observer only
        var (loop, client, _, convo, obs) = Setup();
        client.EnqueueTurn(
            new StreamEvent.TextDelta("<|tool_call_begin|>partial"),
            new StreamEvent.Finished("length", null));

        var result = await loop.RunTurnAsync(convo, "hi", obs, default);

        Assert.Contains("finish_reason=length", result.LastError);
        Assert.Contains(obs.Warnings, w => w.Contains("control tokens stripped"));
    }
}
