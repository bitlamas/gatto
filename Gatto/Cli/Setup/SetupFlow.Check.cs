
namespace Gatto.Cli.Setup;

//the audition offer and the in-session check, from the load ask through the run to its verdict
internal sealed partial class SetupFlow
{
    //the audition offer

    //one header for the whole step, two screens spelling it separately is how a step asks two questions
    private const string CheckTitle = "Can it run commands and edit files?";

    //offer after the scaffold's write, the audition starts its server from the written config
    private SetupScreen OfferAudition(string modelId) //never on the connect road (no model to audition)
    {
        _modelId = modelId;
        //read once here for the whole step, the field's own comment says why
        _modelFile = probes.ActiveFileFor(modelId);

        //never offer a check that cannot run, and skipping it in silence is worse, so the held-server state gets its own explaining screen
        if (probes.ServerHeldByAnother(modelId) is { } holding)
        {
            //in a session the server is held by the model the user talks to, so the check is a swap they can consent to. the refusal stays right for setup
            if (_addRoad) { _heldSaid = true; _heldBy = holding; return InSessionCheckAsk(holding, modelId); }
            //remembered so the completion does not say the same thing twice on this walk
            _heldSaid = true;
            return HeldScreen(holding, modelId);
        }
        //the ask comes before the explanation, body rows render above the question so both live in the body in that order
        return Emit(new SetupScreen.Choice(
            AuditionOfferKey,
            CheckTitle,
            [
                new ChoiceOption(Yes, "Yes, check it", Recommended: true),
                //the skip option states what it does, one load and one arrival, no tasks
                new ChoiceOption(Skip, "Skip the tasks, just make sure it answers"),
                .. _addRoad ? (IReadOnlyList<ChoiceOption>)[new ChoiceOption(AddAnother, AddAnotherLabel)] : [],
            ],
            BodyRows:
            [
                //open the body with the explanation, the question lives in the header. keep the timing in words and say only what was watched
                "gatto will give it five short tasks and watch what happens. "
                    + "Usually a minute or two, longer on a model that barely fits.",
                "",
                .. ModelRow(),
                //numbers map the live screen's task count onto tasks. use 1) rather than 1. so it cannot read as the screen's own options
                CheckFacts.Row("tasks",
                    [.. Roles.Audition.Battery.Tasks.Select((t, i) => $"{i + 1}) {t.Label}")]),
            ]),
            AuditionOfferKey);
    }

    //state the cost before the answer, the check stops the answering model's server and the conversation re-reads after
    private SetupScreen InSessionCheckAsk(string holding, string modelId) => Emit(new SetupScreen.Choice(
        InSessionCheckKey,
        $"Check {modelId} now?",
        [
            new ChoiceOption(Yes, "Check it now"),
            //this road has no switch, so the label promises adding unchecked
            new ChoiceOption(AddUnchecked, AddUncheckedLabel, EscVerb: "add it unchecked"),
            new ChoiceOption(AddAnother, AddAnotherLabel),
        ],
        BodyRows:
        [
            //the model is already added by the time this ask runs, the scaffold wrote first
            new SetupRow($"{modelId} is added to gatto."),
            //name servers, that is what the record holds
            new SetupRow($"The check runs it on the server, so {holding}'s server stops and "
                + $"{modelId}'s starts in its place."),
            //one row only, gatto cannot see what a client of the server does
        ]),
        InSessionCheckKey);

    private SetupScreen InSessionEnding()
    {
        //the add road never draws the summary (its first option starts gatto)
        return !_addRoad && _summaryOutcome is { } done ? SummaryScreen(done) : TerminalScreen();
    }

    //don't name a screen option back, Answer intercepts that key before the screen dispatch

    //yes releases the held server, it is the ask's own promise. no must not touch the server, its sentence says nothing changes
    private SetupScreen AnswerInSessionCheck(string key)
    {
        if (key == AddAnother && _modelId is not null) return AddAnotherModel();
        if (_modelId is null || key != Yes) return Done();
        probes.ReleaseHeldServer();
        return StartAudition(_modelId);
    }

    //gatto audition refuses while a model holds the server, so the sentence offers it for after and names the model that must stop first
    private SetupScreen HeldScreen(string holding, string modelId) => Emit(new SetupScreen.Choice(
        AuditionHeldKey,
        CheckTitle,
        [new ChoiceOption(AuditionHeldNext, "next"),
         new ChoiceOption(AuditionHeldLeave, "leave", Advances: false)],
        BodyRows:
        [
            new SetupRow("gatto can't check this model right now.", Glyph: RowGlyph.Warn),
            "",
            CheckFacts.Row("server", $"{holding} is running", lift: holding),
            CheckFacts.Cont($"checking {modelId} would unload it"),
            "",
            //accent the command rather than backtick it, the terminal shows the marks to the reader
            new SetupRow($"The check couldn't run, so gatto can't judge this model yet. Once "
                + $"{holding} is no longer running, type gatto audition {modelId} in a terminal to "
                + "run the same check.",
                RowTone.Aside, Highlight: [$"gatto audition {modelId}"]),
            "",
        ],
        KeysOnly: true), AuditionHeldKey);

    //the only screen on this step that may name a cause, the header arithmetic decides it before any spawn
    private SetupScreen IncompleteScreen(CheckStumble.Incomplete filed) => Emit(new SetupScreen.Choice(
        AuditionIncompleteKey,
        CheckTitle,
        [
            new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
            new ChoiceOption(CheckAgain, "Check it again", Advances: false),
        ],
        BodyRows:
        [
            new SetupRow("The model file is incomplete, gatto didn't try to load it.",
                Glyph: RowGlyph.Bad),
            "",
            CheckFacts.Row("file", filed.File, lift: filed.File),
            CheckFacts.Cont($"in {filed.Folder}"),
            CheckFacts.Row("found", $"{Gatto.Cli.SizeWords.Gb(filed.FoundBytes)} on disk",
                lift: Gatto.Cli.SizeWords.Gb(filed.FoundBytes)),
            CheckFacts.Row("expected",
                $"{Gatto.Cli.SizeWords.Gb(filed.ExpectedBytes)} or more, the file's own header says so",
                lift: $"{Gatto.Cli.SizeWords.Gb(filed.ExpectedBytes)} or more"),
            "",
            new SetupRow("A download or copy that stopped early looks like this. The check couldn't "
                + "run, so gatto can't judge this model yet, a complete copy of the same file may "
                + "run fine.", RowTone.Aside),
        ]), AuditionIncompleteKey);

    //no cause here, the exhibit is the server's own last lines, one row each and wrapped, llama.cpp puts the reason at the end
    private SetupScreen ExitedScreen(CheckStumble.Exited exited) => Emit(new SetupScreen.Choice(
        AuditionExitedKey, CheckTitle, EngineStumbleOptions,
        BodyRows: EngineStumbleRows(exited, exited.LastLines, exited.LogPath)),
        AuditionExitedKey);

    private SetupScreen StoppedScreen(CheckStumble.Stopped stopped) => Emit(new SetupScreen.Choice(
        AuditionStoppedKey, CheckTitle, EngineStumbleOptions,
        BodyRows: EngineStumbleRows(stopped, stopped.LastLines, logPath: null),
        //tasks ran here, so the purr reports the watch that ran. the exited arm never started anything
        PurredSinceWatch: true),
        AuditionStoppedKey);

    //the Anyway option is the only one that advances, the others go back or round the check again
    private static IReadOnlyList<ChoiceOption> EngineStumbleOptions =>
    [
        new ChoiceOption(CheckAgain, "Try the check again", Advances: false),
        new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
        new ChoiceOption(Anyway, "Carry on, use it unchecked"),
    ];

    private IReadOnlyList<SetupRow> EngineStumbleRows(
        CheckStumble stumble, IReadOnlyList<string> lines, string? logPath)
    {
        var stopped = stumble is CheckStumble.Stopped;
        var done = PollCheck() is { Done.Count: > 0 } t ? t.Done : [];

        return
        [
            new SetupRow(stopped
                ? "The server stopped while the check was running, nothing was measured."
                : "The server exited while loading the model, nothing was measured.",
                Glyph: RowGlyph.Bad),
            "",
            .. ModelRow(),
            .. done.Count > 0
                ? (IReadOnlyList<SetupRow>)[CheckFacts.Row("done", string.Join($" {Glyphs.Dot} ", done))]
                : [],
            //an absent log is said rather than dropped, a killed server writes nothing and "no log" is itself a fact
            .. lines.Count > 0
                ? (IReadOnlyList<SetupRow>)
                  [
                      //wide, because a narrower fact block folds the quote elsewhere, a visible diff on the screen whose job is quoting
                      CheckFacts.Row("last lines", lines[0], wide: true),
                      .. lines.Skip(1).Select(l => CheckFacts.Cont(l, wide: true)),
                  ]
                : [CheckFacts.Row("last lines", "none, the server exited without writing anything")],
            .. logPath is { Length: > 0 } log
                ? (IReadOnlyList<SetupRow>)[CheckFacts.Cont($"full log: {log}", wide: true)]
                : [],
            "",
            stopped
                ? new SetupRow("The check couldn't finish, so gatto can't judge this model yet. "
                    + $"Type gatto serve start {_modelId} in a new terminal to see what the "
                    + "server does on its own.",
                    RowTone.Aside, Highlight: [$"gatto serve start {_modelId}"])
                : new SetupRow("The check couldn't run, so gatto can't judge this model yet, the "
                    + "lines above are the server's own account of what happened.", RowTone.Aside),
        ];
    }

    //not a failure, the model just takes this long, so the question is real and the watch keeps running while it asks
    private SetupScreen LoadAsk() => Emit(new SetupScreen.Choice(
        AuditionLoadAskKey,
        CheckTitle,
        [
            new ChoiceOption(KeepWaiting, "Keep waiting", Advances: false),
            new ChoiceOption(StopUnchecked, "Stop the check, use it unchecked"),
            new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
        ],
        BodyRows:
        [
            new SetupRow("The model has been loading for a while and hasn't answered yet.",
                Glyph: RowGlyph.Warn),
            "",
            .. ModelRow(),
            CheckFacts.Row("server", "running, still loading"),
            "",
            new SetupRow("A model that barely fits can take longer than this. gatto won't wait "
                + "forever without asking, keep waiting, or carry on.", RowTone.Aside),
        ],
        Watching: true,
        FullPurr: true), AuditionLoadAskKey);

    //waiting again restarts nothing, same task and same clock. the other two cancel and join, the check must release the machine before the next screen
    private SetupScreen AnswerLoadAsk(string answer)
    {
        if (answer == KeepWaiting) return Running();

        _auditionCancel!.Cancel();
        var running = _audition!;
        _audition = null;
        try { running.GetAwaiter().GetResult(); }
        catch (Exception) { } //stopping is not a failure
        EndAudition();
        _checkClock = null;

        return answer == PickAnother ? Search() : Done();
    }

    //one home for the model row, four screens share it and cannot disagree about whether it is there
    private IReadOnlyList<SetupRow> ModelRow() =>
        _modelFile is { Length: > 0 } file ? [CheckFacts.Row("model", file, file)] : [];

    //re-entered once the model and llama-server path are on disk, carrying the id the writer produced, null on the connect path
    public SetupScreen ResumeAfterWrites(string? modelId)
    {
        NeedsWritesApplied = false;
        _applied = true;      //from here on, saying nothing was written would be a lie
        _backClosed = true;

        //the write set always holds the unapplied delta, so the total reset cannot forget a member. read the members the closing needs before clearing
        if (Writes.InstallTo is { } landed) _installedTo = landed;
        //the repair arm is captured for the same reason, the summary asks after the reset
        if (Writes.PathOnly) _pathOnlyLanded = true;
        //capture it before the reset, whether a default landed is a question about this apply, the default belongs to the model's own apply now
        var wroteDefault = Writes.DefaultModel is { Length: > 0 };
        Writes = new WriteSet();

        //record success from the apply that ran, either pause can write the default, LeavingRows says so instead of calling a finished setup unfinished
        if (wroteDefault || modelId is { Length: > 0 }) _defaultLanded = true;

        //route on the stage and read modelId only at scaffold, a guessed null id would send the Done-pause into Done()'s apply loop
        return _resumeStage switch
        {
            //a join goes to its own question, the model had its check offer when it was created
            ResumeStage.ScaffoldPause when _joining is { } j => QuantFromNow(j.Id, j.Quant),
            //connect has no model to audition, a null id goes on to Done and prove-it
            ResumeStage.ScaffoldPause => modelId is { Length: > 0 } id ? OfferAudition(id) : Done(),
            ResumeStage.DonePause => ProveScreen(),
            //the consent pause resumes into the summary, null covers the roads that end their own way so they terminate as before
            ResumeStage.ConsentPause => InSessionEnding(),
            ResumeStage.AnotherPause => BackForAnother(),
            _ => throw new InvalidOperationException("unreachable, ResumeStage is closed"),
        };
    }

    private SetupScreen AnswerAuditionOffer(string key) =>
        _modelId is null ? Done()
        : key == AddAnother ? AddAnotherModel()
        : key == Yes ? StartAudition(_modelId)
        //the skip is not a decline, gatto still makes sure the model loads and answers, one load and one arrival
        : key == Skip ? StartTheOneLoad()
        : Done();

    //run the load long-running like the check, a pool worker blocked on a cold load for minutes gets replaced on a timer
    private SetupScreen StartTheOneLoad()
    {
        ResetCheck();

        var id = _modelId;
        _loading = Task.Factory.StartNew(
            () => probes.ProveIt(id, new CheckSink(OnCheck)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Waiting();
    }

    //one waiting screen for two roads, the copy differs because Esc abandons different deeds, here only the one reply
    private SetupScreen Waiting() => Emit(new SetupScreen.Choice(
        AuditionWaitingKey,
        CheckTitle,
        [new ChoiceOption(SkipStop, "stop waiting", Advances: false)],
        BodyRows:
        [
            new SetupRow("Nothing to do here, gatto starts the server, loads the model and asks it "
                + "one question.", RowTone.Aside),
            "",
            .. ModelRow(),
        ],
        Watching: true,
        KeysOnly: true,
        Checking: true,
        FullPurr: true,
        Legend: "it stays set up either way",
        ArmedCost: "Esc again: stops waiting and carries on; the model stays set up"),
        AuditionWaitingKey);

    //claims only what the one load showed, commands were never judged, the speed row shares the stamp's composer so numbers cannot drift
    private SetupScreen Answers(Gatto.Core.Acquire.ProveOutcome outcome) => Emit(new SetupScreen.Choice(
        AuditionAnswersKey,
        CheckTitle,
        [new ChoiceOption(AnswersNext, "next"), new ChoiceOption(AnswersLeave, "leave", Advances: false)],
        BodyRows:
        [
            new SetupRow("It loads and answers.", RowTone.Aside, Glyph: RowGlyph.Good,
                Lift: ["It loads and answers."]),
            "",
            .. outcome.TokensPerSecond is { } rate
                ? (IReadOnlyList<SetupRow>)[SpeedRow(rate), ""]
                : [],
            new SetupRow("The five tasks were skipped, so gatto hasn't judged whether it can run "
                + $"commands or edit files, type gatto audition {_modelId} in a new terminal "
                + "whenever you want that check.",
                RowTone.Aside, Highlight: [$"gatto audition {_modelId}"]),
            "",
        ],
        KeysOnly: true,
        PurredSinceWatch: true), AuditionAnswersKey);

    //stop means stop waiting, ProveIt owns one bounded request and takes no token, the load finishes into a result nobody reads
    private SetupScreen AfterTheOneLoad(string answer)
    {
        var loading = _loading!;
        _loading = null;

        //stopping just means stop waiting (ProveIt owns one bounded request and takes no token to cancel), the load finishes into a result nobody reads
        if (answer == SkipStop) return Done();

        Gatto.Core.Acquire.ProveOutcome got;
        try { got = loading.GetAwaiter().GetResult(); }
        catch (Exception ex) { got = new Gatto.Core.Acquire.ProveOutcome(false, ex.Message, TimeSpan.Zero); }

        //keep the load's result, the completion screen reports it, don't load the model a second time for the same question
        _proved = got;
        return got.Ok ? Answers(got) : Done();
    }

    //run the check on a task so the screen keeps drawing. clear the finished tasks here so a second check starts clean
    private SetupScreen StartAudition(string modelId)
    {
        _auditionCancel = new CancellationTokenSource();
        var ct = _auditionCancel.Token;
        ResetCheck();
        //one wait, one clock, restarting it on keep waiting would double the time before the second question
        _checkClock ??= System.Diagnostics.Stopwatch.StartNew();

        //read Picked on this thread, the delegate runs on the pool (the repo id tags the badge for the search that offered it)
        var repoId = Picked?.RowFile?.RepoId;

        //ask the machine whether this model's server is up, a road flag is only a proxy. read it before StartNew or it races the task it describes
        _serverAlreadyUp = probes.ServerAlreadyUp(modelId);
        //run it LongRunning, a pool worker blocked for minutes gets replaced on a timer. hand the starter no token, it would mean don't start if cancelled
        _audition = Task.Factory.StartNew(
            () => probes.RunAudition(modelId, repoId, new CheckSink(OnCheck), ct),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Running();
    }

    //the live check screen, finished tasks are named and never ticked, its one option cancels so only the watch advances it
    private SetupScreen Running() => Emit(new SetupScreen.Choice(
        AuditionRunningKey,
        CheckTitle,
        [new ChoiceOption(AuditionStop, "stop the check", Advances: false)],
        BodyRows:
        [
            //this model's server may already be up, don't narrate it as work still to come
            new SetupRow(_serverAlreadyUp
                ? $"Nothing to do here, {_modelId} is on the server now; gatto gives it the five "
                    + "tasks and watches what happens."
                : "Nothing to do here, gatto starts the server, loads the model, gives it "
                    + "the five tasks and watches what happens.", RowTone.Aside),
            "",
            .. ModelRow(),
        ],
        Watching: true,
        KeysOnly: true,
        Checking: true,
        //short and watched, so it gets the REPL's own accordion rather than the download watch's calmer set
        FullPurr: true,
        //in a session the user added a model, they didn't run setup, so the legend must say added there
        Legend: _addRoad ? "stopping keeps it added, unchecked" : "stopping keeps it set up, unchecked",
        //the wizard's default armed sentence would lie here, Esc only stops the check and a stray press costs a minute
        ArmedCost: "Esc again: stops the check and carries on; what ran so far is lost, "
            + "the model stays set up"), AuditionRunningKey);

    //the join comes first, the run reports its moments from its own thread, reading them early gets a value that may not have arrived
    private SetupScreen AfterAudition(string answer)
    {
        //the watch also resolves for a question, judge which by the task's state (the clock would ask about a wait already over)
        if (answer == Landed && _audition is { IsCompleted: false } && LoadAskIsDue())
        {
            _asksMade++;
            return LoadAsk();
        }

        var running = _audition!;
        _audition = null;

        if (answer == AuditionStop)
        {
            _auditionCancel!.Cancel();
            //wait for it to unwind, the runner's finally decides what happens to the server. returning early shows done while the check still holds the machine
            try { running.GetAwaiter().GetResult(); } catch (Exception) { } //stopping the check is not a failure
            EndAudition();
            _checkStopped = true;
            //after a stop go to done unchecked, the runner leaves the wizard's server up (it never stops what it did not start)
            return Done();
        }

        AuditionCheck check;
        //a throw is a screen, the watch waits on IsCompleted, so don't park on a dead task. the probe maps its own failures, this is the backstop
        try { check = running.GetAwaiter().GetResult(); }
        catch (Exception) { check = new AuditionCheck(AuditionOutcome.CouldNotRun); }
        EndAudition();

        //keep the run for the completion screen, only this branch has measurements, the screen says something else when they're null
        _check = check;
        var outcome = check.Outcome;

        //a stumble with a known cause gets that cause's own screen, the generic one covers every reason gatto can't name
        if (check.Stumble is CheckStumble.Incomplete filed) return IncompleteScreen(filed);
        if (check.Stumble is CheckStumble.Exited exited) return ExitedScreen(exited);
        if (check.Stumble is CheckStumble.Stopped stopped) return StoppedScreen(stopped);

        //could-not-run has its own sentence, don't dress it up as either verdict
        if (outcome == AuditionOutcome.CouldNotRun)
        {
            //the server arm changes the row and the title on the same key (two screens a row apart make the reader guess)
            var unavailable = check.Stumble as CheckStumble.Unavailable;
            return Emit(new SetupScreen.Choice(
                AuditionFailedKey,
                unavailable is null
                    ? "Couldn't run the check just now"
                    : "Couldn't run the check: the server would not start.",
                [new ChoiceOption(Anyway, "Carry on")],
                //accent a command, don't backtick it (the terminal doesn't render backticks, the user reads the punctuation)
                BodyRows:
                [
                    .. unavailable is { } u
                        ? (IReadOnlyList<SetupRow>)[CheckFacts.Cont(u.Reason), ""]
                        : [],
                    //drop the gatto audition pointer on the server-stopped arm, the command refuses while another model holds the server
                    new SetupRow(unavailable is null
                        ? "Nothing was measured, so this says nothing about the model. "
                          + "gatto audition runs the same check whenever you want it."
                        : "Nothing was measured, so this says nothing about the model.",
                        Highlight: unavailable is null ? ["gatto audition"] : []),
                ]),
                AuditionFailedKey);
        }

        //even a pass gets a screen to answer. if nothing was measured go to Done, that screen would be a headline over nothing
        if (outcome == AuditionOutcome.Passed)
            return check.Facts is { } passed ? PassScreen(check, passed) : Done();

        return Emit(new SetupScreen.Choice(
                AuditionFailedKey,
                //the header stays the step's question, the verdict lives in the body (two headers would ask two questions on one step)
                CheckTitle,
                [
                    new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
                    new ChoiceOption(Anyway, "Use it anyway"),
                ],
                //recommend but keep the way on, it's the user's machine. the stamp sits above the mitigation, even a failing model can be fast
                BodyRows: FailRows(check, Glyphs),
                //reuse the live frame's clock for the settled purr, don't answer the same wait with a second one
                PurredSinceWatch: true),
                AuditionFailedKey);
    }


    //the pass screen waits for an answer, and the recorded row drops when no badge was written
    private SetupScreen PassScreen(AuditionCheck check, AuditionFacts f) => Emit(new SetupScreen.Choice(
        AuditionPassedKey,
        CheckTitle,
        [new ChoiceOption(AuditionPassedNext, "next"),
         new ChoiceOption(AuditionPassedLeave, "leave", Advances: false)],
        BodyRows:
        [
            new SetupRow(PassHeadline(f, Glyphs), RowTone.Aside, Glyph: RowGlyph.Good, Lift: [PassSentence]),
            "",
            //name the tasks that didn't pass from the runner's own records, the tally can't say which. a cut task didn't run, a failed one gave the wrong result
            .. check.Tasks is { } ran && ran.Any(t => !t.Pass)
                ? (IReadOnlyList<SetupRow>)[.. TaskRows([.. ran.Where(t => !t.Pass)]), ""]
                : [],
            .. StampRows(f),
            .. check.Recorded is { } on
                ? (IReadOnlyList<SetupRow>)["", CheckFacts.Row("recorded",
                    $"the shelf shows it as tool-calling verified {Glyphs.Dot} {on:yyyy-MM-dd}")]
                : [],
            //trailing blank, a KeysOnly screen has no option list, the last fact would sit on the footer rule
            "",
        ],
        KeysOnly: true,
        PurredSinceWatch: true), AuditionPassedKey);

    //only next carries on, every other key leaves (the footer promises which deed happened)
    private SetupScreen AnswerAuditionPassed(string key) =>
        key == AuditionPassedNext ? Done() : Leave();

    //one row per task, the words are TaskWords' (the CLI report draws the same sentences). the mark is semantic, a skipped task is not a failed one
    private static IReadOnlyList<SetupRow> TaskRows(IReadOnlyList<Gatto.Roles.Audition.AuditionTaskResult> tasks) =>
    [
        .. tasks.Select(t =>
        {
            var mark = t.Skipped ? RowGlyph.NotRun : t.Pass ? RowGlyph.Good : RowGlyph.Bad;
            var why = Gatto.Roles.Audition.TaskWords.Why(t);
            return new SetupRow(
                why is { Length: > 0 } said ? $"{t.Label}, {said}" : t.Label,
                //fact rows don't wrap (at 80 the longest is 79 cells, and a wrapped row breaks the mark column)
                RowTone.Aside, Indent: 2, Glyph: mark, Lift: [t.Label], Wide: true);
        }),
    ];

    //failure body: the stamp, then the mitigation. the blank row exists only when the stamp does
    private static IReadOnlyList<SetupRow> FailRows(AuditionCheck check, Gatto.Terminal.GlyphSet g)
    {
        var facts = check.Facts;
        var rows = new List<SetupRow>
        {
            //the tally's denominator is what ran, 2 of 4 beside an unrun row, AuditionVerdict computes it
            new(FailHeadline(facts, g), RowTone.Aside, Glyph: RowGlyph.Bad, Lift: [FailSentence]),
            "",
        };
        if (check.Tasks is { Count: > 0 } tasks)
        {
            rows.AddRange(TaskRows(tasks));
            rows.Add("");
        }

        if (facts is { } measured)
        {
            rows.AddRange(StampRows(measured));
            rows.Add("");
        }
        rows.Add("You can still use it if you want to.");
        return rows;
    }

    //a failed check warns and recommends, then proceeds, saying no changes nothing about the write
    private SetupScreen AnswerAuditionFailed(string key) =>
        key == PickAnother ? Search() : Done();

    //resolve a typed number against the screen in front of the user. a refusal names that screen and its own row count
    private static T Numbered<T>(IReadOnlyList<T> rows, string key, string screen)
    {
        //a negative can't parse today, keep the lower bound anyway for the day the styles accept a sign
        if (!int.TryParse(key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var at) || at < 0 || at >= rows.Count)
            throw new InvalidOperationException(
                $"{screen} was answered with [{key}], which is not one of its {rows.Count} rows");

        return rows[at];
    }
}
