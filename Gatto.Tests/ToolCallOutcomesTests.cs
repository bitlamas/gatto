using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public sealed class DeclaredArgsTool(Action onExecute) : ITool
{
    public string Name => "declared";
    public string Description => "declares one argument";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        onExecute();
        return Task.FromResult(new ToolResult("ran"));
    }
}

//every way a call fails, side by side with the exact text the model reads, so a move of the call path keeps the bytes
public class ToolCallOutcomesTests
{
    private static async Task<ToolResult> RunOne(ToolCall call, Action<HookBus>? arm = null, params ITool[] tools)
    {
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        foreach (var t in tools) reg.Register(t);
        var hooks = new HookBus();
        arm?.Invoke(hooks);
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(Path.GetTempPath()), "m");
        client.EnqueueTurn(new StreamEvent.ToolCallReady(call), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));
        var obs = new RecordingObserver();
        await loop.RunTurnAsync(new Conversation(null), "go", obs, default);
        return obs.Results.Single().Item2;
    }

    [Fact]
    public async Task A_tool_that_throws_reads_as_its_message_alone()
    {
        var result = await RunOne(new ToolCall("c1", "bomb", "{}"), null, new ThrowingTool());

        Assert.Equal(new ToolResult("kaboom", IsError: true), result);
    }

    [Fact]
    public async Task A_tool_call_hook_that_throws_reads_as_blocked()
    {
        var executed = false;
        var result = await RunOne(new ToolCall("c1", "probe", "{}"),
            h => h.On(HookEvent.ToolCall, _ => throw new InvalidOperationException("gate refuses")),
            new ProbeTool(() => executed = true));

        Assert.Equal(new ToolResult("blocked: gate refuses", IsError: true), result);
        Assert.False(executed);
    }

    //the permission gate throws this from a tool_call hook, and its text reaches the model bare, with no blocked prefix
    [Fact]
    public async Task A_tool_call_hook_that_cannot_apply_reads_as_its_message_alone()
    {
        var executed = false;
        var result = await RunOne(new ToolCall("c1", "probe", "{}"),
            h => h.On(HookEvent.ToolCall, _ => throw new CannotApplyException("no such file")),
            new ProbeTool(() => executed = true));

        Assert.Equal(new ToolResult("no such file", IsError: true), result);
        Assert.False(executed);
    }

    [Fact]
    public async Task An_argument_the_schema_does_not_declare_is_refused_before_the_hook()
    {
        var executed = false;
        var hooked = false;
        var result = await RunOne(new ToolCall("c1", "declared", """{"pth":"a.txt"}"""),
            h => h.On(HookEvent.ToolCall, _ => { hooked = true; return Task.CompletedTask; }),
            new DeclaredArgsTool(() => executed = true));

        Assert.Equal(new ToolResult("unknown argument: pth. declared takes path", IsError: true), result);
        Assert.False(hooked);
        Assert.False(executed);
    }

    [Fact]
    public async Task An_unknown_tool_and_unparsed_arguments_read_as_the_loop_s_own_texts()
    {
        var unknown = await RunOne(new ToolCall("c1", "ghost", "{}"));
        string parser;
        try { JsonDocument.Parse("{not json"); parser = ""; }
        catch (JsonException ex) { parser = ex.Message; }
        var malformed = await RunOne(new ToolCall("c1", "probe", "{not json"), null, new ProbeTool(() => { }));

        Assert.Equal(new ToolResult("unknown tool: ghost", IsError: true), unknown);
        Assert.NotEqual("", parser);
        Assert.Equal(new ToolResult("malformed tool arguments: " + parser, IsError: true), malformed);
    }
}
