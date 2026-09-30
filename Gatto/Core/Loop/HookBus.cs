using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop;

public sealed record HookPayload(ToolCall? Call = null, ToolResult? Result = null, string? AssistantText = null, SummaryInfo? Summary = null);

public sealed record SummaryInfo(string Text, DateTimeOffset At, string Model, string Gist);

public enum HookEvent { ToolCall, ToolResult, MessageEnd, SessionSummary }

public sealed class HookBus
{
    private readonly List<Func<HookPayload, Task>> _toolCall = new();
    private readonly List<Func<HookPayload, Task>> _messageEnd = new();
    private readonly List<Func<HookPayload, Task>> _sessionSummary = new();
    //a tool_result handler returns a replacement, null leaves the result as it is
    private readonly List<Func<HookPayload, Task<ToolResult?>>> _toolResult = new();
    public event Action<HookEvent, Exception>? OnHandlerError;

    //true when a session_summary listener exists, a caller checks this before doing any summarization work
    public bool HasSessionSummaryListeners => _sessionSummary.Count > 0;

    //observe only, a tool_result handler here can't change the result and sees it as earlier handlers left it
    public void On(HookEvent evt, Func<HookPayload, Task> handler)
    {
        switch (evt)
        {
            case HookEvent.ToolCall: _toolCall.Add(handler); break;
            case HookEvent.MessageEnd: _messageEnd.Add(handler); break;
            case HookEvent.SessionSummary: _sessionSummary.Add(handler); break;
            //run the observe-only handler and hand back no replacement
            case HookEvent.ToolResult: _toolResult.Add(async p => { await handler(p); return null; }); break;
        }
    }

    //return the replacement for the result the model reads, or null to leave it, handlers chain in registration order
    public void OnToolResult(Func<HookPayload, Task<ToolResult?>> handler) => _toolResult.Add(handler);

    //fail closed, a throw here reaches the loop and blocks the tool call
    public async Task EmitToolCallAsync(HookPayload p)
    {
        foreach (var h in _toolCall.ToArray()) await h(p);
    }

    //fail open, a throwing handler contributes nothing and OnHandlerError fires, and the rewritten result comes back
    public async Task<ToolResult> EmitToolResultAsync(HookPayload p)
    {
        //throw on a null result here, no caller should ever do that and a null-forgiving ! would only hide it
        ArgumentNullException.ThrowIfNull(p.Result);
        var result = p.Result;
        foreach (var h in _toolResult.ToArray())
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
    public Task EmitMessageEndAsync(HookPayload p) => EmitOpenAsync(_messageEnd, HookEvent.MessageEnd, p);

    //fail open, a buggy summarizer contributes nothing and the session continues
    public Task EmitSessionSummaryAsync(HookPayload p) => EmitOpenAsync(_sessionSummary, HookEvent.SessionSummary, p);

    private async Task EmitOpenAsync(List<Func<HookPayload, Task>> handlers, HookEvent evt, HookPayload p)
    {
        foreach (var h in handlers.ToArray())
            try { await h(p); }
            catch (Exception ex)
            {
                try { OnHandlerError?.Invoke(evt, ex); } catch { } //a listener that throws here must not break fail-open
            }
    }
}
