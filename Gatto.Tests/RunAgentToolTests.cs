using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Roles;
using Gatto.Roles.Agents;
using Gatto.Roles.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//records every request and answers from a scripted queue. denies once the queue empties.
file sealed class RecordingPermPrompter(params PermissionAnswer[] answers) : IPermissionPrompter
{
    private readonly Queue<PermissionAnswer> _answers = new(answers);
    public List<PermissionRequest> Requests { get; } = new();
    public PermissionDecision AskWithReason(PermissionRequest request)
    {
        Requests.Add(request);
        return new(_answers.Count > 0 ? _answers.Dequeue() : PermissionAnswer.Deny);
    }
}

//a mutating tool the subagent can call, so the shared gate must prompt.
file sealed class MutatingTool : ITool
{
    public string Name => "mutate";
    public string Description => "mutates";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult("mutated"));
}

//records the context a child's call ran with, under the name the role asks for
file sealed class ContextSpyTool(string name) : ITool
{
    public IToolContext? Seen { get; private set; }
    public string Name => name;
    public string Description => "records its context";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        Seen = ctx;
        return Task.FromResult(new ToolResult("seen"));
    }
}

//a parent context with every member set, so a child view that drops one shows up as a null
file sealed class FullContext(string cwd, BackgroundTasks tasks, Gatto.Core.Sandbox.SandboxState sandbox) : IToolContext
{
    public string Cwd => cwd;
    public string HomePath => Path.Combine(cwd, "home");
    public IUserPrompter? Prompter { get; } = new NoPrompter();
    public Gatto.Core.SessionIdentity? Session { get; } = new(Guid.NewGuid(), DateTimeOffset.Now, "named");
    public IReadOnlyList<string> SessionFiles { get; } = [Path.Combine(cwd, "own.jsonl")];
    public Gatto.Core.Sandbox.SandboxState? Sandbox => sandbox;
    public BackgroundTasks? Tasks => tasks;
}

file sealed class NoPrompter : IUserPrompter
{
    public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AskAnswer>>([]);
}

public sealed class RunAgentToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-runagent-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RoleFile Role(
        string name = "helper", string? description = "helps with things",
        IReadOnlyList<string>? tools = null, int turns = 5, string append = "You are helper.",
        bool raw = false, ThinkingLevel? thinking = null, bool contextFiles = true)
        => new(name, null, null, Array.Empty<string>(), raw, append, thinking, description,
            tools ?? new[] { "read_file" }, turns, contextFiles, null, null, null, null);

    //the child's prefix as GattoApp composes it, minus the launch state: no model, no context files, no memory
    private static Composition Compose(RoleFile role) =>
        RoleComposition.Compose(role, null, Array.Empty<(string, string)>(), null, cwd: null);

    //use the real SubagentWiring.Build here, a hand-built copy could pass while the production wiring differs
    private static Func<RoleFile, (ToolRegistry, HookBus)> Wiring(
        PermissionGate gate,
        IReadOnlyDictionary<string, Func<ITool>>? catalog = null,
        Action<ToolRegistry>? capture = null)
    {
        var build = SubagentWiring.Build(gate, catalog);
        if (capture is null) return build;
        return role =>
        {
            var built = build(role);
            capture(built.Item1);
            return built;
        };
    }

    private PermissionGate FreshGate(IPermissionPrompter? prompter, bool autoYes = false)
    {
        var store = PermissionStore.Load(_dir, _dir, out _);
        return new PermissionGate(store, prompter, autoYes);
    }

    private RunAgentTool Build(
        IChatClient client, PermissionGate gate,
        IReadOnlyList<RoleFile>? roles = null,
        IReadOnlyDictionary<string, Func<ITool>>? catalog = null, Action<ToolRegistry>? capture = null,
        int? contextBudget = null,
        Func<RoleFile, string?>? refuseRole = null,
        Func<RoleFile, Composition>? composeChild = null,
        Func<(string, JsonElement?, JsonElement?, string?)>? requestSettings = null,
        IToolContext? parentCtx = null)
        => new(
            roles: () => roles ?? new[] { Role() },
            refuseRole: refuseRole ?? (_ => null),
            composeChild: r => ((composeChild ?? Compose)(r), r.Model ?? "m"),
            subagentWiring: Wiring(gate, catalog, capture),
            client: client,
            requestSettings: requestSettings ?? (() => ("m", (JsonElement?)null, (JsonElement?)null, (string?)null)),
            parentCtx: parentCtx ?? new TestToolContext(_dir),
            progress: null,
            contextBudget: contextBudget);

    private static JsonElement Args(string role, string task) =>
        JsonSerializer.SerializeToElement(new { role, task });

    private static JsonElement Json(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    //builds one SSE frame in the shape the real client parses.
    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static string[] ToolCallRound(string name) => new[]
    {
        Chunk($"{{\"tool_calls\":[{{\"index\":0,\"id\":\"t1\",\"function\":{{\"name\":\"{name}\",\"arguments\":\"{{}}\"}}}}]}}"),
        Chunk("{}", "tool_calls"), "data: [DONE]\n\n",
    };

    private static string[] TextRound(string text) => new[]
    {
        Chunk($"{{\"content\":\"{text}\"}}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
    };

    private static string SystemText(JsonElement body) =>
        Assert.Single(body.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;

    [Fact]
    public async Task Happy_TwoRounds_ResultIsFinalTextVerbatim_WithGloss()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: ToolCallRound("read_file")));
        server.Enqueue(new FakeResponse(Frames: TextRound("the answer")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()));

        var result = await tool.ExecuteAsync(Args("helper", "do it"), new TestToolContext(_dir), default);

        Assert.False(result.IsError);
        Assert.Equal("the answer", result.Text);
        Assert.NotNull(result.Gloss);
        Assert.StartsWith("helper · 2 turns · ", result.Gloss);
        Assert.EndsWith("s", result.Gloss);
    }

    //the subagent gloss pluralizes per round and is built apart from the tool's own, so a one-round run must read '1 turn'
    [Fact]
    public async Task Happy_OneRound_GlossSaysOneTurn_notOneTurns()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("answered straight away")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()));

        var result = await tool.ExecuteAsync(Args("helper", "do it"), new TestToolContext(_dir), default);

        Assert.False(result.IsError);
        Assert.StartsWith("helper · 1 turn · ", result.Gloss);
        //match any duration here, including sub-second ones, the elapsed time is never the same twice
        Assert.Matches(@"^helper · 1 turn · (<1s|[1-9]\d*s)$", result.Gloss);
    }

    [Fact]
    public async Task TURNS_IS_THE_CHILDS_ROUND_CAP()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: ToolCallRound("read_file")));
        //only one round is enqueued, so the cap must stop the loop before a second request goes out
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()),
            roles: new[] { Role(turns: 1) });

        var result = await tool.ExecuteAsync(Args("helper", "loop forever"), new TestToolContext(_dir), default);

        Assert.False(result.IsError);   //the turn cap returns a partial result rather than an error
        Assert.StartsWith("[run_agent: turn limit (1) reached — partial result]\n", result.Text);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task StreamDropBelowCap_ReturnsResponseTruncatedNotice_NotTurnLimitWording()
    {
        await using var server = new FakeOpenAiServer();
        //a stream with no finish reason reports as truncated, and that holds even with four rounds of budget left
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"partial\"}") }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()), roles: new[] { Role(turns: 5) });

        var result = await tool.ExecuteAsync(Args("helper", "go"), new TestToolContext(_dir), default);

        Assert.False(result.IsError);
        Assert.StartsWith("[run_agent: response truncated — partial result]\n", result.Text);
        Assert.DoesNotContain("turn limit", result.Text);
    }

    [Fact]
    public async Task AN_UNKNOWN_ROLE_NAMES_THE_LIST()
    {
        //the real loop turns the throw into an error result holding the message.
        var mainClient = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(Build(mainClient, FreshGate(new RecordingPermPrompter()),
            roles: new[] { Role(name: "alpha"), Role(name: "beta") }));
        var loop = new AgentLoop(mainClient, reg, new HookBus(), new TestToolContext(_dir), "m");
        mainClient.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "run_agent", "{\"role\":\"x\",\"task\":\"t\"}")),
            new StreamEvent.Finished("tool_calls", null));
        mainClient.EnqueueTurn(new StreamEvent.Finished("stop", null));
        var obs = new RecordingObserver();

        await loop.RunTurnAsync(new Conversation(null), "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal("no role named 'x' — available: alpha, beta", result.Text);
    }

    [Fact]
    public async Task A_REFUSED_ROLE_THROWS_THE_CLOSURES_SENTENCE()
    {
        await using var child = new FakeOpenAiServer();
        var childClient = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(child.BaseUrl));
        const string why = "role 'helper' wants model 'other', this session serves 'm'";
        var mainClient = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(Build(childClient, FreshGate(new RecordingPermPrompter()), refuseRole: _ => why));
        var loop = new AgentLoop(mainClient, reg, new HookBus(), new TestToolContext(_dir), "m");
        mainClient.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "run_agent", "{\"role\":\"helper\",\"task\":\"t\"}")),
            new StreamEvent.Finished("tool_calls", null));
        mainClient.EnqueueTurn(new StreamEvent.Finished("stop", null));
        var obs = new RecordingObserver();

        await loop.RunTurnAsync(new Conversation(null), "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal(why, result.Text);
        Assert.Equal(0, child.RequestCount);
    }

    [Fact]
    public async Task THE_CHILD_REGISTRY_LACKS_RUN_AGENT_AND_ASK_USER()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        ToolRegistry? captured = null;
        //the role must request the forbidden names by name. otherwise the null assertions pass even when the filter does nothing.
        var tool = Build(client, FreshGate(new RecordingPermPrompter()),
            roles: new[] { Role(tools: new[] { "read_file", "run_agent", "ask_user", "some_extension_tool" }) },
            capture: r => captured = r);

        await tool.ExecuteAsync(Args("helper", "go"), new TestToolContext(_dir), default);

        Assert.NotNull(captured);
        Assert.Null(captured!.Get("run_agent"));          //a child must not receive run_agent
        Assert.Null(captured.Get("ask_user"));            //a child must not receive ask_user
        //the wiring has its own allowlist, an extension tool named in the role must still not reach a child
        Assert.Null(captured.Get("some_extension_tool"));
        Assert.NotNull(captured.Get("read_file"));        //read_file must come through here, or the null asserts would pass on a filter that drops everything
    }

    //nothing of an extension reaches a child, on or off, so the day a child forwards the parent's hooks this turns red and the flag must be decided there
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_CHILD_CARRIES_NO_EXTENSION_HOOK_OR_TOOL_WHATEVER_THE_FLAG(bool on)
    {
        var armed = new ArmedSources();
        armed.Set("x", on);
        var parentHooks = new HookBus(armed);
        var seen = new List<string>();
        parentHooks.On(HookEvent.ToolCall, p => { seen.Add(p.Call!.Name); return Task.CompletedTask; }, "x");
        var parentTools = new ToolRegistry(armed);
        parentTools.Register(new MutatingTool(), extension: true, origin: "x");
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: ToolCallRound("write_file")));
        server.Enqueue(new FakeResponse(Frames: ToolCallRound("mutate")));
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var gate = FreshGate(new RecordingPermPrompter());
        gate.AllowReadClass("mutate");   //a grant left over from the parent lets nothing run in a child
        ToolRegistry? captured = null;
        var tool = Build(client, gate, roles: [Role(tools: ["read_file", "write_file", "mutate"])], capture: r => captured = r);

        await tool.ExecuteAsync(Args("helper", "go"), new TestToolContext(_dir), default);

        Assert.Empty(seen);
        Assert.DoesNotContain("mutate", captured!.Specs().Select(s => s.Name));
        var answered = server.RequestBodies[2].GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString();
        Assert.Equal("unknown tool: mutate", answered);
    }

    //a child answers with its final text, so an end arriving later would reach no model that knew the task. its shell runs the flag in the foreground
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_CHILD_S_CONTEXT_HAS_NO_BACKGROUND_TASKS_AND_FORWARDS_EVERY_OTHER_MEMBER()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: ToolCallRound("read_file")));
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        using var tasks = new BackgroundTasks();
        using var sandbox = new Gatto.Core.Sandbox.SandboxState(_dir, _dir, []);
        var parent = new FullContext(_dir, tasks, sandbox);
        var spy = new ContextSpyTool("read_file");
        var tool = Build(client, FreshGate(new RecordingPermPrompter()),
            catalog: new Dictionary<string, Func<ITool>> { ["read_file"] = () => spy }, parentCtx: parent);

        await tool.ExecuteAsync(Args("helper", "go"), parent, default);

        var seen = Assert.IsAssignableFrom<IToolContext>(spy.Seen);
        Assert.Null(seen.Tasks);
        Assert.Equal((parent.Cwd, parent.HomePath), (seen.Cwd, seen.HomePath));
        Assert.Same(parent.Prompter, seen.Prompter);
        Assert.Same(parent.Session, seen.Session);
        Assert.Same(parent.Sandbox, seen.Sandbox);
        Assert.Equal(parent.SessionFiles, seen.SessionFiles);
    }

    [Fact]
    public void TOOLS_UNSET_ARMS_THE_SIX_AND_AN_EMPTY_LIST_ARMS_NONE()
    {
        var build = SubagentWiring.Build(FreshGate(null));

        var (unset, _) = build(Role() with { Tools = null });
        var (none, _) = build(Role(tools: Array.Empty<string>()));
        var (one, _) = build(Role(tools: new[] { "grep" }));

        Assert.Equal(new[] { "edit_file", "glob", "grep", "read_file", "shell", "write_file" },
            unset.Specs().Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(none.Specs());
        Assert.Equal(new[] { "grep" }, one.Specs().Select(s => s.Name));
    }

    [Fact]
    public async Task A_MUTATING_CALL_REACHES_THE_SHARED_GATE_WITH_THE_ROLE_LABEL()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: ToolCallRound("mutate")));
        server.Enqueue(new FakeResponse(Frames: TextRound("ok")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var prompter = new RecordingPermPrompter(PermissionAnswer.Once);
        var gate = FreshGate(prompter);
        var catalog = new Dictionary<string, Func<ITool>>(StringComparer.Ordinal) { ["mutate"] = () => new MutatingTool() };
        var tool = Build(client, gate, roles: new[] { Role(tools: new[] { "mutate" }) }, catalog: catalog);

        await tool.ExecuteAsync(Args("helper", "go"), new TestToolContext(_dir), default);

        var req = Assert.Single(prompter.Requests);
        Assert.Equal("helper", req.Agent);        //the gate must stamp the role's name onto the request.
        Assert.Equal("mutate", req.Tool);
    }

    [Fact]
    public void UpdateContextBudget_ChangesBudgetPassedToSubagentLoop()
    {
        var tool = Build(new FakeChatClient(), FreshGate(null), contextBudget: 262144);

        tool.UpdateContextBudget(131072);

        Assert.Equal(131072, tool.ContextBudgetForTest);
    }

    [Fact]
    public void IS_AVAILABLE_FOLLOWS_THE_ROLES_FOLDER()
    {
        Assert.False(Build(new FakeChatClient(), FreshGate(null), roles: Array.Empty<RoleFile>()).IsAvailable);
        Assert.True(Build(new FakeChatClient(), FreshGate(null), roles: new[] { Role() }).IsAvailable);
    }

    [Fact]
    public void THE_DESCRIPTION_LISTS_ROLES_WITH_AND_WITHOUT_A_DESCRIPTION()
    {
        //passed out of order, the tool keeps the order it is handed
        var tool = Build(new FakeChatClient(), FreshGate(null), roles: new[]
        {
            Role(name: "beta", description: null), Role(name: "alpha", description: "does alpha"), Role(name: "gamma", description: ""),
        });

        Assert.Equal("Run a named role on a task. Available roles:\n- beta\n- alpha: does alpha\n- gamma", tool.Description);
    }

    [Fact]
    public void THE_ARGUMENT_IS_ROLE()
    {
        var schema = Build(new FakeChatClient(), FreshGate(null)).ParametersSchema;

        Assert.Equal(new[] { "role", "task" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(schema.GetProperty("properties").TryGetProperty("agent", out _));
    }

    [Fact]
    public async Task THE_CHILD_PREFIX_IS_THE_COMPOSITION()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var callerFiles = new[] { (@"C:\p\GATTO.md", "CALLER CONTEXT") };
        Composition ComposeWithFiles(RoleFile r) => RoleComposition.Compose(
            RoleMerge.ForChild(r, Role(name: "caller", append: "CALLER APPEND")), null,
            r.ContextFiles ? callerFiles : Array.Empty<(string, string)>(), null, cwd: null);
        var tool = Build(client, FreshGate(new RecordingPermPrompter()), composeChild: ComposeWithFiles);

        await tool.ExecuteAsync(Args("helper", "the task"), new TestToolContext(_dir), default);

        var system = SystemText(server.LastRequestBody!.Value);
        Assert.Equal(ComposeWithFiles(Role()).SystemText, system);
        Assert.Contains("CALLER CONTEXT", system, StringComparison.Ordinal);
        Assert.Contains("You are helper.", system, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLER APPEND", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CONTEXT_FILES_FALSE_KEEPS_THE_CALLERS_FILES_OUT()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var callerFiles = new[] { (@"C:\p\GATTO.md", "CALLER CONTEXT") };
        var tool = Build(client, FreshGate(new RecordingPermPrompter()), roles: new[] { Role(contextFiles: false) },
            composeChild: r => RoleComposition.Compose(r, null,
                r.ContextFiles ? callerFiles : Array.Empty<(string, string)>(), null, cwd: null));

        await tool.ExecuteAsync(Args("helper", "the task"), new TestToolContext(_dir), default);

        Assert.DoesNotContain("CALLER CONTEXT", SystemText(server.LastRequestBody!.Value), StringComparison.Ordinal);
    }

    //a raw child's prefix is its append and nothing gatto writes
    [Fact]
    public async Task A_RAW_CALLABLE_PREFIX_IS_THE_APPEND_PLUS_THE_USER_PARTS()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()),
            roles: new[] { Role(raw: true, append: "You are helper.") });

        await tool.ExecuteAsync(Args("helper", "the task"), new TestToolContext(_dir), default);

        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("You are helper.", SystemText(server.LastRequestBody!.Value));
        Assert.Contains(messages, m => m.GetProperty("role").GetString() == "user"
            && m.GetProperty("content").GetString() == "the task");
    }

    [Fact]
    public async Task A_CALLED_ROLES_THINKING_REACHES_THE_CHILD_REQUEST()
    {
        //a map whose bodies name their level, so the request says which one it carries
        var map = new Dictionary<string, JsonElement?>
        {
            ["low"] = Json("""{"marker":"low","prompt_suffix":"/low"}"""), ["medium"] = Json("""{"marker":"medium"}"""),
        };
        async Task<(string? Marker, string? Task)> MarkerFor(RoleFile role)
        {
            await using var server = new FakeOpenAiServer();
            server.Enqueue(new FakeResponse(Frames: TextRound("done")));
            var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
            var tool = Build(client, FreshGate(new RecordingPermPrompter()), roles: new[] { role },
                composeChild: r => RoleComposition.Compose(r, null, Array.Empty<(string, string)>(), map, cwd: null),
                requestSettings: () => ("m", null, Json("""{"marker":"caller"}"""), "/caller"));
            await tool.ExecuteAsync(Args(role.Name, "t"), new TestToolContext(_dir), default);
            var body = server.RequestBodies[0];
            var user = body.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user");
            return (body.GetProperty("marker").GetString(), user.GetProperty("content").GetString());
        }

        //the suffix rides the child's user message as the main loop sends it, the role's with the role's body and the caller's with the caller's
        Assert.Equal(("low", "t /low"), await MarkerFor(Role(thinking: ThinkingLevel.Low)));
        Assert.Equal(("caller", "t /caller"), await MarkerFor(Role(thinking: null)));
        //a role pinning the model the session already runs follows the session's level, which may live only in memory after a resume
        Assert.Equal(("caller", "t /caller"), await MarkerFor(Role(thinking: null) with { Model = "m" }));
        Assert.Equal(("medium", "t"), await MarkerFor(Role(thinking: null) with { Model = "other" }));
    }

    [Fact]
    public async Task Poisoned_subagent_final_text_is_stripped_and_glossed()
    {
        //the final text comes from raw deltas and becomes the parent's tool result, so it needs the strip too
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("<|tool_call_begin|><|tool_call_end|>the summary")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()));

        var result = await tool.ExecuteAsync(Args("helper", "do it"), new TestToolContext(_dir), default);

        Assert.Equal("the summary", result.Text);
        Assert.Contains("stripped 2", result.Gloss);   //the gloss is the only channel reporting the strip.
    }

    [Fact]
    public async Task Clean_subagent_gloss_is_unchanged()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: TextRound("fine")));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var tool = Build(client, FreshGate(new RecordingPermPrompter()));

        var result = await tool.ExecuteAsync(Args("helper", "do it"), new TestToolContext(_dir), default);

        Assert.Equal("fine", result.Text);
        Assert.DoesNotContain("stripped", result.Gloss);
    }
}
