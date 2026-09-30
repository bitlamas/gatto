using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a tool that records whether it ran and what it returns, so the gate and the nudge can be checked against the real result
file sealed class SpyTool(string name, ToolResult? returns = null) : ITool
{
    public bool Executed { get; private set; }
    public string Name => name;
    public string Description => "spy";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        Executed = true;
        return Task.FromResult(returns ?? new ToolResult("ran"));
    }
}

public sealed class GroundingGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-grounding-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    //a paste-service brief that pins the extraction, pass and fail cases the grounding rule must reproduce.
    private const string DropNoteBrief = """
        # DropNote -- a minimal paste service

        Build a paste service in vanilla PHP 8.4 + MariaDB. Endpoints:
        - `POST /api/paste` accepts JSON {content, expires_in} and returns a base62 short code.
        - `GET /p/{code}` returns the paste, or 404 if expired.
        Pastes expire; store `expires_at` as UTC and reject reads after it. Rate-limit to 10 per minute per IP.
        Short codes are base62 of an auto-increment id. Return 301 for a renamed paste.
        """;

    private const string GoodRestate =
        "Build DropNote, a paste service in PHP 8.4 and MariaDB. POST /api/paste takes content and expires_in " +
        "and returns a base62 short code from an auto-increment id; GET /p/{code} returns the paste or 404 when " +
        "expired. Store expires_at in UTC, rate-limit per IP, and return 301 for a renamed paste.";

    private const string LingRestate =
        "The task is to build a flashcard study app with spaced repetition. Users create decks of cards, " +
        "review them on a schedule, and the app tracks correct answers to schedule the next review. " +
        "I will implement deck creation, a review queue, and a scoring algorithm.";

    [Fact]
    public void ExtractAnchors_pulls_the_identifying_tokens_from_the_brief()
    {
        var anchors = Grounding.ExtractAnchors(DropNoteBrief);
        Assert.Contains(anchors, a => a.Contains("DropNote", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(anchors, a =>
            a.Contains("base62", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("expires_at", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("paste", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractAnchors_drops_common_words_and_short_tokens()
    {
        var anchors = Grounding.ExtractAnchors("The task is to Write and Read a File with Tests.");
        //every word of the input is a stop word or under three characters, so no anchor survives
        Assert.DoesNotContain(anchors, a => a.Equals("task", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(anchors, a => a.Equals("file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractAnchors_caps_at_max()
    {
        var many = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"`IdentifierNumber{i}`"));
        Assert.True(Grounding.ExtractAnchors(many, max: 12).Count <= 12);
    }

    [Fact]
    public void ExtractAnchors_empty_brief_yields_no_anchors()
    {
        Assert.Empty(Grounding.ExtractAnchors(""));
    }

    private const string BriefWithFencedTree = """
        # ProjectPlan -- restructure the widget pipeline

        Read the `WidgetFactory` module and update `pipeline_config.json` accordingly.

        ```
        repo/
        +-- src/
        |   +-- widgets/
        |       +-- `factory.ts`
        |   +-- `pipeline_config.json`
        +-- README.md
        ```

        The `RenderQueue` component must stay unaffected.
        """;

    [Fact]
    public void ExtractAnchors_strips_fenced_code_blocks_before_extraction()
    {
        var anchors = Grounding.ExtractAnchors(BriefWithFencedTree);

        //no anchor may span a newline, or fence backticks on different lines would pair into one large anchor
        Assert.DoesNotContain(anchors, a => a.Contains('\n') || a.Contains('\r'));

        //content inside the fence yields no anchor, neither the tree characters nor the backticked names
        Assert.DoesNotContain(anchors, a => a.Contains("repo/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(anchors, a => a.Equals("factory.ts", StringComparison.OrdinalIgnoreCase));

        //keep a known match outside the fence, so the negative checks above can fail
        Assert.Contains(anchors, a => a.Contains("WidgetFactory", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(anchors, a => a.Contains("RenderQueue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractAnchors_backtick_span_regex_does_not_cross_lines()
    {
        var anchors = Grounding.ExtractAnchors("`alpha`\ntext\n`beta`");
        Assert.Contains(anchors, a => a.Equals("alpha", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(anchors, a => a.Equals("beta", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(anchors, a => a.Contains('\n'));
    }

    [Fact]
    public void ExtractAnchors_drops_a_backticked_span_over_sixty_chars()
    {
        var longSpan = new string('X', 61);
        var anchors = Grounding.ExtractAnchors($"`{longSpan}` and also `ShortAnchor`");
        Assert.DoesNotContain(anchors, a => a.Length > 60);
        Assert.Contains(anchors, a => a.Equals("ShortAnchor", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] FourAnchors = { "Alpha", "Bravo", "Charlie", "Delta" };

    [Fact]
    public void CheckRestatement_exactly_half_matched_passes()   //the threshold is inclusive, so exactly two of four passes.
    {
        var r = Grounding.CheckRestatement("we handle Alpha and Bravo only", FourAnchors);
        Assert.Equal(2, r.K);
        Assert.Equal(4, r.N);
        Assert.True(r.Passed);
    }

    [Fact]
    public void CheckRestatement_just_below_half_fails()
    {
        var five = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo" };
        var r = Grounding.CheckRestatement("Alpha and Bravo", five);   //two of five is forty percent, just under the threshold.
        Assert.Equal(2, r.K);
        Assert.False(r.Passed);
    }

    [Fact]
    public void CheckRestatement_is_case_insensitive()
    {
        var r = Grounding.CheckRestatement("ALPHA bRaVo CHARLIE delta", FourAnchors);
        Assert.Equal(4, r.K);
        Assert.True(r.Passed);
    }

    [Fact]
    public void CheckRestatement_reports_missing_anchors()
    {
        var r = Grounding.CheckRestatement("only Alpha here", FourAnchors);
        Assert.Equal(new[] { "Alpha" }, r.Matched);
        Assert.Equal(new[] { "Bravo", "Charlie", "Delta" }, r.Missing);
    }

    [Fact]
    public void CheckRestatement_small_brief_under_four_anchors_always_passes()
    {
        var r = Grounding.CheckRestatement("something totally unrelated", new[] { "Alpha", "Bravo", "Charlie" });
        Assert.False(r.Matched.Any());
        Assert.True(r.Passed);   //a brief with fewer than four anchors always passes, so it can never block a real deliverable.
    }

    [Fact]
    public void CheckRestatement_zero_anchors_passes_with_zero_of_zero()
    {
        var r = Grounding.CheckRestatement("anything at all", Array.Empty<string>());
        Assert.Equal(0, r.K);
        Assert.Equal(0, r.N);
        Assert.True(r.Passed);
    }

    [Fact]
    public void CheckRestatement_reproduces_v1_DropNote_edges()
    {
        var anchors = Grounding.ExtractAnchors(DropNoteBrief);
        Assert.True(anchors.Count >= 4);                                    //the pass needs at least four anchors, or the check below would judge nothing.
        Assert.False(Grounding.CheckRestatement(LingRestate, anchors).Passed);   //a restatement of another task must fail, so the pass below cannot come from a check that always passes.
        Assert.True(Grounding.CheckRestatement(GoodRestate, anchors).Passed);    //the faithful restatement must pass, so the pair shows the rule separates the two.
    }

    private ITool RestateTool(out GroundingGate gate)
    {
        gate = new GroundingGate();
        return gate.RestateTool;
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Restate_tool_is_named_snake_case()
    {
        Assert.Equal("task_restate", new GroundingGate().RestateTool.Name);
    }

    [Fact]
    public async Task Restate_missing_brief_file_param_throws_a_validation_error()
    {
        var tool = RestateTool(out _);
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(
            Args(new { task = "x", deliverables = new[] { "a" }, acceptance = new[] { "b" }, out_of_scope = new[] { "c" } }),
            new TestToolContext(_dir), default));
    }

    [Fact]
    public async Task Restate_missing_array_param_throws_a_validation_error()
    {
        var tool = RestateTool(out _);
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(
            Args(new { brief_file = "b.md", task = "x", acceptance = new[] { "b" }, out_of_scope = new[] { "c" } }),
            new TestToolContext(_dir), default));
    }

    [Fact]
    public async Task Restate_missing_brief_FILE_is_a_non_fatal_result_not_a_throw()
    {
        var tool = RestateTool(out var gate);
        var r = await tool.ExecuteAsync(
            Args(new { brief_file = "nope.md", task = "x", deliverables = new[] { "a" }, acceptance = new[] { "b" }, out_of_scope = new[] { "c" } }),
            new TestToolContext(_dir), default);
        Assert.False(r.IsError);
        Assert.Equal("task_restate: brief_file not found: nope.md", r.Text);
        Assert.False(gate.IsGrounded);
    }

    [Fact]
    public async Task Restate_pass_sets_the_grounded_latch_and_returns_GROUNDED_text()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "brief.md"), DropNoteBrief);
        var tool = RestateTool(out var gate);
        var r = await tool.ExecuteAsync(
            Args(new { brief_file = "brief.md", task = GoodRestate, deliverables = Array.Empty<string>(), acceptance = Array.Empty<string>(), out_of_scope = Array.Empty<string>() }),
            new TestToolContext(_dir), default);
        Assert.False(r.IsError);
        Assert.True(gate.IsGrounded);
        Assert.StartsWith("GROUNDED (", r.Text);
        Assert.Contains("anchors matched). Restatement accepted -- proceed with the work you described.", r.Text);
    }

    [Fact]
    public async Task Restate_fail_returns_the_exact_failure_text_and_does_not_ground()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "brief.md"), DropNoteBrief);
        var tool = RestateTool(out var gate);
        var r = await tool.ExecuteAsync(
            Args(new { brief_file = "brief.md", task = LingRestate, deliverables = Array.Empty<string>(), acceptance = Array.Empty<string>(), out_of_scope = Array.Empty<string>() }),
            new TestToolContext(_dir), default);
        Assert.False(r.IsError);                                     //a failed restatement is feedback the model can act on, so it does not come back as a tool error
        Assert.False(gate.IsGrounded);
        Assert.Contains("of the brief's key terms (brief.md).", r.Text);   //the message names the file by its basename
        Assert.Contains("You may be answering a different task than the one asked.", r.Text);
        Assert.Contains("Missing terms:", r.Text);
        Assert.Contains("Re-read brief.md carefully and call task_restate again.", r.Text);
    }

    [Fact]
    public async Task Restate_fail_truncates_the_missing_list_to_eight()
    {
        var brief = string.Join(" ", "Zulu Yankee Xray Whiskey Victor Uniform Tango Sierra Romeo Quebec"
            .Split(' ').Select(w => $"`{w}Marker`"));   //ten anchors in the brief, more than the message may show
        await File.WriteAllTextAsync(Path.Combine(_dir, "brief.md"), brief);
        var tool = RestateTool(out _);
        var r = await tool.ExecuteAsync(
            Args(new { brief_file = "brief.md", task = "nothing relevant appears in this restatement", deliverables = Array.Empty<string>(), acceptance = Array.Empty<string>(), out_of_scope = Array.Empty<string>() }),
            new TestToolContext(_dir), default);

        var segment = r.Text.Split("Missing terms: ")[1].Split(". Re-read")[0];
        Assert.Equal(8, segment.Split(", ").Length);
    }

    [Fact]
    public async Task Restate_absolute_brief_path_is_used_as_is()
    {
        var abs = Path.Combine(_dir, "brief.md");
        await File.WriteAllTextAsync(abs, DropNoteBrief);
        var tool = RestateTool(out var gate);
        //the working directory is elsewhere, so only the absolute path can resolve.
        var r = await tool.ExecuteAsync(
            Args(new { brief_file = abs, task = GoodRestate, deliverables = Array.Empty<string>(), acceptance = Array.Empty<string>(), out_of_scope = Array.Empty<string>() }),
            new TestToolContext(Path.GetTempPath()), default);
        Assert.True(gate.IsGrounded);
    }

    [Fact]
    public async Task Restate_empty_brief_grounds_with_zero_of_zero()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "brief.md"), "");   //an empty brief yields no anchors, so the fewer-than-four guard passes it.
        var tool = RestateTool(out var gate);
        var r = await tool.ExecuteAsync(
            Args(new { brief_file = "brief.md", task = "whatever", deliverables = Array.Empty<string>(), acceptance = Array.Empty<string>(), out_of_scope = Array.Empty<string>() }),
            new TestToolContext(_dir), default);
        Assert.True(gate.IsGrounded);
        Assert.StartsWith("GROUNDED (0/0 anchors matched).", r.Text);
    }

    private static HookPayload ResultOf(string tool, ToolResult result) =>
        new(Call: new ToolCall("c1", tool, "{}"), Result: result);

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    public async Task Nudge_appends_reminder_to_a_deliverable_result_while_ungrounded(string tool)
    {
        var gate = new GroundingGate();
        var rewritten = await gate.NudgeToolResultAsync(ResultOf(tool, new ToolResult("wrote 3 bytes")));
        Assert.NotNull(rewritten);
        Assert.Equal("wrote 3 bytes\n\n" + GroundingGate.NudgeReminder, rewritten!.Text);
    }

    [Fact]
    public async Task Nudge_leaves_a_deliverable_result_untouched_once_grounded()
    {
        var gate = new GroundingGate();
        gate.MarkGrounded();
        var rewritten = await gate.NudgeToolResultAsync(ResultOf("write_file", new ToolResult("wrote 3 bytes")));
        Assert.Null(rewritten);   //a null return means the tool result stays unchanged.
    }

    [Fact]
    public async Task Nudge_ignores_a_non_deliverable_tool_grounded_or_not()
    {
        var gate = new GroundingGate();
        Assert.Null(await gate.NudgeToolResultAsync(ResultOf("read_file", new ToolResult("contents"))));
        gate.MarkGrounded();
        Assert.Null(await gate.NudgeToolResultAsync(ResultOf("read_file", new ToolResult("contents"))));
    }

    [Fact]
    public async Task Nudge_still_fires_on_an_already_error_deliverable_result()
    {
        var gate = new GroundingGate();
        var rewritten = await gate.NudgeToolResultAsync(ResultOf("write_file", new ToolResult("permission denied", IsError: true)));
        Assert.NotNull(rewritten);
        Assert.True(rewritten!.IsError);   //the nudge must keep the original error flag.
        Assert.EndsWith(GroundingGate.NudgeReminder, rewritten.Text);
    }

    [Fact]
    public async Task Reset_re_arms_the_latch()
    {
        var gate = new GroundingGate();
        gate.MarkGrounded();
        Assert.True(gate.IsGrounded);
        gate.Reset();
        Assert.False(gate.IsGrounded);   //the /new and /compact commands call this reset
    }

    [Fact]
    public async Task Armed_False_SuppressesTheNudge_EvenWhileUngrounded()
    {
        //switching away from a grounding role flips Armed off. the tool stays registered, since no hook can be unregistered, so only the nudge falls silent
        var gate = new GroundingGate { Armed = false };
        var rewritten = await gate.NudgeToolResultAsync(ResultOf("write_file", new ToolResult("wrote 3 bytes")));
        Assert.Null(rewritten);
    }

    [Fact]
    public void Armed_DefaultsTrue()
    {
        Assert.True(new GroundingGate().Armed);
    }

    private (AgentLoop, FakeChatClient, Conversation, RecordingObserver) LoopWith(HookBus hooks, string cwd, params ITool[] tools)
    {
        var client = new FakeChatClient();
        var reg = new ToolRegistry();
        foreach (var t in tools) reg.Register(t);
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(cwd), "m");
        return (loop, client, new Conversation(null), new RecordingObserver());
    }

    private static void EnqueueOneCall(FakeChatClient client, string tool, string argsJson)
    {
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", tool, argsJson)),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));
    }

    [Fact]
    public async Task Gate_never_blocks_an_ungrounded_deliverable_still_runs_and_the_nudge_reaches_the_model()
    {
        var gate = new GroundingGate();
        var hooks = new HookBus();
        hooks.OnToolResult(gate.NudgeToolResultAsync);
        var spy = new SpyTool("write_file");
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, spy);
        EnqueueOneCall(client, "write_file", "{\"path\":\"a.txt\",\"content\":\"x\"}");

        await loop.RunTurnAsync(convo, "implement the feature", obs, default);

        Assert.True(spy.Executed);
        var (_, result) = obs.Results.Single();
        Assert.False(result.IsError);
        Assert.Equal("ran\n\n" + GroundingGate.NudgeReminder, result.Text);   //the reminder must reach the model inside the tool result text.
        var toolMsg = convo.Messages.Single(m => m.Role == "tool");
        Assert.Equal("ran\n\n" + GroundingGate.NudgeReminder, toolMsg.Content);   //the conversation must store the nudged text, so later turns see the same thing.
    }

    [Fact]
    public async Task Two_deliverable_calls_in_one_turn_are_both_nudged()
    {
        var gate = new GroundingGate();
        var hooks = new HookBus();
        hooks.OnToolResult(gate.NudgeToolResultAsync);
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, new SpyTool("write_file"), new SpyTool("edit_file"));
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "write_file", "{}")),
            new StreamEvent.ToolCallReady(new ToolCall("c2", "edit_file", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));

        await loop.RunTurnAsync(convo, "do both", obs, default);

        Assert.Equal(2, obs.Results.Count);
        Assert.All(obs.Results, r => Assert.EndsWith(GroundingGate.NudgeReminder, r.Item2.Text));
    }

    [Fact]
    public async Task A_non_deliverable_result_is_not_nudged_through_the_loop()
    {
        var gate = new GroundingGate();
        var hooks = new HookBus();
        hooks.OnToolResult(gate.NudgeToolResultAsync);
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, new SpyTool("read_file"));
        EnqueueOneCall(client, "read_file", "{}");

        await loop.RunTurnAsync(convo, "read something", obs, default);

        Assert.Equal("ran", obs.Results.Single().Item2.Text);
    }

    [Fact]
    public async Task Grounding_via_the_tool_then_a_later_deliverable_is_not_nudged()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "brief.md"), DropNoteBrief);
        var gate = new GroundingGate();
        var hooks = new HookBus();
        hooks.OnToolResult(gate.NudgeToolResultAsync);
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, gate.RestateTool, new SpyTool("write_file"));

        var restateArgs = JsonSerializer.Serialize(new
        {
            brief_file = "brief.md",
            task = GoodRestate,
            deliverables = Array.Empty<string>(),
            acceptance = Array.Empty<string>(),
            out_of_scope = Array.Empty<string>(),
        });
        EnqueueOneCall(client, "task_restate", restateArgs);
        await loop.RunTurnAsync(convo, "restate first", obs, default);
        Assert.True(gate.IsGrounded);

        EnqueueOneCall(client, "write_file", "{}");
        await loop.RunTurnAsync(convo, "now write", obs, default);
        Assert.Equal("ran", obs.Results.Last().Item2.Text);
    }

    [Fact]
    public async Task Arming_mid_session_the_next_deliverable_is_nudged_even_though_the_hook_was_registered_while_disarmed()
    {
        //the gate and its hook are always constructed and registered, and a role change flips Armed alone. a gate that starts disarmed must still nudge once armed
        var gate = new GroundingGate { Armed = false };   //start disarmed, as a launch role without the grounding gate does.
        var hooks = new HookBus();
        hooks.OnToolResult(gate.NudgeToolResultAsync);
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, new SpyTool("write_file"));

        EnqueueOneCall(client, "write_file", "{}");
        await loop.RunTurnAsync(convo, "before /role", obs, default);
        Assert.Equal("ran", obs.Results.Single().Item2.Text);   //while disarmed the result stays plain.

        gate.Armed = true;   //a role change arms the gate.

        EnqueueOneCall(client, "write_file", "{}");
        await loop.RunTurnAsync(convo, "after /role", obs, default);
        Assert.Equal("ran\n\n" + GroundingGate.NudgeReminder, obs.Results.Last().Item2.Text);   //once armed, the next deliverable gets the reminder.
    }

    [Fact]
    public async Task Disarming_mid_session_the_nudge_falls_silent_without_unregistering_the_hook()
    {
        var gate = new GroundingGate { Armed = true };   //start armed, as a launch role with the grounding gate does.
        var hooks = new HookBus();
        hooks.OnToolResult(gate.NudgeToolResultAsync);
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, new SpyTool("write_file"));

        EnqueueOneCall(client, "write_file", "{}");
        await loop.RunTurnAsync(convo, "before /role", obs, default);
        Assert.Equal("ran\n\n" + GroundingGate.NudgeReminder, obs.Results.Single().Item2.Text);   //while armed, the deliverable gets the reminder.

        gate.Armed = false;   //a role change disarms the gate.

        EnqueueOneCall(client, "write_file", "{}");
        await loop.RunTurnAsync(convo, "after /role", obs, default);
        Assert.Equal("ran", obs.Results.Last().Item2.Text);
    }

    [Fact]
    public async Task Fail_open_a_throwing_tool_result_handler_leaves_the_result_unmodified_and_the_session_runs()
    {
        var hooks = new HookBus();
        Exception? reported = null;
        hooks.OnHandlerError += (_, ex) => reported = ex;
        hooks.OnToolResult(_ => throw new InvalidOperationException("buggy nudge"));
        var spy = new SpyTool("write_file");
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, spy);
        EnqueueOneCall(client, "write_file", "{}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.True(spy.Executed);
        Assert.Equal("ran", obs.Results.Single().Item2.Text);
        Assert.Equal("buggy nudge", reported!.Message);
    }

    [Fact]
    public async Task Chained_tool_result_handlers_each_see_the_previous_ones_output()
    {
        var hooks = new HookBus();
        hooks.OnToolResult(p => Task.FromResult<ToolResult?>(p.Result! with { Text = p.Result!.Text + "-A" }));
        hooks.OnToolResult(p => Task.FromResult<ToolResult?>(p.Result! with { Text = p.Result!.Text + "-B" }));
        var (loop, client, convo, obs) = LoopWith(hooks, _dir, new SpyTool("write_file"));
        EnqueueOneCall(client, "write_file", "{}");

        await loop.RunTurnAsync(convo, "go", obs, default);

        Assert.Equal("ran-A-B", obs.Results.Single().Item2.Text);
    }
}
