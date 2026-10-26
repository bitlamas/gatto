using System.Diagnostics;
using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//records the context each call ran with, so a test reads what a tool saw through the loop
file sealed class ContextProbe : ITool
{
    public IToolContext? Seen { get; private set; }
    public string Name => "probe";
    public string Description => "records its context";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        Seen = ctx;
        return Task.FromResult(new ToolResult("seen"));
    }
}

file sealed class TasksContext(string cwd, BackgroundTasks tasks) : IToolContext
{
    public string Cwd => cwd;
    public string HomePath => cwd;
    public IUserPrompter? Prompter => null;
    public IReadOnlyList<string> SessionFiles { get; } = [Path.Combine(cwd, "own.jsonl")];
    public BackgroundTasks? Tasks => tasks;
}

public sealed class ToolCallRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-callrun-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    //the traced call runs on a context made per call, which forwards each member by name, so the registry must be among them
    [Fact]
    public async Task A_TRACED_CALL_CARRIES_THE_BACKGROUND_REGISTRY_THROUGH()
    {
        using var tasks = new BackgroundTasks();
        var context = new TasksContext(_dir, tasks);
        using var trace = new Gatto.Core.Trace(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => Path.Combine(_dir, "t.trace.jsonl"), () => "s", "b", 1);
        var probe = new ContextProbe();
        var reg = new ToolRegistry();
        reg.Register(probe);
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, reg, new HookBus(), context, "m", trace: trace);

        await loop.RunTurnAsync(new Conversation(null), "go", new RecordingObserver(), default);

        Assert.NotNull(probe.Seen);
        Assert.NotSame(context, probe.Seen);   //the traced wrapper, not the session's own context
        Assert.Same(tasks, probe.Seen!.Tasks);
        Assert.Equal(context.SessionFiles, probe.Seen.SessionFiles);
    }
}
