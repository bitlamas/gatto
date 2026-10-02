using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a test double with a scripted answer queue, recording every request and denying when the queue empties.
file sealed class FakePrompter(params PermissionAnswer[] answers) : IPermissionPrompter
{
    private readonly Queue<PermissionAnswer> _answers = new(answers);
    public List<PermissionRequest> Requests { get; } = new();
    public int CallCount => Requests.Count;

    public PermissionAnswer Ask(PermissionRequest request)
    {
        Requests.Add(request);
        return _answers.Count > 0 ? _answers.Dequeue() : PermissionAnswer.Deny;
    }
}

//a prompter that answers with a reason, so the test proves the gate consults AskWithReason and threads the reason into the message
file sealed class FakeReasonPrompter(PermissionDecision decision) : IPermissionPrompter
{
    public int CallCount { get; private set; }
    public PermissionAnswer Ask(PermissionRequest request) => decision.Answer;   //the gate never calls this once AskWithReason exists
    public PermissionDecision AskWithReason(PermissionRequest request)
    {
        CallCount++;
        return decision;
    }
}

//a prompter with only Ask, so the seam's default AskWithReason must forward to it
file sealed class AskOnlyPrompter(PermissionAnswer answer) : IPermissionPrompter
{
    public PermissionAnswer Ask(PermissionRequest request) => answer;
}

//a tool that records whether it ran, so a blocked call must leave Executed false
file sealed class SpyTool(string name) : ITool
{
    public bool Executed { get; private set; }
    public string Name => name;
    public string Description => "spy";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        Executed = true;
        return Task.FromResult(new ToolResult("ran"));
    }
}

public sealed class PermissionGateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-gate-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string PermsPath => Path.Combine(_root, ".gatto", "permissions.json");

    //the real AgentLoop with the tool registered, so a test exercises the gating the product runs
    private static (AgentLoop, FakeChatClient, Conversation, RecordingObserver) LoopWith(HookBus hooks, ITool tool)
    {
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        reg.Register(tool);
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(Path.GetTempPath()), "m");
        return (loop, client, new Conversation(null), new RecordingObserver());
    }

    private static void EnqueueOneCall(FakeChatClient client, string tool, string argsJson)
    {
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", tool, argsJson)),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));
    }

    private HookBus GateOn(PermissionStore store, IPermissionPrompter? prompter, bool autoYes)
    {
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, new PermissionGate(store, prompter, autoYes).CheckAsync);
        return hooks;
    }

    [Theory]
    [InlineData("read_file", "{\"path\":\"x\"}")]
    [InlineData("glob", "{\"pattern\":\"*\"}")]
    [InlineData("grep", "{\"pattern\":\"x\"}")]
    //the ask_user tool is not in the built-in read class, a vetted extension grants it and AskUserExtensionTests covers the no-prompt path
    public async Task ReadClass_never_prompts_and_runs(string tool, string args)
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter();          //an empty queue answers Deny, so the test fails if the prompter is consulted.
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool(tool);
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, tool, args);

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
        Assert.False(obs.Results.Single().Item2.IsError);
    }

    //reads the brief like read_file, so it belongs to the read class
    [Fact]
    public async Task TaskRestate_is_read_class_and_runs_without_prompt_or_grant()
    {
        var store = PermissionStore.Load(_root, out _);
        var hooks = GateOn(store, prompter: null, autoYes: false);   //with no prompter a mutating tool would throw, lacking consent.
        var spy = new SpyTool("task_restate");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "task_restate", "{\"brief_file\":\"b.md\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.True(spy.Executed);
        Assert.False(obs.Results.Single().Item2.IsError);
    }

    [Fact]
    public async Task Deny_blocks_with_exact_text_and_tool_never_runs()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git status\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal("blocked: " + PermissionGate.DenyNudge, result.Text);   //pin the exact model-facing string, built from PermissionGate.DenyNudge
        Assert.False(spy.Executed);                              //a denied tool must never run
        Assert.Equal(1, prompter.CallCount);
    }

    [Fact]
    public async Task Once_runs_without_recording_a_grant()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git status\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.True(spy.Executed);
        Assert.Equal(1, prompter.CallCount);
        Assert.False(store.AllowsShell("git status"));
        Assert.False(File.Exists(PermsPath));
    }

    [Fact]
    public async Task Always_persists_and_a_second_matching_call_does_not_prompt()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Always);   //one scripted answer only. a second prompt would dequeue nothing and answer Deny.
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);

        EnqueueOneCall(client, "shell", "{\"command\":\"git status\"}");
        await loop.RunTurnAsync(convo, "go", obs, default);
        //the second call matches the prefix grant, in a new turn
        EnqueueOneCall(client, "shell", "{\"command\":\"git status --short\"}");
        await loop.RunTurnAsync(convo, "again", obs, default);

        Assert.Equal(1, prompter.CallCount);
        Assert.True(File.Exists(PermsPath));
        Assert.True(PermissionStore.Load(_root, out _).AllowsShell("git status --short"));
        Assert.All(obs.Results, r => Assert.False(r.Item2.IsError));
    }

    [Fact]
    public async Task AutoYes_bypasses_everything()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter();                       //an empty queue answers Deny, so any consult fails the test.
        var hooks = GateOn(store, prompter, autoYes: true);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"rm -rf /\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
        Assert.False(File.Exists(PermsPath));                    //the --yes bypass must not persist a grant
    }

    [Fact]
    public async Task NullPrompter_throws_exact_no_tty_message()
    {
        var store = PermissionStore.Load(_root, out _);
        var gate = new PermissionGate(store, prompter: null, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(
            "no interactive terminal to grant permission (use --yes or pre-grant in .gatto\\permissions.json)",
            ex.Message);
    }

    [Fact]
    public async Task NullPrompter_through_loop_yields_blocked_error_and_tool_never_runs()
    {
        var store = PermissionStore.Load(_root, out _);
        var hooks = GateOn(store, prompter: null, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git status\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        //the loop adds blocked: exactly once, and a second prefix anywhere breaks this pin
        Assert.Equal(
            "blocked: no interactive terminal to grant permission (use --yes or pre-grant in .gatto\\permissions.json)",
            result.Text);
        Assert.False(spy.Executed);
    }

    //a chained command must never be covered by a matching prefix grant
    [Fact]
    public async Task ChainedCommand_prompts_even_with_matching_prefix_grant()
    {
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: false);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git status; rm x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
        Assert.False(spy.Executed);
        Assert.True(obs.Results.Single().Item2.IsError);
    }

    [Fact]
    public async Task MissingCommandArgument_prompts_with_raw_json_summary()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{}");                   //no command argument on purpose

        await loop.RunTurnAsync(convo, "go", obs, default);

        var req = Assert.Single(prompter.Requests);
        Assert.Equal("{}", req.Summary);                         //the summary is the raw arguments JSON
        Assert.Null(req.GrantOffer);
        Assert.True(spy.Executed);
    }

    [Fact]
    public async Task UnknownTool_is_treated_as_mutating_and_prompts()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("frobnicate");                     //an unknown name stands for a future extension tool.
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "frobnicate", "{\"x\":1}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
        Assert.False(spy.Executed);
    }

    [Fact]
    public async Task ExtensionTool_Always_persists_a_toolname_grant_that_silences_the_next_call()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Always);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("web_search");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "web_search", "{\"query\":\"x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var req = Assert.Single(prompter.Requests);
        Assert.Equal("web_search", req.GrantOffer);
        Assert.True(spy.Executed);

        //the store reloads from disk with an empty queue, so a prompt the grant failed to silence answers Deny.
        var reloaded = PermissionStore.Load(_root, out _);
        var prompter2 = new FakePrompter();
        var hooks2 = GateOn(reloaded, prompter2, autoYes: false);
        var spy2 = new SpyTool("web_search");
        var (loop2, client2, convo2, obs2) = LoopWith(hooks2, spy2);
        EnqueueOneCall(client2, "web_search", "{\"query\":\"y\"}");

        await loop2.RunTurnAsync(convo2, "go", obs2, default);

        Assert.Equal(0, prompter2.CallCount);
        Assert.True(spy2.Executed);
    }

    //a call its tool cannot run is refused before the prompt in the tool's own words, so no prompt asks and no grant is offered for it
    [Theory]
    [InlineData("write_file", "{}", "path")]
    [InlineData("write_file", "{\"path\":\"a.txt\"}", "content")]
    [InlineData("edit_file", "{\"path\":\"a.txt\",\"old_string\":\"x\"}", "new_string")]
    [InlineData("edit_file", "{\"path\":\"a.txt\",\"new_string\":\"x\"}", "old_string")]
    [InlineData("edit_file", "{\"path\":7,\"old_string\":\"x\",\"new_string\":\"y\"}", "path")]
    public async Task A_file_call_missing_an_argument_is_refused_before_the_prompt(string tool, string args, string missing)
    {
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(PermissionStore.Load(_root, out _), prompter, autoYes: false);
        var ex = await Assert.ThrowsAsync<CannotApplyException>(() => gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", tool, args))));
        Assert.Equal(ToolArgs.MissingParameter(missing), ex.Message);
        Assert.Empty(prompter.Requests);
        var toolError = await Assert.ThrowsAnyAsync<Exception>(() => (tool == "write_file" ? (ITool)new WriteFileTool() : new EditFileTool())
            .ExecuteAsync(JsonDocument.Parse(args).RootElement.Clone(), new TestToolContext(_root), default));
        Assert.Equal(toolError.Message, ex.Message);
    }

    //arguments that are not JSON fail in the loop before any tool runs, so the gate says the loop's words and asks nothing
    [Fact]
    public async Task A_call_whose_arguments_are_not_json_is_refused_before_the_prompt()
    {
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(PermissionStore.Load(_root, out _), prompter, autoYes: false);
        var ex = await Assert.ThrowsAsync<CannotApplyException>(() => gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "my_tool", "{oops"))));
        var parse = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("{oops"));
        Assert.Equal(LoopErrors.MalformedArgumentsPrefix + parse.Message, ex.Message);
        Assert.Empty(prompter.Requests);
    }

    private PermissionRequest CaptureRequest(string tool, string argsJson)
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(store, prompter, autoYes: false);
        gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", tool, argsJson))).GetAwaiter().GetResult();
        return Assert.Single(prompter.Requests);
    }

    [Fact]
    public void WriteFile_inside_project_sizes_lines_and_offers_project_root()
    {
        var full = Path.GetFullPath(Path.Combine(_root, "sub", "a.txt"));
        var req = CaptureRequest("write_file", "{\"path\":\"sub/a.txt\",\"content\":\"line1\\nline2\\n\"}");

        Assert.Equal($"{full} (+2 lines)", req.Summary);
        Assert.Equal(Path.GetFullPath(_root), req.GrantOffer);
    }

    [Fact]
    public void EditFile_sizes_new_string_bytes()
    {
        var full = Path.GetFullPath(Path.Combine(_root, "a.txt"));
        File.WriteAllText(full, "x\n");   //the edit must be one that can apply, or the gate refuses it before any prompt
        var req = CaptureRequest("edit_file", "{\"path\":\"a.txt\",\"old_string\":\"x\",\"new_string\":\"hello\"}");

        Assert.Equal($"{full} (5 bytes)", req.Summary);
        Assert.Equal(Path.GetFullPath(_root), req.GrantOffer);
    }

    [Fact]
    public void Write_outside_project_offers_the_files_own_directory()
    {
        //a sibling of _root, so the path is outside the project
        var outsideDir = Path.GetFullPath(Path.Combine(_root, "..", "gatto-gate-outside"));
        var target = Path.Combine(outsideDir, "x.txt");
        var req = CaptureRequest("write_file", $"{{\"path\":{JsonSerializer.Serialize(target)},\"content\":\"hi\"}}");

        Assert.Equal(outsideDir, req.GrantOffer);
    }

    [Fact]
    public async Task Approval_latch_is_reference_keyed_a_distinct_equal_call_does_not_ride_it()
    {
        var store = PermissionStore.Load(_root, out _);
        var callA = new ToolCall("c1", "shell", "{\"command\":\"git commit -m x\"}");
        var approval = new CheckpointApproval { Call = callA };      //the approval holds the exact object A
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new PermissionGate(store, prompter, autoYes: false, approval: approval);

        //call B is a separate instance with the same field values as A
        var callB = new ToolCall("c1", "shell", "{\"command\":\"git commit -m x\"}");
        Assert.Equal(callA, callB);
        Assert.False(ReferenceEquals(callA, callB));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: callB)));

        Assert.Equal(PermissionGate.DenyNudge, ex.Message);                //call B prompted and denied instead of using A's latch.
        Assert.Equal(1, prompter.CallCount);
        Assert.Same(callA, approval.Call);
    }

    [Fact]
    public async Task Approval_with_no_pending_call_is_inert_shell_still_prompts()
    {
        var store = PermissionStore.Load(_root, out _);
        var approval = new CheckpointApproval();                     //no pending call, so the approval cannot satisfy anything.
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new PermissionGate(store, prompter, autoYes: false, approval: approval);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyNudge, ex.Message);
        Assert.Equal(1, prompter.CallCount);
    }

    [Fact]
    public void ChainedShellCommand_offers_no_always_grant()
    {
        var req = CaptureRequest("shell", "{\"command\":\"git status; git log\"}");
        Assert.Null(req.GrantOffer);        //a chained command gives no grant offer
    }

    [Fact]
    public void UnchainedShellCommand_still_offers_two_token_prefix()
    {
        var req = CaptureRequest("shell", "{\"command\":\"git status --short\"}");
        Assert.Equal("git status", req.GrantOffer);   //an unchained command offers the first two tokens as the prefix.
    }

    [Theory]
    [InlineData("git status > out.txt")]
    [InlineData("git status >> out.txt")]
    [InlineData("git status 2> err.txt")]
    [InlineData("cat < in.txt")]
    public void RedirectShellCommand_offers_no_always_grant(string command)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        var req = CaptureRequest("shell", json);
        Assert.Null(req.GrantOffer);        //a redirect counts as chained
    }

    [Theory]
    [InlineData("python -c print(1)")]
    [InlineData("pwsh -Command Get-Date")]
    [InlineData("powershell -EncodedCommand ZQ==")]
    [InlineData("cmd /c dir")]
    public void FlagShapedSecondToken_offers_no_always_grant(string command)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        var req = CaptureRequest("shell", json);
        Assert.Null(req.GrantOffer);        //no prefix offer when the second token looks like a flag
    }

    [Fact]
    public void PlainSecondToken_still_offers_two_token_prefix()
    {
        var req = CaptureRequest("shell", "{\"command\":\"python build.py --verbose\"}");
        Assert.Equal("python build.py", req.GrantOffer);   //a plain second token still offers the prefix
    }

    [Fact]
    public void OneTokenInterpreter_still_offers_the_bare_token()
    {
        var req = CaptureRequest("shell", "{\"command\":\"python\"}");
        Assert.Equal("python", req.GrantOffer);   //a bare interpreter starts a REPL, so it still gets a prefix offer
    }

    [Fact]
    public async Task AllowReadClass_grants_a_named_tool_read_class_and_it_never_prompts()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter();                       //an empty queue answers Deny, so any consult fails the test.
        var gate = new PermissionGate(store, prompter, autoYes: false);
        gate.AllowReadClass("web_probe");
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("web_probe");                      //an unknown extension tool is mutating by default.
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "web_probe", "{}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
        Assert.False(obs.Results.Single().Item2.IsError);
    }

    [Fact]
    public async Task Without_AllowReadClass_the_same_extension_tool_still_prompts()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new PermissionGate(store, prompter, autoYes: false);
        //no AllowReadClass call on purpose, the tool counts as mutating
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("web_probe");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "web_probe", "{}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
        Assert.False(spy.Executed);
    }

    [Fact]
    public async Task AllowReadClass_grants_only_the_named_tool_a_different_tool_still_prompts()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new PermissionGate(store, prompter, autoYes: false);
        gate.AllowReadClass("web_probe");
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("web_fetch");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "web_fetch", "{}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);                     //the read-class grant must not leak to a sibling name.
        Assert.False(spy.Executed);
    }

    [Fact]
    public async Task Deny_with_no_reason_throws_exactly_the_bare_message()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new AskOnlyPrompter(PermissionAnswer.Deny);   //this prompter implements only Ask, so the seam's default must forward
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyNudge, ex.Message);
    }

    [Fact]
    public async Task Deny_with_reason_throws_the_reason_appended()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Deny, "use read_file instead"));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyWithReasonPrefix + "use read_file instead", ex.Message);
        Assert.Equal(1, prompter.CallCount);
    }

    //the literal text is pinned here, other tests compare against the constant and would pass a reword
    [Fact]
    public void Deny_strings_are_pinned_verbatim()
    {
        Assert.Equal(
            "The user declined this tool call. Do not retry it — ask the user what to do instead.",
            PermissionGate.DenyNudge);
        Assert.Equal("The user declined this tool call: ", PermissionGate.DenyWithReasonPrefix);
    }

    //the user's reason replaces the ask-instead clause, appending ours would talk over it
    [Fact]
    public async Task Deny_with_reason_omits_the_ask_instead_clause()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Deny, "use the sandbox dir"));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal("The user declined this tool call: use the sandbox dir", ex.Message);
        Assert.DoesNotContain("ask the user what to do instead", ex.Message);
    }

    [Fact]
    public async Task Deny_reason_is_sanitized_control_bytes_and_newlines_do_not_survive()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakeReasonPrompter(
            new PermissionDecision(PermissionAnswer.Deny, "line one\nline two\r\x1b[31mtail\x07"));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyWithReasonPrefix + "line oneline two[31mtail", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\x1b', ex.Message);
    }

    [Fact]
    public async Task Deny_reason_over_256_chars_is_capped()
    {
        var store = PermissionStore.Load(_root, out _);
        var longReason = new string('x', 300);
        var prompter = new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Deny, longReason));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyWithReasonPrefix + new string('x', 256), ex.Message);
    }

    [Fact]
    public async Task Deny_reason_that_is_whitespace_only_degrades_to_bare_message()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Deny, "   \n\t  "));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyNudge, ex.Message);
    }

    //the gate's PrepareReason is the one home for the model-facing text, the display sanitizer would expand tabs and claim a truncation

    [Fact]
    public async Task Deny_reason_padded_with_tabs_is_delivered_in_full()
    {
        //tabs drop as control characters, so the 200 real chars stay under the cap and reach the model whole
        var store = PermissionStore.Load(_root, out _);
        var reason = new string('x', 200) + new string('\t', 60);
        var prompter = new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Deny, reason));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyWithReasonPrefix + new string('x', 200), ex.Message);
        Assert.False(PermissionGate.PrepareReason(reason).Truncated);
    }

    [Fact]
    public void PrepareReason_reports_truncation_exactly_when_the_cap_cuts_content()
    {
        //content past the cap marks truncation, and Text is the capped copy
        var over = PermissionGate.PrepareReason(new string('x', PermissionGate.MaxReasonLength + 44));
        Assert.Equal(new string('x', PermissionGate.MaxReasonLength), over.Text);
        Assert.True(over.Truncated);

        //exactly at the cap is not truncated, guarding the off-by-one.
        var exact = PermissionGate.PrepareReason(new string('x', PermissionGate.MaxReasonLength));
        Assert.Equal(new string('x', PermissionGate.MaxReasonLength), exact.Text);
        Assert.False(exact.Truncated);

        //the excess is whitespace that trim removes anyway, so truncated stays false
        var padded = PermissionGate.PrepareReason(
            new string('x', PermissionGate.MaxReasonLength) + new string(' ', 50));
        Assert.Equal(new string('x', PermissionGate.MaxReasonLength), padded.Text);
        Assert.False(padded.Truncated);

        //no reason means nothing was said, so nothing is lost.
        Assert.Equal(new PermissionGate.ModelReason(null, false), PermissionGate.PrepareReason(null));
        Assert.Equal(new PermissionGate.ModelReason(null, false), PermissionGate.PrepareReason(""));

        //a reason that sanitizes to nothing still yields a null text, but with LostEntirely to tell it from silence.
        Assert.Equal(
            new PermissionGate.ModelReason(null, false) { LostEntirely = true },
            PermissionGate.PrepareReason("   \n\t  "));
    }

    //a lost reason and no reason both give a null text, so LostEntirely tells them apart

    [Fact]
    public void PrepareReason_flags_a_reason_that_sanitizes_away_entirely()
    {
        //all tabs is the production shape, the filter drops every character and the reason vanishes
        var onlyTabs = PermissionGate.PrepareReason("\t\t\t");

        Assert.Null(onlyTabs.Text);
        Assert.False(onlyTabs.Truncated);      //nothing was cut, so the flag stays false
        Assert.True(onlyTabs.LostEntirely);
    }

    [Fact]
    public void PrepareReason_flags_a_reason_whose_cap_falls_entirely_inside_leading_whitespace()
    {
        //real text sits past the cap, so the window trims to nothing and LostEntirely outranks Truncated
        var reason = new string(' ', PermissionGate.MaxReasonLength) + "real text";

        var model = PermissionGate.PrepareReason(reason);

        Assert.Null(model.Text);
        Assert.True(model.Truncated);
        Assert.True(model.LostEntirely);
    }

    [Fact]
    public void PrepareReason_never_flags_LostEntirely_when_the_model_receives_anything()
    {
        //any surviving character means the notice must stay silent, so the flag may never over-fire.
        Assert.False(PermissionGate.PrepareReason("ok").LostEntirely);
        Assert.False(PermissionGate.PrepareReason("\t\tok\t\t").LostEntirely);
        Assert.False(PermissionGate.PrepareReason(new string('x', PermissionGate.MaxReasonLength + 44)).LostEntirely);
        Assert.False(PermissionGate.PrepareReason("  padded  ").LostEntirely);
    }

    [Fact]
    public async Task Deny_reason_that_sanitizes_away_entirely_still_yields_the_bare_message()
    {
        //a reason of only control characters sanitizes to nothing, so the message is the bare DenyNudge
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Deny, "\t\t\t"));
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.DenyNudge, ex.Message);
    }

    [Fact]
    public void PrepareReason_drops_exactly_the_control_characters()
    {
        //drops C0 controls, DEL and C1, while the line and paragraph separators and bidi overrides survive
        var r = PermissionGate.PrepareReason("a\tb\nc\rd\u001Be\u007Ff\u0085g\u2028h\u202Ei");
        Assert.Equal("abcdefg\u2028h\u202Ei", r.Text);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Once_and_Always_are_untouched_by_AskWithReason_wiring()
    {
        var store = PermissionStore.Load(_root, out _);
        var onceGate = new PermissionGate(store, new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Once)), autoYes: false);
        await onceGate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}")));

        var alwaysStore = PermissionStore.Load(_root, out _);
        var alwaysGate = new PermissionGate(alwaysStore, new FakeReasonPrompter(new PermissionDecision(PermissionAnswer.Always)), autoYes: false);
        await alwaysGate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}")));
        Assert.True(alwaysStore.AllowsShell("git status"));
    }

    [Fact]
    public async Task Wild_on_auto_allows_and_wild_off_restores_prompting_on_the_same_gate()
    {
        var wild = new WildState();
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new PermissionGate(store, prompter, autoYes: false, approval: null, wild: wild);
        var call = new ToolCall("c1", "shell", "{\"command\":\"del something.txt\"}");

        wild.On = true;
        await gate.CheckAsync(new HookPayload(Call: call));
        Assert.Equal(0, prompter.CallCount);

        wild.On = false;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.CheckAsync(new HookPayload(Call: call)));
        Assert.Equal(1, prompter.CallCount);
    }

    [Fact]
    public async Task MemoryWrite_PassesWithoutPrompt()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter();                       //an empty queue answers Deny, so any consult fails the test.
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("memory_write");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "memory_write", "{\"filename\":\"test.md\",\"content\":\"hello\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
        Assert.False(obs.Results.Single().Item2.IsError);
    }

    [Fact]
    public async Task RecallMemory_PassesWithoutPrompt()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter();                       //an empty queue answers Deny, so any consult fails the test.
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("recall_memory");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "recall_memory", "{\"query\":\"test\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
        Assert.False(obs.Results.Single().Item2.IsError);
    }

    [Fact]
    public async Task WriteFile_StillPrompts()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("write_file");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "write_file", "{\"path\":\"test.txt\",\"content\":\"hello\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
        Assert.False(spy.Executed);
        Assert.True(obs.Results.Single().Item2.IsError);
    }

    //a cancel withdraws the question, so the turn ends

    //the Cancel arm must stay separate, folded into default a cancel would read as a deny
    [Fact]
    public async Task Cancel_ThrowsPinnedMessage()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Cancel);
        var gate = new PermissionGate(store, prompter, autoYes: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.Equal(PermissionGate.CancelMessage, ex.Message);
        Assert.NotEqual(PermissionGate.DenyNudge, ex.Message);
        Assert.DoesNotContain(PermissionGate.DenyWithReasonPrefix, ex.Message);
        Assert.Equal(1, prompter.CallCount);
    }

    //the literal is pinned here, the UI renders this string and the model reads it verbatim
    [Fact]
    public void Cancel_message_is_pinned_verbatim()
    {
        Assert.Equal("cancelled by user", PermissionGate.CancelMessage);
    }

    //only the cancel message reaches the model, behind the loop's blocked: prefix
    [Fact]
    public async Task Cancel_blocks_the_tool_and_carries_only_the_pinned_message()
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Cancel);
        var hooks = GateOn(store, prompter, autoYes: false);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git status\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal("blocked: " + PermissionGate.CancelMessage, result.Text);
        Assert.False(spy.Executed);
    }

    //a cancel must not record or persist a grant
    [Fact]
    public async Task Cancel_persists_no_grant()
    {
        var store = PermissionStore.Load(_root, out _);
        var gate = new PermissionGate(store, new FakePrompter(PermissionAnswer.Cancel), autoYes: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git status\"}"))));

        Assert.False(store.AllowsShell("git status"));
        Assert.False(File.Exists(PermsPath));
    }

    //the loop reports an edit the gate refused as unappliable with the tool's text, not as a block
    [Fact]
    public async Task An_edit_that_cannot_apply_reaches_the_model_as_the_tools_error()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x\n");
        var prompter = new FakePrompter();
        var hooks = GateOn(PermissionStore.Load(_root, out _), prompter, autoYes: false);
        var spy = new SpyTool("edit_file");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "edit_file", JsonSerializer.Serialize(new { path = Path.Combine(_root, "a.txt"), old_string = "nowhere", new_string = "y" }));

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal($"old_string not found in {Path.Combine(_root, "a.txt")}", result.Text);
        Assert.Equal(0, prompter.CallCount);
        Assert.False(spy.Executed);
    }

    //core supplies the preview lines only, the renderer numbers them, adds the +N lines tail and sanitizes

    private async Task<PermissionRequest> RequestFor(string tool, object args)
    {
        var store = PermissionStore.Load(_root, out _);
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(store, prompter, autoYes: false);
        await gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", tool, JsonSerializer.Serialize(args))));
        return prompter.Requests.Single();
    }

    [Fact]
    public async Task WritePreview_FirstFiveAndTotal()
    {
        var eight = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line {i}"));
        var req = await RequestFor("write_file", new { path = "a.txt", content = eight });

        Assert.NotNull(req.PreviewLines);
        Assert.Equal(new[] { "line 1", "line 2", "line 3", "line 4", "line 5" }, req.PreviewLines);
        Assert.Equal(8, req.PreviewTotalLines);

        //a preview under five lines shows all of them, and the total matches
        var three = await RequestFor("write_file", new { path = "b.txt", content = "one\ntwo\nthree" });
        Assert.Equal(new[] { "one", "two", "three" }, three.PreviewLines);
        Assert.Equal(3, three.PreviewTotalLines);
    }

    [Fact]
    public async Task Edit_request_carries_its_strings_and_no_head_preview()
    {
        //an edit's prompt shows its change from the two strings, the head preview is a write's
        var six = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"new {i}"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x\n");   //an edit that can apply, the only kind the gate asks about
        var req = await RequestFor("edit_file", new { path = "a.txt", old_string = "x", new_string = six });

        Assert.Equal(("x", six), (req.EditOld, req.EditNew));
        Assert.Null(req.PreviewLines);
        Assert.Equal(0, req.PreviewTotalLines);
    }

    [Fact]
    public async Task Preview_AbsentArg_Defaults()
    {
        //empty content gets no preview, there is no first line. a call missing its content is refused before any request is built
        var empty = await RequestFor("write_file", new { path = "a.txt", content = "" });
        Assert.Null(empty.PreviewLines);
        Assert.Equal(0, empty.PreviewTotalLines);
    }

    //the total counts like the summary, a trailing newline ends the last line
    [Fact]
    public async Task Preview_TrailingNewline_agrees_with_the_summary_line_count()
    {
        var req = await RequestFor("write_file", new { path = "a.txt", content = "one\ntwo\nthree\n" });

        Assert.Equal(3, req.PreviewTotalLines);
        Assert.Equal(new[] { "one", "two", "three" }, req.PreviewLines);
        Assert.Contains("(+3 lines)", req.Summary);                        //the summary and the preview must report the same count.
    }

    //the gate measures the file a write would replace, lines for text, bytes for anything else, nothing for a new file
    [Fact]
    public async Task A_write_over_a_file_carries_what_it_replaces()
    {
        File.WriteAllText(Path.Combine(_root, "text.txt"), "one\ntwo\nthree\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), new byte[] { 1, 0, 2, 3 });

        Assert.Null((await RequestFor("write_file", new { path = "new.txt", content = "x" })).Existing);
        Assert.Equal(new ExistingFile(3, 14), (await RequestFor("write_file", new { path = "text.txt", content = "x" })).Existing);
        Assert.Equal(new ExistingFile(null, 4), (await RequestFor("write_file", new { path = "blob.bin", content = "x" })).Existing);
        Assert.Null((await RequestFor("edit_file", new { path = "text.txt", old_string = "one", new_string = "1" })).Existing);
    }

    //a file the gate cannot read still says it exists, and the prompt is asked all the same
    [Fact]
    public async Task A_write_over_an_unreadable_file_still_prompts_and_says_it_exists()
    {
        File.WriteAllText(Path.Combine(_root, "locked.txt"), "x");
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(PermissionStore.Load(_root, out _), prompter, autoYes: false)
            { ReadBytes = _ => throw new IOException("locked") };

        await gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "write_file", JsonSerializer.Serialize(new { path = "locked.txt", content = "y" }))));

        Assert.Equal(new ExistingFile(null, null), prompter.Requests.Single().Existing);
    }

    [Fact]
    public async Task Preview_is_never_populated_for_non_write_tools()
    {
        //shell and opaque calls have no write surface, so no preview is built for them
        var shell = await RequestFor("shell", new { command = "git status" });
        Assert.Null(shell.PreviewLines);
        Assert.Equal(0, shell.PreviewTotalLines);

        var opaque = await RequestFor("some_extension_tool", new { content = "a\nb\nc" });
        Assert.Null(opaque.PreviewLines);
        Assert.Equal(0, opaque.PreviewTotalLines);
    }
}
