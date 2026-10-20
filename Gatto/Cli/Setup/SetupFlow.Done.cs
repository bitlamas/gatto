using Gatto.Cli.Install;

namespace Gatto.Cli.Setup;

//done and what follows: another model, the prove, the connect check, the summary, the update ask and the leave
internal sealed partial class SetupFlow
{
    //shared

    //last write, it names the default and arms the pause before prove-it
    private SetupScreen Done()
    {
        //belt for the roads that scaffolded nothing, the pause applies it. on the add road fill it only when no default exists, overwriting one erases the user's choice
        if (_modelId is { } id)
            Writes = _addRoad
                ? Writes with { DefaultModelIfAbsent = id }
                : Writes with { DefaultModel = id };

        //arm the pause on every arrival, even with nothing to write, reuse and collision paths write their default here only
        _resumeStage = ResumeStage.DonePause;
        NeedsWritesApplied = true;
        return Emit(new SetupScreen.Info("default.ready", [$"Saving your choices{Glyphs.Ellipsis}"]), null);
    }

    //the write and the saving pause of Done, resumed to the shelf the model came from rather than to the prove load
    private SetupScreen AddAnotherModel()
    {
        var saving = Done();
        _resumeStage = ResumeStage.AnotherPause;
        return saving;
    }

    //the shelf comes back with the model on it and nothing behind it, its chips, lift, sort and search as the user left them
    private SetupScreen BackForAnother()
    {
        _added.Add((_modelId!, AddedHow));
        ResetForAnotherModel();
        //the local shelf rescans, so a model this walk downloaded is listed
        return _pickedFromLocal ? ToLocalShelf() : SearchScreen();
    }

    //the shelf the model was picked from, the Hub's (a typed id's one-row shelf included) or this machine's
    private bool _pickedFromLocal;

    //the models added before the one in hand, in the order added, each with its closing words
    private readonly List<(string Id, string How)> _added = [];

    //every field one model's walk sets, from its pick to its check, read off the declarations. the shelf's and the road's fields stay
    private void ResetForAnotherModel()
    {
        Picked = null;
        Selected = null;
        _modelId = null;
        _modelFile = null;
        _modelOffer = null;
        _modelView = null;
        _modelVerified = false;
        _modelFetch = null;
        _modelFetchCancel = null;
        _modelFetchPause = null;
        _modelPaused = false;
        _modelClock = null;
        _modelElapsedMs = 0;
        _heldSaid = false;
        _heldBy = null;
        _check = null;
        _checkStopped = false;
        _checkClock = null;
        _asksMade = 0;
        _audition = null;
        _auditionCancel = null;
        ResetCheck();
        _proved = null;
        _proveFailed = null;
        _proving = null;
        _loading = null;
        _summaryOutcome = null;
        _joining = null;
        _moveOffer = null;
        _moveReplace = false;
        _projector = null;
        _projectorReplace = false;
        _declinedEncoder = false;
        _clashingModelId = null;
        _arrivedByWatch = false;
        _droppedPartial = null;
        _watchMark = "";
        _watchCause = WatchCause.NoFingerprint;
        _watchingSet = null;
        _rootNote = null;
        _localChoice = null;
        lock (_tickGate) _tick = null;
        _resumeStage = ResumeStage.ScaffoldPause;
        _back.Clear();
        _backClosed = false;
        _controlStep = false;
    }

    //prove-it runs after the write so the saved sentence is true. guard its one run by counting probe calls, a ran-already flag would hide the routing bug
    private SetupScreen ProveScreen()
    {
        //the marker promises a question, paths that ask nothing withdraw it, the two paths that ask still carry it
        var firstQuestion = new SetupScreen.Info("step.first", [new SetupRow("First question")]);
        _narrate.Add(firstQuestion);
        //a stranger's server can take a cold load's time, so watch it and let Esc stop. start the task once, the watch comes back to this same screen
        if (Path == SetupPath.Connect && _proved is null)
        {
            if (_proving is null)
            {
                _proving = Task.Run(() => probes.ProveIt(_modelId), CancellationToken.None);
                _narrate.Remove(firstQuestion);
                return ConnectChecking();
            }

            //join before reading the outcome, it's produced on another thread
            _proved = _proving.GetAwaiter().GetResult();
            _proving = null;
        }

        //the skip road already loaded and asked, report its result, every other road proves here
        var outcome = _proved ?? probes.ProveIt(_modelId);

        //another model holds the server, so nothing was asked and nothing failed. the model is saved, the swap confirm comes next
        if (outcome.ServedByAnother is { } holding)
        {
            //keep the held outcome so the summary's held row is reachable
            _summaryOutcome = outcome;
            _narrate.Remove(firstQuestion);
            _narrate.Add(new SetupScreen.Info(ProveKey, [
                new SetupRow($"{_modelId} is saved. {holding} is still the running server, so "
                    + "nothing has been asked of it yet."),
                //only say it when the check step didn't already say it (the old whenever-you-want promise was false while a model holds the server)
                .. _heldSaid
                    ? (IReadOnlyList<SetupRow>)[]
                    : [new SetupRow($"Once {holding} is no longer running, type gatto audition "
                        + $"{_modelId} in a terminal to run the check.",
                        Highlight: [$"gatto audition {_modelId}"])],
                //one serve-restart sentence for both roads, the switch question the add branch promised no longer exists
                new SetupRow($"gatto serve stop, then gatto serve start {_modelId} when you "
                    + "want to run it.", Highlight: ["gatto serve stop", "gatto serve start"]),
            ]));
            return DoneStep();
        }

        if (outcome.Ok)
        {
            //in-session success is narrated rather than asked, start-now and give-me-the-command are both empty inside /model add
            if (_addRoad)
            {
                //narrate the close only on a face that doesn't draw the Choice's body rows, otherwise the user hears it twice
                if (!FaceShowsChoiceBodyRows)
                    _narrate.Add(new SetupScreen.Info(ProveKey, SuccessRows(outcome)));
                //the record prints this walk's facts, so the add road keeps its outcome too
                _summaryOutcome = outcome;
                //there's no REPL to start here, the consent question stays, it's asked once and only if unanswered
                return DoneStep();
            }

            //the connect check asks one question of a stranger's server, its facts are about that server rather than a model
            if (Path == SetupPath.Connect)
            {
                //keep the outcome so the connect road reaches the summary too
                _summaryOutcome = outcome;
                return Emit(new SetupScreen.Choice(
                    ProveKey,
                    ConnectCheckTitle,
                    [new ChoiceOption(OpenRepl, "next"),
                     new ChoiceOption(Finish, "leave", Advances: false)],
                    KeysOnly: true,
                    PurredMs: (long)outcome.FirstToken.TotalMilliseconds,
                    BodyRows: [
                        new SetupRow("It answers.", RowTone.Aside, Glyph: RowGlyph.Good),
                        "",
                        ProveFacts.Row("server", _connectBaseUrl ?? ""),
                        .. _connectModel is { Length: > 0 } named
                            ? (IReadOnlyList<SetupRow>)[ProveFacts.Row("model", Gatto.Core.Models.ServedModelName.Display(named))]
                            : [],
                        ProveFacts.Row("context",
                            $"{_connectContext:N0} {Glyphs.Dot} " + ContextNote()),
                        .. outcome.TokensPerSecond is { } rate
                            ? (IReadOnlyList<SetupRow>)[ProveFacts.Speed(rate)]
                            : [],
                        //trailing blank, a KeysOnly screen has no option list, the last fact would sit on the footer rule
                        "",
                    ]), ProveKey);
            }

            //the summary waits, its gatto row is computed from the install answer, so the questions come first
            _summaryOutcome = outcome;
            return DoneStep();
        }

        _proveFailed = outcome;
        //the capability sentence speaks only of a stranger's server, our own llama-server is never the cause. give each fact its own row
        var rows = new List<SetupRow> { outcome.Detail, "" };
        //the missing-capability sentence needs evidence, only 404 and 405 prove the chat route is absent. a reply in any other status proves the protocol is spoken
        if (_modelId is null && outcome.ServerAnswered is 404 or 405)
        {
            rows.Add(Gatto.Core.Acquire.ServerConnect.Capabilities[0].IfAbsent);
            rows.Add("");
        }
        //this sentence avoids the word setup on purpose, it is about the machine, which gatto doctor checks
        rows.Add(new("gatto doctor checks everything and says what to fix.",
            Highlight: ["gatto doctor"]));

        //the connect check fails on its own screen, its sentences are about a server, the screen below is about a model
        if (Path == SetupPath.Connect) return ConnectCheckFailed(outcome);

        //when the server never started, say that, a weaker headline sends the user to a model that was never loaded
        return Emit(new SetupScreen.Choice(
            ProveKey,
            //in-session nothing was set up, the headline says the model is saved
            (outcome.StartFailed, _addRoad) switch
            {
                (true, true) => "The model is saved, but its server didn't start",
                (true, false) => "The setup is written, but the server didn't start",
                (false, true) => "The model is saved, but it didn't answer",
                (false, false) => "The setup is written, but the model didn't answer",
            },
            [
                new ChoiceOption(Finish, "Finish anyway, I'll look into it"),
            ],
            BodyRows: rows), ProveKey);
    }

    //the screen lists what was asked, a user who doesn't know that is looking at the wrong server, and it names the stop cost
    private SetupScreen ConnectChecking() => Emit(new SetupScreen.Choice(
        ProveKey,
        ConnectCheckTitle,
        [new ChoiceOption(ConnectCheckStop, "stop the check", Advances: false)],
        BodyRows: [
            new SetupRow("Nothing to do here, gatto asks the server one small question and waits "
                + "for the first word back.", RowTone.Aside),
            "",
            ProveFacts.Row("server", _connectBaseUrl ?? ""),
            .. _connectModel is { Length: > 0 } named
                ? (IReadOnlyList<SetupRow>)[ProveFacts.Row("model", Gatto.Core.Models.ServedModelName.Display(named))]
                : [],
            ProveFacts.Row("asking", "\"Say OK.\", waiting for the first reply"),
            "",
        ],
        Watching: true,
        KeysOnly: true,
        FullPurr: true,
        Legend: "the server stays set up, unchecked"), ProveKey);

    //branch on the status code, a message text is not evidence. offer try again only where asking twice could help
    private SetupScreen ConnectCheckFailed(Gatto.Core.Acquire.ProveOutcome outcome)
    {
        var (verdict, said, advice, canRetry) = outcome.ServerAnswered switch
        {
            //it answered when gatto found it, so a silence now is transport, don't blame the protocol
            null => ("The server stopped answering, nothing was asked.",
                     "nothing, the connection was refused",
                     "It answered a moment ago, when gatto found it. Check that it is still "
                     + "running, then try again.",
                     true),
            //it streamed and sent nothing, so the protocol is fine and asking again can get an answer
            200 when outcome.StreamedEmpty => ("The server answered, but its reply was empty.",
                    $"200 {Glyphs.Dot} the stream closed with no content",
                    "Check that the server has a model loaded and that it answers a chat request, then try again.",
                    true),
            200 => ("The server answered, but didn't stream the reply.",
                    $"200 {Glyphs.Dot} a reply arrived whole, not streamed",
                    Gatto.Core.Acquire.ServerConnect.Capabilities[0].IfAbsent,
                    false),
            //one verdict true of both codes, the row underneath carries the difference. retrying can't change this, so don't offer it
            404 or 405 => ("The server answered, but not to a chat request.",
                           $"{outcome.ServerAnswered} {Glyphs.Dot} " + (outcome.ServerAnswered is 404
                               ? "the chat endpoint is not there"
                               : "the chat endpoint refused the request"),
                           Gatto.Core.Acquire.ServerConnect.Capabilities[0].IfAbsent,
                           false),
            var status => ("The server answered, but said no.",
                           $"{status} {Glyphs.Dot} \"{outcome.Detail}\"",
                           "A server that answers is one gatto can talk to. Do what it asks, then "
                           + "try again.",
                           true),
        };

        return Emit(new SetupScreen.Choice(
            ProveKey,
            ConnectCheckTitle,
            canRetry
                ? [new ChoiceOption(Retry, "Try again"),
                   new ChoiceOption(Finish, "Finish anyway, I'll look into it")]
                : [new ChoiceOption(Finish, "Finish anyway, I'll look into it")],
            BodyRows: [
                new SetupRow(verdict, RowTone.Aside, Glyph: RowGlyph.Bad),
                "",
                ProveFacts.Row("server", _connectBaseUrl ?? ""),
                ProveFacts.Row("said", said),
                "",
                new SetupRow(advice),
            ]), ProveKey);
    }

    //accept only answers the awaited screen actually drew, several screens share a handler and an answer can land silently on the wrong one
    private void RefuseUndrawn(string key, string screen)
    {
        if (_awaitingOptions is { } drew && !drew.Contains(key, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"the {screen} screen offered [{string.Join(", ", drew)}] and was answered '{key}'");
    }

    //land somewhere, don't exit into a vanishing console: either gatto opens or the next command is on screen
    private SetupScreen AnswerProve(string key)
    {
        RefuseUndrawn(key, "prove");
        _awaiting = null;
        //retry re-runs the check, that's the deed the row names
        if (key == Retry) return ProveScreen();

        //both stop and finish return through the watch, only the arriving key tells them apart
        if (_proving is not null)
        {
            if (key == ConnectCheckStop)
            {
                //stop means stop waiting, let the task finish on its own, nothing reads it after
                _proving = null;
                return Complete(_connectBaseUrl!, _connectContext);
            }
            return ProveScreen();     //join here, then report the verdict
        }
        StartReplWhenDone = key == OpenRepl;
        return DoneStep();
    }

    //the summary's two options go two places, it has its own key so no screen asks whether it is the summary
    private SetupScreen AnswerSummary(string key)
    {
        RefuseUndrawn(key, "summary");
        _awaiting = null;
        StartReplWhenDone = key == OpenRepl;
        return TerminalScreen();
    }

    //the done step's questions in one method, each call site tested. in session the install question is skipped, /model add must not offer to install
    private SetupScreen DoneStep() =>
        (_addRoad ? null : InstallSegment()) ?? AfterTheInstallAsk();

    //the consent is asked last, once, an existing answer skips the ask (once comes from the probe's read)
    private SetupScreen AfterTheInstallAsk()
    {
        //the one place that can say the install question is past, both roads arrive here
        _pastTheInstallAsk = true;
        return probes.UpdateConsent() is null ? UpdateAsk() : CloseWizard();
    }

    //the summary draws after both questions, its gatto row computes from the install answer and the config path exists only past the consent pause
    private SetupScreen SummaryScreen(Gatto.Core.Acquire.ProveOutcome outcome) =>
        Emit(new SetupScreen.Choice(
            SummaryKey,
            //the setup line appears only here and there's no congratulations, the record itself is the celebration
            "gatto is set up.",
            [
                new ChoiceOption(OpenRepl, "Start gatto now"),
                //the option promises instructions, that's what the closing sentence actually gives
                new ChoiceOption(Finish, "Not now, show me how to start it later"),
            ],
            BodyRows: SuccessRows(outcome)), SummaryKey);

    //null means this walk draws no summary, the connect road, in-session and the held arm end their own way
    private Gatto.Core.Acquire.ProveOutcome? _summaryOutcome;

    private SetupScreen UpdateAsk() => Emit(new SetupScreen.Choice(
        UpdateKey,
        "Should gatto check for updates?",
        [
            //no pre-selection, a highlighted recommendation on a network question pushes the user toward gatto's preference
            new ChoiceOption(UpdateYes, "Yes, check weekly"),
            new ChoiceOption(UpdateNo, "No"),
        ],
        BodyRows:
        [
            new SetupRow("Once a week, gatto asks GitHub whether a newer release exists. "
                + "Nothing about you or your machine is sent."),
            //a blank row separates the two paragraphs, without it they read as one block
            "",
            //say how to change it, a consent you can't revoke isn't consent
            new SetupRow("Say no and gatto never contacts the network on its own. "
                + "You can change it later with the update_check key in gatto.json."),
        ],
        //this screen has no default answer, the cursor must start from nothing
        NoDefault: true), UpdateKey);

    private SetupScreen AnswerUpdate(string key)
    {
        _awaiting = null;
        Writes = Writes with { UpdateCheck = key == UpdateYes };
        return CloseWizard();
    }

    //write the consent answer before the terminal renders. can't arm the pause and return Terminal at once, the runner drops that return and done prints twice
    private SetupScreen CloseWizard()
    {
        _resumeStage = ResumeStage.ConsentPause;
        NeedsWritesApplied = true;
        return Emit(new SetupScreen.Info("consent.ready", [$"Saving your answer{Glyphs.Ellipsis}"]), null);
    }

    //the run-gatto-then-slash-model line, its id from what this walk added (one test pins it literally)
    internal SetupRow AddRoadNextStep =>
        new($"run gatto, then /model {_modelId} to use it",
            Highlight: ["gatto", $"/model {_modelId}"]);

    //the closing sentence reads what the install really did, a null InstallTo means many things, _installState tells them apart
    internal SetupRow CompletionNextStep =>
        //the intent is consumed at the pause, check _installedTo too or an accepted install reads as declined
        (Writes.InstallTo ?? _installedTo) is not null
            //the new-terminal caveat is only true when the install was accepted
            ? new SetupRow(
                "A new terminal will find it; this one will not. When you're ready, open a new "
                + "terminal in your project folder and type gatto. Have fun with your local model!",
                Highlight: ["gatto"])
        : _installState switch
        {
            //already findable from any terminal, so the new-terminal caveat warns about a problem this user doesn't have
            InstallState.Installed or InstallState.AlreadyInstalledElsewhere => new SetupRow(
                "Open a terminal in your project folder and type gatto. Have fun with your local "
                + "model!", Highlight: ["gatto"]),

            //declined, so name where it runs from and offer gatto setup without pushing
            InstallState.NotInstalled or InstallState.InstalledButNotOnPath => new SetupRow(
                $"gatto stays where it is, {probes.RunningFrom()}. Run it from there; gatto setup "
                + "can install it later. Have fun with your local model!",
                Highlight: ["gatto setup"]),

            //a dev build was never installable, so nothing here was declined
            InstallState.DevBuild => new SetupRow(
                "Run gatto from where it is. Have fun with your local model!", Highlight: ["gatto"]),

            _ => new SetupRow("Have fun with your local model!"),
        };

    //the closing line, read by both the done screen and the record, so one walk can't close twice
    private SetupRow FinishedNextStep => _addRoad ? AddRoadNextStep : CompletionNextStep;

    private SetupScreen TerminalScreen()
    {
        _finished = true;
        //in-session this screen carries nothing, the swap confirm is next. standalone keeps one deliberate blank before the closing line
        return Emit(new SetupScreen.Terminal(
            "done",
            //the blank returns on the add road, it ends at a shell prompt like setup's
            StartReplWhenDone ? [] : [""],
            //the starting line is true only while a server happens to be running, don't paper over the gap till auto-serve ships
            NextStep: StartReplWhenDone ? new SetupRow($"Starting gatto{Glyphs.Ellipsis}")
                : FinishedNextStep,
            Success: true), null);
    }

    //one stamp composer for the pass, failure and completion screens, drop every row whose fact is missing
    private static IReadOnlyList<SetupRow> StampRows(AuditionFacts f)
    {
        var rows = new List<SetupRow>();
        //the quant gets lifted, its neighbour notes don't, the quant is what readers scan for
        if (f.Quant is { Length: > 0 } q) rows.Add(CheckFacts.Row("quant", q, lift: q));
        if (f.Sampling is { Length: > 0 } s) rows.Add(CheckFacts.Row("sampling", s));
        if (f.Thinking is { Length: > 0 } t) rows.Add(CheckFacts.Row("thinking", t));
        if (f.TokensPerSecond is { } rate)
        {
            //round the number first and read the band off the rounded value, else the sentence contradicts what's on screen
            var shown = Math.Round(rate, 1);
            var figure = $"~{shown:0.#} tok/s";
            //accent the speed figure, it's the number readers turn into an expectation of waiting
            rows.Add(SpeedRow(rate));
        }

        return rows;
    }

    //one number feeds figure and band words, rounded first so they match. the figure takes the accent, it's what readers turn into wait time
    private static SetupRow SpeedRow(double rate)
    {
        var shown = Math.Round(rate, 1);
        var figure = $"~{shown:0.#} tok/s";
        return new SetupRow(
            "speed".PadRight(CheckFacts.Column)
                + $"{figure}, {Gatto.Roles.Audition.AuditionReport.SpeedNote(shown)}",
            RowTone.Aside, Hang: CheckFacts.Column, Highlight: [figure]);
    }

    //its own constant so the row can lift the sentence out of the aside, the tally beside it stays context
    private const string FailSentence = "This model struggled to run commands and edit files";

    //kept apart from the pass headline though only the joiner is shared, two verdicts shouldn't become one line
    private static string FailHeadline(AuditionFacts? f, Gatto.Terminal.GlyphSet g) =>
        FailSentence
        + (f is { TasksPassed: { } passed, TasksTotal: { } total }
            ? $" {g.Dot} {passed} of {total} tasks"
            : "");

    //its own constant because the row lifts it exactly, the sentence and its tally take different ink
    private const string PassSentence = "It ran commands and edited files";

    //the sentence plus the verdict's own tally, dropped when absent. no glyph in the text, the row draws its own, and the joiner is a middle dot
    private static string PassHeadline(AuditionFacts f, Gatto.Terminal.GlyphSet g) =>
        PassSentence
        + (f is { TasksPassed: { } passed, TasksTotal: { } total }
            ? $" {g.Dot} {passed} of {total} tasks"
            : "");


    //the closing facts, each computed from the deed. a row drops when nothing measured it, no unknown and no blank
    private IReadOnlyList<SetupRow> SuccessRows(Gatto.Core.Acquire.ProveOutcome outcome)
    {
        return Tui.Epilogue.Compose(WalkFacts(outcome), Tui.Epilogue.Face.Summary, Glyphs);
    }

    //the record from the same composer as the summary, empty when no summary was reached so the printer skips it
    internal IReadOnlyList<SetupRow> RecordRows() =>
        //a crash keeps the last frame, only a real leave or a finished walk prints a record
        _summaryOutcome is not null || _left || _finished ? RecordFacts() : [];

    //a leave records with a null outcome, CheckRow says not-run. empty before the write pause, a walk with nothing on disk has no record
    private IReadOnlyList<SetupRow> RecordFacts() =>
        _summaryOutcome is { } outcome
            ? Tui.Epilogue.Compose(WalkFacts(outcome), Tui.Epilogue.Face.Record, Glyphs)
        : _applied
            ? Tui.Epilogue.Compose(WalkFacts(null), Tui.Epilogue.Face.Record, Glyphs)
            : [];

    //whether the user left the wizard, as distinct from a run that threw (a throw keeps the last frame)
    internal bool Left => _left;

    //the sentence a leave that wrote nothing prints. gatto model names the add command, gatto setup keeps its own
    internal SetupRow? PrintedLeave =>
        _addRoad
            ? new SetupRow($"nothing added {Glyphs.Dot} gatto model whenever you're ready", Highlight: ["gatto model"])
            : LeaveStep;

    //gatto model's closing is one line per model added under the header, in the order added, like its nothing-added leave. empty on setup, before the writes, and on a throw
    internal IReadOnlyList<SetupRow> AddedClosings
    {
        get
        {
            if (!_addRoad || !_applied || !(_finished || _left)) return [];
            List<(string Id, string How)> added = [.. _added];
            if (_modelId is { } current) added.Add((current, AddedHow));
            if (added.Count == 0) return [];
            //the earlier models have their own outcome words, and only the last row says how to use its model
            var rows = added.SkipLast(1).Select(a => new SetupRow($"{a.Id} added{a.How}")).ToList();
            var (id, how) = added[^1];
            rows.Add(new SetupRow($"{id} added{how} {Glyphs.Dot} "
                + (_modelId is not null && _proveFailed is not null && _summaryOutcome is null
                    ? "gatto doctor checks everything and says what to fix"
                    : $"run gatto, then /model {id} to use it"),
                Highlight: ["gatto doctor", "gatto", $"/model {id}"]));
            return rows;
        }
    }

    //what the user did with the check, read from the deed. a server that failed after the check outranks its verdict, the tail sends the user to doctor
    private string AddedHow =>
        _proveFailed is { StartFailed: true } && _summaryOutcome is null ? ", but its server didn't start"
        : _proveFailed is not null && _summaryOutcome is null ? ", but it didn't answer"
        : _check is { Outcome: AuditionOutcome.Passed } ? " and checked"
        : _check is { Outcome: AuditionOutcome.Failed } ? ", it struggled on the check and you kept it"
        : _check is { Outcome: AuditionOutcome.CouldNotRun } ? ", the check couldn't run"
        : _checkStopped ? ", you stopped the check"
        : _summaryOutcome?.ServedByAnother is { } holding ? $", not checked while {holding} holds the server"
        //a model added for another with no check run keeps the holder the check ask named
        : !_left && _check is null && _summaryOutcome is null && _heldBy is { } held ? $", not checked while {held} holds the server"
        : _proved is not null ? ", it answers and the five tasks were skipped"
        : _left ? ", you left before checking it"
        : ", not checked";

    //the flow picks the closing row, null on the road whose tail is a live countdown, it writes the tail itself
    internal SetupRow? RecordClosing =>
        StartReplWhenDone ? null
        : _left ? LeaveStep
        : FinishedNextStep;

    //where the walk stopped, in the words the welcome map uses. null rather than a guess when the screen isn't on the strip
    internal string? LeftAt =>
        _left && _lastShown is { } shown && WalkSection.Of.TryGetValue(shown, out var section)
            ? $"left at the {section}"
            : null;

    //read each missed question from what can answer it, install from the probe, consent from what's on disk (an old answer was not missed)
    private IReadOnlyList<Tui.Epilogue.LeaveQuestion> UnreachedQuestions()
    {
        List<Tui.Epilogue.LeaveQuestion> unreached = [];
        //the add road never reaches the install question, so it can't have been missed either
        if (!_addRoad && !_pastTheInstallAsk && InstallAsks(probes.InstallStatus().State))
            unreached.Add(Tui.Epilogue.LeaveQuestion.Install);
        if (probes.UpdateConsent() is null)
            unreached.Add(Tui.Epilogue.LeaveQuestion.Updates);
        return unreached;
    }

    //which sentence a leave opens with, from what actually landed
    private Tui.Epilogue.LeaveLead Lead =>
        !_applied ? Tui.Epilogue.LeaveLead.NothingWritten
        : !_defaultLanded ? Tui.Epilogue.LeaveLead.SavedNotPointed
        : _modelId is { Length: > 0 } ? Tui.Epilogue.LeaveLead.PointedAt
        //the connect road points at someone else's server, no model to name, the server row above says it already
        : Tui.Epilogue.LeaveLead.SetUp;

    //the finished walk's facts, read once per render so two renders can't disagree
    private Tui.Epilogue.Facts WalkFacts(Gatto.Core.Acquire.ProveOutcome? outcome)
    {
        var (model, folder) = ModelRows();
        return new Tui.Epilogue.Facts(
            MachineRow(), model, folder, EngineRow(), CheckRow(outcome), GattoRow(),
            //accent the config path, it's the one thing on screen a user may want to open
            SummaryFacts.Row("config", probes.ConfigPath(), probes.ConfigPath()),
            ServerFact());
    }

    //where the context number came from, said once, the check screen and the summary share the wording
    private string ContextNote() =>
        _contextWasReported
            ? "reported by the server"
            : "your input, the server didn't report its context window";

    //hand on the server facts as data, the composer picks the rows. the context note comes from ContextNote, same wording everywhere
    private Tui.Epilogue.ServerFact? ServerFact() =>
        Path == SetupPath.Connect && _connectBaseUrl is { Length: > 0 } url
            ? new Tui.Epilogue.ServerFact(url,
                _connectModel is { } served ? Gatto.Core.Models.ServedModelName.Display(served) : null, $"{_connectContext:N0}",
                ContextNote(),
                //endpoint names stay in the config, the row's job is which server it talks to and that the earlier setup is kept
                _besideLocal ? "used from now on, the earlier setup is kept" : null)
            : null;

    //the machine fact row, one grammar across the four shapes. state one budget and say where it applies, promise nothing a record can't keep
    private SetupRow? MachineRow()
    {
        if (probes.Hardware() is not { } hw) return null;

        var c = Gatto.Core.Hardware.HardwareClassifier.Classify(hw);
        var installed = SizeWords.WholeGb(hw.InstalledBytes ?? hw.OsVisibleBytes);

        return SummaryFacts.Row("machine", c.Shape switch
        {
            Gatto.Core.Hardware.MachineShape.UnifiedWithShare =>
                $"{installed} shared with the graphics chip {Glyphs.Dot} about "
                + $"{SizeWords.WholeGb(c.GpuBudgetBytes)} for a model",

            Gatto.Core.Hardware.MachineShape.UnifiedNoShare =>
                $"{installed} {Glyphs.Dot} the graphics chip has almost none of its own {Glyphs.Dot} about "
                + $"{SizeWords.WholeGb(c.RamBudgetBytes)} for a model",

            //a card too small to hold a model says so straight, don't say about 0 GB
            Gatto.Core.Hardware.MachineShape.Discrete when c.GpuBudgetBytes == 0 =>
                $"{installed} {Glyphs.Dot} a {SizeWords.WholeGb(hw.GraphicsMemoryBytes ?? 0)} card, too "
                + $"small for a model {Glyphs.Dot} about {SizeWords.WholeGb(c.RamBudgetBytes)} for a model",

            //don't default card memory to 0, this arm always has one, a printed 0 GB would be a wrong number
            Gatto.Core.Hardware.MachineShape.Discrete =>
                $"{installed} {Glyphs.Dot} a graphics card with "
                + $"{SizeWords.WholeGb(hw.GraphicsMemoryBytes ?? throw new InvalidOperationException(
                    "a discrete machine with no readable card memory: the parser reports the discrete "
                    + "kind only with a heap behind it, so this arm cannot be reached without one$"))} {Glyphs.Dot} "
                + $"about {SizeWords.WholeGb(c.GpuBudgetBytes)} for a model on the card",

            //say no usable graphics driver, that's a fact about the machine, the old wording described the probe
            _ => $"{installed} {Glyphs.Dot} no graphics driver gatto can use {Glyphs.Dot} about "
                 + $"{SizeWords.WholeGb(c.RamBudgetBytes)} for a model",
        });
    }

    //the folder line reads the file's own directory, a config key never appears here. verified shows only when this walk verified it
    private (SetupRow? Row, SetupRow? Folder) ModelRows()
    {
        if (_modelId is not { Length: > 0 } id) return (null, null);
        if (probes.ActiveModelFile(id) is not { } file) return (null, null);

        var facts = $"{System.IO.Path.GetFileName(file.Path)} {Glyphs.Dot} {SizeWords.Auto(file.Bytes)}";
        return (
            SummaryFacts.Row("model", _modelVerified ? facts + $" {Glyphs.Dot} verified" : facts),
            SummaryFacts.Row("", System.IO.Path.GetDirectoryName(file.Path) + System.IO.Path.DirectorySeparatorChar));
    }

    //the found road drops the backend word from the engine row, only the truth fills it
    private SetupRow? EngineRow()
    {
        if (_offer is { } offer)
            return SummaryFacts.Row("engine",
                $"llama.cpp {Gatto.Roles.Serve.LlamaAssetSteering.PinnedRelease} {Glyphs.Dot} {offer.Pick.BuildName}"
                + $" {Glyphs.Dot} {offer.Into}");

        if (_llamaPath is { Length: > 0 } exe && _foundBuild is { Length: > 0 } build)
            return SummaryFacts.Row("engine",
                $"llama.cpp {build} {Glyphs.Dot} {System.IO.Path.GetDirectoryName(exe)}"
                + System.IO.Path.DirectorySeparatorChar);

        return null;
    }

    //the check row is a verdict sentence, no numbers. the arms must be tested in order, a held server is also a walk with no audition
    private SetupRow CheckRow(Gatto.Core.Acquire.ProveOutcome? outcome, bool pointer = true)
    {
        //a connect server was only asked whether it answers, the row says just that
        if (Path == SetupPath.Connect)
            return SummaryFacts.Row("check", $"{Glyphs.Ok} it loads and answers");

        if (outcome?.ServedByAnother is not null)
            //the row states the fact and promises nothing, the serve stop and start advice is said once in the narration
            return SummaryFacts.Row("check",
                $"{Glyphs.Warn} not run, another model holds the server");

        if (_check is { Outcome: AuditionOutcome.Passed })
            return SummaryFacts.Row("check", $"{Glyphs.Ok} passed, it ran commands and edited files");

        if (_check is { Outcome: AuditionOutcome.Failed })
            return SummaryFacts.Row("check", $"{Glyphs.Bad} struggled, you chose to use it anyway");

        //the skip road loaded once and got an answer, the row says exactly that much
        if (_proved is not null)
            return SummaryFacts.Row("check", $"{Glyphs.Ok} it loads and answers, the five tasks were skipped");

        //the done-step row shows the fact and stops, the audition promise holds only after a branch is picked
        if (!pointer) return SummaryFacts.Row("check", $"{Glyphs.Warn} not run");

        //the command is one unbreakable accent token, so the row takes a lift
        var run = $"gatto audition {_modelId}";
        return SummaryFacts.Row("check", $"{Glyphs.Warn} not run, {run} runs the check whenever you want it", run);
    }

    //read the deed before the state, an accepted install is true whatever the probe said. only the installed form gets a glyph, facts get no verdict
    private SetupRow GattoRow()
    {
        //the add road skips the install step, the one place the probe is read, so an unread state is read here
        var (state, dir) = _installState is { } read ? (read, _installDir) : probes.InstallStatus();
        //check the intent and the landed fact, the write set is cleared before this row draws
        if (Writes.InstallTo is { Length: > 0 } || _installedTo is { Length: > 0 }
            || Writes.PathOnly || _pathOnlyLanded
            || state is InstallState.Installed)
            return SummaryFacts.Row("gatto", $"{Glyphs.Ok} installed, any terminal finds it");

        return state switch
        {
            InstallState.DevBuild =>
                SummaryFacts.Row("gatto", "runs from a development build"),
            InstallState.AlreadyInstalledElsewhere =>
                SummaryFacts.Row("gatto",
                    $"runs from {probes.RunningFrom()}, gatto is also installed at {dir}"),
            _ => SummaryFacts.Row("gatto", $"not installed, runs from {probes.RunningFrom()}"),
        };
    }


    //the plural follows the rounded number the reader sees, 1.4 renders 1 and reads 1 second
    private static string Seconds(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "under a second";
        var shown = (int)Math.Round(t.TotalSeconds, MidpointRounding.AwayFromZero);
        return Gatto.Core.Plural.Of(shown, "second");
    }


    private SetupScreen Leave()
    {
        _awaiting = null;

        //leaving means intending nothing, this clear is explicit insurance, no apply path reaches it today
        Writes = new WriteSet();
        //mark leaving before composing, LeavingRows and LeaveStep read it
        MarkLeaving();
        return Emit(new SetupScreen.Terminal(
            "left", LeavingRows, NextStep: LeaveStep, Success: true), null);
    }

    //the leave copy says what actually landed, silent before the writes, ownership after. one home because Esc shows this too

    //the leave's rows are the record on both roads
    internal IReadOnlyList<SetupRow> LeavingRows => RecordFacts();

    //the sentence is what the frame draws, comma included, a test reads it against that golden
    internal const string NothingAddedText = "Nothing added, gatto model whenever you're ready";

    //the computed leave sentence sits in the next-step slot, it replaces the generic pair instead of adding a row
    internal SetupRow? LeaveStep =>
        !_addRoad ? Tui.Epilogue.LeaveSentence(Lead, _modelId, UnreachedQuestions())
        : _applied ? null
        : new SetupRow(NothingAddedText, Highlight: ["gatto model"]);

    //classify once from the same probe so the hardware sentence and the shelf agree. a null snapshot borrows CpuOnly, the shelf only asks discrete-or-not
    private Gatto.Core.Hardware.MachineShape Shape =>
        probes.Hardware() is { } hw
            ? Gatto.Core.Hardware.HardwareClassifier.Classify(hw).Shape
            : Gatto.Core.Hardware.MachineShape.CpuOnly;

    //quote what a model can use rather than the raw inventory, whole GB with the rounding Math.Round gives. the line says what fits and leaves speed to the audition
    private static string HardwareLine(Gatto.Core.Hardware.HardwareSnapshot? hw)
    {
        if (hw is null)
            return "Couldn't read this machine's memory, so nothing on the shelf is priced against it.";

        var c = Gatto.Core.Hardware.HardwareClassifier.Classify(hw);
        var installed = WholeGb(hw.InstalledBytes ?? hw.OsVisibleBytes);

        return c.Shape switch
        {
            Gatto.Core.Hardware.MachineShape.UnifiedWithShare =>
                $"This machine: {installed} of memory shared with the graphics chip, a model can "
                + $"use about {WholeGb(c.GpuBudgetBytes)} of it.",

            //almost none of its own rather than a number, an unusable carve-out printed as 0 GB is a fact nobody can act on
            Gatto.Core.Hardware.MachineShape.UnifiedNoShare =>
                $"This machine: {installed} of memory. Its graphics chip has almost none of its "
                + $"own, so models run in system memory, about {WholeGb(c.RamBudgetBytes)} of it.",

            //a card whose budget rounds to zero is too small, say that rather than offering about 0 GB
            Gatto.Core.Hardware.MachineShape.Discrete when c.GpuBudgetBytes == 0 =>
                $"This machine: {installed} of memory, and a graphics card with "
                + $"{WholeGb(hw.GraphicsMemoryBytes ?? 0)} that is too small for a model, so models "
                + $"run in system memory, about {WholeGb(c.RamBudgetBytes)} of it.",

            Gatto.Core.Hardware.MachineShape.Discrete =>
                $"This machine: {installed} of memory, and a graphics card with "
                + $"{WholeGb(hw.GraphicsMemoryBytes ?? 0)}, a model can use about "
                + $"{WholeGb(c.GpuBudgetBytes)} on the card, or about {WholeGb(c.RamBudgetBytes)} "
                + "more slowly in memory.",

            //a machine with no usable driver says so as a fact and names the memory models get
            _ => $"This machine: {installed} of memory and no graphics driver gatto can use, so "
                 + $"models run in system memory, about {WholeGb(c.RamBudgetBytes)} of it.",
        };
    }

    //this delegates, fact rows and the summary sentence must print one answer
    private static string WholeGb(ulong bytes) => Gatto.Cli.SizeWords.WholeGb(bytes);

    private static string Gb(ulong bytes) =>
        Gatto.Cli.SizeWords.Gb(bytes);

    //placeholder returned while pausing for the writer, the runner checks the pause flag first so it's never shown
    private static readonly SetupScreen WritesPending = new SetupScreen.Info("writes.pending", []);

    //stamp the strip at the one exit a screen can take. the key is an argument because the two exits know it differently
    private SetupScreen Crossing(SetupScreen s, string stripKey) =>
        s with { Strip = WalkSection.For(stripKey, Path, _addRoad) };
}
