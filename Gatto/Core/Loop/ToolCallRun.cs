using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop;

//one call from its arguments to its result: a refused argument, a blocked call and a failed tool each reach the model as their own text
internal sealed class ToolCallRun(ToolRegistry tools, HookBus hooks, IToolContext toolContext)
{
    //a cancel of the turn propagates, so the loop answers this call and every remaining one as cancelled
    public async Task<ToolResult> RunAsync(ToolCall call, CancellationToken ct)
    {
        try
        {
            //an argument name the tool does not declare fails here, before the gate can ask about a call that would run wrong
            if (UnknownArguments(call) is { } refused) throw new CannotApplyException(refused);
            await hooks.EmitToolCallAsync(new HookPayload(Call: call));  //a throwing handler blocks the call (fails closed)
            return await ExecuteAsync(call, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CannotApplyException ex) { return new ToolResult(ex.Message, IsError: true); }  //the refusal and the gate's own words, no blocked prefix
        catch (Exception ex) { return new ToolResult($"blocked: {ex.Message}", IsError: true); }
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
