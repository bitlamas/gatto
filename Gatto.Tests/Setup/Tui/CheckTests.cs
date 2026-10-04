using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//renders the check screens, and every helper enters through the setup start, since the frames depend on the entry
public class CheckTests
{
    private static WizardProbes Probes(bool holdTheCheck = false, string? heldBy = null) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        AuditionBlockUntilCancelled = holdTheCheck,
        HeldBy = heldBy,
    };

    //a model name a running session could plausibly hold, long on purpose so the sentence wraps
    private const string Holding = "minimax-m2.7-REAP-139B-A10B-Q4_K_S";

    //a held model skips the offer, so the run reaches the refusal, and the screen comes from running the flow
    private static WizardScreen.Choice Held(SetupFlow flow)
    {
        flow.StartPastEngine();
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return Assert.IsType<WizardScreen.Choice>(screen);
    }

    //the moment the live frames draw: task 3 of 5 with two finished, typed here and tied to what the flow builds
    private static readonly CheckTick Moment =
        new(3, 5, "edit a file", ["1) run a command", "2) use two tools in order"]);

    //the clock reads 0 when the wait opens and 72,800 ms after, since the face measures now minus began
    private static Func<long> Purring()
    {
        var opened = false;
        return () => { if (!opened) { opened = true; return 0; } return 72_800; };
    }

    //the wait on the skip path, drawn at 1m 3s with the widest animation frame, on the same 140 ms window rule as Purring
    private static Func<long> Loading()
    {
        var opened = false;
        return () => { if (!opened) { opened = true; return 0; } return 63_840; };
    }

    //the check's wait, drawn at 2m 41s on a narrow frame, since every frame is padded to the widest in its set
    private static Func<long> StillComingUp()
    {
        var opened = false;
        return () => { if (!opened) { opened = true; return 0; } return 161_840; };
    }

    //the offer is answered yes and the flow starts the check, since the screen is what that answer produces. a hand-built screen is one no run reaches
    private static WizardScreen.Choice Live(SetupFlow flow)
    {
        flow.StartPastEngine();
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionOfferKey, Assert.IsType<WizardScreen.Choice>(screen).Key);

        var running = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));
        Assert.Equal(SetupFlow.AuditionRunningKey, running.Key);
        return running;
    }

    //the writes are done and the flow resumes with the writer's id, since the screen reads the model file when the step enters
    private static WizardScreen.Choice Offer(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return Assert.IsType<WizardScreen.Choice>(screen);
    }

    [Fact]
    public void THE_OFFER_RENDERS_AT_100()
    {
        var captured = WalkRender.SettledFrame(Offer(Probes()), 100, "offer");

        Golden.AssertEquals("s7", "offer", 100, captured.Rows);
    }

    //the model row is read once when the step enters, so an unreadable profile costs the row, and the four screens share that read
    [Fact]
    public void WITH_NO_READABLE_PROFILE_THE_MODEL_ROW_DROPS_AND_THE_SCREEN_STANDS()
    {
        var probes = Probes();
        probes.ActiveFile = null;

        var rows = string.Join("\n", Offer(probes).BodyRows!.Select(r => r.Text));

        Assert.DoesNotContain("model  ", rows, StringComparison.Ordinal);
        Assert.Contains("tasks", rows, StringComparison.Ordinal);
        Assert.Contains("five short tasks", rows, StringComparison.Ordinal);
    }

    //the live frame: what is running, what finished, and one way out, the first screen drawn while gatto works
    [Fact]
    public void THE_LIVE_SCREEN_RENDERS_AT_100() =>
        Golden.AssertEquals("s7", "running", 100,
            WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
                nowMs: Purring(), check: Moment).Rows);

    //the face is given pool member 1, so the frame drawn is that member's, and purr purr is the discriminator
    [Fact]
    public void THE_CHECK_SCREEN_DRAWS_THE_PURR_ITS_FACE_WAS_GIVEN()
    {
        var drawn = WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
            nowMs: Purring(), check: Moment,
            fullPurr: Gatto.Repl.Render.PurrFrames.FromPool(1)).Rows;
        var byDefault = WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
            nowMs: Purring(), check: Moment).Rows;

        Assert.Contains(drawn, r => r.Contains("purr purr", StringComparison.Ordinal));
        Assert.DoesNotContain(byDefault, r => r.Contains("purr purr", StringComparison.Ordinal));
    }

    //a second width makes the fold a measured property rather than a claim, with the intro wrapping and the fact rows keeping their column
    [Fact]
    public void THE_LIVE_SCREEN_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s7", "running-80", 80,
            WalkRender.Watching(Live(new SetupFlow(Probes())), 80, tick: null,
                nowMs: Purring(), check: Moment).Rows);

    //the default armed sentence would be false here, so the screen names its own price, since a minute of the machine is spent
    [Fact]
    public void THE_ARMED_ESC_NAMES_WHAT_STOPPING_COSTS() =>
        Golden.AssertEquals("s7", "running-armed", 100,
            WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
                script: [WizardRig.Esc, WizardRig.Esc], nowMs: Purring(), check: Moment).Rows);

    //derive the pinned moment from the events the flow builds, so a hand-written tick cannot pass when the sink renumbers or drops a task
    [Fact]
    public void THE_MOMENT_THE_FRAMES_DRAW_IS_THE_ONE_THE_FLOW_BUILDS()
    {
        var battery = Gatto.Roles.Audition.Battery.Tasks;
        var probes = Probes(holdTheCheck: true);
        probes.AuditionMoments.AddRange([
            Gatto.Roles.Audition.AuditionProgress.Started(battery[0], 1, battery.Count),
            Gatto.Roles.Audition.AuditionProgress.Done(battery[0], 1, battery.Count, true),
            Gatto.Roles.Audition.AuditionProgress.Started(battery[1], 2, battery.Count),
            Gatto.Roles.Audition.AuditionProgress.Done(battery[1], 2, battery.Count, true),
            Gatto.Roles.Audition.AuditionProgress.Started(battery[2], 3, battery.Count),
        ]);
        var flow = new SetupFlow(probes);
        Live(flow);

        Assert.True(SpinWait.SpinUntil(() => flow.PollCheck() is { Done.Count: 2 },
            TimeSpan.FromSeconds(10)), "the check's moments never reached the flow");
        var built = flow.PollCheck()!.Value;

        Assert.Equal(Moment.Index, built.Index);
        Assert.Equal(Moment.Total, built.Total);
        Assert.Equal(Moment.Task, built.Task);
        Assert.Equal(Moment.Done, built.Done);

        flow.Answer(SetupFlow.AuditionStop);   //the held check is released here, so no thread is left running.
    }

    //no task count before the first task, the step drops the row rather than leaving it blank
    [Fact]
    public void BEFORE_THE_FIRST_TASK_STARTS_THE_LIVE_ROWS_ARE_ABSENT()
    {
        var rows = WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
            nowMs: Purring(), check: null).Rows;

        var joined = string.Join("\n", rows);
        Assert.Contains("model         gemma", joined, StringComparison.Ordinal);
        //the running row says the server is starting, no task count until a task begins
        Assert.Contains("running       starting the server", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("task 0", joined, StringComparison.Ordinal);
        //the footer rule is the last row, and a blank sits one row above it.
        Assert.Equal("", rows[^3]);
    }

    //the badge date is fixed here, a date read from the clock would make the golden true for one day only
    private static WizardProbes Passing() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Audition = new AuditionCheck(
            AuditionOutcome.Passed,
            new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
            new DateOnly(2026, 8, 24)),
    };

    //one walk returns both frames, the settled screen's activity line is the face's memory of the watch (painted alone it would report zero seconds)
    private static (WizardScreen.Choice Running, WizardScreen.Choice Settled) Walk(
        WizardProbes probes, string key)
    {
        var flow = new SetupFlow(probes);
        var running = Live(flow);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the check never finished, so the walk never reached its outcome");
        var settled = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(key, settled.Key);
        return (running, settled);
    }

    //the pass frame takes its elapsed from the watch, a time the flow composed would be a second clock
    [Fact]
    public void THE_PASS_IS_A_SCREEN_YOU_ANSWER_AND_IT_MATCHES_ITS_GOLDEN()
    {
        var (running, settled) = Walk(Passing(), SetupFlow.AuditionPassedKey);

        Golden.AssertEquals("s7", "passed", 100,
            WalkRender.AfterWatch(running, settled, 100, watchedMs: 108_000).Rows);
    }

    //with no badge written the recorded row drops, the rest of the screen stands. writing the register is never fatal
    [Fact]
    public void WITH_NO_BADGE_WRITTEN_THE_RECORDED_ROW_DROPS_AND_THE_STAMP_STAYS()
    {
        var unrecorded = Passing();
        var probes = new WizardProbes
        {
            Llama = unrecorded.Llama,
            ActiveFile = unrecorded.ActiveFile,
            Audition = unrecorded.Audition with { Recorded = null },
        };

        var body = string.Join("\n", Walk(probes, SetupFlow.AuditionPassedKey).Settled.BodyRows!.Select(r => r.Text));

        Assert.DoesNotContain("recorded", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tool-calling verified", body, StringComparison.Ordinal);
        Assert.Contains("quant", body, StringComparison.Ordinal);
        Assert.Contains("~31.4 tok/s", body, StringComparison.Ordinal);
    }

    //the stamp aligns at the step's own column, a literal number here would go red and blame the wrong thing
    [Fact]
    public void THE_STAMP_SITS_AT_THE_STEPS_OWN_COLUMN()
    {
        var rows = Walk(Passing(), SetupFlow.AuditionPassedKey).Settled.BodyRows!.Select(r => r.Text).ToList();

        foreach (var label in new[] { "quant", "sampling", "thinking", "speed", "recorded" })
        {
            var row = rows.Single(r => r.StartsWith(label, StringComparison.Ordinal));
            Assert.Equal(SetupFlow.CheckFacts.Column, row.Length - row[label.Length..].TrimStart().Length);
        }
    }

    //each key goes where the footer promises, a screen that continues either way lies in its own footer
    [Fact]
    public void THE_PASS_SCREENS_TWO_KEYS_GO_TWO_PLACES()
    {
        var onward = new SetupFlow(Passing());
        Live(onward);
        Assert.True(SpinWait.SpinUntil(onward.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        onward.Answer(SetupFlow.Landed);
        var next = onward.Answer(SetupFlow.AuditionPassedNext);
        Assert.Equal("default.ready", Assert.IsType<WizardScreen.Info>(next).Key);

        var leaving = new SetupFlow(Passing());
        Live(leaving);
        Assert.True(SpinWait.SpinUntil(leaving.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        leaving.Answer(SetupFlow.Landed);
        Assert.IsType<WizardScreen.Terminal>(leaving.Answer(SetupFlow.AuditionPassedLeave));
    }

    //build each record from Battery.Tasks, an invented label would test the fixture's own words
    private static Gatto.Roles.Audition.AuditionTaskResult Did(
        int at, bool pass, Gatto.Roles.Audition.FailureShape? shape = null, bool skipped = false)
    {
        var task = Gatto.Roles.Audition.Battery.Tasks[at];
        return new Gatto.Roles.Audition.AuditionTaskResult(task.Id, pass,
            shape is { } s ? [s] : [], TimeSpan.FromSeconds(3), task.Label, skipped, null, task.Act);
    }

    //two failures end the check early, so the headline counts 2 of 4 and the fifth task never ran
    private static WizardProbes Failing() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Audition = new AuditionCheck(
            AuditionOutcome.Failed,
            new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 2, 4),
            null,
            [
                Did(0, pass: true),
                Did(1, pass: true),
                Did(2, pass: false, Gatto.Roles.Audition.FailureShape.NoToolCall),
                Did(3, pass: false, Gatto.Roles.Audition.FailureShape.IgnoredError),
                Did(4, pass: false, skipped: true),
            ]),
    };

    //all five tasks ran and the last invented a file's contents. the screen says what happened and leaves disqualified to the CLI report
    private static WizardProbes Disqualified() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Audition = new AuditionCheck(
            AuditionOutcome.Failed,
            new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 3, 5),
            null,
            [
                Did(0, pass: true),
                Did(1, pass: true),
                Did(2, pass: true),
                Did(3, pass: false, Gatto.Roles.Audition.FailureShape.RepeatLoop),
                Did(4, pass: false, Gatto.Roles.Audition.FailureShape.FabricatedResult),
            ]),
    };

    [Fact]
    public void THE_FAILURE_LISTS_WHAT_EACH_TASK_DID() =>
        Golden.AssertEquals("s7", "failed", 100,
            WalkRender.AfterWatch(Walk(Failing(), SetupFlow.AuditionFailedKey).Running, Walk(Failing(), SetupFlow.AuditionFailedKey).Settled, 100, 63_000).Rows);

    [Fact]
    public void THE_DISQUALIFYING_FAILURE_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s7", "failed-dq", 100,
            WalkRender.AfterWatch(Walk(Disqualified(), SetupFlow.AuditionFailedKey).Running, Walk(Disqualified(), SetupFlow.AuditionFailedKey).Settled,
                100, 132_000).Rows);

    [Fact]
    public void THE_FAILURE_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s7", "failed-80", 80,
            WalkRender.AfterWatch(Walk(Failing(), SetupFlow.AuditionFailedKey).Running, Walk(Failing(), SetupFlow.AuditionFailedKey).Settled, 80, 63_000).Rows);

    //one verdict must say the same sentences in the wizard rows and the CLI report, so derive both sides from the surfaces
    [Fact]
    public void THE_WIZARDS_ROWS_AND_THE_CLI_REPORT_SAY_THE_SAME_WORDS()
    {
        var probes = Failing();
        var tasks = probes.Audition.Tasks!;
        var rows = string.Join("\n", Walk(probes, SetupFlow.AuditionFailedKey).Settled.BodyRows!.Select(r => r.Text));
        var report = Gatto.Roles.Audition.AuditionReport.Render(
            new Gatto.Roles.Audition.AuditionVerdict(Pass: false, Disqualified: false, tasks,
                new Gatto.Roles.Audition.AuditionStamp("v0.5.0", "m.gguf", "Q4_K_M", 8192, "defaults"),
                TimeSpan.FromSeconds(9), 31.4),
            s => s, Gatto.Roles.EngineMarks.Unicode);

        var said = 0;
        foreach (var task in tasks)
        {
            if (Gatto.Roles.Audition.TaskWords.Why(task) is not { Length: > 0 } words) continue;
            said++;
            Assert.Contains(words, rows, StringComparison.Ordinal);
            Assert.Contains(words, report, StringComparison.Ordinal);
        }

        //a fixture where every task passed would agree about nothing, so this count keeps the check real.
        Assert.Equal(3, said);
    }

    //the done row drops while nothing has finished, a real moment between the first Started and the first Done
    [Fact]
    public void WHILE_THE_FIRST_TASK_RUNS_THERE_IS_NOTHING_DONE_TO_SAY()
    {
        var rows = WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
            nowMs: Purring(), check: new CheckTick(1, 5, "run a command", [])).Rows;

        var joined = string.Join("\n", rows);
        Assert.Contains("running       task 1 of 5 · run a command", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("done ", joined, StringComparison.Ordinal);
    }

    //the Enter key is next everywhere else, so a press here must be inert. the test reads what Choose answered, a frame would look the same either way
    [Fact]
    public void ENTER_DOES_NOTHING_WHILE_THE_CHECK_RUNS()
    {
        var answered = WalkRender.WatchAfterKeys(Live(new SetupFlow(Probes())), 100,
            new FetchTick("", 0, 0, 0, 0, 0), [WizardRig.Enter]);

        Assert.Equal(SetupFlow.Landed, answered);
        Assert.NotEqual(SetupFlow.AuditionStop, answered);
    }

    //the watch must ask the same task the check runs on, each half alone passes for the wrong reason
    [Fact]
    public void THE_CHECK_RUNS_ON_A_TASK_AND_THE_WATCH_ASKS_THAT_TASK()
    {
        var held = Probes(holdTheCheck: true);
        var waiting = new SetupFlow(held);
        Live(waiting);

        Assert.False(waiting.PollWatch(), "the watch resolved while the check was still running");
        waiting.Answer(SetupFlow.AuditionStop);
        //read AuditionUnwound after the stop joins, it proves the false above came from a check really in flight
        Assert.True(held.AuditionUnwound, "no check was running, so the assertion above said nothing");

        var finishing = new SetupFlow(Probes());
        Live(finishing);

        Assert.True(SpinWait.SpinUntil(finishing.PollWatch, TimeSpan.FromSeconds(10)),
            "the check finished and the watch never noticed");
    }

    //the next screen waits for the check to unwind, the runner's own end decides the server's fate. the test counts one start so a second check cannot hide
    [Fact]
    public void ESC_ESC_STOPS_THE_CHECK_AND_CARRIES_ON_UNCHECKED()
    {
        var probes = Probes(holdTheCheck: true);
        var flow = new SetupFlow(probes);
        Live(flow);

        var next = flow.Answer(SetupFlow.AuditionStop);

        Assert.True(probes.AuditionUnwound, "the flow moved on before the check had unwound");
        Assert.Equal(1, probes.AuditionStarts);
        Assert.Equal("default.ready", Assert.IsType<WizardScreen.Info>(next).Key);
        Assert.True(flow.NeedsWritesApplied);
    }

    //only the watch advances the live screen, its one option cancels and no other input exists. a face that cannot poll waits rather than asking
    [Fact]
    public void ONLY_THE_WATCH_CAN_ADVANCE_THE_LIVE_SCREEN() =>
        Assert.True(Live(new SetupFlow(Probes())).OnlyTheWatchAdvances);

    //the offer and the live screen both ask the step's question, so one action cannot get two questions
    [Fact]
    public void THE_LIVE_SCREEN_KEEPS_THE_STEPS_OWN_QUESTION()
    {
        var flow = new SetupFlow(Probes());
        var offer = Offer(Probes());

        Assert.Equal(offer.Question, Live(flow).Question);
    }

    //read the task list from Battery.Tasks, a copy in the test could agree with the screen's copy while both drift from the real tasks
    [Fact]
    public void THE_TASKS_ARE_THE_BATTERYS_OWN()
    {
        var rows = string.Join("\n", Offer(Probes()).BodyRows!.Select(r => r.Text));
        var items = Offer(Probes()).BodyRows!.Single(r => r.Items is not null).Items!;

        Assert.Equal(Gatto.Roles.Audition.Battery.Tasks.Count, items.Count);
        foreach (var (task, i) in Gatto.Roles.Audition.Battery.Tasks.Select((t, i) => (t, i)))
            Assert.Equal($"{i + 1}) {task.Label}", items[i]);
        Assert.Contains("tasks", rows, StringComparison.Ordinal);
    }

    //the fixture reports the server's own rate, so the speed row draws. with no rate the test would check an absence
    private static WizardProbes Answering() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Prove = new Gatto.Core.Acquire.ProveOutcome(true, "the model answered",
            TimeSpan.FromSeconds(2), TokensPerSecond: 31.4),
    };

    //reach the waiting screen by answering Skip, a screen built by hand could show a state no answer reaches
    private static (SetupFlow Flow, WizardScreen.Choice Waiting) Skipping(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionOfferKey, Assert.IsType<WizardScreen.Choice>(screen).Key);

        var waiting = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Skip));
        Assert.Equal(SetupFlow.AuditionWaitingKey, waiting.Key);
        return (flow, waiting);
    }

    //the skip reuses the check's waiting screen, since the key abandons one reply rather than a battery. the golden renders a watched frame
    [Fact]
    public void THE_SKIP_WAITS_WHERE_YOU_CAN_SEE_IT()
    {
        var (_, waiting) = Skipping(Answering());

        Golden.AssertEquals("s7", "waiting", 100,
            WalkRender.Watching(waiting, 100, tick: null, nowMs: Loading(),
                check: new CheckTick(0, 0, "", [], ServerUp: true)).Rows);
    }

    //the arrival screen claims only that the model loads and answers, and names the command that judges command running
    [Fact]
    public void THE_SKIP_ARRIVES_AT_ANSWERS()
    {
        var (flow, waiting) = Skipping(Answering());
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the load never finished");
        var answers = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.AuditionAnswersKey, answers.Key);

        Golden.AssertEquals("s7", "answers", 100,
            WalkRender.AfterWatch(waiting, answers, 100, watchedMs: 81_000).Rows);
    }

    //the completion reports the skip's own load, it must not run a second one behind the same question. only a count tells one load from two
    [Fact]
    public void THE_SKIP_ROAD_LOADS_ONCE_AND_THE_COMPLETION_REPORTS_THAT_LOAD()
    {
        var probes = Answering();
        var (flow, _) = Skipping(probes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the load never finished");
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(1, probes.Proofs);

        //drain the flow to its last screen, the completion screen is where a second load would happen. stopping earlier asserts about a part the test never ran
        WizardScreen screen = flow.Answer(SetupFlow.AnswersNext);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        while (screen is WizardScreen.Choice c)
        {
            screen = flow.Answer(c.Options[0].Key);
            while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        }

        Assert.IsType<WizardScreen.Terminal>(screen);
        //holding the skip's result is what keeps this at one, a second probe reads two while the screen looks the same
        Assert.Equal(1, probes.Proofs);
    }

    //the speed row reports the reply's own rate and drops when the server gave none, a wall-clock rate would fold prefill into decode
    [Fact]
    public void WITH_NO_TIMINGS_THE_SPEED_ROW_DROPS_AND_THE_CLAIM_STANDS()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            Prove = new Gatto.Core.Acquire.ProveOutcome(true, "the model answered", TimeSpan.FromSeconds(2)),
        };
        var (flow, _) = Skipping(probes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the load never finished");
        var answers = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        var body = string.Join("\n", answers.BodyRows!.Select(r => r.Text));
        Assert.DoesNotContain("tok/s", body, StringComparison.Ordinal);
        Assert.Contains("It loads and answers.", body, StringComparison.Ordinal);
        Assert.Contains("five tasks were skipped", body, StringComparison.Ordinal);
    }

    //the key stops the wait, the model is set up either way as the legend promises. the test presses it, a footer that lies about its key only shows when pressed
    [Fact]
    public void STOPPING_THE_WAIT_CARRIES_ON_WITH_THE_MODEL_SET_UP()
    {
        var (flow, _) = Skipping(Answering());

        var next = flow.Answer(SetupFlow.SkipStop);

        Assert.Equal("default.ready", Assert.IsType<WizardScreen.Info>(next).Key);
        Assert.True(flow.NeedsWritesApplied);
    }

    //a face that cannot poll waits, every answer to a question here would stall
    [Fact]
    public void ONLY_THE_WATCH_CAN_ADVANCE_THE_SKIPS_WAIT() =>
        Assert.True(Skipping(Answering()).Waiting.OnlyTheWatchAdvances);
    //the server row needs the note a successful start sends, a tick alone must not draw it. a false ServerUp is rendered too, so the guard is visible
    [Fact]
    public void BEFORE_THE_SERVER_IS_UP_THERE_IS_NO_SERVER_ROW()
    {
        var (_, waiting) = Skipping(Answering());

        var up = string.Join("\n", WalkRender.Watching(waiting, 100, tick: null, nowMs: Loading(),
            check: new CheckTick(0, 0, "", [], ServerUp: true)).Rows);
        var notYet = string.Join("\n", WalkRender.Watching(waiting, 100, tick: null, nowMs: Loading(),
            check: new CheckTick(0, 0, "", [], ServerUp: false)).Rows);

        Assert.Contains("server        started, loading the model", up, StringComparison.Ordinal);
        //match the row's leading label, a bare word match would pass on a screen whose prose also names the server
        Assert.DoesNotContain("server        ", notYet, StringComparison.Ordinal);
        //only the server row drops, the rest of the frame stays
        Assert.Contains("model         gemma", notYet, StringComparison.Ordinal);
    }

    //the frame before task 1, with the server row for a start and the reassurance row once the load has run a while
    [Fact]
    public void THE_LOADING_SCREEN_RENDERS_AT_100() =>
        Golden.AssertEquals("s7", "loading", 100,
            WalkRender.Watching(Live(new SetupFlow(Probes())), 100, tick: null,
                nowMs: StillComingUp(),
                check: new CheckTick(0, 0, "", [], ServerUp: true, StillLoading: true)).Rows);

    //both facts must come from the moments the flow reports. the flags stay separate, StillLoading is about a wait and ServerUp about a spawn
    [Fact]
    public void THE_LOADING_FRAME_S_FACTS_ARE_THE_ONES_THE_FLOW_BUILDS()
    {
        var probes = Probes(holdTheCheck: true);
        probes.AuditionMoments.AddRange([
            Gatto.Roles.Audition.AuditionProgress.Note("serving gemma-4-26B-A4B-it"),
            Gatto.Roles.Audition.AuditionProgress.Loading(),
        ]);
        var flow = new SetupFlow(probes);
        Live(flow);

        Assert.True(SpinWait.SpinUntil(() => flow.PollCheck() is { StillLoading: true },
            TimeSpan.FromSeconds(10)), "the loading moments never reached the flow");
        var built = flow.PollCheck()!.Value;

        Assert.True(built.ServerUp);
        Assert.Equal(0, built.Total);
        Assert.Empty(built.Done);

        flow.Answer(SetupFlow.AuditionStop);   //release the held check so no blocked thread outlives the test
    }

    //the reassurance line needs a wait behind it, so a start on its own draws none and the server row stays
    [Fact]
    public void A_START_ON_ITS_OWN_DRAWS_NO_REASSURANCE()
    {
        var live = Live(new SetupFlow(Probes()));

        var justUp = string.Join("\n", WalkRender.Watching(live, 100, tick: null,
            nowMs: StillComingUp(), check: new CheckTick(0, 0, "", [], ServerUp: true)).Rows);

        Assert.Contains("server        started, loading the model", justUp, StringComparison.Ordinal);
        Assert.DoesNotContain("Still loading", justUp, StringComparison.Ordinal);
    }

    //the first task proves the model came up, so the reassurance must go. keeping it would reassure about a wait that already ended
    [Fact]
    public void THE_REASSURANCE_GOES_WHEN_THE_FIRST_TASK_STARTS()
    {
        var live = Live(new SetupFlow(Probes()));

        var running = string.Join("\n", WalkRender.Watching(live, 100, tick: null,
            nowMs: StillComingUp(),
            check: new CheckTick(1, 5, "run a command", [], ServerUp: true, StillLoading: true)).Rows);

        Assert.Contains("running       task 1 of 5", running, StringComparison.Ordinal);
        Assert.DoesNotContain("Still loading", running, StringComparison.Ordinal);
    }
    //the guard in PollCheck needs a term for every fact, a fact left out would leave the screen with nothing to draw. the test reports Loading() on its own
    [Fact]
    public void A_TICK_EXISTS_AS_SOON_AS_ANY_FACT_HAS()
    {
        var probes = Probes(holdTheCheck: true);
        probes.AuditionMoments.Add(Gatto.Roles.Audition.AuditionProgress.Loading());
        var flow = new SetupFlow(probes);
        Live(flow);

        Assert.True(SpinWait.SpinUntil(() => flow.PollCheck() is not null, TimeSpan.FromSeconds(10)),
            "a reported fact left the screen with no tick to draw");
        var built = flow.PollCheck()!.Value;

        Assert.True(built.StillLoading);
        Assert.False(built.ServerUp);

        flow.Answer(SetupFlow.AuditionStop);   //release the held check so no blocked thread outlives the test
    }

    //a held server makes the offer refuse, and the step must say so on its own screen. finishing silently would hide a recommended step
    [Fact]
    public void THE_HELD_SCREEN_RENDERS_AT_100() =>
        Golden.AssertEquals("s7", "held", 100,
            WalkRender.SettledFrame(Held(new SetupFlow(Probes(heldBy: Holding))), 100).Rows);

    //drive the flow here, a screen handed to the render test would pass even on a flow that never reached it
    [Fact]
    public void A_HELD_SERVER_REACHES_THE_SCREEN_RATHER_THAN_ENDING_THE_STEP()
    {
        var held = Held(new SetupFlow(Probes(heldBy: Holding)));

        Assert.Equal(SetupFlow.AuditionHeldKey, held.Key);
        Assert.Contains(Holding, string.Join("\n", held.BodyRows!), StringComparison.Ordinal);
    }

    //both keys get pressed, a screen that renders can still throw on a key nobody pressed. the leave arm needs its own press
    [Fact]
    public void THE_HELD_SCREEN_IS_ANSWERED_BOTH_WAYS()
    {
        var next = new SetupFlow(Probes(heldBy: Holding));
        Held(next);
        Assert.IsNotType<WizardScreen.Choice>(next.Answer(SetupFlow.AuditionHeldNext));

        var leave = new SetupFlow(Probes(heldBy: Holding));
        Held(leave);
        Assert.IsNotType<WizardScreen.Choice>(leave.Answer(SetupFlow.AuditionHeldLeave));
    }

    //the audition command refuses while another model holds the server, so the screen offers it for later and names what must stop first
    [Fact]
    public void THE_COMMAND_IS_OFFERED_FOR_AFTER_NEVER_WHENEVER_YOU_WANT()
    {
        var said = string.Join("\n", Held(new SetupFlow(Probes(heldBy: Holding))).BodyRows!);

        Assert.Contains($"Once {Holding} is no longer running", said, StringComparison.Ordinal);
        Assert.DoesNotContain("whenever you want", said, StringComparison.Ordinal);
    }

    //the command must stay whole on one row at every width, a split one reads as two things to type. a highlight across a wrap boundary would not fire
    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void THE_COMMAND_IS_NEVER_BROKEN_ACROSS_A_WRAP(int width)
    {
        var rows = WalkRender.SettledFrame(
            Held(new SetupFlow(Probes(heldBy: Holding))), width, key: $"held-{width}").Rows;

        Assert.Contains(rows, r => r.Contains("gatto audition gemma-4-26B-A4B-it",
            StringComparison.Ordinal));
    }

    //fixture for a pick skipped for lack of a published fingerprint, with byte counts the one size formatter turns into the shown figures
    private static WizardProbes Filed() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Audition = new AuditionCheck(AuditionOutcome.CouldNotRun,
            Stumble: new CheckStumble.Incomplete(
                "Llama-3.3-70B-Instruct-IQ2_XS.gguf",
                @"D:\ai\test\exploration\.lmstudio\models\lmstudio-community\Llama-3.3-70B\",
                19_756_849_561, 22_655_952_486)),
    };

    //this arm may name a cause, bytes on disk against the header's tensor end rule out the alternatives. it says incomplete, a shorter file is all that is proved
    [Fact]
    public void THE_FILED_ARM_RENDERS_AT_100()
    {
        var (_, settled) = Walk(Filed(), SetupFlow.AuditionIncompleteKey);

        Golden.AssertEquals("s7", "incomplete", 100,
            WalkRender.SettledFrame(settled, 100, "incomplete").Rows);
    }

    //the figures come from the one size formatter. a screen with its own gigabytes would pass the golden while drifting from the corpus
    [Fact]
    public void THE_FILED_ARM_QUOTES_THE_HOUSE_SIZE_FORMATTER()
    {
        var (_, settled) = Walk(Filed(), SetupFlow.AuditionIncompleteKey);
        var said = string.Join("\n", settled.BodyRows!);

        Assert.Contains($"{Gatto.Cli.SizeWords.Gb(19_756_849_561)} on disk", said,
            StringComparison.Ordinal);
        Assert.Contains($"{Gatto.Cli.SizeWords.Gb(22_655_952_486)} or more", said,
            StringComparison.Ordinal);
        //the number reads as a floor, the header proves a minimum and the file may be larger
        Assert.Contains("or more, the file's own header says so", said, StringComparison.Ordinal);
    }

    //both options must be answered, one goes back to the models and one retries the check. a retry must start a real check, and the count tells it from a redraw
    [Fact]
    public void THE_FILED_ARM_IS_ANSWERED_BOTH_WAYS()
    {
        var probes = Filed();
        var flow = new SetupFlow(probes);
        Live(flow);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");
        Assert.Equal(SetupFlow.AuditionIncompleteKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);

        var restarted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CheckAgain));
        Assert.Equal(SetupFlow.AuditionRunningKey, restarted.Key);
        //wait on the flow's own join before reading AuditionStarts, a read right after the answer would assert on thread timing
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the second check never finished");
        Assert.Equal(2, probes.AuditionStarts);

        var back = new SetupFlow(Filed());
        Live(back);
        Assert.True(SpinWait.SpinUntil(back.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");
        back.Answer(SetupFlow.Landed);

        var elsewhere = back.Answer(SetupFlow.PickAnother);
        Assert.NotNull(elsewhere);
        Assert.False(elsewhere is WizardScreen.Choice { Key: SetupFlow.AuditionIncompleteKey },
            "Pick a different model redrew the screen it was leaving");
    }

    //the server's own words on a truncated file, kept verbatim, the reason sits at the end of a line
    private static readonly string[] ServerSaid =
    [
        "llama_model_load: error loading model: tensor 'blk.61.ffn_down.weight' data is not within "
            + "the file bounds, model is corrupted or incomplete",
        "llama_model_load_from_file_impl: failed to load model",
        "srv    load_model: failed to load model, 'D:\\ai\\test\\exploration\\.lmstudio\\models\\"
            + "lmstudio-community\\Llama-3.3-70B\\Llama-3.3-70B-Instruct-IQ2_XS.gguf'",
        "main: exiting due to model loading error",
    ];

    private static WizardProbes Stumbled(CheckStumble? stumble) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Audition = new AuditionCheck(AuditionOutcome.CouldNotRun, Stumble: stumble),
    };

    //the reason must reach the screen as text, a stumble object that never gets drawn is the same silent screen
    [Fact]
    public void THE_UNAVAILABLE_ARM_PUTS_THE_REASON_ON_THE_SCREEN()
    {
        const string Reason = "qwen-qwen3.6-35b-a3b is the running server";
        var settled = Walk(Stumbled(new CheckStumble.Unavailable(Reason)), SetupFlow.AuditionFailedKey).Settled;

        Assert.Contains(Reason, string.Join("\n", settled.BodyRows!.Select(r => r.Text)),
            StringComparison.Ordinal);
    }

    //the audition promise is false under a held server, so this arm drops it. both sides are asserted, so a screen that drops it everywhere cannot pass
    [Fact]
    public void THE_UNAVAILABLE_ARM_DROPS_THE_POINTER_AND_THE_OTHERS_KEEP_IT()
    {
        const string Pointer = "whenever you want it";

        Assert.DoesNotContain(Pointer, Body(Stumbled(new CheckStumble.Unavailable("held"))),
            StringComparison.Ordinal);
        Assert.Contains(Pointer, Body(Stumbled(stumble: null)), StringComparison.Ordinal);
    }

    private static string Body(WizardProbes probes) =>
        string.Join("\n", Walk(probes, SetupFlow.AuditionFailedKey).Settled.BodyRows!
            .Select(r => r.Text));

    //the server died before the model was up, and no cause may be named here. the last lines show the server's own guess
    [Fact]
    public void THE_EXITED_ARM_RENDERS_AT_100()
    {
        var (_, settled) = Walk(
            Stumbled(new CheckStumble.Exited(ServerSaid, @"C:\Users\you\.gatto\serve.log")),
            SetupFlow.AuditionExitedKey);

        Golden.AssertEquals("s7", "exited", 100,
            WalkRender.SettledFrame(settled, 100, "exited").Rows);
    }

    //the exhibit must wrap at any width, the server's reason sits at the end of a line and a clip would cut it. the test sweeps widths so one cannot hide
    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void THE_SERVERS_LAST_WORDS_ARE_WRAPPED_NEVER_CLIPPED(int width)
    {
        var (_, settled) = Walk(
            Stumbled(new CheckStumble.Exited(ServerSaid, @"C:\Users\you\.gatto\serve.log")),
            SetupFlow.AuditionExitedKey);
        var rows = WalkRender.SettledFrame(settled, width, $"exited-{width}").Rows;

        Assert.Contains(rows, r => r.Contains("model is corrupted or incomplete",
            StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains('…'));
        Assert.Contains(rows, r => r.Contains(@"full log: C:\Users\you\.gatto\serve.log",
            StringComparison.Ordinal));
    }

    //the tasks that ran stay on screen, numbered as the offer numbered them. the empty log is stated, a killed server writes nothing and its absence is the fact
    [Fact]
    public void THE_STOPPED_ARM_RENDERS_AT_100()
    {
        var battery = Gatto.Roles.Audition.Battery.Tasks;
        var probes = Stumbled(new CheckStumble.Stopped([]));
        for (var i = 0; i < 3; i++)
        {
            probes.AuditionMoments.Add(
                Gatto.Roles.Audition.AuditionProgress.Started(battery[i], i + 1, battery.Count));
            probes.AuditionMoments.Add(
                Gatto.Roles.Audition.AuditionProgress.Done(battery[i], i + 1, battery.Count, true));
        }

        var flow = new SetupFlow(probes);
        var running = Live(flow);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");
        var settled = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.AuditionStoppedKey, settled.Key);

        Golden.AssertEquals("s7", "stopped", 100,
            WalkRender.AfterWatch(running, settled, 100, watchedMs: 147_000).Rows);
    }

    //both engine arms must answer all three keys, and only continuing with the model advances setup. neither other key is the screen's default
    [Fact]
    public void THE_ENGINE_ARMS_ARE_ANSWERED_EVERY_WAY()
    {
        foreach (var stumble in new CheckStumble[]
                 {
                     new CheckStumble.Exited(ServerSaid, @"C:\Users\you\.gatto\serve.log"),
                     new CheckStumble.Stopped([]),
                 })
        {
            var probes = Stumbled(stumble);
            var flow = new SetupFlow(probes);
            Live(flow);
            Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");
            flow.Answer(SetupFlow.Landed);

            Assert.Equal(SetupFlow.AuditionRunningKey,
                Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CheckAgain)).Key);
            Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "no second outcome");
            Assert.Equal(2, probes.AuditionStarts);

            var back = new SetupFlow(Stumbled(stumble));
            Live(back);
            Assert.True(SpinWait.SpinUntil(back.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");
            back.Answer(SetupFlow.Landed);
            Assert.NotNull(back.Answer(SetupFlow.PickAnother));

            var on = new SetupFlow(Stumbled(stumble));
            Live(on);
            Assert.True(SpinWait.SpinUntil(on.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");
            on.Answer(SetupFlow.Landed);
            Assert.NotNull(on.Answer(SetupFlow.Anyway));
        }
    }

    //the clock gives 0 once then 302_820 ms, which is 5m 2s on screen. the frame drawn must be the one the product shows in that second
    private static Func<long> StillComing()
    {
        var opened = false;
        return () => { if (!opened) { opened = true; return 0; } return 302_820; };
    }

    //the key chars as the face reads them, NUL for a special key since a printable char would reach the typing path
    private static ConsoleKeyInfo Enter => new('\0', ConsoleKey.Enter, false, false, false);

    private static ConsoleKeyInfo Digit(char d) =>
        new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    //reach the load-ask screen by letting the flow's own clock pass the threshold, and wait on the predicate the face polls
    private static (SetupFlow Flow, WizardScreen.Choice Ask) Asking(WizardProbes probes)
    {
        //inject a small threshold rather than faking the clock, so the flow's real predicate runs on its real stopwatch
        var flow = new SetupFlow(probes) { LoadAskAfter = TimeSpan.FromMilliseconds(40) };
        Live(flow);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(20)),
            "the wizard never came back with a question");
        var ask = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.AuditionLoadAskKey, ask.Key);
        return (flow, ask);
    }

    //a slow load gets a question, giving up here would manufacture a verdict from a slow machine. only the time waited is drawn, the threshold stays off the screen
    [Fact]
    public void THE_LOAD_ASK_RENDERS_AT_100()
    {
        var (_, ask) = Asking(Probes(holdTheCheck: true));

        Golden.AssertEquals("s7", "still-loading", 100,
            WalkRender.Watching(ask, 100, tick: null, nowMs: StillComing(), check: null).Rows);
    }

    //the ask must answer Enter and a digit, a footer that offers Enter over a handler which ignores it is unanswerable. the test reads what Choose answered
    [Fact]
    public void THE_LOAD_ASK_ANSWERS_TO_ENTER_AND_TO_A_DIGIT()
    {
        var (_, ask) = Asking(Probes(holdTheCheck: true));

        Assert.Equal(SetupFlow.KeepWaiting,
            WalkRender.WatchAfterKeys(ask, 100, tick: null, [Enter]));
        Assert.Equal(SetupFlow.StopUnchecked,
            WalkRender.WatchAfterKeys(ask, 100, tick: null, [Digit('2')]));
    }

    //the Enter key must stay inert on every screen the OnlyTheWatchAdvances gate covers. this test drives two of the five, the rest are driven where they live
    [Fact]
    public void ENTER_STAYS_INERT_ON_EVERY_SCREEN_ONLY_THE_WATCH_ADVANCES()
    {
        var live = Live(new SetupFlow(Probes(holdTheCheck: true)));
        Assert.True(live.OnlyTheWatchAdvances);

        var (_, waiting) = Skipping(Answering());
        Assert.True(waiting.OnlyTheWatchAdvances);

        //the ask must report false here, otherwise the true assertions above never see the gate off
        var (_, ask) = Asking(Probes(holdTheCheck: true));
        Assert.False(ask.OnlyTheWatchAdvances);
    }

    //a question that dismisses itself must end on the screen a patient user would have reached, so the test asserts it
    [Fact]
    public void A_MODEL_THAT_FINALLY_LOADS_DISMISSES_THE_QUESTION_ABOUT_IT()
    {
        var probes = Probes();                       //a plain Probes() returns at once, standing in for a model that finishes loading
        var flow = new SetupFlow(probes);
        Live(flow);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");

        //the watch resolved because the check finished, so the walk continues to the outcome a patient user would have reached
        var landed = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.NotEqual(SetupFlow.AuditionLoadAskKey, landed.Key);
        Assert.Equal(SetupFlow.AuditionPassedKey, landed.Key);
    }

    //keep-waiting must not restart the check, a restart would throw away the minutes the user agreed to spend. the clock keeps running, one start stays counted
    [Fact]
    public void KEEPING_ON_WAITING_KEEPS_THE_SAME_CHECK()
    {
        var probes = Probes(holdTheCheck: true);
        var (flow, _) = Asking(probes);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.KeepWaiting));
        Assert.Equal(SetupFlow.AuditionRunningKey, back.Key);
        Assert.Equal(1, probes.AuditionStarts);

        //the question falls due again off the same clock, and still one check has started
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(20)), "no second question");
        Assert.Equal(SetupFlow.AuditionLoadAskKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);
        Assert.Equal(1, probes.AuditionStarts);
    }

    //a stop answer must join the check before the next screen exists, the runner's own end decides the server's fate
    [Fact]
    public void STOPPING_THE_WAIT_JOINS_THE_CHECK_BEFORE_MOVING_ON()
    {
        var probes = Probes(holdTheCheck: true);
        var (flow, _) = Asking(probes);

        Assert.NotNull(flow.Answer(SetupFlow.StopUnchecked));
        Assert.True(probes.AuditionUnwound, "the flow moved on while the check was still running");
    }
    //the question is about a model still coming up, so it must not fire once a task is running. the same fixture without that task must ask
    [Fact]
    public void A_RUNNING_BATTERY_IS_NOT_ASKED_ABOUT()
    {
        var battery = Gatto.Roles.Audition.Battery.Tasks;
        var counting = Probes(holdTheCheck: true);
        counting.AuditionMoments.Add(
            Gatto.Roles.Audition.AuditionProgress.Started(battery[0], 1, battery.Count));

        var flow = new SetupFlow(counting) { LoadAskAfter = TimeSpan.FromMilliseconds(20) };
        Live(flow);
        Assert.True(SpinWait.SpinUntil(() => flow.PollCheck() is { Total: > 0 },
            TimeSpan.FromSeconds(10)), "the task never started");

        //well past the threshold the watch stays put, there is nothing to ask about
        Assert.False(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromMilliseconds(400)),
            "the wizard asked about a load that had already finished");

        //the same fixture with no running task must ask, so the silence above comes from the task
        var (_, ask) = Asking(Probes(holdTheCheck: true));
        Assert.Equal(SetupFlow.AuditionLoadAskKey, ask.Key);

        flow.Answer(SetupFlow.AuditionStop);
    }

    //the predicate reads the task's own state, so a finished check is never asked about. the duration wait is deliberate, both conditions must hold at the answer
    [Fact]
    public void A_CHECK_THAT_FINISHED_IS_NEVER_ASKED_ABOUT()
    {
        var probes = Probes();                      //the fake check completes at once, so the threshold is what the wait runs on
        var flow = new SetupFlow(probes) { LoadAskAfter = TimeSpan.FromMilliseconds(150) };
        Live(flow);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "no outcome");

        //wait past the threshold after the check has finished, so both conditions hold at the answer.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        SpinWait.SpinUntil(() => waited.ElapsedMilliseconds > 400, TimeSpan.FromSeconds(2));

        var landed = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.AuditionPassedKey, landed.Key);
    }

    private static Gatto.Roles.Audition.AuditionTaskResult Task(
        string label, bool pass, bool skipped = false) =>
        new(label.Replace(" ", "-"), pass, [], TimeSpan.FromSeconds(2), label, skipped);

    //a passing probe with the runner's task records set, the Tasks field left null by Passing() is a fixture gap
    private static WizardProbes PassingWith(params Gatto.Roles.Audition.AuditionTaskResult[] tasks)
    {
        var p = Passing();
        return new WizardProbes
        {
            Llama = p.Llama,
            ActiveFile = p.ActiveFile,
            Audition = p.Audition with { Tasks = tasks },
        };
    }

    private static IReadOnlyList<WizardRow> PassBody(params Gatto.Roles.Audition.AuditionTaskResult[] tasks) =>
        Walk(PassingWith(tasks), SetupFlow.AuditionPassedKey).Settled.BodyRows!;

    //a pass that was not clean must name the failed task from the runner's own records. a row built from the tally would only count
    [Fact]
    public void A_PASS_WITH_A_FAILED_TASK_NAMES_IT()
    {
        var body = PassBody(
            Task("read a file", pass: true), Task("edit a file", pass: true),
            Task("run a command", pass: true), Task("call two tools", pass: true),
            Task("write a file", pass: false));

        Assert.Contains(body, r => r.Text.Contains("write a file", StringComparison.Ordinal)
            && r.Tone == RowTone.Aside);
        //only the failed task may be named, a headline that already counts would tell the same tally twice
        Assert.DoesNotContain(body, r => r.Text.Contains("read a file", StringComparison.Ordinal)
            || r.Text.Contains("edit a file", StringComparison.Ordinal)
            || r.Text.Contains("run a command", StringComparison.Ordinal)
            || r.Text.Contains("call two tools", StringComparison.Ordinal));
    }

    //five of five has nothing to name, so no extra row may render. the oracle is the task labels, a passing task has no reason for a guard to read
    [Fact]
    public void A_CLEAN_PASS_SAYS_NOTHING_EXTRA()
    {
        var body = PassBody(
            Task("read a file", pass: true), Task("edit a file", pass: true),
            Task("run a command", pass: true), Task("call two tools", pass: true),
            Task("write a file", pass: true));

        Assert.DoesNotContain(body, r => Labels.Any(l => r.Text.Contains(l, StringComparison.Ordinal)));
    }

    //the five task labels this file's fixtures use, so a guard can assert that no task is named
    private static readonly string[] Labels =
        ["read a file", "edit a file", "run a command", "call two tools", "write a file"];

    //a skipped task says not run, since blaming the model would describe a chance it never had. the tally alone knows only how many ran
    [Fact]
    public void A_TASK_THAT_WAS_CUT_SAYS_CUT_RATHER_THAN_WRONG()
    {
        var body = PassBody(
            Task("read a file", pass: true), Task("edit a file", pass: true),
            Task("run a command", pass: true), Task("call two tools", pass: true),
            Task("write a file", pass: false, skipped: true));

        var row = Assert.Single(body, r => r.Text.Contains("write a file", StringComparison.Ordinal));
        Assert.Contains("not run", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong result", row.Text, StringComparison.Ordinal);
    }

    //the Tasks field stays null on every unmeasured outcome, so no records is a real state and no row is drawn
    [Fact]
    public void A_PASS_WITH_NO_TASK_RECORDS_DRAWS_NO_ROW()
    {
        var body = Walk(Passing(), SetupFlow.AuditionPassedKey).Settled.BodyRows!;

        Assert.DoesNotContain(body, r => r.Text.Contains("not run", StringComparison.Ordinal)
            || r.Text.Contains("wrong result", StringComparison.Ordinal));
    }

}
