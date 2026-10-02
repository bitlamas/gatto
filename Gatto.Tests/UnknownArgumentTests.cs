using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//answers yes and counts the prompts it was asked
file sealed class CountingPrompter : IPermissionPrompter
{
    public int CallCount { get; private set; }
    public PermissionAnswer Ask(PermissionRequest request) { CallCount++; return PermissionAnswer.Once; }
}

//a tool refuses an argument name its schema does not declare, before any prompt and before it runs, and the error names what it takes
public sealed class UnknownArgumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-args-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<(ToolResult Result, int Prompts)> Run(ITool tool, object args)
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", tool.Name, JsonSerializer.Serialize(args))),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));
        var prompter = new CountingPrompter();
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, new PermissionGate(PermissionStore.InMemory(_root), prompter, autoYes: false).CheckAsync);
        var reg = new ToolRegistry();
        reg.Register(tool);
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(_root), "m");
        var obs = new RecordingObserver();
        await loop.RunTurnAsync(new Conversation(null), "go", obs, default);
        return (obs.Results.Single().Item2, prompter.CallCount);
    }

    //the call that searched the wrong folder with no error: path is not one of grep's names
    [Fact]
    public async Task Grep_with_path_is_refused_and_says_what_grep_takes()
    {
        var (result, _) = await Run(new GrepTool(), new { pattern = "x", path = "src" });

        Assert.True(result.IsError);
        Assert.Equal("unknown argument: path. grep takes pattern, root, glob", result.Text);
    }

    //a mutating call with an unknown name never reaches its prompt and never runs
    [Fact]
    public async Task A_write_with_an_unknown_argument_asks_nothing_and_writes_nothing()
    {
        var (result, prompts) = await Run(new WriteFileTool(), new { path = "a.txt", content = "x", mode = "append", encoding = "utf8" });

        Assert.Equal("unknown arguments: mode, encoding. write_file takes path, content", result.Text);
        Assert.Equal(0, prompts);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
    }

    //a call with only declared names runs as before
    [Fact]
    public async Task A_call_with_declared_names_runs()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x\n");
        var (result, _) = await Run(new GrepTool(), new { pattern = "x", root = "." });

        Assert.False(result.IsError);
    }

    //a schema that declares no properties names nothing, so an extension tool with a loose schema is not checked
    [Fact]
    public void A_schema_without_properties_refuses_nothing()
    {
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        Assert.Null(ToolArgumentNames.Refusal("ext_tool", schema, JsonDocument.Parse("""{"anything":1}""").RootElement));
    }

    //a schema that accepts other names, by true or by a schema for them, is not refused them, and false still refuses
    [Theory]
    [InlineData("true", false)]
    [InlineData("""{"type":"string"}""", false)]
    [InlineData("false", true)]
    public void A_schema_that_accepts_other_names_is_not_refused_them(string additional, bool refused)
    {
        var schema = JsonDocument.Parse($$$"""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":{{{additional}}}}""").RootElement;
        var refusal = ToolArgumentNames.Refusal("ext_tool", schema, JsonDocument.Parse("""{"b":"x"}""").RootElement);
        Assert.Equal(refused, refusal is not null);
    }

    //an empty properties object declares that the tool takes nothing, and the refusal says so instead of ending on an empty list
    [Fact]
    public void A_tool_that_takes_no_arguments_says_so()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        Assert.Equal("unknown argument: x. ext_tool takes no arguments",
            ToolArgumentNames.Refusal("ext_tool", schema, JsonDocument.Parse("""{"x":1}""").RootElement));
    }
}
