using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop;

//one call from its arguments to its result: a refused argument, a blocked call and a failed tool each reach the model as their own text
internal sealed class ToolCallRun(ToolRegistry tools, HookBus hooks, IToolContext toolContext, Trace trace)
{
    //a cancel of the turn propagates, so the loop answers this call and every remaining one as cancelled
    public async Task<ToolResult> RunAsync(ToolCall call, CancellationToken ct, long toolSpan = 0)
    {
        try
        {
            //an argument name the tool does not declare fails here, before the gate can ask about a call that would run wrong
            if (UnknownArguments(call) is { } refused) throw new CannotApplyException(refused);
            await GateAsync(call, toolSpan);
            return await ExecuteAsync(call, ct, toolSpan);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CannotApplyException ex) { return new ToolResult(ex.Message, IsError: true); }  //the refusal and the gate's own words, no blocked prefix
        catch (Exception ex) { return new ToolResult($"blocked: {ex.Message}", IsError: true); }
    }

    //a throwing handler blocks the call (fails closed), and the gate span records which way it went
    private async Task GateAsync(ToolCall call, long toolSpan)
    {
        using var span = trace.Open(Trace.ToolGate, toolSpan);
        span.Set("handlers", hooks.HandlerCount(HookEvent.ToolCall));
        try
        {
            await hooks.EmitToolCallAsync(new HookPayload(Call: call));
            span.Set("blocked", 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            span.Set("blocked", 1);
            throw;
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

    private async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct, long toolSpan)
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
        using var span = trace.Open(Trace.ToolExec, toolSpan);
        //a context per call only when tracing, so the tool can open its own spans under this one
        var ctx = trace.Enabled ? new TracedToolContext(toolContext, trace, span.Id) : toolContext;
        try { return await tool.ExecuteAsync(args, ctx, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ToolResult(ex.Message, IsError: true); }
    }
}

//the session's tool context with the trace and the open tool.exec span, every other member read through
file sealed class TracedToolContext(IToolContext inner, Trace trace, long parent) : IToolContext
{
    public string Cwd => inner.Cwd;
    public string HomePath => inner.HomePath;
    public IUserPrompter? Prompter => inner.Prompter;
    public SessionIdentity? Session => inner.Session;
    public Gatto.Core.Sandbox.SandboxState? Sandbox => inner.Sandbox;
    public Trace Trace => trace;
    public long TraceParent => parent;
}
