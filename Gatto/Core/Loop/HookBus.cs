using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop;

public sealed record HookPayload(ToolCall? Call = null, ToolResult? Result = null, string? AssistantText = null, SummaryInfo? Summary = null);

public sealed record SummaryInfo(string Text, DateTimeOffset At, string Model, string Gist);

public enum HookEvent { ToolCall, ToolResult, MessageEnd, SessionSummary }

//null arms every owner, so a bus built without the flag runs every handler as it always did
public sealed class HookBus(ArmedSources? armed = null)
{
    //every list keeps each handler's owner, which the hook span names and the off flag reads. null is gatto's own, which no flag turns off
    private readonly List<(string? Owner, Func<HookPayload, Task> Handler)> _toolCall = new();
    private readonly List<(string? Owner, Func<HookPayload, Task> Handler)> _messageEnd = new();
    private readonly List<(string? Owner, Func<HookPayload, Task> Handler)> _sessionSummary = new();
    //a tool_result handler returns a replacement, null leaves the result as it is
    private readonly List<(string? Owner, Func<HookPayload, Task<ToolResult?>> Handler)> _toolResult = new();
    public event Action<HookEvent, Exception>? OnHandlerError;

    //set once at launch, the hook spans of message_end and session_summary go here
    public Trace Trace { get; set; } = Trace.Off;

    //true when a session_summary listener exists, a caller checks this before doing any summarization work
    public bool HasSessionSummaryListeners => Live(_sessionSummary).Length > 0;

    //the handlers that will run, so a span's count says what ran
    public int HandlerCount(HookEvent evt) => evt switch
    {
        HookEvent.ToolCall => Live(_toolCall).Length,
        HookEvent.ToolResult => Live(_toolResult).Length,
        HookEvent.MessageEnd => Live(_messageEnd).Length,
        _ => Live(_sessionSummary).Length,
    };

    //observe only, a tool_result handler here can't change the result, and owner names the extension
    public void On(HookEvent evt, Func<HookPayload, Task> handler, string? owner = null)
    {
        switch (evt)
        {
            case HookEvent.ToolCall: _toolCall.Add((owner, handler)); break;
            case HookEvent.MessageEnd: _messageEnd.Add((owner, handler)); break;
            case HookEvent.SessionSummary: _sessionSummary.Add((owner, handler)); break;
            //run the observe-only handler and hand back no replacement
            case HookEvent.ToolResult: _toolResult.Add((owner, async p => { await handler(p); return null; })); break;
        }
    }

    //return the replacement for the result the model reads, or null to leave it, handlers chain in registration order
    public void OnToolResult(Func<HookPayload, Task<ToolResult?>> handler, string? owner = null) => _toolResult.Add((owner, handler));

    //a snapshot of the handlers whose owner is on, taken at each emit so an off source stops at the next one
    private (string? Owner, T Handler)[] Live<T>(List<(string? Owner, T Handler)> handlers) =>
        [.. handlers.ToArray().Where(h => armed is null || armed.IsOn(h.Owner))];

    //fail closed, a throw here reaches the loop and blocks the tool call
    public async Task EmitToolCallAsync(HookPayload p)
    {
        foreach (var (_, h) in Live(_toolCall)) await h(p);
    }

    //fail open, a throwing handler contributes nothing and OnHandlerError fires, and the rewritten result comes back
    public async Task<ToolResult> EmitToolResultAsync(HookPayload p)
    {
        //throw on a null result here, no caller should ever do that and a null-forgiving ! would only hide it
        ArgumentNullException.ThrowIfNull(p.Result);
        var result = p.Result;
        foreach (var (_, h) in Live(_toolResult))
        {
            try
            {
                var replacement = await h(p with { Result = result });
                if (replacement is not null) result = replacement;
            }
            catch (Exception ex)
            {
                try { OnHandlerError?.Invoke(HookEvent.ToolResult, ex); } catch { } //a listener that throws here must not break fail-open
            }
        }
        return result;
    }

    //fail open, a buggy handler contributes nothing and the session continues
    public Task EmitMessageEndAsync(HookPayload p, long traceParent = 0) => EmitOpenAsync(_messageEnd, HookEvent.MessageEnd, p, traceParent);

    //fail open, a buggy summarizer contributes nothing and the session continues
    public Task EmitSessionSummaryAsync(HookPayload p, long traceParent = 0) => EmitOpenAsync(_sessionSummary, HookEvent.SessionSummary, p, traceParent);

    private async Task EmitOpenAsync(List<(string? Owner, Func<HookPayload, Task> Handler)> handlers, HookEvent evt, HookPayload p, long traceParent)
    {
        foreach (var (owner, h) in Live(handlers))
        {
            using var span = Trace.Open(Trace.Hook, traceParent);
            span.Set("event", evt == HookEvent.MessageEnd ? "message_end" : "session_summary");
            span.Set("extension", owner ?? "gatto");
            try { await h(p); span.Set("threw", 0); }
            catch (Exception ex)
            {
                span.Set("threw", 1);
                try { OnHandlerError?.Invoke(evt, ex); } catch { } //a listener that throws here must not break fail-open
            }
        }
    }
}
