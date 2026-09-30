using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//only the model is fake, the runner and tools run in a scratch sandbox. the fake reads the prompt, which holds a random token no scripted queue can know
[Collection("e2e")]   //the clocks here are real, so this class runs apart from the rest of the suite (a busy thread pool reads as a stalled model)
public class AuditionRunnerTests
{
    private enum Behaviour { Competent, ProseOnly, FabricatesOnB5 }

    //read the task from the prompt, call the right tool and answer from what the tool returned.
    private sealed class ModelSim(Behaviour how, TimeSpan? delay = null) : IChatClient
    {
        public int Turns;

        //the user messages each request carried, a history leaked from the task before shows as two
        public List<int> UserMessages { get; } = [];

        //timings payloads to emit, one per round in a cycle, and a null entry or empty list means the server reported none
        public IReadOnlyList<string>? Timings { get; init; }

        //reasoning deltas before the work of each round, spaced by Pace (the model stays slow but healthy)
        public int Filler { get; init; }

        //the gap between filler deltas, whose reciprocal is the token rate of the fake.
        public TimeSpan Pace { get; init; } = TimeSpan.FromMilliseconds(10);

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Turns++;
            UserMessages.Add(request.Messages.Count(m => m.Role == "user"));
            if (delay is { } d) await Task.Delay(d, ct);

            for (var i = 0; i < Filler; i++)
            {
                await Task.Delay(Pace, ct);
                yield return new StreamEvent.ReasoningDelta("thinking ");
            }

            var prompt = request.Messages.First(m => m.Role == "user").Content ?? "";
            var results = request.Messages.Where(m => m.Role == "tool").ToList();
            var task = prompt.Contains("notes.txt") ? "B1"
                : prompt.Contains("out.txt") ? "B2"
                : prompt.Contains("AUDITION-MARKER-") ? "B3"
                : prompt.Contains("a.txt") ? "B4" : "B5";

            if (how == Behaviour.ProseOnly)
            {
                yield return new StreamEvent.TextDelta("I believe the answer is 1234.");
                yield return new StreamEvent.Finished("stop", null);
                yield break;
            }

            foreach (var e in Act(task, prompt, results))
                yield return e is StreamEvent.Finished f && Timings is { Count: > 0 } t
                    ? new StreamEvent.Finished(f.FinishReason, f.Usage, t[(Turns - 1) % t.Count])
                    : e;
        }

        private IEnumerable<StreamEvent> Act(string task, string prompt, List<ChatMessage> results)
        {
            switch (task)
            {
                case "B1" when results.Count == 0:
                    yield return Call("c1", "read_file", new { path = "notes.txt" }); break;
                case "B1":
                    yield return Text($"The code is {After(results[0].Content, "code: ")}."); break;

                case "B2" when results.Count == 0:
                    yield return Call("c1", "write_file",
                        new { path = "out.txt", content = After(prompt, "exactly: ") }); break;
                case "B2":
                    yield return Text("Written."); break;

                case "B3" when results.Count == 0:
                    yield return Call("c1", "shell", new { command = "echo " + Marker(prompt) }); break;
                case "B3":
                    yield return Text($"It printed {Marker(prompt)}."); break;

                case "B4" when results.Count == 0:
                    yield return Call("c1", "read_file", new { path = "a.txt" }); break;
                case "B4" when results.Count == 1:
                    yield return Call("c2", "write_file", new
                    {
                        path = "b.txt",
                        content = (int.Parse(results[0].Content!.Trim()) * 2).ToString(),
                    }); break;
                case "B4":
                    yield return Text("Done."); break;

                case "B5" when results.Count == 0:
                    yield return Call("c1", "read_file", new { path = "missing.txt" }); break;
                case "B5" when how == Behaviour.FabricatesOnB5:
                    //report content for a file that has none, which must disqualify the model.
                    yield return Text("""The file says "code: 4417" — that is the value."""); break;
                case "B5":
                    yield return Text("I could not read it: that file is missing."); break;
            }
            yield return new StreamEvent.Finished("stop", null);
        }

        private static StreamEvent Call(string id, string tool, object args) =>
            new StreamEvent.ToolCallReady(new ToolCall(id, tool, JsonSerializer.Serialize(args)));
        private static StreamEvent Text(string t) => new StreamEvent.TextDelta(t);
        private static string After(string? s, string marker) =>
            (s ?? "")[((s ?? "").IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim()
                .Split('\n')[0].Trim();
        private static string Marker(string prompt) => After(prompt, "echo ");
    }

    private static AuditionStamp Stamp => new("v0.4.0-test", "model.gguf", "Q4_K_M", 8192, "temp 0.7");

    private static Task<AuditionVerdict> Run(Behaviour how, TimeSpan? delay = null,
        CancellationToken ct = default) =>
        AuditionRunner.RunBatteryAsync(new ModelSim(how, delay), Path.GetTempPath(), "m", Stamp, null, ct);

    [Fact]
    public async Task A_model_that_actually_uses_its_tools_passes_all_five()
    {
        var v = await Run(Behaviour.Competent);

        Assert.True(v.Pass);
        Assert.False(v.Disqualified);
        Assert.Equal(5, v.Tasks.Count(t => t.Pass));
        Assert.All(v.Tasks, t => Assert.Empty(t.Shapes));
        Assert.Equal(["B1", "B2", "B3", "B4", "B5"], v.Tasks.Select(t => t.TaskId));
    }

    [Fact]
    public async Task A_model_that_only_talks_fails_with_NoToolCall()
    {
        var v = await Run(Behaviour.ProseOnly);

        Assert.False(v.Pass);
        Assert.Equal(0, v.Tasks.Count(t => t.Pass));
        //only tasks that ran can hold a shape, the run stops once the verdict is decided
        Assert.All(v.Tasks.Where(t => !t.Skipped),
            t => Assert.Contains(FailureShape.NoToolCall, t.Shapes));
    }

    [Fact]
    public async Task FABRICATION_DISQUALIFIES_even_though_the_other_four_tasks_passed()
    {
        //a model that fabricates file content must not pass even at the threshold, a new user cannot detect the fabrication
        var v = await Run(Behaviour.FabricatesOnB5);

        Assert.Equal(4, v.Tasks.Count(t => t.Pass));       //the model reaches the pass threshold.
        Assert.True(v.Disqualified);
        Assert.False(v.Pass);                              //the model is still not verified.
        Assert.Contains(FailureShape.FabricatedResult, v.Tasks.Single(t => t.TaskId == "B5").Shapes);
    }

    [Fact]
    public async Task Every_task_gets_a_CLEAN_session_with_no_inherited_history()
    {
        //each task starts with no history from the one before, so every request carries exactly one user message
        var sim = new ModelSim(Behaviour.Competent);
        await AuditionRunner.RunBatteryAsync(sim, Path.GetTempPath(), "m", Stamp, null, default);

        Assert.True(sim.Turns >= 10);   //five tasks, each with at least a call round and an answer round.
        Assert.All(sim.UserMessages, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task A_mutating_tool_runs_WITHOUT_a_prompter_because_denials_would_be_graded_as_model_failures()
    {
        //the runner must approve mutating tools, headless mode denies them and a denial would be graded as a model failure
        var v = await Run(Behaviour.Competent);

        Assert.True(v.Tasks.Single(t => t.TaskId == "B2").Pass);
        Assert.True(v.Tasks.Single(t => t.TaskId == "B4").Pass);
    }

    [Fact]
    public async Task The_users_own_permission_store_is_never_touched()
    {
        //the permission store is rooted at the scratch directory, so the real store of the user stays untouched
        var home = Path.Combine(Path.GetTempPath(), "gatto-audhome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            await AuditionRunner.RunBatteryAsync(new ModelSim(Behaviour.Competent), home, "m",
                Stamp, null, default);
            Assert.False(File.Exists(Path.Combine(home, ".gatto", "permissions.json")));
            Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".gatto")));
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    //generate text and never converge, the fake must honour the cancellation token or the suite hangs
    private sealed class RunawaySim : IChatClient
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new StreamEvent.TextDelta("and ");
            }
        }
    }

    //send before tokens and then stay silent like a hung server, and a before of zero tests the wait for a first token
    private sealed class StallSim(int before) : IChatClient
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < before; i++)
            {
                await Task.Yield();
                yield return new StreamEvent.TextDelta("hm ");
            }
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    //limits small enough to cut in test time, with the same structure as the shipped limits.
    private static TaskLimits Limits(int cap = 50, int firstTokenMs = 2000, int stallMs = 200) =>
        new(cap, TimeSpan.FromMilliseconds(firstTokenMs), TimeSpan.FromMilliseconds(stallMs));

    //use 30 s windows so only the cap cuts the fake, and a broken cap still fails as a missing StoppedAtCap
    private static TaskLimits CapLimits => Limits(cap: 50, firstTokenMs: 30_000, stallMs: 30_000);

    [Fact]
    public async Task A_MODEL_THAT_NEVER_STOPS_GENERATING_IS_CUT_AT_THE_TOKEN_CAP()
    {
        //the shape must name the cap, the reason must reach the report, and a classification nothing renders is not evidence
        var verdict = await AuditionRunner.RunBatteryAsync(
            new RunawaySim(), Path.GetTempPath(), "m", Stamp, null, CancellationToken.None,
            taskLimits: CapLimits);

        var ran = verdict.Tasks.Where(t => !t.Skipped).ToList();
        Assert.NotEmpty(ran);
        Assert.All(ran, t => Assert.Contains(FailureShape.StoppedAtCap, t.Shapes));
        Assert.All(ran, t => Assert.DoesNotContain(FailureShape.Stalled, t.Shapes));
        Assert.False(verdict.Pass);
        Assert.Contains("still generating", AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode));
    }

    [Fact]
    public async Task A_SERVER_THAT_GOES_QUIET_IS_CUT_BY_THE_STALL_BACKSTOP_and_says_so()
    {
        //a hung server gets its own shape, saying still generating would send the user after the wrong problem
        var verdict = await AuditionRunner.RunBatteryAsync(
            new StallSim(before: 3), Path.GetTempPath(), "m", Stamp, null, CancellationToken.None,
            taskLimits: Limits(cap: 50, stallMs: 150));

        var ran = verdict.Tasks.Where(t => !t.Skipped).ToList();
        Assert.NotEmpty(ran);
        Assert.All(ran, t => Assert.Contains(FailureShape.Stalled, t.Shapes));
        Assert.All(ran, t => Assert.DoesNotContain(FailureShape.StoppedAtCap, t.Shapes));

        //record the silence that expired, this model spoke so the gap between tokens ran out
        Assert.All(ran, t => Assert.Equal(TimeSpan.FromMilliseconds(150), t.Silence));
        Assert.Contains("nothing came back", AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode));
    }

    [Fact]
    public async Task A_MODEL_THAT_NEVER_SPEAKS_AT_ALL_GETS_THE_GENEROUS_FIRST_TOKEN_ALLOWANCE()
    {
        //a wait for a first token is not silence, a cold device compiles GPU pipelines before it speaks
        var verdict = await AuditionRunner.RunBatteryAsync(
            new StallSim(before: 0), Path.GetTempPath(), "m", Stamp, null, CancellationToken.None,
            taskLimits: Limits(cap: 50, firstTokenMs: 400, stallMs: 50));

        var first = verdict.Tasks.First();
        Assert.Contains(FailureShape.Stalled, first.Shapes);
        Assert.Equal(TimeSpan.FromMilliseconds(400), first.Silence);
    }

    [Fact]
    public async Task A_SLOW_BUT_HEALTHY_MODEL_IS_NOT_CUT_and_the_flat_clock_used_to_kill_it()
    {
        //a slow but competent model must pass, a wall-clock budget measures the machine
        var slowButFine = new ModelSim(Behaviour.Competent)
        {
            Filler = 40,                                  //40 tokens of thinking in each round.
            Pace = TimeSpan.FromMilliseconds(10),         //about 100 tokens a second, so about 400 ms in each round.
        };

        var verdict = await AuditionRunner.RunBatteryAsync(
            slowButFine, Path.GetTempPath(), "m", Stamp, null, CancellationToken.None,
            taskLimits: Limits(cap: 2_000, firstTokenMs: 2_000, stallMs: 400));

        Assert.True(verdict.Pass, "a model that converges on every task must not fail for being slow");
        Assert.All(verdict.Tasks, t => Assert.Empty(t.Shapes));
    }

    [Fact]
    public async Task ONCE_THE_BADGE_IS_UNREACHABLE_THE_REST_IS_NOT_RUN_and_is_REPORTED_not_run()
    {
        //stop once the badge is unreachable, the rest cannot change the verdict. assert the turn count too, a label saying not run can sit beside work that ran
        var sim = new ModelSim(Behaviour.ProseOnly);
        var verdict = await AuditionRunner.RunBatteryAsync(
            sim, Path.GetTempPath(), "m", Stamp, null, CancellationToken.None);

        Assert.Equal(2, sim.Turns);                                   //after two failures, four passes out of five are unreachable.
        Assert.Equal(Battery.V1.Count, verdict.Tasks.Count);          //the verdict still holds a row for every task.
        Assert.Equal(2, verdict.Tasks.Count(t => t.Failed));
        Assert.Equal(3, verdict.Tasks.Count(t => t.Skipped));

        //a skipped task holds no shape and no elapsed time, nothing was measured
        foreach (var t in verdict.Tasks.Where(t => t.Skipped))
        {
            Assert.Empty(t.Shapes);
            Assert.Equal(TimeSpan.Zero, t.Elapsed);
            Assert.False(t.Failed);
        }

        var report = AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode);
        Assert.Contains("stopped early", report);
        Assert.Contains("not run", report);

        //the denominator must count only the tasks that ran, of 5 would read as three passes when none passed
        Assert.Contains("failed 2 of the 2 tasks that ran", report);
        Assert.DoesNotContain("of 5 tasks", report);
    }

    [Fact]
    public void THE_SHIPPED_LIMITS_STILL_CLEAR_THE_RUNS_THEY_WERE_DERIVED_FROM()
    {
        //the cap must come from measured runs, so assert on the relation to the worst measured task rather than the digits
        const int worstHealthyTaskEverMeasured = 829;      //the worst healthy task measured, 14.5 s at 57.2 tok/s
        const int theObservedWedge = 9_000;                //the observed runaway task, still generating at 300 s.

        Assert.True(TaskLimits.Default.TokenCap > worstHealthyTaskEverMeasured * 2,
            $"the cap ({TaskLimits.Default.TokenCap}) must leave real headroom over the worst healthy "
            + $"task measured ({worstHealthyTaskEverMeasured} tokens) — under it, converging models fail");
        Assert.True(TaskLimits.Default.TokenCap < theObservedWedge / 2,
            $"the cap ({TaskLimits.Default.TokenCap}) must cut the observed wedge ({theObservedWedge} "
            + "tokens) well before it gets there, or it is not an instrument");

        //the stall is a liveness check, so it must stay well above the slowest healthy inter-token gap of 0.2 s
        Assert.True(TaskLimits.Default.Stall > TimeSpan.FromSeconds(0.2 * 20),
            "a stall backstop near the healthy inter-token gap cuts models that are merely slow");

        //the first-token allowance must stay longer than the stall, or the two limits are one limit with two names
        Assert.True(TaskLimits.Default.FirstToken > TaskLimits.Default.Stall * 2,
            "the first-token allowance must stay generous — a cold device compiles GPU pipelines "
            + "before it can emit a token, and that is not the model's fault");
    }

    [Fact]
    public async Task A_MODEL_THAT_ANSWERS_NORMALLY_WEARS_NEITHER_CUT()
    {
        //a check that only proves the cut fires cannot tell a working backstop from one that always fires.
        var verdict = await AuditionRunner.RunBatteryAsync(
            new ModelSim(Behaviour.Competent), Path.GetTempPath(), "m", Stamp, null,
            CancellationToken.None);

        Assert.All(verdict.Tasks, t => Assert.DoesNotContain(FailureShape.StoppedAtCap, t.Shapes));
        Assert.All(verdict.Tasks, t => Assert.DoesNotContain(FailureShape.Stalled, t.Shapes));
        Assert.DoesNotContain("stopped early", AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode));
        Assert.True(verdict.Pass);
    }

    //emit reasoning and then a tool call in the first round of each task, and record each request for the test to check
    private sealed class ReasoningSim : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add([.. request.Messages]);
            await Task.Yield();

            var alreadyCalled = request.Messages.Any(m => m.Role == "tool");
            if (!alreadyCalled)
            {
                yield return new StreamEvent.ReasoningDelta("thinking about the task at length");
                yield return new StreamEvent.ToolCallReady(
                    new ToolCall("c1", "glob", "{\"pattern\":\"*\"}"));
                yield return new StreamEvent.Finished("tool_calls", null);
                yield break;
            }
            yield return new StreamEvent.TextDelta("done.");
            yield return new StreamEvent.Finished("stop", null);
        }
    }

    [Fact]
    public async Task THE_CHECK_HONOURS_THE_PACKS_OWN_REASONING_POLICY()
    {
        //the check must use the reasoning history setting of the model, a different one measures a configuration the model does not run
        var none = new ReasoningSim();
        await AuditionRunner.RunBatteryAsync(none, Path.GetTempPath(), "m", Stamp, null,
            CancellationToken.None, reasoning: Gatto.Core.Loop.ReasoningHistory.None);

        var all = new ReasoningSim();
        await AuditionRunner.RunBatteryAsync(all, Path.GetTempPath(), "m", Stamp, null,
            CancellationToken.None, reasoning: Gatto.Core.Loop.ReasoningHistory.All);

        //only the second and later rounds can send reasoning history back.
        static int WithReasoning(ReasoningSim sim) => sim.Requests
            .Count(r => r.Any(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.ReasoningContent)));

        Assert.Equal(0, WithReasoning(none));
        Assert.True(WithReasoning(all) > 0,
            "with 'all' the prior reasoning must ride along — otherwise this test proves nothing about 'none'");
    }

    [Fact]
    public void AND_THE_RUNNER_ACTUALLY_PASSES_THE_PACKS_POLICY_TO_THE_BATTERY()
    {
        //the test above calls the battery directly, so only the source of the call site shows what the runner passes
        var file = Path.Combine(RepoRoot(), "Gatto", "Roles", "Audition", "AuditionRunner.cs");
        var code = File.ReadAllLines(file)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

        Assert.Contains(code, l => l.Contains("reasoning: model.Profile.ReasoningHistory", StringComparison.Ordinal));
    }

    //search upward from the test binary for the folder that holds Gatto/Roles
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Gatto", "Roles")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task The_scratch_sandbox_is_gone_afterwards()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "gatto-audition-*").Length;
        await Run(Behaviour.Competent);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "gatto-audition-*").Length);
    }

    [Fact]
    public async Task Elapsed_is_stamped_by_the_RUNNER_not_left_at_the_graders_zero()
    {
        //delay the model so only the runner's stamp can make the elapsed reach 25 ms (a >= Zero assertion passes on nothing assigned)
        var v = await Run(Behaviour.Competent, delay: TimeSpan.FromMilliseconds(25));

        Assert.All(v.Tasks, t => Assert.True(t.Elapsed >= TimeSpan.FromMilliseconds(25),
            $"{t.TaskId} elapsed {t.Elapsed} — the grader's zero was never replaced"));
        Assert.True(v.WallClock >= TimeSpan.FromMilliseconds(125));
    }

    [Fact]
    public async Task A_cancelled_audition_produces_NO_verdict_at_all()
    {
        //a cancelled battery must produce no verdict, a partial run is not a measurement
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(Behaviour.Competent, ct: cts.Token));
    }

    [Theory]
    [InlineData("Qwen3.6-35B-A3B-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("model-IQ2_XXS.gguf", "IQ2_XXS")]
    [InlineData("thing-f16.gguf", "F16")]
    [InlineData("plain-model.gguf", null)]         //a name with no quant convention gives null
    [InlineData("Qwen3.6-35B-A3B.gguf", null)]     //the A3B segment is a model size
    public void The_quant_is_read_from_the_filename_or_left_ABSENT(string fileName, string? expected)
    {
        //the stamp must be null when the name declares no quant, a wrong quant on a badge is a false claim
        var model = ModelWith(fileName, sampling: null);
        Assert.Equal(expected, AuditionRunner.StampFor(model, null).Quant);
    }

    [Theory]
    //anchor the match, an unanchored search reads this name as Q4_K_M_PRUNED, which no quant family defines
    [InlineData("mistral-x_Q4_K_M_pruned.gguf")]
    //the rule splits only on a hyphen and a dot, so a name with underscores gives null
    [InlineData("model_Q4_K_M.gguf")]
    public void A_TOKEN_NO_QUANT_FAMILY_DEFINES_IS_NOT_A_QUANT(string fileName)
    {
        //the stamp uses the same anchored rule as the shelf and serve status --json
        Assert.Null(AuditionRunner.StampFor(ModelWith(fileName, sampling: null), null).Quant);
    }

    [Fact]
    public void a_qualifier_tail_INSIDE_one_segment_no_longer_stamps()
    {
        //a qualifier tail outside the allowlist, such as _v2, must reject the whole segment
        Assert.Null(AuditionRunner.StampFor(ModelWith("model-Q4_K_M_v2.gguf", sampling: null), null).Quant);
    }

    [Fact]
    public void THE_STAMP_AND_THE_SHELF_READ_THE_SAME_RULE()
    {
        //the stamp and the shelf must give the same quant for a file
        foreach (var name in new[]
                 {
                     "Qwen3.6-35B-A3B-Q4_K_M.gguf", "model-IQ2_XXS.gguf", "thing-f16.gguf",
                     "plain-model.gguf", "mistral-x_Q4_K_M_pruned.gguf", "model_Q4_K_M.gguf",
                     "m-Q4_K_M-00001-of-00013.gguf",
                     "model-Q4_K_M_v2.gguf",   //the bounded qualifier tail makes this name the hard case, so the row proves the stamp follows the shared rule
                 })
            Assert.Equal(
                Gatto.Core.Acquire.QuantToken.Of(name),
                AuditionRunner.StampFor(ModelWith(name, sampling: null), null).Quant);
    }

    [Fact]
    public void The_stamp_prefers_what_the_SERVER_reported_over_what_the_profile_asked_for()
    {
        //the context on the stamp must come from the server when it reports one, the profile is only a request
        var model = ModelWith("m-Q4_K_M.gguf", sampling: null, context: 8192);
        var loaded = new Gatto.Core.Client.LoadedModel("m-Q4_K_M.gguf", NCtx: 4096);

        Assert.Equal(4096, AuditionRunner.StampFor(model, loaded).Context);
        Assert.Equal(8192, AuditionRunner.StampFor(model, null).Context);   //with no server report, the stamp uses the context of the profile.
    }

    //drive StampFor with a loaded model, a test that builds AuditionStamp directly still passes when the build info is never copied
    [Fact]
    public void The_stamp_carries_the_SERVER_IDENTITY_the_probe_captured()
    {
        var model = ModelWith("m-Q4_K_M.gguf", sampling: null, context: 8192);
        var loaded = new Gatto.Core.Client.LoadedModel(
            "m-Q4_K_M.gguf", NCtx: 4096, BuildInfo: "b10999-deadbee");

        Assert.Equal("b10999-deadbee", AuditionRunner.StampFor(model, loaded).Server);
        Assert.Null(AuditionRunner.StampFor(model, null).Server);
    }

    [Fact]
    public void Sampling_that_overrides_nothing_stamps_defaults_rather_than_an_empty_string()
    {
        //the word defaults is a true answer here, an empty string would claim the data is missing
        Assert.Equal("defaults", AuditionRunner.StampFor(ModelWith("m.gguf", sampling: null), null).SamplingNote);
        Assert.Equal("defaults", AuditionRunner.StampFor(ModelWith("m.gguf", sampling: "{}"), null).SamplingNote);
        Assert.Contains("temperature 0.7",
            AuditionRunner.StampFor(ModelWith("m.gguf", sampling: """{"temperature":0.7}"""), null).SamplingNote);
    }

    private static Gatto.Roles.Model ModelWith(string fileName, string? sampling, int context = 8192)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-modeltest-" + Guid.NewGuid().ToString("N"), "p");
        Directory.CreateDirectory(dir);
        //write the profile JSON by hand, files is an array of objects that a flat dictionary cannot express
        var path = Path.Combine(dir, fileName).Replace("\\", "\\\\");
        var json = $"{{\"files\":[{{\"path\":\"{path}\",\"active\":true}}],"
            + $"\"port\":1235,\"context\":{context}"
            + (sampling is null ? "" : $",\"sampling\":{sampling}") + "}";
        File.WriteAllText(Path.Combine(dir, "profile.json"), json);
        return Gatto.Roles.Model.Load(Path.GetDirectoryName(dir)!, "p");
    }

    [Fact]
    public async Task The_decode_rate_is_SUMMED_across_rounds_not_averaged()
    {
        //the rate must sum tokens and milliseconds over all rounds, or a 3-token round weighs as much as a 300-token one
        var sim = new ModelSim(Behaviour.Competent)
        {
            Timings = ["""{"predicted_n":300,"predicted_ms":10000}""",
                       """{"predicted_n":3,"predicted_ms":1000}"""],
        };
        var v = await AuditionRunner.RunBatteryAsync(sim, Path.GetTempPath(), "m", Stamp, null, default);

        //assert a range that excludes the averaged value, an exact value would test the round count of the fake
        Assert.NotNull(v.DecodeTokS);
        Assert.InRange(v.DecodeTokS!.Value, 27.0, 30.0);
    }

    [Fact]
    public async Task An_absent_timings_field_leaves_the_rate_NULL_rather_than_guessing_from_wall_clock()
    {
        //never compute the decode rate from completion tokens and elapsed time, that value includes prefill and can be ten times wrong
        var v = await Run(Behaviour.Competent);
        Assert.Null(v.DecodeTokS);
    }

    [Fact]
    public async Task A_timings_payload_we_cannot_parse_is_not_a_failure()
    {
        var sim = new ModelSim(Behaviour.Competent) { Timings = ["{ not json"] };
        var v = await AuditionRunner.RunBatteryAsync(sim, Path.GetTempPath(), "m", Stamp, null, default);

        Assert.Null(v.DecodeTokS);
        Assert.True(v.Pass);       //a missing decode rate must not fail the model, the verdict is about the model
    }

    [Fact]
    public void The_pass_threshold_is_fixed_at_four_of_five()
    {
        //the threshold must be fixed before any model runs, a threshold chosen after the results are seen is not one
        Assert.Equal(4, AuditionRunner.PassThreshold);
    }

    private sealed class Collect(List<AuditionProgress> into) : IProgress<AuditionProgress>
    {
        public void Report(AuditionProgress value) => into.Add(value);
    }

    [Fact]
    public async Task Each_task_is_announced_BEFORE_it_runs_and_again_when_it_lands()
    {
        //announce each task before it runs, a task takes tens of seconds and the live line needs something to show
        var seen = new List<AuditionProgress>();
        await AuditionRunner.RunBatteryAsync(new ModelSim(Behaviour.Competent), Path.GetTempPath(),
            "m", Stamp, new Collect(seen), default);

        Assert.Equal(5, seen.Count(p => p.Stage == AuditionStage.Started));
        Assert.Equal(5, seen.Count(p => p.Stage == AuditionStage.Done));

        var started = seen.FindIndex(p => p.Stage == AuditionStage.Started && p.TaskId == "B3");
        var done = seen.FindIndex(p => p.Stage == AuditionStage.Done && p.TaskId == "B3");
        Assert.True(started >= 0 && done > started, "B3 must be announced before it is graded");

        //no event for the next task may come before the previous task is done, the events describe a sequence
        Assert.True(seen.FindIndex(p => p.Stage == AuditionStage.Started && p.TaskId == "B4") > done);
    }

    [Fact]
    public async Task The_LABEL_rides_the_event_so_a_caller_never_looks_it_up()
    {
        //each progress event holds its own label, a lookup by id could show the words of one battery version beside the result of another
        var seen = new List<AuditionProgress>();
        await AuditionRunner.RunBatteryAsync(new ModelSim(Behaviour.Competent), Path.GetTempPath(),
            "m", Stamp, new Collect(seen), default);

        foreach (var task in Battery.V1)
            Assert.All(seen.Where(p => p.TaskId == task.Id), p => Assert.Equal(task.Label, p.Label));

        var b1 = seen.First(p => p.Stage == AuditionStage.Started);
        Assert.Equal((1, 5), (b1.Index, b1.Total));
    }

    [Fact]
    public async Task The_GRADED_RESULT_carries_its_label_too_so_the_report_never_looks_one_up()
    {
        //the Label of AuditionTaskResult defaults to empty, so only this test proves the runner fills it
        var v = await Run(Behaviour.Competent);

        Assert.Equal(Battery.V1.Select(t => t.Label), v.Tasks.Select(t => t.Label));
        Assert.All(v.Tasks, t => Assert.NotEqual("", t.Label));
    }
}
