using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop;

public enum TurnOutcome { Completed, Truncated, Cancelled }

//the two ways a truncated turn happens, a mid-stream drop and the subagent round cap. the Length member means the round cap, which only run_agent reaches
public enum TruncationKind { Length, Stream }

//the full shape of a finished turn, where finish_reason tells a token-limit stop from a clean one
public sealed record TurnResult(TurnOutcome Outcome, string? FinishReason, TruncationKind? Truncation, int Rounds, string? LastError);

//the loop's malformed-arguments text as one constant, since the audition grader reads the same string
public static class LoopErrors
{
    public const string MalformedArgumentsPrefix = "malformed tool arguments: ";

    //the answer of a call a stopped turn never ran, which a resumed session reads to draw the call cancelled
    public const string Cancelled = "cancelled";
}

public interface ITurnObserver
{
    void OnTextDelta(string text);
    void OnReasoningDelta(string text);
    //fires per chunk, for the rich renderer only. partialArguments is incomplete mid-stream JSON, so never parse it expecting success
    void OnToolCallDelta(string? name = null, string? partialArguments = null) { }
    //the server's prefill progress for the request in flight, and only the rich renderer shows it
    void OnPromptProgress(long total, long processed) { }
    void OnToolCallStart(ToolCall call);
    void OnToolResult(ToolCall call, ToolResult result);
    void OnWarning(string message);
    void OnUsage(Usage usage);
}

public sealed class AgentLoop(
    IChatClient client, ToolRegistry tools, HookBus hooks, IToolContext toolContext, string model,
    JsonElement? samplingOverrides = null, JsonElement? bodyOverrides = null, int? budgetTokens = null,
    int? maxRounds = null, string? promptSuffix = null,
    ICompactionHandler? compaction = null, ContextUsageState? usageState = null, double? autoCompactAt = null,
    ReasoningHistory reasoningHistory = ReasoningHistory.All)
{
    //swap the overrides every later turn reads, without rebuilding the loop and losing its tools. budgetTokens stays out, the window has its own seam
    public void UpdateOverrides(string newModel, JsonElement? newSampling, JsonElement? newBody, string? newPromptSuffix = null)
    {
        model = newModel;
        samplingOverrides = newSampling;
        bodyOverrides = newBody;
        promptSuffix = newPromptSuffix;
    }

    //swap the context window used for elision, its own seam since the window and the model change independently. null means no enforcement
    public void UpdateContextBudget(int? newBudget) => budgetTokens = newBudget;

    //the policy seam, kept separate like the context window since a model switch can change either alone
    public void UpdateReasoningHistory(ReasoningHistory policy) => reasoningHistory = policy;

    //the strip notice fires once, latched here, and ResetStripWarning exists because the loop outlives /new
    private bool _stripWarned;

    //the flush-failure line fires once, since a bad path or a full disk stays bad and a line per round buries it
    private bool _flushWarned;

    //re-arm the strip notice for a new session, which both /new handlers call. compact does not call it, since the session continues
    public void ResetStripWarning() => _stripWarned = false;

    //persist after each tool result, so a killed run still leaves a transcript. null unless a caller sets it, keeping a subagent out of the parent's file
    public Action<Conversation>? OnRoundPersisted { get; set; }

    //arm auto-compaction after construction, since the handler wraps a Repl that is built later. the one-shot and subagent loops never call it
    public void EnableAutoCompact(ICompactionHandler? newCompaction, ContextUsageState? newUsageState, double? newAutoCompactAt)
    {
        compaction = newCompaction;
        usageState = newUsageState;
        autoCompactAt = newAutoCompactAt;
    }

    //off for the rest of the session once an auto-compaction failed or was stopped, so a session past the trigger stays usable and /compact is the user's
    private bool _autoCompactOff;

    //re-arm auto-compaction after a successful /compact and on /new, the only ways a session leaves the state that failed
    public void ResetAutoCompact() => _autoCompactOff = false;

    //the threshold both triggers act on, or null when no handler is armed. a line that quotes the threshold must read this, or it names a compaction that never runs.
    public double? ArmedAutoCompactAt => compaction is null || _autoCompactOff ? null : autoCompactAt;

    //the line a failed or stopped auto-compaction leaves, with the handler's reason when it has one
    private string AutoCompactOffLine(bool stopped) =>
        (stopped ? "auto-compact stopped" : "auto-compact failed" + (compaction?.LastFailure is { } why ? $": {why}" : ""))
        + " — it stays off for this session, /compact runs it";

    //the tools, overrides and reasoning policy every request of this loop carries, read at the moment of asking so a /role or /model swap is seen
    public RequestShape RequestShape => new(tools.Specs(), samplingOverrides, bodyOverrides, reasoningHistory, _lastSent, _lastSentFrom);

    //the last request's messages as sent and the conversation at that moment, copies, so a compaction can resend what the server has cached
    private ChatMessage[]? _lastSent;
    private ChatMessage[]? _lastSentFrom;

    //images for this user turn, since the loop adds the user message and the caller cannot attach them itself
    public async Task<TurnResult> RunTurnAsync(
        Conversation convo, string userMessage, ITurnObserver observer, CancellationToken ct,
        IReadOnlyList<ImageRef>? images = null)
    {
        //the current turn's user-message index, captured before AddUser, and the prompt_suffix splice target
        var turnUserIndex = convo.Count;
        //the index at each round's start, which ElideBefore reads to protect the recent rounds. a compaction clears it with the conversation these indices describe
        var roundStarts = new List<int>();
        var elisionWarned = false;   //the elision warning fires once per turn rather than once per round-trip
        var stillOverWarned = false; //the same once-per-turn latch for the over-the-window warning
        //the last warning the turn produced, and a connection failure never reaches this variable
        string? lastWarning = null;
        //the terminal round's finish_reason, so the -p exit can tell a clean stop from a token-limit cut
        string? finishReason = null;
        void Warn(string message) { observer.OnWarning(message); lastWarning = message; }
        //a round is one StreamAsync round-trip, which maxRounds caps for a subagent turn. at the cap, pending tool calls never run and the turn ends Truncated
        var round = 0;
        //a rescue may fire more than once per turn, since a fresh oversized result is a new problem
        var reactiveHasRescued = false;
        var madeProgressSinceRescue = false;
        convo.AddUser(userMessage, images);
        while (true)
        {
            round++;

            //the proactive trigger, at the round top only, where nothing is half-streamed. lastPrompt alone misses what was appended since, so the growth is scaled in
            if (compaction is not null && !_autoCompactOff && autoCompactAt is double th
                && budgetTokens is int win and > 0
                && usageState is not null
                && ContextBudget.UsedTokens(convo, usageState) is int used
                && used >= th * win)
            {
                //announce before the await, so the wait is explained while it happens. quote the same figure the trigger acted on, or the line names a different percentage
                observer.OnWarning(
                    $"auto-compacting at {(int)Math.Round(100.0 * used / win)}% — summarizing the session, this can take a while");
                var rebuilt = await compaction.CompactAsync(CompactionReason.Proactive, userMessage, observer, ct);
                if (rebuilt is not null)
                {
                    convo.ReplaceAll(rebuilt.SystemText, rebuilt.Messages, rebuilt.Baseline);
                    //the rebuilt conversation starts again at index 1, so the round history and ageing restart here
                    turnUserIndex = 1;
                    roundStarts.Clear();
                    usageState.Clear();   //clearing the usage means a re-fire needs fresh over-threshold evidence
                    //the same figure the trigger acted on, and used is captured above so the Clear cannot strand it
                    observer.OnWarning($"auto-compacted at {(int)Math.Round(100.0 * used / win)}% — continuing");
                }
                else
                {
                    _autoCompactOff = true;
                    observer.OnWarning(AutoCompactOffLine(stopped: ct.IsCancellationRequested));
                }
            }

            var text = new StringBuilder();
            var reasoning = new StringBuilder();   //the reasoning_content accumulated for this turn
            var calls = new List<ToolCall>();
            var truncated = false;
            var droppedCall = false;
            var cancelled = false;
            //the round's usage and timings, threaded onto the completed turn's AddAssistant below. the truncated and empty-turn paths deliberately record neither
            Usage? turnUsage = null;
            string? turnTimings = null;

            roundStarts.Add(convo.Count);   //after any compaction above, so it describes the live conversation

            //reasoning ages first, before elision, and both passes shape only the request copy
            var (requestMessages, elidedCount, stillOver) = ContextBudget.Apply(
                ContextBudget.ShapeReasoning(ContextBudget.ShapeUpdates(convo.Messages), reasoningHistory),
                budgetTokens,
                ContextBudget.ElideBefore(roundStarts, turnUserIndex));

            //the suffix goes on the request copy of the current user message, and the JSONL keeps the pure one
            if (!string.IsNullOrEmpty(promptSuffix) && turnUserIndex < requestMessages.Count)
            {
                var withSuffix = requestMessages.ToArray();
                var userMsg = withSuffix[turnUserIndex];
                withSuffix[turnUserIndex] = userMsg with { Content = (userMsg.Content ?? "") + " " + promptSuffix };
                requestMessages = withSuffix;
            }

            if (elidedCount > 0 && !elisionWarned)
            {
                Warn($"context: elided {elidedCount} oldest tool results");
                elisionWarned = true;
            }
            if (stillOver && !stillOverWarned)
            {
                Warn("context estimate exceeds the window — sending anyway");
                stillOverWarned = true;
            }

            var estimateAtRequest = ContextBudget.Estimate(requestMessages);
            _lastSent = requestMessages.ToArray();
            _lastSentFrom = convo.Messages.ToArray();

            try
            {
                await foreach (var ev in client.StreamAsync(
                    new ChatRequest(model, requestMessages, tools.Specs(), samplingOverrides, bodyOverrides), ct))
                {
                    switch (ev)
                    {
                        case StreamEvent.TextDelta t: text.Append(t.Text); observer.OnTextDelta(t.Text); break;
                        case StreamEvent.ReasoningDelta r: reasoning.Append(r.Text); observer.OnReasoningDelta(r.Text); break;
                        case StreamEvent.ToolCallDelta d: observer.OnToolCallDelta(d.Name, d.PartialArguments); break;
                        case StreamEvent.PromptProgress pg: observer.OnPromptProgress(pg.Total, pg.Processed); break;
                        case StreamEvent.ToolCallReady c: calls.Add(c.Call); break;
                        case StreamEvent.Finished f:
                            turnUsage = f.Usage;
                            turnTimings = f.Timings;
                            finishReason = f.FinishReason;
                            if (f.Usage is not null)
                            {
                                observer.OnUsage(f.Usage);
                                usageState?.Record(f.Usage.PromptTokens, estimateAtRequest, requestMessages.Count);
                            }
                            if (f.FinishReason == "length")
                                Warn("response truncated by token limit (finish_reason=length)");
                            break;
                        case StreamEvent.Truncated tr: truncated = true; droppedCall = tr.DroppedInFlightToolCall; break;
                    }
                }
            }
            catch (OperationCanceledException) { truncated = true; cancelled = ct.IsCancellationRequested; }
            //the first overflow is rescued unconditionally, since madeProgressSinceRescue starts false. later rescues need a round to have streamed since the last one
            catch (GattoContextOverflowException ovf) when (compaction is not null && !_autoCompactOff && (!reactiveHasRescued || madeProgressSinceRescue))
            {
                //nothing streamed for this round, so the conversation holds no partial state and the round can be re-entered
                reactiveHasRescued = true;
                madeProgressSinceRescue = false;   //a later rescue needs fresh progress first
                if (ovf.PromptTokens is int actual)                    //a free exact reading for the ratio estimate
                    usageState?.Record(actual, estimateAtRequest, requestMessages.Count);
                //say it before the summarize, since the user just saw a request fail and silence would read as a hang
                observer.OnWarning("context overflowed — compacting and retrying this turn");
                var rebuilt = await compaction.CompactAsync(CompactionReason.Overflow, userMessage, observer, ct);
                if (rebuilt is null)
                {
                    _autoCompactOff = true;
                    observer.OnWarning(AutoCompactOffLine(stopped: ct.IsCancellationRequested));
                    //a Ctrl+C during the summarize gives a null here, which would otherwise surface as the original overflow
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw;                                             //a failed compact throws here, with the state untouched
                }
                convo.ReplaceAll(rebuilt.SystemText, rebuilt.Messages, rebuilt.Baseline);
                turnUserIndex = 1;                                     //the rebuilt user message sits at index 1
                roundStarts.Clear();                                   //ageing restarts on the rebuilt conversation
                usageState?.Clear();
                var pct = ovf.PromptTokens is int p && ovf.ContextSize is int cw and > 0
                    ? $" at {(int)Math.Round(100.0 * p / cw)}%" : "";
                observer.OnWarning($"auto-compacted{pct} — continuing");
                continue;                                              //re-enter the round on the rebuilt conversation
            }

            madeProgressSinceRescue = true;   //this round streamed without overflowing, so a later overflow may rescue again

            //strip the accumulated strings, since a delimiter can split across chunks. it goes before the empty-turn check so a stripped-to-empty turn is not recorded
            var (cleanText, strippedText) = ControlTokens.Strip(text.ToString());
            var (cleanReasoning, strippedReasoning) = ControlTokens.Strip(reasoning.ToString());
            var strippedCount = strippedText + strippedReasoning;
            if (strippedCount > 0 && !_stripWarned)
            {
                //call OnWarning directly, since Warn would also set LastError and a stripped turn's real error is the truncation
                observer.OnWarning($"control tokens stripped from model output ({strippedCount}) — see the session file's stripped counts");
                _stripWarned = true;
            }

            if (truncated)
            {
                //mark the partial output and drop the in-flight calls, a Ctrl+C says cancelled rather than truncated
                convo.AddAssistant(cleanText + " [truncated]", reasoningContent: cleanReasoning, stripped: strippedCount);
                var what = cancelled ? "turn cancelled" : "turn truncated";
                Warn(droppedCall
                    ? what + " — an in-flight tool call was discarded, not executed"
                    : what);
                return new TurnResult(
                    cancelled ? TurnOutcome.Cancelled : TurnOutcome.Truncated,
                    finishReason, cancelled ? null : TruncationKind.Stream, round, lastWarning);
            }

            if (cleanText.Length == 0 && calls.Count == 0)
            {
                //a turn with no text and no calls records nothing, the chat template rejects an empty assistant message on every later replay
                Warn("model returned an empty turn — nothing recorded");
                await hooks.EmitMessageEndAsync(new HookPayload(AssistantText: cleanText));
                return new TurnResult(TurnOutcome.Completed, finishReason, null, round, lastWarning);
            }

            convo.AddAssistant(cleanText, calls.Count > 0 ? calls : null, turnUsage, turnTimings, cleanReasoning, strippedCount);

            if (calls.Count == 0)
            {
                await hooks.EmitMessageEndAsync(new HookPayload(AssistantText: cleanText));
                return new TurnResult(TurnOutcome.Completed, finishReason, null, round, lastWarning);
            }

            //at the round cap the pending calls never run and the caller reports a partial result rather than an error
            if (maxRounds is int cap && round >= cap)
                return new TurnResult(TurnOutcome.Truncated, finishReason, TruncationKind.Length, round, lastWarning);

            //persist before running the calls, so a tool that hangs still leaves its call in the session file
            Persist(convo, observer);

            //answer this call and every remaining call so --continue reads a balanced transcript, then end the turn
            TurnResult CancelFrom(int first)
            {
                for (var j = first; j < calls.Count; j++)
                    convo.AddToolResult(calls[j].Id, new ToolResult(LoopErrors.Cancelled, IsError: true));
                Warn("turn cancelled — remaining tool calls marked cancelled, not executed");
                return new TurnResult(TurnOutcome.Cancelled, finishReason, null, round, lastWarning);
            }

            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                //a stopped turn reaches no further call, so an esc at one prompt ends the round and no later call can prompt or run
                if (ct.IsCancellationRequested) return CancelFrom(i);
                observer.OnToolCallStart(call);
                ToolResult result;
                try
                {
                    //an argument name the tool does not declare fails here, before the gate can ask about a call that would run wrong
                    if (UnknownArguments(call) is { } refused) throw new CannotApplyException(refused);
                    await hooks.EmitToolCallAsync(new HookPayload(Call: call));  //a throwing handler blocks the call (fails closed)
                    result = await ExecuteAsync(call, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return CancelFrom(i);   //a cancel that lands mid-tool, Ctrl+C or esc
                }
                catch (CannotApplyException ex)
                {
                    result = new ToolResult(ex.Message, IsError: true);
                }
                catch (Exception ex)
                {
                    result = new ToolResult($"blocked: {ex.Message}", IsError: true);
                }
                //a tool_result hook may rewrite the result the model reads, a throwing handler leaves it unchanged
                result = await hooks.EmitToolResultAsync(new HookPayload(Call: call, Result: result));
                convo.AddToolResult(call.Id, result);
                //persist after AddToolResult, so a kill from here on still leaves the result on disk
                Persist(convo, observer);
                observer.OnToolResult(call, result);
            }

            //a stop at the round's last prompt ends the turn here, the next request would go out on the stopped turn and leave a truncated record
            if (ct.IsCancellationRequested)
            {
                Warn("turn cancelled");
                return new TurnResult(TurnOutcome.Cancelled, finishReason, null, round, lastWarning);
            }
        }
    }

    //flush the conversation, a failed flush warns once through observer.OnWarning and doesn't end the turn or become the turn's error
    private void Persist(Conversation convo, ITurnObserver observer)
    {
        if (OnRoundPersisted is not { } persist) return;
        try { persist(convo); }
        catch (Exception ex)
        {
            if (!_flushWarned)
            {
                observer.OnWarning($"session flush failing — {ex.Message}; crash guarantee degraded");
                _flushWarned = true;
            }
        }
    }

    //the refusal of a call naming an argument its tool's schema does not declare, null for an unknown tool or arguments that do not parse, which ExecuteAsync reports itself
    private string? UnknownArguments(ToolCall call)
    {
        if (tools.Get(call.Name) is not { } tool) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson.Length > 0 ? call.ArgumentsJson : "{}");
            return ToolArgumentNames.Refusal(call.Name, tool.ParametersSchema, doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        var tool = tools.Get(call.Name);
        if (tool is null) return new ToolResult($"unknown tool: {call.Name}", IsError: true);
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson.Length > 0 ? call.ArgumentsJson : "{}");
            args = doc.RootElement.Clone();
        }
        catch (JsonException ex) { return new ToolResult(LoopErrors.MalformedArgumentsPrefix + ex.Message, IsError: true); }
        try { return await tool.ExecuteAsync(args, toolContext, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ToolResult(ex.Message, IsError: true); }
    }
}
