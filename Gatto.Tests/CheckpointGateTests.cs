using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a prompter with scripted answers that records every request it is asked.
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

//a tool that records whether it ran, so a test can prove a denied call never reaches it
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

public sealed class CheckpointGateTests
{
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

    private static Func<string, string> ReadOnly(string statusOutput) => _ => statusOutput;

    private static Func<string, string> ThrowingReadOnly(Exception ex) => _ => throw ex;

    [Fact]
    public async Task CommitCommand_pauses_with_status_in_summary_and_no_always_offered()
    {
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly(" M Gatto/Roles/CheckpointGate.cs"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var req = Assert.Single(prompter.Requests);
        Assert.Equal("checkpoint", req.Tool);
        Assert.Contains(" M Gatto/Roles/CheckpointGate.cs", req.Summary);
        Assert.Contains("git commit -m x", req.Summary);
        Assert.Null(req.GrantOffer);
        Assert.True(spy.Executed);                   //the checkpoint answered once and no permission gate is registered here, so the tool runs
    }

    [Fact]
    public async Task Disabled_skips_the_pause_but_PermissionGate_still_applies()
    {
        var prompter = new FakePrompter(PermissionAnswer.Once);   //the gate below is disabled, so it must never ask this prompter
        var gate = new CheckpointGate(prompter, ReadOnly("clean")) { Enabled = false };
        var permPrompter = new FakePrompter(PermissionAnswer.Deny);
        var store = PermissionStore.InMemory(Directory.CreateTempSubdirectory("gatto-cg-").FullName);
        var permGate = new PermissionGate(store, permPrompter, autoYes: false);
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);      //hooks run in the order they were added, so the checkpoint sees the call first
        hooks.On(HookEvent.ToolCall, permGate.CheckAsync);  //the permission layer stays registered while the checkpoint is disabled.
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);          //a disabled gate never consults the prompter, so the count stays zero
        Assert.Equal(1, permPrompter.CallCount);
        Assert.False(spy.Executed);                    //a denied call must not reach the tool
        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git status --short")]
    [InlineData("git log")]
    public async Task NonCommit_git_commands_do_not_trigger(string command)
    {
        var prompter = new FakePrompter();   //the fake answers deny if it's asked, so a zero call count means the gate never asked it
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", JsonSerializer.Serialize(new { command }));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
    }

    [Fact]
    public async Task Deny_delivers_exact_blocked_message_and_tool_never_runs()
    {
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal("blocked: checkpoint declined — commit not run", result.Text);   //the blocked text must match letter for letter, don't relax this to a substring check
        Assert.False(spy.Executed);
    }

    //the fourth value must be refused like deny. stamping approval.Call would hand the permission gate downstream a free pass
    [Fact]
    public async Task Cancel_is_refused_like_deny_and_never_stamps_the_permission_latch()
    {
        var approval = new CheckpointApproval();
        var prompter = new FakePrompter(PermissionAnswer.Cancel);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"), approval);
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal("blocked: checkpoint declined — commit not run", result.Text);
        Assert.False(spy.Executed);
        Assert.Null(approval.Call);   //the downstream permission gate must get no pre-approval on this path
    }

    [Fact]
    public async Task Chained_commit_still_pauses()
    {
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x; rm -rf x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);          //chaining must not skip the pause.
        Assert.False(spy.Executed);
    }

    [Fact]
    public async Task RunReadOnly_throwing_still_pauses_and_does_not_fail_open()
    {
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new CheckpointGate(prompter, ThrowingReadOnly(new InvalidOperationException("git not found")));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var req = Assert.Single(prompter.Requests);
        Assert.Contains("git not found", req.Summary);      //the summary must include why the read failed.
        Assert.Equal(1, prompter.CallCount);                 //a throwing read must not skip the pause.
        Assert.False(spy.Executed);                          //the scripted deny still blocks the tool
    }

    [Theory]
    [InlineData("   git commit -m x")]      //the match tolerates leading whitespace.
    [InlineData("GIT COMMIT -m x")]         //the match is case-insensitive.
    public async Task Whitespace_and_case_variants_still_match(string command)
    {
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", JsonSerializer.Serialize(new { command }));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
    }

    [Fact]
    public async Task CommitTree_is_a_prefix_match_and_pauses_too()
    {
        //the check matches a literal prefix, so git commit-tree pauses too. a false positive that still asks is the safe direction
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit-tree abc123\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
    }

    [Fact]
    public async Task MissingCommandArgument_never_matches()
    {
        var prompter = new FakePrompter();
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
    }

    [Fact]
    public async Task NonShellTool_never_matches()
    {
        var prompter = new FakePrompter();
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("write_file");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "write_file", "{\"path\":\"a.txt\",\"content\":\"git commit\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
    }

    [Fact]
    public async Task NoTty_armed_checkpoint_blocks_commit_with_exact_message_and_never_runs_tool()
    {
        var gate = new CheckpointGate(null, ReadOnly("clean"));   //a null prompter means no terminal is attached.
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        var (_, result) = obs.Results.Single();
        Assert.True(result.IsError);
        Assert.Equal(
            "blocked: checkpoint armed and no interactive terminal — pass --auto, and --yes or a standing grant for the command, to run commits unattended",
            result.Text);
        Assert.False(spy.Executed);
    }

    [Fact]
    public async Task NoTty_disabled_checkpoint_passes_the_auto_path()
    {
        var gate = new CheckpointGate(null, ReadOnly("clean")) { Enabled = false };   //a disabled checkpoint is the auto path
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.True(spy.Executed);   //the disabled checkpoint passed the call and no permission gate is registered here, so the tool runs
    }

    [Fact]
    public async Task NoTty_NonCommit_command_with_null_prompter_passes_untouched()
    {
        var gate = new CheckpointGate(null, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git status\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.True(spy.Executed);
    }

    [Fact]
    public async Task Interactive_yes_still_pauses_the_checkpoint_before_PermissionGate_autoYes()
    {
        //the checkpoint must ask before the permission layer auto-passes. an interactive yes must not defeat it
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var store = PermissionStore.InMemory(Directory.CreateTempSubdirectory("gatto-cg-").FullName);
        var permGate = new PermissionGate(store, prompter, autoYes: true);
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);      //hooks run in the order they were added, so the checkpoint sees the call first
        hooks.On(HookEvent.ToolCall, permGate.CheckAsync);  //autoYes passes the call through, so this hook must stay registered after the checkpoint
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);   //one ask means the checkpoint paused before the auto-pass
        Assert.True(spy.Executed);             //the checkpoint answered once and the permission layer auto-passed after, so the tool runs
    }

    [Fact]
    public async Task EnabledToggle_mid_turn_takes_effect_on_the_next_matching_call()
    {
        var prompter = new FakePrompter(PermissionAnswer.Once, PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly("clean")) { Enabled = false };
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);

        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m first\"}");
        await loop.RunTurnAsync(convo, "go", obs, default);
        Assert.Equal(0, prompter.CallCount);          //the disabled checkpoint never asks.

        gate.Enabled = true;                          //the auto toggle turns the pause back on mid-session
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m second\"}");
        await loop.RunTurnAsync(convo, "again", obs, default);
        Assert.Equal(1, prompter.CallCount);          //turning the checkpoint back on pauses the commit again
    }

    [Theory]
    [InlineData("git  commit -m x")]        //two spaces between git and commit
    [InlineData("git\tcommit")]             //a tab separates the tokens
    [InlineData("git.exe commit")]          //the name without its extension is still git, so git.exe matches too
    [InlineData("GIT COMMIT")]              //both tokens are upper case
    [InlineData("git -c a=b commit")]       //an interleaved -c flag must not hide the commit subcommand
    [InlineData("cd . && git commit -m x")] //a leading cd with an && chain must not hide the commit
    public async Task Token_normalized_commit_variants_pause(string command)
    {
        var prompter = new FakePrompter(PermissionAnswer.Deny);
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", JsonSerializer.Serialize(new { command }));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);
        Assert.False(spy.Executed);            //the scripted deny blocked the tool
    }

    [Theory]
    [InlineData("git log")]
    [InlineData("git status")]
    public async Task Non_commit_git_subcommands_do_not_pause(string command)
    {
        var prompter = new FakePrompter();     //the fake answers deny if it's asked, so a zero call count means the gate never asked it
        var gate = new CheckpointGate(prompter, ReadOnly("clean"));
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", JsonSerializer.Serialize(new { command }));

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(0, prompter.CallCount);
        Assert.True(spy.Executed);
    }

    [Fact]
    public async Task Arming_mid_session_via_role_switch_the_next_commit_now_pauses_even_though_the_hook_was_registered_while_disabled()
    {
        //the app registers the hook once at launch and the role switch only flips Enabled, so a gate armed later must still pause
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly("clean")) { Enabled = false };   //the launch role has no checkpoints, so the gate starts disabled
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);

        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m first\"}");
        await loop.RunTurnAsync(convo, "before /role", obs, default);
        Assert.Equal(0, prompter.CallCount);   //the generalist role has no checkpoints, so nothing pauses.
        Assert.True(spy.Executed);

        gate.Enabled = true;   //the coder role chosen through /role enables the checkpoint

        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m second\"}");
        await loop.RunTurnAsync(convo, "after /role", obs, default);
        Assert.Equal(1, prompter.CallCount);   //once enabled, the gate pauses on the next commit.
    }

    [Fact]
    public async Task Checkpoint_approval_satisfies_permission_gate_only_one_prompt()
    {
        //with both gates registered and no stored grants, a commit must ask exactly once, at the checkpoint pause. the permission gate consumes the same latch
        var approval = new CheckpointApproval();
        var prompter = new FakePrompter(PermissionAnswer.Once);   //one answer is scripted and must go to the checkpoint pause
        var checkpoint = new CheckpointGate(prompter, ReadOnly("clean"), approval);
        var store = PermissionStore.InMemory(Directory.CreateTempSubdirectory("gatto-cg-").FullName);
        var permGate = new PermissionGate(store, prompter, autoYes: false, approval: approval);
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, checkpoint.CheckAsync);   //hooks run in the order they were added, so the checkpoint sees the call first
        hooks.On(HookEvent.ToolCall, permGate.CheckAsync);     //hooks run in the order they were added, so the permission gate runs after the checkpoint
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal(1, prompter.CallCount);                     //one ask in total means the permission gate asked nothing after the checkpoint pause
        Assert.Equal("checkpoint", prompter.Requests.Single().Tool);   //the single request came from the checkpoint pause.
        Assert.True(spy.Executed);                               //both gates passed the call through, so the tool ran
        Assert.False(obs.Results.Single().Item2.IsError);
    }

    [Fact]
    public async Task Checkpoint_latch_is_single_use_the_next_uncovered_shell_call_prompts()
    {
        var approval = new CheckpointApproval();
        var prompter = new FakePrompter(PermissionAnswer.Once, PermissionAnswer.Deny);
        var checkpoint = new CheckpointGate(prompter, ReadOnly("clean"), approval);
        var store = PermissionStore.InMemory(Directory.CreateTempSubdirectory("gatto-cg-").FullName);
        var permGate = new PermissionGate(store, prompter, autoYes: false, approval: approval);
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, checkpoint.CheckAsync);
        hooks.On(HookEvent.ToolCall, permGate.CheckAsync);
        var spy = new SpyTool("shell");
        var (loop, client, convo, obs) = LoopWith(hooks, spy);

        //turn 1, the commit pauses once and its approval reaches the permission gate through the latch
        EnqueueOneCall(client, "shell", "{\"command\":\"git commit -m x\"}");
        await loop.RunTurnAsync(convo, "go", obs, default);
        Assert.Equal(1, prompter.CallCount);

        //turn 2, the latch is consumed, so the permission gate must prompt on its own for a non-commit call
        EnqueueOneCall(client, "shell", "{\"command\":\"rm -rf x\"}");
        await loop.RunTurnAsync(convo, "again", obs, default);
        Assert.Equal(2, prompter.CallCount);                     //this second ask came from the permission gate
        Assert.Equal("shell", prompter.Requests[1].Tool);        //the permission gate names the shell tool in its request, so this one is the permission gate asking
        Assert.True(obs.Results[^1].Item2.IsError);              //the scripted deny turned the result into an error
    }

    [Fact]
    public async Task Wild_on_skips_the_pause_even_while_enabled_and_wild_off_restores_it()
    {
        var wild = new WildState { On = true };
        var prompter = new FakePrompter(PermissionAnswer.Once);
        var gate = new CheckpointGate(prompter, ReadOnly(""), approval: null, wild: wild) { Enabled = true };
        var payload = new HookPayload(Call: new ToolCall("c1", "shell", "{\"command\":\"git commit -m \\\"x\\\"\"}"));

        await gate.CheckAsync(payload);            //in wild mode the gate does not pause.
        Assert.Equal(0, prompter.CallCount);

        wild.On = false;
        await gate.CheckAsync(payload);            //with wild off and the gate enabled, it pauses again.
        Assert.Equal(1, prompter.CallCount);
    }
}
