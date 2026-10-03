using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//which way the run goes, decided by the fork question alone.
internal enum SetupPath
{
    //the state before the fork question is answered.
    Unknown,
    //gatto runs the model itself, through llama.cpp, with auto-serve later.
    Llama,
    //gatto connects to a server the user already runs.
    Connect,
}

//keep the flow pure and resumeless, a user who leaves mid-way leaves nothing behind. every fact from ISetupProbes, every word out as a WizardScreen

//five states rather than a bool, still arriving, unreadable and wrong file are three facts, collapsing them accuses a half-written file

//keep the two shas beside the verdict, so the screen shows the pair the check compared rather than hashing the file again.
internal readonly record struct WatchCheck(
    WatchVerdict Verdict, string? Actual = null, string? Expected = null);

internal enum WatchVerdict
{
    //the name, size and published fingerprint all agree.
    Verified,

    //the name and size agree and no fingerprint exists, so the screen claims no more than that.
    NameAndSizeOnly,

    //a .part file sits beside it, so the file is still being written and the watch continues
    StillArriving,

    //the size or fingerprint disagrees with the published value.
    NotThePublishedFile,

    //the file is present but gatto could not hash it, which is not the same as wrong.
    Unreadable,
}

internal enum WatchCause
{
    //the fetch could not reach Hugging Face and nothing arrived, but the offer's fingerprints are known so the watch can still verify.
    Unreachable,

    //no file has a published sha256, so gatto never offered the fetch and cannot check the bytes that arrive.
    NoFingerprint,
}

internal sealed class SetupFlow(ISetupProbes probes)
{
    //keep screen keys stable for tests, the copy beside them changes but the key can't

    //write keys as literals, the census greps raw source for them, and it greps comments too, so a comment must not quote the constructor pattern

    //the first segment, which puts gatto where a terminal can find it.
    public const string WelcomeKey = "welcome";
    public const string WelcomeGo = "welcome.go";
    public const string WelcomeNot = "welcome.not";
    public const string MachineKey = "machine";
    public const string MachineNext = "machine.next";
    public const string InstallKey = "install";

    //update the installed copy from the running image, no download and no digest (those bytes already arrived)
    public const string UpdateInstalledKey = "install.update";
    public const string ConfirmServerKey = "connect.confirm";
    public const string RetryKey = "connect.retry";
    public const string ContextKey = "connect.context";
    //ask which listed model to name only when the server listed more than one (one is not a choice)
    public const string ModelPickKey = "connect.model";
    public const string ModelNameKey = "connect.modelname";
    public const string DiscoveredKey = "model.found";
    //let the user type a folder for the generic picker to search, and offer whatever it finds as a list.
    public const string ScanPathKey = "model.scanpath";
    //the download watch's own folder key, don't route on the question text (the copy changes)
    public const string SearchKey = "model.search";
    public const string TypedIdKey = "model.typedid";
    //the download step, which shows the link and waits for the file.
    public const string DownloadKey = "model.download";

    //keep DownloadKey apart from the three fetch screens (consent, live, arrival). it's the browser watch reached when gatto can't fetch
    public const string ModelConsentKey = "model.consent";
    public const string ModelFetchingKey = "model.fetching";

    //give the paused fetch its own screen, space resumes it and the bar and Esc text differ from the running one
    public const string ModelFetchingPausedKey = "model.fetching.paused";
    public const string ModelArrivedKey = "model.arrived";

    //give the drop and the mismatch separate screens, a drop can be resumed and a mismatch can't (its file is deleted)
    public const string ModelDroppedKey = "model.dropped";
    public const string ModelMismatchKey = "model.mismatch";
    public const string AuditionOfferKey = "model.audition.offer";
    //show the running check as a screen that needs no input, with one key that stops the check rather than the wizard.
    public const string AuditionRunningKey = "model.audition.running";

    //reuse the check's screen for the skip wait, but under its own key (the two abandon different things)
    public const string AuditionWaitingKey = "model.audition.waiting";

    //say only that the model loaded and answered (nobody asked it to run commands)
    public const string AuditionAnswersKey = "model.audition.answers";
    //give a held server its own key, nothing was measured and a shared key could report it as a verdict
    public const string AuditionHeldKey = "model.audition.held";
    //give the incomplete model its own key, nothing ran and only this screen may name that cause
    public const string AuditionIncompleteKey = "model.audition.incomplete";
    //ask again while the model is still coming up, the process is alive and nothing is wrong
    public const string AuditionLoadAskKey = "model.audition.stillloading";

    //show the server's last lines when it exits while loading (they say what it managed to report)
    public const string AuditionExitedKey = "model.audition.exited";

    //give a mid-check stop its own key, the output stays on screen and the next step differs
    public const string AuditionStoppedKey = "model.audition.stopped";
    public const string AuditionFailedKey = "model.audition.failed";
    //the pass screen waits for an answer, the check output stays on screen
    public const string AuditionPassedKey = "model.audition.passed";
    //the model id this file wants is already held by a different file.
    public const string ModelCollisionKey = "model.collision";
    public const string SteerKey = "llama.steer";
    public const string LlamaPathKey = "llama.path";
    //ask which build to run when an engine is already here (the machine plainly has one)
    public const string FoundKey = "llama.found";
    //offer the found engine as a row beside the other options (a lone Enter row hides the connect choice)
    public const string FoundUse = "found.use";
    //one key for both spellings of the fetch label, only the wording differs and the deed is the same
    public const string FoundFetch = "found.fetch";
    //retry the same engine path after the runtime is installed, the file is right and Windows could not load it
    public const string FailedKey = "llama.failed";
    public const string FailedRetry = "failed.retry";

    //record that the sweep tried an exe and set it down, as a note rather than a screen.
    public const string EngineSkippedKey = "engine.skipped";
    //ask once before the large download, and reuse the fork's connect key so both screens share one handler.
    public const string ConsentKey = "llama.consent";
    public const string ConsentFetch = "consent.fetch";
    public const string FetchingKey = "llama.fetching";
    public const string FetchStop = "fetching.stop";
    //remake the browser-steered screen for the offline arm and keep the original (other cases still reach it and need its copy)
    public const string FallbackKey = "llama.fallback";
    //look in the folder gatto named on return, and keep the typed field for a user who extracted elsewhere.
    public const string FallbackDone = "fallback.done";
    //the engine arrived, matched GitHub's digest and then ran.
    public const string FetchedKey = "llama.fetched";
    public const string FetchedNext = "fetched.next";
    public const string FetchedLeave = "fetched.leave";
    public const string ProveKey = "prove";

    //give the closing summary its own key (sharing the check's key leaves the option guard unable to tell the two apart)
    public const string SummaryKey = "done.summary";
    //the one question asked once at the end of a completed setup.
    public const string UpdateKey = "update";

    //keep the fork's name on the connect key (many test sites and the reachability guard name it)
    public const string ForkConnect = "connect";
    //render back as a row rather than a keystroke, so both askable kinds stay uniform without touching SelectPrompt
    public const string BackKey = "back";

    //say why going back is refused and what to do instead (a re-run probes as satisfied and only adds)
    public const string BackRefused =
        "can't go back, the model and config were already written; finish, then re-run gatto setup "
        + "to change things.";
    public const string UseExistingModel = "model.use";

    //offer to join the file as another quant of the same model, and show it only when gatto cannot tell on its own.
    public const string AddAsFile = "model.addfile";

    //ask whether to run the model on the joined quant from now on.
    public const string QuantFromNowKey = "model.quantfromnow";
    public const string ReplaceModel = "model.replace";
    public const string UpdateYes = "update.yes";
    public const string UpdateNo = "update.no";
    public const string InstallYes = "install.yes";
    public const string InstallNo = "install.no";
    public const string Yes = "yes";
    public const string No = "no";
    public const string Retry = "retry";
    public const string Elsewhere = "elsewhere";
    public const string SearchInstead = "search";
    public const string TypeAnId = "typeid";
    //widen to every approved publisher in one step, offer no way to narrow again (the narrow shelf already failed)
    public const string Broaden = "broaden";

    //name what a control press did and let the face translate it, so the flow needs no second entry point. put the pressed key in the face
    public const string CtlPublisher = "shelf.publisher";

    //open the publisher picker from its slot, re-search as the picked publisher, and return to the shelf on Esc.
    public const string PublisherKey = "model.publisher";

    //prefix the picker row's answer, a publisher slug and a screen key are both strings and a bare one could match an option key
    public const string PickPublisher = "publisher:";

    //put the wide shelf on its own picker row (the only way to all publishers)
    public const string PickEveryPublisher = "publisher:*";

    //prefix the shelf's typed answer, a typed number and a row index are the same string
    public const string CtlTyped = "shelf.typed:";
    public const string CtlSort = "shelf.sort";
    public const string CtlLift = "shelf.lift";
    public const string CtlSearch = "shelf.search";

    //prefix the family answer with the chip's own name, the face hands it over and the helper composes it (other controls take no argument)
    public const string CtlFamily = "shelf.family:";

    //use one answer for both directions of the source toggle, the flow knows which shelf it shows. wire the m key too, the footer has advertised it
    public const string CtlSource = "shelf.source";

    //prefix the picked file onto the row's own answer, the pane's Enter does not advance the flow and the list's Enter does
    public const string CtlPick = "shelf.pick:";
    //the vision-projector question.
    public const string ProjectorKey = "projector";
    //the offer of a per-model llama.cpp build, and the path it asks for.
    public const string CustomBuildKey = "custom.build";
    public const string CustomBuildPathKey = "custom.build.path";
    //the offer to move a model, and the folder it asks for.
    public const string MoveKey = "model.move";
    public const string MovePathKey = "model.move.path";
    public const string Skip = "skip";
    //give a partial set its own key, still arriving is different from nothing arrived and the count tells them apart
    public const string PartialSetKey = "model.partial";
    //produce the landed answer only from a poll that returned true, no ChoiceOption holds it and nobody presses anything
    public const string Landed = "landed";
    public const string Anyway = "anyway";
    public const string PickAnother = "another";

    //prefix the model fetch keys, the engine step has its own fetch and a shared key would leave the dispatch guessing
    public const string ModelFetchNow = "model.fetch";
    public const string ModelFetchStop = "model.fetching.stop";

    //give the shelf resume its own key (it starts a fetch the flow was not running)
    public const string ResumePausedFetch = "model.resume.paused";

    //keep the pause key's own spelling, the dispatch decides which screen follows. both keys stop the transfer and keep the .part, but where the user ends up differs
    public const string ModelFetchPause = "model.fetching.pause";

    //give the paused screen's Space its own value, the drop screen's resume is a numbered row
    public const string ModelFetchResume = "model.fetching.resume";

    //name the pass screen's keys for that screen, two screens with two deeds must not share one next key
    public const string AuditionPassedNext = "model.audition.passed.next";
    public const string AuditionPassedLeave = "model.audition.passed.leave";

    //name the check's stop for the check, its cost differs from the fetch's stop and a shared key would confuse the dispatch
    public const string AuditionStop = "model.audition.stop";

    //give the check's stop its own key, a watched screen tells a user stop from a finish by which answer returns
    public const string ConnectCheckStop = "connect.check.stop";

    //re-run the same check on the same model, nothing was measured and there is no result to continue from
    public const string CheckAgain = "model.audition.again";

    //give the keep-waiting answer its own key, it answers a wait still running so it must stay apart from the offer
    public const string KeepWaiting = "model.audition.keepwaiting";

    //give the stop-unchecked choice its own key, it is a numbered row and the live screen's Esc is a different stop
    public const string StopUnchecked = "model.audition.stopunchecked";

    //give the held screen's next its own key, every screen that says next means a different next
    public const string AuditionHeldNext = "model.audition.held.next";
    public const string AuditionHeldLeave = "model.audition.held.leave";

    //ask before an in-session check, it swaps the model the user is talking to. give it its own key, the setup offer is free and this needs consent
    public const string InSessionCheckKey = "model.audition.insession";

    //add the model without checking it, the skip path would load it and that is the swap this answer declines
    public const string AddUnchecked = "model.audition.insession.unchecked";

    //keep the decline label in one const, a test scripts the prompter with it and a short script reads as the user leaving
    internal const string AddUncheckedLabel = "Add it unchecked, nothing on this machine changes";

    //the skip path's own stop, and the two keys on its arrival.
    public const string SkipStop = "model.audition.skip.stop";
    public const string AnswersNext = "model.audition.answers.next";
    public const string AnswersLeave = "model.audition.answers.leave";
    public const string ModelArrivedNext = "model.arrived.next";
    public const string ModelArrivedLeave = "model.arrived.leave";
    public const string ModelResume = "model.resume";

    //let the user accept the smaller unit after a partial vision fetch, and remember the answer so nothing asks again.
    public const string UseTextOnly = "model.textonly";
    public const string ModelRefetch = "model.refetch";
    public const string OpenRepl = "repl";
    public const string Finish = "finish";
    //context for a local model when the fit can't be judged or nothing fits (LiveSetupProbes.ContextFor)
    public const int OfferedContext = 32768;

    //keep a separate offer for a server that will not report its window, the other constant is the llama path's fallback. a change here must not move that path
    public const int OfferedServerContext = 65536;

    //use the face's budget, a second derivation fetches rows the screen can't show

    //set the row budget from the runner, it holds both the flow and the face. keep the default for a flow driven by a test
    internal int RowBudget { get; set; } = HubSearch.DefaultRowBudget;

    //compose the flow's copy in the glyph set the runner passes, a flow that read it itself could disagree. tests that build bare default to Unicode
    internal Gatto.Terminal.GlyphSet Glyphs { get; set; } = Gatto.Terminal.GlyphSet.Unicode;

    //gate every sentence that names the m key on the face's ability to switch source. set it from the runner, the flow and the face are built apart
    internal bool CanSwitchSource { get; set; }

    //set FaceShowsChoiceBodyRows from the face like the source switch, and default it false so the narration draws unless the face says otherwise
    internal bool FaceShowsChoiceBodyRows { get; set; }

    //use this port as the fallback for a fresh machine (no local endpoint exists to derive one from)
    public const int DefaultPort = 1235;

    private readonly List<WizardScreen> _emitted = [];

    //step markers are named rather than counted, each announced as one word where its step begins. an info screen asks no question, so its marker commits and stays

    //keep the narration in its own list, an info added to _emitted does not render and draining it would draw every key at once
    private readonly List<WizardScreen.Info> _narrate = [];

    //return the narration since the last screen and clear it, so each marker renders where the flow reached it.
    public IReadOnlyList<WizardScreen.Info> TakeNarration()
    {
        if (_narrate.Count == 0) return [];
        //stamp the marker when it is drained, so the user sees what was true when it is shown
        var taken = _narrate.Select(n => (WizardScreen.Info)Crossing(n, n.Key)).ToArray();
        _narrate.Clear();
        return taken;
    }
    private string? _awaiting;

    //record the last screen actually shown, an answer handler clears _awaiting and it cannot name where the flow stopped. take it from Emit's argument
    private string? _lastShown;

    //record whether the install question was passed, the Writes.InstallTo field is null for both a decline and never arriving. set it at the point that knows
    private bool _pastTheInstallAsk;

    //record that the user left, a flow that threw reaches the same composer and must not be labelled as a leave. set it from both the leave option and Esc
    private bool _left;

    //the walk reached its done screen, so a stopped or skipped check still ends on the record rather than the last frame
    private bool _finished;

    //the user stopped the check, which the add road's closing line names as the user's own act
    private bool _checkStopped;

    //the last prove that did not answer, the add road's closing line says the server failed rather than that all is well
    private Gatto.Core.Acquire.ProveOutcome? _proveFailed;

    //delete the kept partial on leave only when the drop screen armed that promise. mark the leave before the leave screen is composed
    internal void MarkLeaving()
    {
        _left = true;
        if (_droppedPartial is not { } advertised) return;
        _droppedPartial = null;
        probes.DeletePartial(advertised);
    }

    //hold the offer whose partial the drop screen offered to delete, set at build and cleared on answer
    private ModelFetchOffer? _droppedPartial;

    //record the option keys the awaited screen drew, six emissions share one prove key and nothing else tells them apart
    private IReadOnlyList<string>? _awaitingOptions;
    private ConnectProbe? _found;
    private IReadOnlyList<FoundModel> _discovered = [];

    //keep the last sweep's roots out of the back-step snapshot (a stale list would describe a sweep that did not happen)
    private IReadOnlyList<string> _scanRoots = [];

    //fill the folder slot only when the user typed a folder, the ambient sweep reads several roots and naming one would be a lie
    private string? _scanTyped;

    //record which screen armed the pause, inferring the route from the model id would loop back into the arming screen
    private enum ResumeStage { ScaffoldPause, DonePause, ConsentPause }

    private ResumeStage _resumeStage = ResumeStage.ScaffoldPause;

    //record that default_model reached disk, the leaving sentence must come from the completed write. set it only after the done-pause's write applies
    private bool _defaultLanded;
    private IReadOnlyList<ShelfRow> _rows = [];
    //hold why the last search was empty when the engine can say, and read it with the row count (null covers both cases)
    private HubSearchCause? _searchCause;
    //open the curated shelf and let the broaden row move it (curated shows the dated publisher by download count)
    private HubSearchView _view = HubSearchView.Curated;

    //keep the chosen axis null until the user picks one (null means nobody chose and the view's own order answers)
    private SearchOrder? _axis;

    //default the fit filter to on, and say what it hid (a is what lifts it)
    private bool _lift;

    //leave the family null for all, all is the absence of a filter and a stored string would miss the family lookup
    private string? _family;

    //keep the local shelf's chip in its own field, one shared family could empty the local shelf when the user narrows the Hub
    private string? _localFamily;

    //draw the approved publishers as a screen, the wizard borrows no REPL component. mark the current publisher rather than hide it
    private WizardScreen PublisherPicker()
    {
        var approved = probes.ApprovedPublishers();
        var current = _publisher ?? _curatedPublisher;

        return Emit(new WizardScreen.Choice(
            PublisherKey,
            "Which publisher should gatto search?",
            [
                .. approved.Select(p => new ChoiceOption(
                    PickPublisher + p, p,
                    //take the mark from the option's own recommended flag, so the screen cannot tick one name and answer with another.
                    Recommended: _view == HubSearchView.Curated
                        && string.Equals(p, current, StringComparison.OrdinalIgnoreCase),
                    //word the mark current rather than recommended, it means the shelf searches this publisher
                    MarkWord: "current")),
                new ChoiceOption(PickEveryPublisher, "Every approved publisher",
                    Recommended: _view == HubSearchView.Broadened, MarkWord: "current"),
            ],
            BodyRows:
            [
                new WizardRow("gatto searches publishers it has reviewed, so a search cannot reach "
                    + "an upload nobody looked at."),
            ]), PublisherKey);
    }

    //keep the chosen publisher on the request, one keystroke must not change a stored setting
    private string? _publisher;

    //keep the searched words, the empty screen quotes them and a re-run searches the same. clear them when a family chip is pressed, the engine cannot hold both
    private string? _search;

    //latch whether the curated view can narrow, a non-null publisher on the first search means the allowlist's default view is a real org


    //take the hidden-by-fit count from the engine, a screen that recomputed it would hold a second opinion about a measured number
    private int _hiddenByFit;
    //count the rows set aside below and above the reviewed generation in two buckets (the count line names them separately)
    private int _hiddenOlder;
    private int _hiddenNewer;
    //keep the hidden-by-kind count apart from the fit count, only the fit count has a key to undo it
    private int _hiddenByKind;
    //keep the family count separate from the kind count, only the family filter is undone by pressing a
    private int _hiddenByFamily;
    //name the publisher whose shelf this is, and branch the copy on it rather than the view, so the copy describes what was searched.
    private string? _curatedPublisher;
    //keep the projector found beside the chosen model from the ask to its answer, per run like every other field here.
    private string? _projector;
    //keep the replace flag across the projector ask, that ask sits between the collision answer and the scaffold
    private bool _projectorReplace;
    //keep a second replace flag for the custom-build ask, two screens sharing one field lets a later change corrupt the other
    private bool _customBuildReplace;
    //give the move screens their own replace flag, two screens sharing one field lets a later change corrupt the other
    private bool _moveReplace;
    //keep the measured offer from the screen to its answer, so the picked folder is the one that was shown.
    private MoveOffer? _moveOffer;
    private string? _modelId;

    //read the model row once at the step's entry, the audition checks the file it started with
    private string? _modelFile;


    //report the armed model id even when no scaffold ran, the reuse path writes a default model without the writer producing an id. leave it null on the connect path
    internal string? ArmedModelId => _modelId;
    private string? _llamaPath;

    //hold the exe the found screen named, set only by the screen and read only by its answer, so the two cannot come apart.
    private string? _foundEngine;

    //the build tag the found screen showed.
    private string? _foundBuild;

    //record whether this flow verified the model file, verified is what the fetch did
    private bool _modelVerified;

    //hold the fetch the screen described, the deed behind the key must use the exact values the user was shown
    private Offer? _offer;

    //hold the running fetch while its screen is up, the watch resolves on the task completing rather than a guess about a folder
    private Task<Gatto.Cli.EngineFetch>? _fetching;

    //keep the model fetch apart from the engine's, the two never run at once and one field would make a null ambiguous
    private Task<Gatto.Core.Acquire.HubFetchResult>? _modelFetch;
    private CancellationTokenSource? _modelFetchCancel;
    private System.Diagnostics.Stopwatch? _modelClock;

    //give the pause its own token beside the stop's, the stop deletes the partial and a pause must keep those bytes
    private CancellationTokenSource? _modelFetchPause;

    //record that the fetch is paused, the watch must answer no and would otherwise fall through to the browser's disk poll
    private bool _modelPaused;

    //run the check as a task rather than inside its answer handler, a blocking check turns no clock and draws nothing
    private Task<AuditionCheck>? _audition;

    //time the check in the flow rather than the face, the face's clock draws the purr while this one decides when to ask again
    private System.Diagnostics.Stopwatch? _checkClock;
    private int _asksMade;

    //make the load-ask threshold injectable, a guard about five minutes cannot wait five minutes and a faked clock only tests the fake
    internal TimeSpan LoadAskAfter { get; init; } = Gatto.Roles.Audition.AuditionRunner.LoadAskAfter;

    //ask keep-waiting once per whole multiple of the threshold, and only while nothing has started, a counting check is not a stalled load
    private bool LoadAskIsDue() =>
        _checkClock is { } clock
        && PollCheck() is not { Total: > 0 }
        && clock.Elapsed >= LoadAskAfter * (_asksMade + 1);

    //suppress the completion's held-server sentence once the check step said it, one fact must not speak twice
    private bool _heldSaid;

    //remember which model held the server at the ask, a later read would answer a different question than the one asked the user
    private string? _heldBy;


    //keep the skip load's outcome, the completion screen reports it and a second call would repeat the wait
    private Task<Gatto.Core.Acquire.ProveOutcome>? _loading;
    private Gatto.Core.Acquire.ProveOutcome? _proved;
    private CancellationTokenSource? _auditionCancel;

    //reset the elapsed time at the consent rather than at a start, a resume continues one wait and the arrival reports the whole total
    private long _modelElapsedMs;
    //hold the sentence the consent's field last showed, the move and the refusal each need one. a flag would leave the copy to be re-derived
    private string? _rootNote;

    private ModelFetchOffer? _modelOffer;
    private ModelView? _modelView;

    //set the watch cause on every path onto that screen (it can't be inferred there)
    private WatchCause _watchCause = WatchCause.NoFingerprint;

    //remember the text-only answer, the projector offer asks the same question one screen later
    private bool _declinedEncoder;

    //set where the watch adopts the model, it picks the arrival screen
    private bool _arrivedByWatch;
    private CancellationTokenSource? _fetchCancel;

    //lock this on both threads, a torn read would mix one file's name with another's byte count
    private readonly object _tickGate = new();
    private FetchTick? _tick;

    private void OnTick(FetchTick t) { lock (_tickGate) _tick = t; }

    //lock this on both threads, the read builds one snapshot and a torn read would split it across two moments
    private readonly object _checkGate = new();
    private int _checkIndex;
    private int _checkTotal;
    private string _checkTask = "";
    private readonly List<string> _checkDone = [];
    private bool _serverUp;
    private bool _stillLoading;

    //number finished tasks the way the offer numbered them (the runner counts the same list in the same order)
    private void OnCheck(Gatto.Roles.Audition.AuditionProgress p)
    {
        lock (_checkGate)
            switch (p.Stage)
            {
                case Gatto.Roles.Audition.AuditionStage.Started:
                    _checkIndex = p.Index;
                    _checkTotal = p.Total;
                    _checkTask = p.Label;
                    break;
                case Gatto.Roles.Audition.AuditionStage.Done:
                    _checkDone.Add($"{p.Index}) {p.Label}");
                    break;
                //a note's arrival is the server-up fact, its text goes to the transcript
                case Gatto.Roles.Audition.AuditionStage.Note:
                    _serverUp = true;
                    break;
                //set still-loading on its own, a screen must not say still loading about a start it never reported
                case Gatto.Roles.Audition.AuditionStage.Loading:
                    _stillLoading = true;
                    break;
            }
    }

    //wipe the check in one place, only when a run starts (the outcome screens read the ending run)
    private void ResetCheck()
    {
        lock (_checkGate)
        {
            _checkIndex = 0;
            _checkTotal = 0;
            _checkTask = "";
            _checkDone.Clear();
            _serverUp = false;
            _stillLoading = false;
        }
    }

    //null when nothing has been reported yet, so the screen draws no rows
    public CheckTick? PollCheck()
    {
        lock (_checkGate)
            return _checkTotal == 0 && _checkDone.Count == 0 && !_serverUp && !_stillLoading
                ? null
                : new CheckTick(_checkIndex, _checkTotal, _checkTask, [.. _checkDone], _serverUp,
                    _stillLoading);
    }

    //the latest fetch moment, null when nothing is fetching
    public FetchTick? PollTick() { lock (_tickGate) return _tick; }

    //teardown in one place (a live token source is a leak and a stale tick draws on the next screen). keepTick leaves the tick for the paused screen's bar and bytes
    private void EndFetch(bool keepTick = false)
    {
        _fetchCancel?.Dispose();
        _fetchCancel = null;
        _modelFetchCancel?.Dispose();
        _modelFetchCancel = null;
        _modelFetchPause?.Dispose();
        _modelFetchPause = null;
        _modelClock = null;
        if (!keepTick) lock (_tickGate) _tick = null;
    }

    //don't clear the check fields here, the outcome screens are made of those moments
    private void EndAudition()
    {
        _auditionCancel?.Dispose();
        _auditionCancel = null;
    }

    //report ticks synchronously (Progress<T> posts to a captured context and would deliver a moment late)
    private sealed class TickSink(Action<FetchTick> onReport) : IProgress<FetchTick>
    {
        public void Report(FetchTick value) => onReport(value);
    }

    //report check events synchronously, the events are minutes apart and a late one would name the wrong task
    private sealed class CheckSink(Action<Gatto.Roles.Audition.AuditionProgress> onReport)
        : IProgress<Gatto.Roles.Audition.AuditionProgress>
    {
        public void Report(Gatto.Roles.Audition.AuditionProgress value) => onReport(value);
    }

    //hold the confirmed URL, context and endpoint name across the screens before the write pause (the flow keeps no resume state)

    //hold the model id the chosen file collides with, from the screen to its answer, per run
    private string? _clashingModelId;

    //hold the joined model and quant after the pause (the write set is cleared and the done step still needs a subject)
    private (string Id, string Quant)? _joining;
    //hold the install target from the segment to its answer
    private string? _installDir;

    //findability repair only, no copy needed (the installed copy is newer than this image or unreadable)
    private bool _repairOnly;
    private string? _connectBaseUrl;
    private int _connectContext;
    private string? _connectEndpointName;

    //record that a second endpoint was written beside the user's own (by summary time the probe would describe the changed config)
    private bool _besideLocal;

    //keep the model the connect flow settled on for the check screen (the write set is consumed by then)
    private string? _connectModel;

    //record whether the server reported the context or the user named it (a typed address replaces the probe, a later read would mislead)
    private bool _contextWasReported;

    //hold the running prove task (the watch says whether it ended and this is the only thing that knows)
    private Task<Gatto.Core.Acquire.ProveOutcome>? _proving;
    //null means the check was skipped or could not run. treat it as we did not look

    //store the whole check rather than its facts (the completion screen can't infer a pass from facts a failed audition also measures)
    private AuditionCheck? _check;
    //record that the writes were applied, set only where they are applied
    private bool _applied;

    //hold the chosen path, unknown until the fork is answered
    public SetupPath Path { get; private set; } = SetupPath.Unknown;

    //the strip's standing comes from the waiting screen, exposed for the flow's own tests
    public IReadOnlyList<StripSection> Strip => WalkSection.For(_awaiting, Path);

    //give a screen that asks nothing its own position, done is the last section of the strip
    private static string KeyOf(WizardScreen s) => s switch
    {
        WizardScreen.Choice c => c.Key,
        WizardScreen.Ask a => a.Key,
        WizardScreen.Info i => i.Key,
        WizardScreen.Progress p => p.Key,
        WizardScreen.Terminal t => t.Key,
        _ => throw new InvalidOperationException("unreachable, WizardScreen is closed"),
    };

    //every emitted screen, oldest first (the tests' window onto the flow)
    public IReadOnlyList<WizardScreen> Emitted => _emitted;

    //what this run intends to write, empty until a path completes. nobody applies it here
    public WriteSet Writes { get; private set; } = new();

    //the model this run will use, once one is chosen
    public FoundModel? Selected { get; private set; }

    //the shelf row the user chose when the model comes from the Hub. the browser does the download, this flow only records the choice
    public ShelfRow? Picked { get; private set; }

    //whether the user asked to go straight into gatto when setup ends (the caller does the launching)
    public bool StartReplWhenDone { get; private set; }

    //true when the run's writes must be applied before the next screen. the runner applies them, then calls ResumeAfterWrites
    public bool NeedsWritesApplied { get; private set; }

    //don't snapshot the transcript, write-boundary flags or probe latches, a step back must not unshow a screen, undo a write or re-arm a start
    private sealed record Snapshot(
        string? Awaiting, ConnectProbe? Found, IReadOnlyList<FoundModel> Discovered,
        //store the typed folder beside the rows it filtered (a user choice, going back restores both together)
        string? ScanTyped,
        IReadOnlyList<ShelfRow> Rows, HubSearchCause? SearchCause, HubSearchView View,
        //the Axis and Lift controls travel with going back, the next search reads them. shelf counts stay out, a search runs before each render and rewrites them
        SearchOrder? Axis, bool Lift,
        //the search, the family and the publisher stay live rather than snapshotted. back must show the shelf as the user left it filtered
        string? CuratedPublisher, string? ModelId, string? LlamaPath, AuditionCheck? Check,
        string? ConnectBaseUrl, int ConnectContext, string? ConnectEndpointName,
        bool ConnectBesideLocal,
        SetupPath Path, WriteSet Writes, FoundModel? Selected, ShelfRow? Picked,
        bool StartReplWhenDone, WizardScreen Screen,
        //whether this step's screen came from a control that hit no list. going back restores the flag, so a control pressed there still undoes the step
        bool ControlStep);

    //a stack of snapshots, no persistence and no resume (replaying would re-run probes, which are neither free nor idempotent)
    private readonly Stack<Snapshot> _back = new();

    private bool CanGoBack => _back.Count > 0 && !_applied;

    //the screen the flow waits on, the last emitted one (every branch emits its screen last)
    private WizardScreen? LastScreen => _emitted.Count > 0 ? _emitted[^1] : null;

    private Snapshot Capture(WizardScreen screen) => new(
        _awaiting, _found, _discovered, _scanTyped, _rows, _searchCause, _view, _axis, _lift,
        _curatedPublisher,
        _modelId, _llamaPath, _check,
        _connectBaseUrl, _connectContext, _connectEndpointName, _besideLocal,
        Path, Writes, Selected, Picked, StartReplWhenDone, screen, _controlStep);

    //step back to the previous screen with the state it was answered under. after a write the step refuses with a screen, so the flow shows it and a test can open it
    private WizardScreen Back()
    {
        if (!CanGoBack)
            //second guard, reachable when a face offers back without advertising AllowBack. it renders a sentence (silence would teach the user their key is broken)
            return Emit(new WizardScreen.Info("back.refused", [new WizardRow(BackRefused)]), _awaiting);

        //back from an empty typed-folder shelf goes to the machine's shelf. steps of the same search are skipped, and one entry always stays
        if (_awaiting == DiscoveredKey && _scanTyped is not null && _discovered.Count == 0)
            while (_back.Count > 1 && IsFolderSearchStep(_back.Peek())) _back.Pop();

        var s = _back.Pop();
        _controlStep = s.ControlStep;
        _awaiting = s.Awaiting;
        _found = s.Found;
        _discovered = s.Discovered;
        _scanTyped = s.ScanTyped;
        _rows = s.Rows;
        _searchCause = s.SearchCause;
        _view = s.View;
        _axis = s.Axis;
        _lift = s.Lift;
        _curatedPublisher = s.CuratedPublisher;
        _modelId = s.ModelId;
        _llamaPath = s.LlamaPath;
        _check = s.Check;
        _connectBaseUrl = s.ConnectBaseUrl;
        _connectContext = s.ConnectContext;
        _connectEndpointName = s.ConnectEndpointName;
        _besideLocal = s.ConnectBesideLocal;
        Path = s.Path;
        //restoring Writes on a step back drops an abandoned intent (otherwise a screen between the write and its pause would let it reach disk)
        Writes = s.Writes;
        Selected = s.Selected;
        Picked = s.Picked;
        StartReplWhenDone = s.StartReplWhenDone;

        //recompose the shelf from live fields and re-run the search, a chips row could otherwise show a filter the rows lack
        if (s.Screen is WizardScreen.Choice { Key: SearchKey }) return Search();

        //the screen is emitted again, the transcript keeps both visits (both happened)
        return Emit(WithBack(s.Screen), s.Awaiting);
    }

    //true for the two screens of one folder search: the folder question and a local shelf filtered by a typed folder
    private static bool IsFolderSearchStep(Snapshot s) =>
        s.Screen is WizardScreen.Ask { Key: ScanPathKey }
        || (s.Screen is WizardScreen.Choice { Key: DiscoveredKey } && s.ScanTyped is not null);

    //set AllowBack from the one rule (no screen author decides it, no face guesses it)
    private WizardScreen WithBack(WizardScreen screen) => screen switch
    {
        WizardScreen.Choice c => c with { AllowBack = CanGoBack },
        WizardScreen.Ask a => a with { AllowBack = CanGoBack },
        _ => screen,     //the Info, Progress and Terminal screens ask nothing, they can't offer a back row
    };

    //enter the model segment of the same flow (no second flow). skip the engine binary and hardware line, a live session already runs a server
    public WizardScreen StartAtModelSegment()
    {
        Path = SetupPath.Llama;
        _addRoad = true;
        return Discover(extraRoot: null);
    }

    //true when the flow entered at the model segment rather than through the full wizard. the face never reads this, screen ownership is its own concern
    private bool _addRoad;

    //whether the model's server was up when the check began. read it once in StartAudition, on the flow's thread
    private bool _serverAlreadyUp;

    //the install state InstallStatus reported, null when the install segment never ran. null makes the closing sentence say nothing about terminals
    private InstallState? _installState;

    //where the install went, recorded when the intent is consumed (the write set is wiped before the closing sentence reads it)
    private string? _installedTo;

    //whether a repair made an existing copy findable, recorded beside the install target (otherwise the summary line would say gatto is not installed)
    private bool _pathOnlyLanded;

    public WizardScreen Start() => Emit(Welcome(), WelcomeKey);

    //the welcome screen draws the flow as a map (a step counter can't be right when the flow forks)

    //keep the copy from the generator, a paraphrase reads well but is wrong about the screen
    private WizardScreen Welcome()
    {
        var engineInstalled = probes.LlamaServerPath() is { Length: > 0 };

        return new WizardScreen.Choice(
            WelcomeKey,
            "Ready to set gatto up?",
            //keep these labels lowercase, the footer is their only reader and the face renders them verbatim
            [
                new ChoiceOption(WelcomeGo, "get started"),
                new ChoiceOption(WelcomeNot, "not now"),
            ],
            BodyRows:
            [
                //keep the second body sentence as one clause, examples inside it read as a list that turns out to be an aside
                new WizardRow("gatto is an AI assistant that lives in your terminal. Describe a job "
                    + "in plain words and it does the work itself: reading, writing and running "
                    + "things on your computer, showing you every step."),
                new WizardRow(""),
                new WizardRow("The thinking happens on this machine too: the model is a file on your "
                    + "own disk, so your words and your files never have to leave it."),
                new WizardRow(""),
                //machine line on the welcome, the nothing-fits screen shouldn't be where the user first sees it
                new WizardRow(HardwareLine(probes.Hardware())),
                new WizardRow(""),
                new WizardRow("Setting up takes a few minutes:"),
                new WizardRow(""),
                //one gloss row per word of the strip, the user meets each step before it. the engine row states a fact about this run (gatto reports what it discovered)
                .. StripMap.Rows(engineInstalled, Glyphs),
                new WizardRow(""),
                //optional row, dropped last by height fitting. it names a glyph, so the word and the highlight both read the glyph table and can't disagree
                new WizardRow($"Inside, Tab moves between the parts of a screen, {Glyphs.Prompt} shows "
                    + "where the keys are.", RowTone.Aside, Highlight: [Glyphs.Prompt],
                    Optional: true),
            ],
            KeysOnly: true)
        {
            //draw the cat here and nowhere else, in the wizard or in session (no entry point may draw a second cat)
            Hero = _addRoad ? null : CatOf(Glyphs),
        };
    }

    //the cat rows come from Cats.For, the one home for the drawing. the glyph set is a parameter, or a value computed too early draws the Unicode cat
    private static string[] CatOf(Gatto.Terminal.GlyphSet g) =>
        [.. Gatto.Repl.Cats.For("", g).Split('\n').Select(l => Gutter + l.TrimEnd('\r'))];

    //a two-space gutter for hero rows (the cat is pre-composed, without it it sits two cells left of the rows below)
    private const string Gutter = "  ";

    //the map column is separate from ColumnLabel's, one width over both label sets fits neither
    private static class StripMap
    {
        public static readonly string[] Labels =
            [WalkSection.Machine, WalkSection.Engine, WalkSection.Model, WalkSection.Check, WalkSection.Done];

        private static readonly Dictionary<string, string> Gloss = new(StringComparer.Ordinal)
        {
            [WalkSection.Machine] = "what this computer can run",
            [WalkSection.Engine] = "a copy of llama.cpp, fetched for it",
            [WalkSection.Model] = "pick one from Hugging Face, or bring your own",
            [WalkSection.Check] = "gatto proves it works",
            [WalkSection.Done] = "start talking to gatto",
        };

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static IEnumerable<WizardRow> Rows(bool engineInstalled,
            Gatto.Terminal.GlyphSet? glyphs) =>
            Labels.Select(l => new WizardRow(
                l.PadRight(Column) + (l == WalkSection.Engine && engineInstalled
                    ? $"{(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ok} already on this machine"
                    : Gloss[l]),
                RowTone.Aside, Indent: 2, Hang: Column));
    }

    //the one home for the two-column labels the map and the install pair share. the width reads every label here, so a rename can't collapse the alignment
    private static class ColumnLabel
    {
        public const string Install = "Install";
        public const string Model = "Model";
        public const string First = "First question";
        public const string From = "from";
        public const string To = "to";

        public static readonly string[] All = [Install, Model, First, From, To];

        //the gutter between the two columns: widest label plus two spaces
        public static readonly int Column = All.Max(s => s.Length) + 2;
    }

    //the from and to rows sit on SummaryFacts rows, so they take the done step's column rather than ColumnLabel's
    private static WizardRow MapRow(string step, string what) => SummaryFacts.Row(step, what);

    private WizardScreen AnswerWelcome(string key)
    {
        if (key != WelcomeGo) return Leave();
        return Machine();
    }

    //the machine screen asks nothing (gatto reports what it found), its summary is the welcome's HardwareLine
    private WizardScreen Machine() => Emit(new WizardScreen.Choice(
        MachineKey,
        "What can this machine run?",
        [
            new ChoiceOption(MachineNext, "next"),
            new ChoiceOption(WelcomeNot, "leave"),
        ],
        BodyRows: [new WizardRow(MachineSummary(probes.Hardware()))],
        Machine: new MachineView(probes.Hardware(), probes.HardwareNames()),
        KeysOnly: true), MachineKey);

    //the readable shapes reuse the welcome's HardwareLine. the unreadable shape gets its own sentence, its system-memory pricing promise fits only the rows below
    private string MachineSummary(Gatto.Core.Hardware.HardwareSnapshot? hw) =>
        //null means the probe told us nothing, CpuOnly means it found no graphics driver. different facts get different sentences
        hw is null
            ? "Couldn't read this machine's memory, so nothing here is priced against it."
            : HardwareLine(hw);

    private WizardScreen AnswerMachine(string key) =>
        key == MachineNext ? EngineStep() : Leave();

    //true only when the running image is strictly newer than the installed copy, so both sites share one comparison and an unreadable version gives false
    private bool RunningIsNewerThanInstalled() =>
        ReleaseVersion.TryParseRunning(probes.RunningVersion(), out var mine)
        && ReleaseVersion.TryParse(probes.InstalledVersion(), out var theirs)
        && mine.IsNewerThan(theirs);

    //the one home for which install states ask. any state not named here falls into the offer, so a new state is asked rather than silently skipped
    private bool InstallAsks(InstallState state) => state switch
    {
        InstallState.Installed => false,
        InstallState.DevBuild => false,
        //an installed copy elsewhere asks only while this image is newer than it. an older or equal copy passes in silence, with the fact on the summary's row
        InstallState.AlreadyInstalledElsewhere => RunningIsNewerThanInstalled(),
        _ => true,
    };

    //first question of the done step, asked after the proof (a gatto that can't be typed does not work). null means nothing to ask
    private WizardScreen? InstallSegment()
    {
        var (state, dir) = probes.InstallStatus();
        //record the state, the closing sentence claims whether a new terminal can find gatto. null in Writes.InstallTo means declined, installed and dev build at once
        _installState = state;
        //record the directory beside the state, the summary fact names it. the Writes.InstallTo readers stay safe, they run only after an install screen is answered
        _installDir = dir;

        //offer replacement only when the running image is strictly newer, an older image would be a downgrade. nothing is downloaded, the bytes are already local
        if (!InstallAsks(state))
        {
            //one silent state still speaks, a dev build changes what happens. the other states leave a fact on the summary's gatto row
            if (state is InstallState.DevBuild)
                _narrate.Add(new WizardScreen.Info("segment.install",
                    [new WizardRow("running from a development build, which can't be copied as one "
                        + "file, skipping the install step")]));
            return null;
        }

        if (state is InstallState.AlreadyInstalledElsewhere)
        {
            //no heading here, the install question sits inside the done step and a heading would announce a section the strip doesn't have
            _installDir = dir;
            return Emit(new WizardScreen.Choice(
                UpdateInstalledKey,
                "A newer gatto is running than the one installed",
                [
                    //a replacement offer must not arrive pre-answered.
                    new ChoiceOption(InstallYes, "Update the installed copy"),
                    new ChoiceOption(InstallNo, "Leave it"),
                ],
                BodyRows:
                [
                    new WizardRow($"Installed: {probes.InstalledVersion()} at {dir}"),
                    new WizardRow($"This one:  {probes.RunningVersion()}"),
                    new WizardRow(""),
                    new WizardRow("Updating copies this gatto over the installed one. Nothing is "
                        + "downloaded, the files are already here."),
                ],
                NoDefault: true),
                UpdateInstalledKey);
        }

        _installDir = dir;
        var half = state == InstallState.InstalledButNotOnPath;

        //this repair offer needs the same version comparison as the update offer, or an older image silently downgrades a newer install
        var installedHere = probes.InstalledVersion();
        _repairOnly = half && !RunningIsNewerThanInstalled();
        return Emit(new WizardScreen.Choice(
            InstallKey,
            //the title says what's wrong in the user's terms (PATH is jargon). the option says what gatto will do rather than naming the mechanism
            half ? "gatto is installed, but only this window can find it"
                 : "Should gatto install itself?",
            [
                new ChoiceOption(InstallYes, half ? "Fix that for me" : "Yes, install it", Recommended: true),
                new ChoiceOption(InstallNo, "Not now"),
            ],
            //the first branch names its type, two collection expressions in a ternary leave the spread nothing to convert between
            BodyRows: [.. half
                ?
                (IReadOnlyList<WizardRow>)[
                    //keep these as two rows, the copy's location then why the window can't find it, one paragraph wraps them together
                    new WizardRow($"A copy is already at {dir}."),
                    new WizardRow("Windows doesn't know to look there yet, so typing gatto in a "
                        + "new window won't work until it does."),
                    //show the version rows only when both versions can be read, a row that says unknown teaches nothing
                    .. installedHere is { Length: > 0 } iv && _repairOnly
                        ? new[]
                        {
                            new WizardRow(""),
                            //an equal version also sets the repair flag. an equal pair gets its own sentence, two equal numbers read as a fault
                            new WizardRow(string.Equals(iv, probes.RunningVersion(), StringComparison.Ordinal)
                                ? $"The installed copy is the same version ({iv}), so gatto will "
                                  + "leave it in place and just make it findable."
                                : $"The installed copy is {iv} and this one is "
                                  + $"{probes.RunningVersion()}, so gatto will leave it in "
                                  + "place and just make it findable."),
                        }
                        : [],
                ]
                :
                [
                    //the from and to rows say which gatto this is when the exe sits beside other copies. keep them as two rows, where gatto is now and what installing does
                    new WizardRow("gatto is running from the folder you unzipped it into."),
                    new WizardRow("Installing puts a copy in your account's programs folder, so "
                        + "you can type gatto in any terminal instead of finding this file again."),
                    new WizardRow(""),
                    MapRow(ColumnLabel.From, probes.RunningFrom()),
                    MapRow(ColumnLabel.To, dir),
                    new WizardRow(""),
                    //the new-terminal line belongs on the completion screen (before a decision it reads as a caveat)
                    new WizardRow("Your account only, Windows won't ask for administrator "
                        + "permission."),
                ],
                //one aside from one place, appended to both branches (two copies would drift). it ends by saying this console works, or it would read as a requirement
                .. TerminalNudgeRows()]),
            InstallKey);
    }

    //the Windows Terminal aside, legacy console only, after a blank row. the probe also decides the glyph set, so the aside and the vocabulary can't disagree
    private IReadOnlyList<WizardRow> TerminalNudgeRows() =>
        probes.IsLegacyConsole()
            //mark the nudge optional, a short terminal drops it before it drops a fact (this is not a thing the install does)
            ? [new WizardRow(""),
               new WizardRow("gatto looks better in Windows Terminal (free, in the Microsoft "
                   + "Store); this console works too.", RowTone.Aside, Optional: true)]
            : [];

    private WizardScreen AnswerInstall(string optionKey)
    {
        _awaiting = null;
        //this records intent, leaving after this screen installs nothing. the answer sets exactly one of the two (a copy beside a repair would install the older image)
        if (optionKey == InstallYes)
            Writes = _repairOnly
                ? Writes with { PathOnly = true }
                : Writes with { InstallTo = _installDir };
        //the answer continues the done step. returning EngineStep would send a user who just proved a model back to pick an engine
        return AfterTheInstallAsk();
    }

    //the update answer must set InstallTo with no PathOnly (AnswerInstall would route it through _repairOnly, which could drift when either branch changes)
    private WizardScreen AnswerUpdateInstalled(string optionKey)
    {
        _awaiting = null;
        if (optionKey == InstallYes)
            Writes = Writes with { InstallTo = _installDir };
        return AfterTheInstallAsk();
    }

    public WizardScreen Answer(string answer)
    {
        //handle the back key before the dispatch, a handler could read it as an option. route on the key alone, deferring to AllowBack let a handler end the wizard on it
        if (answer == BackKey && _awaiting is not null) return Back();

        //unwrap the pane's pick before the dispatch, the row's own key continues as if no pane existed and consumers read PickedQuant
        if (ShelfControls.Unpick(answer) is { } picked)
        {
            ApplyPickedQuant(picked.Key, picked.Quant);
            answer = picked.Key;
        }

        //capture the step being left before a handler changes a field. a control that stays on the shelf pushes nothing, except when the screen it reaches holds no list
        var control = ShelfControls.IsControl(answer);
        if (control && _controlStep) { _back.Pop(); _controlStep = false; }
        var stepped = _awaiting is not null && !_applied && LastScreen is { } current
            && current is not WizardScreen.Choice { Watching: true }   //a watch ends with its task, so a later back to it shows a stop for work that finished
            && Push(Capture(current));
        //clear the flag only after the capture, the step being left keeps the value a step back restores
        if (!control) _controlStep = false;

        return control && stepped ? KeepStepIfNoList(Dispatch(answer)) : Dispatch(answer);
    }

    //whether the top of _back came from a control that found no list. cleared by any other answer, restored by a step back
    private bool _controlStep;

    private bool Push(Snapshot step)
    {
        _back.Push(step);
        return true;
    }

    //keep a control's step only when its screen holds no list, otherwise drop it and stamp the screen again
    private WizardScreen KeepStepIfNoList(WizardScreen landed)
    {
        if (landed is WizardScreen.Choice { Key: SearchKey or DiscoveredKey } c
            && (c.Shelf is null || c.Shelf.NothingFetched))
        {
            _controlStep = true;
            return landed;
        }

        _back.Pop();
        if (_emitted.Count > 0 && ReferenceEquals(_emitted[^1], landed))
            _emitted[^1] = landed = WithBack(landed);
        return landed;
    }

    private WizardScreen Dispatch(string answer)
    {
        return _awaiting switch
        {
            MachineKey => AnswerMachine(answer),
            WelcomeKey => AnswerWelcome(answer),
            InstallKey => AnswerInstall(answer),
            UpdateInstalledKey => AnswerUpdateInstalled(answer),
            //each guard names its screen's own option keys, typed answers fall through. guards stay lists, the options are already written where the screen is built
            FoundKey when answer is ForkConnect or FoundFetch or FoundUse => AnswerFound(answer),
            ConsentKey when answer is ConsentFetch or ForkConnect => AnswerConsent(answer),
            FetchingKey => AnswerFetching(answer),
            //read the key even though this screen has one option, it also has a typed answer. a handler that ignores its argument swallows a typed path silently
            FallbackKey when answer == FallbackDone => AnswerFallback(answer),
            FetchedKey => AnswerFetched(answer),
            //verify the exe the screen named, a typed answer asks a different question and gets the typed arm
            FailedKey when answer == FailedRetry => VerifyLlama(_foundEngine!),
            //guard these screens to their own keys or typed text. an odd answer goes to the throw rather than being probed as an address
            ConfirmServerKey when answer is Yes or No
                || ShelfControls.Untyped(answer) is not null => AnswerConfirm(answer),
            RetryKey when answer == Retry
                || ShelfControls.Untyped(answer) is not null => AnswerRetry(answer),
            ContextKey => AnswerContext(answer),
            ModelCollisionKey => AnswerModelCollision(answer),
            QuantFromNowKey => AnswerQuantFromNow(answer),
            ProjectorKey => AnswerProjector(answer),
            MoveKey => AnswerMove(answer),
            MovePathKey => AnswerMovePath(Gatto.Roles.LlamaAssetSteering.NormalizePath(answer)),
            CustomBuildKey => AnswerCustomBuild(answer),
            CustomBuildPathKey =>
                AnswerCustomBuildPath(Gatto.Roles.LlamaAssetSteering.NormalizePath(answer)),
            ModelPickKey => FinishConnect(_connectEndpointName!, answer),
            ModelNameKey => FinishConnect(_connectEndpointName!, answer.Trim()),
            DiscoveredKey => AnswerDiscovered(answer),
            ScanPathKey => DiscoverTyped(Gatto.Roles.LlamaAssetSteering.NormalizePath(answer)),
            SearchKey => AnswerSearch(answer),
            //the picker's rows are shelf answers and get their own arm. the census asks whether Answer can dispatch each screen, a shared key would answer that by accident
            PublisherKey => AnswerSearch(answer),
            //route the typed-id ask through the same classifier, it gets the path handling too
            TypedIdKey => AnswerTypedText(answer),
            //guard the watch's own keys or a typed folder, same reason as the guards above
            DownloadKey when answer is Landed or PickAnother or UseTextOnly or Elsewhere
                || ShelfControls.Untyped(answer) is not null => AnswerDownload(answer),
            //guard the consent screen's own keys or a typed folder, any other answer falls to the throw
            ModelConsentKey when answer is ModelFetchNow or PickAnother
                || ShelfControls.Untyped(answer) is not null => AnswerModelConsent(answer),
            ModelFetchingKey => AfterModelFetch(answer),
            ModelFetchingPausedKey => AnswerModelFetchingPaused(answer),
            ModelArrivedKey => AnswerModelArrived(answer),
            ModelDroppedKey or ModelMismatchKey => AnswerModelEnding(answer),
            PartialSetKey => AnswerPartialSet(answer),
            AuditionOfferKey => AnswerAuditionOffer(answer),
            InSessionCheckKey => AnswerInSessionCheck(answer),
            AuditionRunningKey => AfterAudition(answer),
            AuditionPassedKey => AnswerAuditionPassed(answer),
            AuditionHeldKey => answer == AuditionHeldNext ? Done() : Leave(),
            //every pick-another answer goes to Search(), the one place that shows the models
            AuditionIncompleteKey => answer == CheckAgain ? StartAudition(_modelId!) : Search(),
            //only the Anyway answer advances, the other two return to a model or round the check again
            AuditionLoadAskKey => AnswerLoadAsk(answer),
            AuditionExitedKey or AuditionStoppedKey => answer == CheckAgain
                ? StartAudition(_modelId!)
                : answer == PickAnother ? Search() : Done(),
            AuditionWaitingKey => AfterTheOneLoad(answer),
            AuditionAnswersKey => answer == AnswersNext ? Done() : Leave(),
            AuditionFailedKey => AnswerAuditionFailed(answer),
            ProveKey => AnswerProve(answer),
            SummaryKey => AnswerSummary(answer),
            UpdateKey => AnswerUpdate(answer),
            //the steer answer is the path itself, the option row opens the connect road instead. the LlamaPathKey screen stays typed-only (a re-ask offers no options)
            SteerKey when answer == ForkConnect => TakeConnectRoad(),
            //one arm serves every engine screen with a typed path, one deed: check the exe the user pointed at. the guard tests typed, so a stray key falls to the throw
            (SteerKey or FoundKey or ConsentKey or FallbackKey or FailedKey)
                when ShelfControls.Untyped(answer) is { } typedPath =>
                VerifyLlama(Gatto.Roles.LlamaAssetSteering.NormalizePath(typedPath)),
            //the LlamaPathKey screen is an ask, its answer is free text and needs no unwrap
            LlamaPathKey => VerifyLlama(Gatto.Roles.LlamaAssetSteering.NormalizePath(answer)),

            //a caller answering when nothing waits is a caller bug, so this arm throws. the message names the screen, a waiting screen with no dispatch arm also arrives here
            _ => throw new InvalidOperationException(
                $"the screen '{_awaiting ?? "(none)"}' has no handler for the answer '{answer}'"),
        };
    }

    //the engine step sets the llama road, a user who never opens connect is not undecided. one handler serves the connect row on both first screens
    private WizardScreen EngineStep()
    {
        Path = SetupPath.Llama;
        _narrate.Add(new WizardScreen.Info("step.model", [new WizardRow("Model")]));
        return NextLlamaSegment();
    }


    //leave the llama road for the user's own server in one home (three screens open it, three copies could disagree)
    private WizardScreen TakeConnectRoad()
    {
        Path = SetupPath.Connect;
        return ProbeForServer();
    }

    //the one sentence for a server that is already gatto's own, shown on the found or retry screen. the value is at most one, one serve.json names one port
    private string? _ownServerSaid;

    //use a comma rather than an em dash (em dashes are forbidden on any wizard screen). the wording follows the drawn mock
    private static string OwnServerRow(string baseUrl, string model) =>
        $"The server at {baseUrl} is gatto's own, it is serving {model}, which is the road option 1 "
        + "on the engine screen takes.";

    //the typed door for a server not on a port gatto reaches. it is also why the retry state offers one option instead of two

    //a property rather than a const, a const can't read the chosen glyph set
    private string ServerDoorPlaceholder =>
        $"type the server's address{Glyphs.Ellipsis} e.g. http://127.0.0.1:8080";

    //a screen on the road rather than a failure page, the user is still asked something. ports are named by owner, so the user can recognise their own setup
    private WizardScreen NoServerAnswered() => Emit(new WizardScreen.Choice(
        RetryKey,
        "Which server should gatto talk to?",
        [new ChoiceOption(Retry, "Look again")],
        BodyRows: _ownServerSaid is { } said
            ? [
                new WizardRow($"{said} No other server answered."),
                new WizardRow(""),
                new WizardRow("Start the server you run yourself, then look again, or type its "
                    + "address if it lives somewhere else."),
              ]
            : [
                new WizardRow("No server answered on the ports llama.cpp and LM Studio usually use "
                    + "on this machine."),
                new WizardRow(""),
                new WizardRow("Start your server, then look again, or type its address if it lives "
                    + "somewhere else."),
              ],
        Door: ServerDoorPlaceholder), RetryKey);

    //accept only the retry option or a typed address. falling into the default arm would leave setup, the worst reading of a typed address
    private WizardScreen AnswerRetry(string optionKey) =>
        optionKey == Retry ? ProbeForServer() : TypedAddress(ShelfControls.Untyped(optionKey)!);

    //probe exactly the address the user typed, on silence re-show that screen (they told gatto where to look)
    private WizardScreen TypedAddress(string typed)
    {
        _found = probes.ProbeAt(typed.Trim());
        _contextWasReported = _found?.NCtx is > 0;
        return _found is null ? NoServerAnswered() : Complete(_found.BaseUrl, _found.NCtx ?? 0);
    }

    //look for a server, treat nothing found as a retry state rather than a dead exit. the user probably hasn't started their server yet, quitting means starting over
    private WizardScreen ProbeForServer()
    {
        //gatto's own serving server is narrated and passed rather than offered. the match keys on the answered port, a typed read stops a dead process claiming it
        List<int>? spent = null;
        _ownServerSaid = null;
        while (true)
        {
            _found = probes.ProbeServer(spent);
            if (_found is null) break;
            if (!Uri.TryCreate(_found.BaseUrl, UriKind.Absolute, out var uri)) break;
            if (probes.GattoServingOn(uri.Port) is not { Length: > 0 } serving) break;

            _ownServerSaid = OwnServerRow(_found.BaseUrl, serving);
            (spent ??= []).Add(uri.Port);
            _found = null;
        }

        if (_found is null) return NoServerAnswered();

        //never adopt a found server silently, the user confirms it. its url and model list are the server's own strings, shown through the sanitizing widget
        return Emit(new WizardScreen.Choice(
            ConfirmServerKey,
            //ask the road's question the same way on every screen of it, the title doesn't change under the user
            "Which server should gatto talk to?",
            [new ChoiceOption(Yes, "Use this server")],
            //compute the row from the model list, several entries are a catalogue rather than all up. the cost sentence stays here too
            BodyRows: [
                .. _ownServerSaid is { } skipped ? (IReadOnlyList<WizardRow>)[skipped, ""] : [],
                ConnectCost, "",
                ServerFacts.Row(ServerFacts.Server, _found.BaseUrl),
                //compute the row from the list itself: one entry is a model serving, several is a catalogue of what it could load on demand
                .. (IReadOnlyList<WizardRow>)(_found.Models switch
                {
                    { Count: 0 } => [ServerFacts.Row(ServerFacts.Serving, "it did not say")],
                    { Count: 1 } m => [ServerFacts.Row(ServerFacts.Serving, m[0])],
                    var many =>
                        [ServerFacts.Row(ServerFacts.Lists, $"{many.Count} models it can load")],
                }),
                //say the context wasn't reported rather than guessing at it, and name the ask that the next screen keeps
                ServerFacts.Row(ServerFacts.Context,
                    _found.NCtx is { } n && n > 0
                        ? $"{n:N0} tokens {Glyphs.Dot} reported by the server"
                        : "not reported, gatto will ask"),
            ],
            Door: OtherServerDoorPlaceholder), ConfirmServerKey);
    }


    //its own fact family, different steps use different words and a longer label must not collapse another's alignment. the labels come from the drawn mock
    internal static class SummaryFacts
    {
        public static readonly string[] Labels =
            ["machine", "engine", "model", "check", "config", "gatto", "server", "context"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static WizardRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);
    }

    //the check step's own label column, kept separate even when widths match today. different steps use different words, one shared width fits none
    internal static class ProveFacts
    {
        public static readonly string[] Labels =
            ["server", "model", "asking", "context", "said", "speed"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static WizardRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);

        //round the figure first and read the band from the rounded value, so the sentence can't disagree with the number shown. the band only chooses words
        public static WizardRow Speed(double rate)
        {
            var shown = Math.Round(rate, 1);
            var figure = $"~{shown:0.#} tok/s";
            return new WizardRow(
                "speed".PadRight(Column)
                    + $"{figure}, {Gatto.Roles.Audition.AuditionReport.SpeedNote(shown)}",
                RowTone.Aside, Hang: Column, Highlight: [figure]);
        }
    }

    //its own title rather than CheckTitle: that asks whether a model can run commands, this asks whether a server answers
    private const string ConnectCheckTitle = "Does it answer?";

    //the connect road's own label column, one shared width fits neither step's labels. the lists label stays listed so a wider label later can't collapse the rows
    internal static class ServerFacts
    {
        public const string Server = "server";
        public const string Serving = "serving";
        public const string Lists = "lists";
        public const string Context = "context";

        public static readonly string[] Labels = [Server, Serving, Lists, Context];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static WizardRow Row(string label, string value) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column);
    }

    //the door placeholder says another, a server is already found and named above it
    private string OtherServerDoorPlaceholder =>
        $"type another server's address{Glyphs.Ellipsis} e.g. http://127.0.0.1:8080";

    //state what this option is, don't phrase it as a warning, so a user of another server isn't turned away
    private static readonly WizardRow ConnectCost = new(
        "gatto will talk to this server as it is, it can't switch models or change settings on a "
        + "server it didn't start.");

    private WizardScreen AnswerConfirm(string optionKey)
    {
        //a typed answer here is another address rather than a refusal, the door invites one. read the typed mark directly, the wrapper sets it
        if (ShelfControls.Untyped(optionKey) is { } address) return TypedAddress(address);

        if (optionKey != Yes || _found is null) return Leave();

        //the context is required, so a server that reports its window settles it and one that doesn't gets asked. never infer the answer from a model ceiling
        _contextWasReported = _found.NCtx is > 0;
        return _found.NCtx is { } n && n > 0
            ? Complete(_found.BaseUrl, n)
            : Emit(new WizardScreen.Ask(
                ContextKey,
                //say why the number is asked, gatto asks only when the server would not say
                "How much context should gatto use?",
                BodyRows: [new WizardRow(
                    "This server didn't report its context window, so gatto has to ask. If the "
                    + "server reports its own context later, that number overrides this answer.")],
                Validate: ContextComplaint,
                //offer the default as a row the user can pick. a keystroke the render never showed the widget can't honour
                Placeholder: $"or type a number of tokens{Glyphs.Ellipsis}",
                //compute the label and the value from one constant, so the sentence and the number can't part. the value stays plain because it gets parsed
                Offer: new AskOffer(
                    $"Use {OfferedServerContext:N0}",
                    OfferedServerContext.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                //use next as the enter verb, answering this question is the step. one offered row with a door gives the same answer
                EnterVerb: "next"),
                ContextKey);
    }

    private WizardScreen AnswerContext(string typed)
    {
        if (_found is null) return Leave();

        //an empty answer accepts the offer, the placeholder is the offer and enter must mean what the screen says
        var text = typed.Trim();
        if (text.Length == 0) return Complete(_found.BaseUrl, OfferedServerContext);

        //re-check the answer here, the face validates but a test drives the flow directly
        if (ContextComplaint(text) is not null) return Emit(_emitted[^1], ContextKey);
        return Complete(_found.BaseUrl,
            int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    //null when the typed context is usable, otherwise the sentence to show. empty is usable, it takes the offer
    private static string? ContextComplaint(string typed)
    {
        var t = typed.Trim();
        if (t.Length == 0) return null;
        return int.TryParse(t, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? null
            : "That needs to be a whole number of tokens, like 4096";
    }

    //the connect path ends with one endpoint whose context is known.

    //the connect flow's completion. the endpoint and a model must both be set here, or the next launch throws
    private WizardScreen Complete(string baseUrl, int context)
    {
        _connectBaseUrl = baseUrl;
        _connectContext = context;

        //reuse an endpoint that already points here rather than adding a second one, server names only urls nothing is set for
        var existing = probes.ExistingEndpointFor(baseUrl);

        //the name local decides the launch branch, so an unmanaged server goes beside it rather than replacing it. this covers the config-only case
        _besideLocal = string.Equals(existing, "local", StringComparison.Ordinal);
        var name = _besideLocal ? null : existing;

        return ContinueConnect(name ?? "server");
    }

    //the served model that requests will name, gatto cannot invent it (only the server knows what it answers to)
    private WizardScreen ContinueConnect(string endpointName)
    {
        _connectEndpointName = endpointName;
        var models = _found?.Models ?? [];

        //ask only when several models are listed, one is not a choice and picking for the user would be gatto choosing their model
        if (models.Count == 1) return FinishConnect(endpointName, models[0]);

        if (models.Count > 1)
            return Emit(new WizardScreen.Choice(
                ModelPickKey,
                "Which model should gatto ask for?",
                //the options are the server's own strings, rendered through the widget that sanitizes them.
                [.. models.Select(m => new ChoiceOption(m, m))],
                BodyRows: [new WizardRow("This server lists several. gatto names one on every "
                    + "request; you can change it later in gatto.json.")],
                //leave the rows unnumbered, the server's own strings can be any length or count and the footer names the arrows
                Unnumbered: true),
                ModelPickKey);

        //keep the question in the title and the reason in the body (a title carrying both became the longest line)
        return Emit(new WizardScreen.Ask(
            ModelNameKey,
            "What model should gatto ask for?",
            BodyRows: [new WizardRow("This server didn't list its models. gatto names one on every "
                + "request, so it needs the name this server answers to.")],
            Validate: t => string.IsNullOrWhiteSpace(t) ? "Type the model name the server expects" : null,
            Placeholder: $"the name of the model this server answers to{Glyphs.Ellipsis}",
            //use next as the enter verb, it names where the answer goes while the default continue reads as resuming something
            EnterVerb: "next"), ModelNameKey);
    }

    //write the intent, then pause, the prove reads gatto.json from disk and would measure the config that stood before this flow
    private WizardScreen FinishConnect(string endpointName, string modelId)
    {
        _connectModel = modelId;
        Writes = Writes with
        {
            UpsertEndpoint = new WriteSet.Endpoint(endpointName, _connectBaseUrl!, _connectContext),
            //the connect endpoint resolves through the cloud branch, where default_model is a verbatim model string. do not turn this into a model lookup
            DefaultEndpoint = endpointName,
            DefaultModel = modelId,
        };
        NeedsWritesApplied = true;
        return Emit(WritesPending, null);
    }

    //the engine step's own label column, one width over two steps' label sets fits neither. list every label the screens use so a later one cannot move the column
    internal static class EngineFacts
    {
        public const string Found = "found";
        public const string Build = "build";
        public const string Checked = "checked";
        public const string Tested = "tested";

        //list the labels of screens that have not shipped yet, they are not dead and the widest sets the column the goldens pin
        public static readonly string[] Labels =
            [Found, Build, Checked, Tested, "fetch", "fetched", "from", "into", "download", "extract to"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        //one fact row, whose hang keeps a wrapped value under its own column rather than at the margin. the lift marks a file name the reader must recognise later.
        public static WizardRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);

    }


    //the step's label column, the widest label anywhere in the step sets it so the value column does not jump between frames
    internal static class CheckFacts
    {
        //list every label the check step can draw, the ones not yet on a shipped screen still set the column
        public static readonly string[] Labels =
        [
            "model", "tasks", "server", "running", "done", "quant", "sampling", "thinking",
            "speed", "recorded", "file", "found", "expected", "last lines", "first reply",
        ];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        //let this row take the two cells the frame keeps for prose, a fact block folds where prose wraps
        public static WizardRow Row(string label, string value, string? lift = null, bool wide = false) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift], Wide: wide);

        //a second line under a fact, at the fact's own column, so a continuation never sits off the value it continues.
        public static WizardRow Cont(string value, bool wide = false) =>
            new(new string(' ', Column) + value, RowTone.Aside, Indent: 0, Hang: Column, Wide: wide);

        //a fact whose value is a list that may break only between its items.
        public static WizardRow Row(string label, IReadOnlyList<string> items) =>
            new(label.PadRight(Column), RowTone.Aside, Indent: 0, Hang: Column, Items: items);
    }

    //the model block's label column, computed from its own widest label (a shared constant would use one block's word for the other)
    internal static class ModelFacts
    {
        public static readonly string[] Labels =
            ["fetch", "fetching", "fetched", "with", "and", "then", "from", "into", "download",
             "kept", "expected", "got", "found"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        //one fact row at this block's column, the same shape as the engine block but deliberately not shared, the column must differ
        public static WizardRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);
    }

    //compose the fetch block in one place, so the consent screen and the offline arm cannot describe one fetch two ways.
    private static EngineView EngineBlock(Offer offer, bool gattoFetches) => new(
        gattoFetches,
        offer.Pick.ZipName,
        offer.Assets is { } a ? SizeWords.Auto(a.Server.Size) : null,
        Gatto.Roles.LlamaAssetSteering.PinnedRelease,
        offer.Into,
        //take the companion from the pick rather than the release read, a runtime is a fact about the machine. the offline arm has no release read to consult
        offer.Pick.CudartZipName,
        offer.Assets?.Companion is { } c ? SizeWords.Auto(c.Size) : null);

    //the one typed-row placeholder for every engine screen that takes a path, so the door reads the same everywhere.

    //take the glyph set as an argument rather than a const, tests must name the vocabulary they assert
    internal static string LlamaDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"type the path to llama-server.exe{g.Ellipsis}";

    //the hub shelf's typed row, taken from the drawn corpus rather than written anew
    internal static string ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"search models{g.Ellipsis}";

    //the local shelf's longer placeholder, from the local shelf's own goldens, its door both searches the machine and takes a path
    internal static string LocalShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"search these, or type a .gguf to add, a folder to look in{g.Ellipsis}";

    //the one title for every screen of the engine step, a title that varied would read as a different step. it asks which build gatto should use
    internal const string EngineTitle = "Which engine build fits this machine?";

    //the model step's question, one function rather than two constants (a caller that picks could drift)
    internal static string ModelTitleFor(bool inSession) =>
        inSession ? "Which model should gatto add?" : "Which model should gatto start with?";

    //this flow's own question, for the road it is on.
    private string ModelTitle => ModelTitleFor(_addRoad);

    //the one door label for every screen that offers the connect road, so the spelling cannot drift. it names two servers so the reader recognises their own case
    internal const string ConnectDoorLabel =
        "Use a server you start yourself, LM Studio, Ollama, or another";

    //the one home for the opening fact of why this machine gets this build, both variables highlighted
    private static WizardRow SteeringRow(Gatto.Roles.LlamaAsset asset) =>
        new($"According to your hardware, {asset.HardwareFact}, you need the {asset.BuildName} build.",
            Highlight: [asset.HardwareFact, asset.BuildName]);

    private WizardScreen NextLlamaSegment()
    {
        //sweep every engine rather than reading one configured path, a machine with two builds is the case this screen exists for
        foreach (var engine in probes.EngineSweep())
        {
            //read the build from the verify that already ran, so the flow does not probe twice. the complaint sentences stay LlamaProbeComplaint's
            var result = probes.VerifyLlamaServer(engine.Path);

            //the screen names the exe that actually ran, and a use-it writes that one.
            if (result.Shape is Gatto.Core.Tools.ProbeShape.ClassicServer && result.Build is { } build)
                return Found(result.Ran ?? engine.Path, build,
                    Gatto.Roles.LlamaAssetSteering.Classify(build, engine.GattoFolder));

            //an exe that fails verification is not a found engine, the sweep is gatto's guess. a skipped engine goes into a note instead of a screen
            _narrate.Add(new WizardScreen.Info(EngineSkippedKey,
                [new WizardRow(
                    $"skipped {System.IO.Path.GetFileName(engine.Path)}, {SweepSkipReason(result)}",
                    RowTone.Aside)]));
        }

        return NoEngine();
    }


    //the short note for why the sweep dropped a candidate, written for the record rather than for the user
    private static string SweepSkipReason(Gatto.Core.Tools.ProbeResult result) => result.Shape switch
    {
        Gatto.Core.Tools.ProbeShape.KnownSibling when result.SiblingName is { Length: > 0 } =>
            "not the server",
        Gatto.Core.Tools.ProbeShape.NotClassic => "it did not answer as the server",
        Gatto.Core.Tools.ProbeShape.TimedOut => "it did not answer in time",
        Gatto.Core.Tools.ProbeShape.DllNotFound => "this Windows could not load it",
        _ => "gatto could not run it",
    };

    //the found screen states three facts and offers the two roads out. it asks nothing about whether the engine exists, the title already asks which build to run
    private WizardScreen Found(string path, string build, Gatto.Roles.LlamaAssetSteering.FoundState state)
    {
        //hold the exe the screen named, a second sweep at answer time could disagree with the row the user saw
        _foundEngine = path;
        //hold the build for the same reason as the path, the summary must report the build the user saw
        _foundBuild = build;

        var pin = Gatto.Roles.LlamaAssetSteering.PinnedRelease;
        var name = Gatto.Roles.LlamaAssetSteering.ReleaseName(build);

        //build the door row once for all four arms, so one ruled label cannot drift into four spellings.
        ChoiceOption door = new(ForkConnect, ConnectDoorLabel);
        ChoiceOption use = new(FoundUse, "Use it");

        //keep the first option as the default, so the order carries the choice and no flag can disagree with the rows
        (string Tail, IReadOnlyList<ChoiceOption> Options, string? Note) arm = state switch
        {
            Gatto.Roles.LlamaAssetSteering.FoundState.Exact =>
                ($" {Glyphs.Dot} the release gatto is tested with",
                 (IReadOnlyList<ChoiceOption>)[use, door], null),

            Gatto.Roles.LlamaAssetSteering.FoundState.Newer =>
                ($", newer than {pin}, the release gatto is tested with",
                 [use, new ChoiceOption(FoundFetch, $"Fetch {pin}, the tested release, instead"), door], null),

            //state the two builds and leave the reader to draw the relation. a sentence that calls one older reads as a verdict on a build the user chose.
            Gatto.Roles.LlamaAssetSteering.FoundState.Older =>
                ($" {Glyphs.Dot} {pin}, the release gatto is tested with",
                 [new ChoiceOption(FoundFetch, $"Fetch {pin}, the tested release"),
                  new ChoiceOption(FoundUse, "Keep the one that's here"), door],
                 "Newer models sometimes need a newer engine, the shelf's \"runs on this machine\" "
                 + "answers assume the tested release."),

            Gatto.Roles.LlamaAssetSteering.FoundState.Fork =>
                ($", not a release gatto recognises; the tested one is {pin}",
                 [use, new ChoiceOption(FoundFetch, $"Fetch {pin}, the tested release, instead"), door], null),

            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no found arm for this state"),
        };

        return Emit(new WizardScreen.Choice(
        FoundKey,
        EngineTitle,
        arm.Options,
        BodyRows:
        [
            new WizardRow("llama-server.exe is already on this machine.", Glyph: RowGlyph.Good),
            "",
            EngineFacts.Row(EngineFacts.Found, path),
            //spell the build through ReleaseName, the probe captures what the banner said while gatto writes the b form elsewhere
            EngineFacts.Row(EngineFacts.Build, name + arm.Tail),
            //use the past tense, this reports a run that already happened and the build above came from that same run
            EngineFacts.Row(EngineFacts.Checked, "it ran, and it answered as the server gatto drives"),
            //show the one dim line on the arm where the default flips, it explains why the shelf's answers depend on the engine
            .. arm.Note is null ? (IReadOnlyList<WizardRow>)[] : ["", new WizardRow(arm.Note, RowTone.Aside)],
        ],
        //keep the door idle rather than focused, a user with one engine is not being asked to type
        Door: LlamaDoorPlaceholderOf(Glyphs)), FoundKey);
    }

    //fail closed on a key this screen does not know, treating any other answer as use-it would commit a config write by omission
    private WizardScreen AnswerFound(string optionKey)
    {
        if (optionKey == ForkConnect) return TakeConnectRoad();

        //go straight to the fetch rather than through the consent screen, the pressed option already names it
        if (optionKey == FoundFetch)
            return FetchableOffer() is { Assets: not null } offer ? StartFetch(offer) : Steer();

        if (optionKey != FoundUse)
            throw new InvalidOperationException(
                $"the found screen has no handler for the answer '{optionKey}'");

        //non-null by construction, FoundKey reaches here only from Found
        _llamaPath = _foundEngine!;
        Writes = Writes with { LlamaServer = _foundEngine! };
        return Discover(extraRoot: null);
    }

    //what a fetch of the pinned release would be here, or null when there is none. a null asset means GitHub did not answer
    private sealed record Offer(Gatto.Roles.LlamaAsset Pick, string Into, EnginePair? Assets);

    //ask whether this machine can be fetched for, the only machine without a fetch is one gatto cannot read, it can name no build
    private Offer? FetchableOffer() =>
        probes.ChooseLlamaAsset() is { } pick && probes.EngineOffer() is { } o
            ? new Offer(pick, o.IntoDir, o.Assets)
            : null;

    //the engine step with nothing found: fetch the engine, fall back to a manual download, or steer when no fetch is possible.
    private WizardScreen NoEngine() =>
        FetchableOffer() is not { } offer ? Steer()
        : offer.Assets is not null ? Consent(offer)
        : Fallback(offer, "gatto couldn't reach GitHub to fetch the engine, so this time the "
            + "download is yours, everything below is exact.");

    //the one question before the engine download, stating what arrives, where from and where it goes. the size is read from GitHub rather than estimated
    private WizardScreen Consent(Offer offer)
    {
        _offer = offer;
        return Emit(new WizardScreen.Choice(
            ConsentKey,
            EngineTitle,
            [
                new ChoiceOption(ConsentFetch, "Fetch it now"),
                new ChoiceOption(ForkConnect, ConnectDoorLabel),
            ],
            BodyRows:
            [
                //lift llama.cpp rather than accent it, the engine is the subject of the sentence
                new WizardRow(
                    "Models don't run by themselves, they need an engine: llama.cpp, the open-source "
                    + "server gatto drives. gatto looked in the usual places and hasn't found it.",
                    RowTone.Aside, Lift: ["llama.cpp"]),
                "",
                SteeringRow(offer.Pick),
            ],
            Engine: EngineBlock(offer, gattoFetches: true),
            //keep the door idle rather than focused, a user who runs their own build points setup at it without leaving the screen
            Door: LlamaDoorPlaceholderOf(Glyphs)), ConsentKey);
    }

    //the offline arm, shown when the release read or the fetch failed, using the same destination the consent screen names
    private WizardScreen Fallback(Offer offer, string why)
    {
        _offer = offer;
        return Emit(new WizardScreen.Choice(
            FallbackKey,
            EngineTitle,
            [new ChoiceOption(FallbackDone, "Done, check that folder")],
            BodyRows: [new WizardRow(why, RowTone.Aside), "", SteeringRow(offer.Pick)],
            Engine: EngineBlock(offer, gattoFetches: false),
            Door: LlamaDoorPlaceholderOf(Glyphs)), FallbackKey);
    }

    //verify what arrived through the same probe every other door runs, so a wrong exe is refused before it runs
    private WizardScreen StartFetch(Offer offer)
    {
        //non-null by construction, both call sites match Assets first
        var assets = offer.Assets!;
        _fetchCancel = new CancellationTokenSource();
        var ct = _fetchCancel.Token;
        //fetch once, a second attempt to read the error could succeed and report a failure gatto no longer has
        _fetching = Task.Run(() => probes.FetchEngine(assets, new TickSink(OnTick), ct), CancellationToken.None);
        return Fetching(offer);
    }

    //the fetch has stopped, either it finished or Esc stopped it
    private WizardScreen AfterFetch(Offer offer, string answer)
    {
        var fetch = _fetching!;
        _fetching = null;

        if (answer == FetchStop)
        {
            _fetchCancel!.Cancel();
            //wait for the task to unwind before reporting, the partial is deleted inside the download's own failing path
            try { fetch.GetAwaiter().GetResult(); } catch (Exception) { } //stopping the fetch is not a failure, so the catch is empty.
            EndFetch();
            //return to the consent screen rather than leaving setup, declining the download is not leaving
            return Consent(offer);
        }

        Gatto.Cli.EngineFetch got;
        //turn a throw from the fetch into a screen, the task has already completed. an unhandled throw would leave the wizard watching a finished subject
        try { got = fetch.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            got = new(null, $"gatto couldn't finish the download: {ex.Message}");
        }
        EndFetch();

        if (got.ServerPath is not { } exe)
            return Fallback(offer, got.Error
                ?? "gatto couldn't finish the download, so this time it is yours, everything below "
                + "is exact.");

        var result = probes.VerifyLlamaServer(exe);
        if (LlamaProbeComplaint(result.Shape, offer.Pick.CudartZipName is not null,
                steeredDoor: true, result.VcRuntimeAbsent, result.SiblingName) is { } complaint)
            return Failed(result.Shape, complaint, exe);

        var verified = result.Ran ?? exe;
        _llamaPath = verified;
        Writes = Writes with { LlamaServer = verified };
        //take the zip name from the offer the screen was built from, the arrival sentence names what the user agreed to fetch
        return Fetched(offer, offer.Assets!.Server.ZipName);
    }

    //the engine arrived, so this line states a verification result rather than repeating the answer the user just gave.
    private WizardScreen Fetched(Offer offer, string zipName) => Emit(new WizardScreen.Choice(
        FetchedKey,
        EngineTitle,
        [new ChoiceOption(FetchedNext, "next"), new ChoiceOption(FetchedLeave, "leave")],
        BodyRows:
        [
            new WizardRow("That's the one, fetched, verified, and it answers as the server gatto "
                + "drives.", RowTone.Aside, Glyph: RowGlyph.Good, Lift: ["That's the one"]),
            "",
            EngineFacts.Row("fetched", zipName + $" {Glyphs.Dot} digest matched", lift: zipName),
            EngineFacts.Row("into", offer.Into),
            EngineFacts.Row(EngineFacts.Checked,
                $"{Gatto.Roles.EngineSearch.ExeName} ran, the path gatto writes to its config is "
                + "the .exe that ran"),
            //keep the closing blank, without it the last fact sits directly on the footer rule
            "",
        ],
        KeysOnly: true,
        //let the face supply the elapsed time from the watch it just ran, a figure composed here would be a second clock
        PurredSinceWatch: true), FetchedKey);

    private WizardScreen AnswerConsent(string optionKey)
    {
        if (optionKey == ForkConnect) return TakeConnectRoad();

        //fail closed on an unknown key, an unrecognised answer must not start a large download by omission
        if (optionKey != ConsentFetch)
            throw new InvalidOperationException(
                $"the consent screen has no handler for the answer '{optionKey}'");

        return StartFetch(_offer!);
    }

    //the screen where gatto does the work, one option only (nothing to choose). reuses the consent's EngineView, so nothing changes while the user watches
    private WizardScreen Fetching(Offer offer)
    {
        _offer = offer;
        return Emit(new WizardScreen.Choice(
            FetchingKey,
            EngineTitle,
            //declare the key even though its face cannot reach this screen. a fact stated only where it matters now turns a later reachability change into a defect
            [new ChoiceOption(FetchStop, "stop, deletes the partial", Advances: false)],
            BodyRows:
            [
                //state the screen's whole job in one aside, it is context for the rows below. a companion download gets its own sentence, both files must reach one folder
                new WizardRow(offer.Assets?.Companion is null
                    ? "Nothing to do here, gatto fetches the build, checks it against GitHub's own "
                        + "digest, and moves on by itself."
                    : "Nothing to do here, gatto fetches both files into one folder, checks each "
                        + "against GitHub's own digest, and moves on by itself.", RowTone.Aside),
            ],
            Engine: EngineBlock(offer, gattoFetches: true),
            Watching: true,
            KeysOnly: true), FetchingKey);
    }

    //one entry for both endings of the watch, each means the fetch has stopped and which one it was is the parameter
    private WizardScreen AnswerFetching(string answer) => AfterFetch(_offer!, answer);

    //answer with the folder rather than a composed exe path, one resolution rule finds the server inside it
    private WizardScreen AnswerFallback(string optionKey) =>
        optionKey == FallbackDone
            ? VerifyLlama(_offer!.Into)
            : throw new InvalidOperationException(
                $"the fallback screen has no handler for the answer '{optionKey}'");

    private WizardScreen AnswerFetched(string optionKey) =>
        optionKey == FetchedNext ? Discover(extraRoot: null) : Leave();

    //name the one asset this machine wants, companion included as the same unit, choosing among thirteen needs facts a user cannot know

    //the screen for a machine gatto cannot read, it states no build and offers no fetch (gatto must not guess a build it cannot price)
    private WizardScreen Steer() =>
        Emit(new WizardScreen.Choice(
            SteerKey,
            EngineTitle,
            [new ChoiceOption(ForkConnect, ConnectDoorLabel)],
            Door: LlamaDoorPlaceholderOf(Glyphs),
            BodyRows:
            [
                "Couldn't read this machine well enough to pick a llama.cpp build.",
                "",
                "You can download any llama.cpp Windows build and point gatto at llama-server.exe.",
                "",
                //name where to get a build, this branch has the least to go on. the url comes straight from LlamaAssetSteering
                new("All builds:", RowTone.Aside),
                new(Gatto.Roles.LlamaAssetSteering.ReleasePage, RowTone.Aside),
                "",
                "Once you're done, type the path below.",
            ]),
            SteerKey);

    //hint pasting rather than typing, people paste paths and a pasted folder works too. name a folder, the screen above tells the user to extract into one
    internal const string LlamaPathPlaceholder = @"Type or paste the path, e.g. C:\llama";

    //the one home for the path rule, shared by the three screens that ask for a path.
    private static string? LlamaPathComplaint(string typed) =>
        Gatto.Roles.LlamaAssetSteering.NormalizePath(typed).Length == 0
            ? "Paste the path to llama-server.exe"
            : null;

    //drive every branch off ProbeShape rather than parsing the output, and leave the cuda-no-device shape out until a real run captures it
    private WizardScreen VerifyLlama(string path)
    {
        var result = probes.VerifyLlamaServer(path);
        if (LlamaProbeComplaint(result.Shape, probes.ChooseLlamaAsset()?.CudartZipName is not null,
                steeredDoor: true, result.VcRuntimeAbsent, result.SiblingName)
            is { } complaint) return Failed(result.Shape, complaint, path);

        //write the exe the probe ran, writing back the folder would store a path that cannot start a server while claiming verification
        var verified = result.Ran ?? path;
        _llamaPath = verified;
        Writes = Writes with { LlamaServer = verified };
        //keep this narration line, it reports a verification result rather than repeating the answer, and the channel hands it to a face
        _narrate.Add(new WizardScreen.Info("llama.ok", [new WizardRow($"{Glyphs.Ok} That's the one")]));
        return Discover(extraRoot: null);
    }

    //the one home for what each probe shape means, shared by both verification paths. the cudart cause is certain only on the steered door
    private static string? LlamaProbeComplaint(
        Gatto.Core.Tools.ProbeShape shape, bool cudartExpected, bool steeredDoor,
        bool vcRuntimeAbsent, string? sibling) => shape switch
    {
        Gatto.Core.Tools.ProbeShape.ClassicServer => null,

        //name the right file beside the wrong one, a release ships many programs that answer alike and the user is one edit away
        Gatto.Core.Tools.ProbeShape.KnownSibling when sibling is { Length: > 0 } =>
            $"That is {sibling}, the server is {Gatto.Core.Acquire.LlamaSiblings.ServerName} "
            + "in the same folder.",

        //check the runtime first, a missing Visual C++ runtime cannot be fixed by anything beside the exe
        Gatto.Core.Tools.ProbeShape.DllNotFound when vcRuntimeAbsent =>
            "That build needs the Microsoft Visual C++ runtime, and this Windows doesn't have it. "
            + "Install the 'Visual C++ Redistributable (x64)' from Microsoft.",

        //the measured exit code is the signal, with empty output streams.
        Gatto.Core.Tools.ProbeShape.DllNotFound when cudartExpected && steeredDoor =>
            "That build starts and immediately stops, its CUDA runtime is missing. "
            + "Extract the cudart zip into the SAME folder.",

        //name no cause without a discriminator, so the sentence states what was observed and offers a conditional hint.
        Gatto.Core.Tools.ProbeShape.DllNotFound =>
            "That build starts and immediately stops, Windows says a file it needs is missing. "
            + "Check that everything from the download was extracted into the same folder."
            + (cudartExpected
                ? " If this is a CUDA build, it also needs the cudart zip extracted beside it."
                : ""),

        //three programs are called llama.cpp and only one shape works, so the sentence names the file to look for.
        Gatto.Core.Tools.ProbeShape.NotClassic =>
            "That runs, but it isn't the llama-server gatto drives, some builds ship "
            + "a single combined program instead. Look for llama-server.exe specifically.",

        Gatto.Core.Tools.ProbeShape.TimedOut =>
            "That didn't answer in time. If it's on a network drive, try a local copy.",

        _ => "Couldn't run that, check the path points at llama-server.exe itself.",
    };

    //two failure arms, re-checking the same path helps only when the file is right, otherwise the typed door is the only move
    private WizardScreen Failed(Gatto.Core.Tools.ProbeShape shape, string sentence, string tested)
    {
        _foundEngine = tested;

        IReadOnlyList<WizardRow> body =
        [
            //accent the exe name inside the sentence, it is the word to look for, and the highlight skips a span that is absent
            new WizardRow(sentence, Highlight: [Gatto.Roles.EngineSearch.ExeName], Glyph: RowGlyph.Bad),
            "",
            EngineFacts.Row(EngineFacts.Tested, tested),
        ];

        //name the runtime shape explicitly, only there does re-checking the same path help. every other shape falls to the typed door
        if (shape is Gatto.Core.Tools.ProbeShape.DllNotFound)
            return Emit(new WizardScreen.Choice(
                FailedKey,
                EngineTitle,
                [new ChoiceOption(FailedRetry, "Check it again")],
                BodyRows: body,
                Door: LlamaDoorPlaceholderOf(Glyphs)), FailedKey);

        return Emit(new WizardScreen.Ask(
            LlamaPathKey,
            //use the step's title as the label, the bad row says what happened and this says where in the flow the user is
            Label: EngineTitle,
            Validate: LlamaPathComplaint,
            Placeholder: LlamaDoorPlaceholderOf(Glyphs),
            BodyRows: body,
            EnterVerb: "check the path"), LlamaPathKey);
    }


    //strip the -GGUF suffix, the sentence is about the model rather than the quantized files. this stays a single-purpose helper, other screens name a file
    internal static string ModelDisplayName(string repoId)
    {
        var name = repoId.Split('/').Last();
        return name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    //the repo as a person reads it, without a scheme, this row is a fact rather than a link
    private static string HubLabel(string repoId) => "huggingface.co/" + repoId;

    //put the weights first and the encoder last, that is the fetch order and the next-file reader reads position
    private ModelView ViewOf(ModelFetchOffer offer)
    {
        var files = new List<ModelFetchFile>();
        foreach (var m in offer.Weights.Members)
            files.Add(new(m.FileName, SizeWords.Auto(m.Bytes)));
        if (offer.Projector is { } encoder)
            foreach (var m in encoder.Members)
                files.Add(new(m.FileName, SizeWords.Auto(m.Bytes), Encoder: true));
        return new(HubLabel(offer.RepoId), offer.IntoDir, files, FreeWords(offer.IntoDir));
    }

    //return nothing when there is only one file, the sentence exists to explain two files and absence is the honest copy
    private IReadOnlyList<WizardRow> ConsentProse(ModelFetchOffer offer)
    {
        //show the root note first, it is about the deed the user just did
        if (_rootNote is { Length: > 0 } note)
            return [new WizardRow(note, RowTone.Aside)];
        var shards = offer.Weights.Members.Count;
        if (shards > 1)
            return [new WizardRow($"This model is {shards} files, gatto fetches them one by one "
                + "into one folder, and checks each file, byte for byte, as it lands.", RowTone.Aside)];
        if (offer.Projector is not null)
            return [new WizardRow($"{ModelDisplayName(offer.RepoId)} can see images, so the fetch "
                + "is two files, the model and the small encoder that does the seeing.", RowTone.Aside)];
        return [];
    }

    //keep a margin above the download size, a disk that fills mid-fetch loses the whole transfer
    internal const long WeightsMarginBytes = 1_000_000_000L;

    //use the engine door's shape and words, a user who typed a path once should not learn a second way
    internal static string WeightsDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"type a folder to keep model files in{g.Ellipsis}";

    //word the free space on the drive at this path, or null when the probe cannot read it, and null renders nothing.
    private string? FreeWords(string path) =>
        probes.FreeSpaceOn(path) is { } free ? SizeWords.Auto(free) + " free" : null;

    //count the weights and the encoder together, a refusal computed from the weights alone would pass a target that fills mid-fetch
    private static long DownloadBytes(ModelFetchOffer offer) =>
        offer.Weights.Members.Sum(m => m.Bytes)
        + (offer.Projector is { } p ? p.Members.Sum(m => m.Bytes) : 0L);

    //name all three figures in the refusal, so the user can see which number to change. refuse only when the drive can be read
    private string? TooSmall(ModelFetchOffer offer, string root)
    {
        if (probes.FreeSpaceOn(root) is not { } free) return null;
        var wanted = DownloadBytes(offer);
        if (free >= wanted + WeightsMarginBytes) return null;
        return $"not enough room in {root}: {SizeWords.Auto(wanted)} to fetch plus a "
            + $"{SizeWords.Auto(WeightsMarginBytes)} margin, and {SizeWords.Auto(free)} free.";
    }

    //say the model's name and how far the download got. one home, so the option and the table cannot spell the row differently
    private static string ResumeRow(ISetupProbes.PausedFetch p) =>
        $"resume {ModelDisplayName(p.RepoId)}, {SizeWords.Auto(p.Done)} of {SizeWords.Auto(p.Total)}";

    //gatto asks before it fetches. one option, the way out is Esc and the footer says so. a second numbered row saying no is the same choice offered twice
    private WizardScreen ModelConsent(ModelFetchOffer offer)
    {
        _modelOffer = offer;
        _modelView = ViewOf(offer);
        //reset the clock for a new consent, resuming skips this screen and the elapsed must accumulate across a drop
        _modelElapsedMs = 0;
        return Emit(new WizardScreen.Choice(
            ModelConsentKey,
            ModelTitle,
            //add the second option, one option plus an escape is a trap with no way back to the shelf. reuses the watch screen's words, no new copy
            [new ChoiceOption(ModelFetchNow, "Fetch it now"),
             new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
            BodyRows: ConsentProse(offer),
            //state in the placeholder what typing into the folder door does, a refusal with no words looks like a folder that vanished
            Door: WeightsDoorPlaceholderOf(Glyphs),
            Model: _modelView), ModelConsentKey);
    }

    //the fetch runs and the user waits, the face draws from the tick the flow stores
    private WizardScreen ModelFetching()
    {
        var shards = _modelOffer!.Weights.Members.Count;
        return Emit(new WizardScreen.Choice(
            ModelFetchingKey,
            ModelTitle,
            //pause keeps the bytes and stop deletes the partial. the escape option goes last, as the face requires on a watching screen
            [
                new ChoiceOption(ModelFetchPause, "pause", Advances: false, Press: "Space"),
                new ChoiceOption(ModelFetchStop, "stop, deletes the partial", Advances: false),
            ],
            BodyRows:
            [
                new WizardRow(shards > 1
                    ? $"This model is {shards} files, gatto fetches them one by one into one folder, "
                        + "and checks each file, byte for byte, as it lands."
                    : "Nothing to do here, gatto fetches, checks each file is byte for byte the one "
                        + "Hugging Face publishes, and moves on by itself.", RowTone.Aside),
            ],
            Model: _modelView,
            Watching: true,
            KeysOnly: true), ModelFetchingKey);
    }

    //nothing is running here, so stop deletes the partial through the probe and goes back to consent, as the live stop does
    private WizardScreen AnswerModelFetchingPaused(string answer)
    {
        if (answer == ModelFetchResume)
        {
            //start the fetch the same way as everywhere else, HubFetch reads the partial file to decide what resume means
            _modelPaused = false;
            return StartModelFetch(_modelOffer!);
        }

        if (answer != ModelFetchStop)
            throw new InvalidOperationException(
                $"the paused fetch screen has no handler for the answer '{answer}'");

        probes.DeletePartial(_modelOffer!);
        _modelPaused = false;
        EndFetch();
        return ModelConsent(_modelOffer!);
    }

    //keep Watching true while paused, the escape chord depends on it and Esc would otherwise leave the session with the bytes still on disk
    private WizardScreen ModelFetchingPaused()
    {
        var shards = _modelOffer!.Weights.Members.Count;
        return Emit(new WizardScreen.Choice(
            ModelFetchingPausedKey,
            ModelTitle,
            //put resume on the key that paused, a screen whose only ways out delete the bytes is a trap. the go-back option stays out, an option leading nowhere is worse
            [
                new ChoiceOption(ModelFetchResume, "carry on", Advances: false, Press: "Space"),
                new ChoiceOption(ModelFetchStop, "delete the partial", Advances: false),
            ],
            BodyRows:
            [
                new WizardRow(shards > 1
                    ? $"This model is {shards} files, gatto fetches them one by one into one folder, "
                        + "and checks each file, byte for byte, as it lands."
                    : "Nothing to do here, gatto fetches, checks each file is byte for byte the one "
                        + "Hugging Face publishes, and moves on by itself.", RowTone.Aside),
            ],
            Model: _modelView,
            Watching: true,
            KeysOnly: true,
            Paused: true), ModelFetchingPausedKey);
    }

    //every file was checked against its published digest, that's why the screen says byte for byte. the elapsed comes from the caller, a second clock could disagree
    private WizardScreen ModelArrived(ModelView view, long elapsedMs)
    {
        //set the verified flag here, the fetch checked every file to reach it, other routes to this screen don't
        _modelVerified = true;
        return ModelArrivedScreen(view, elapsedMs);
    }

    private WizardScreen ModelArrivedScreen(ModelView view, long elapsedMs) => Emit(new WizardScreen.Choice(
        ModelArrivedKey,
        ModelTitle,
        [new ChoiceOption(ModelArrivedNext, "next"), new ChoiceOption(ModelArrivedLeave, "leave")],
        BodyRows:
        [
            new WizardRow($"{view.First.Name} arrived, byte for byte the "
                + (view.Files.Count > 1 ? "files" : "file") + " Hugging Face publishes.",
                RowTone.Aside, Glyph: RowGlyph.Good, Lift: [view.First.Name]),
            "",
            ModelFacts.Row("fetched", view.First.Name + $" {Glyphs.Dot} " + view.First.Size + $" {Glyphs.Dot} verified",
                lift: view.First.Name),
            .. view.Files.Skip(1).Select(f => ModelFacts.Row(
                f.Encoder ? "with" : "and", f.Name + $" {Glyphs.Dot} " + f.Size + $" {Glyphs.Dot} verified", lift: f.Name)),
            ModelFacts.Row("into", view.Into),
            //keep the closing blank, a keys-only screen has no option list
            "",
        ],
        KeysOnly: true,
        PurredMs: elapsedMs), ModelArrivedKey);


    //keep the partial after a drop, an option that deleted it would break the promise on the screen. the two rows read one figure off the kept tick
    private WizardScreen ModelDropped(FetchTick kept)
    {
        var (done, total) = SizeWords.Pair(kept.Done, kept.Total);
        //the delete option appears only on the setup screen. a session leaves on one Esc, a delete no line shows must not destroy bytes
        _droppedPartial = _addRoad ? null : _modelOffer;
        return Emit(new WizardScreen.Choice(
            ModelDroppedKey,
            ModelTitle,
            [new ChoiceOption(ModelResume, "Resume the fetch"),
             new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
            BodyRows:
            [
                new WizardRow("The connection dropped mid-fetch. What already arrived is safe, "
                    + "resuming picks up where it stopped, nothing is fetched twice.",
                    RowTone.Aside, Glyph: RowGlyph.Bad),
                "",
                ModelFacts.Row("kept", kept.FileName, lift: kept.FileName),
                ModelFacts.Row("", $"{done} of {total}"),
            ],
            Kept: kept), ModelDroppedKey);
    }

    //show both fingerprints, a screen saying only there was no match leaves nothing to check. the bad file is deleted, nothing is kept and no delete is promised
    private WizardScreen ModelMismatch(Gatto.Core.Acquire.HubFetchResult got) => Emit(new WizardScreen.Choice(
        ModelMismatchKey,
        ModelTitle,
        [new ChoiceOption(ModelRefetch, "Fetch it again"),
         new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
        BodyRows:
        [
            new WizardRow("What arrived is not the file Hugging Face publishes, the copy broke "
                + "somewhere on the way, so gatto deleted it. Fetching again usually fixes it.",
                RowTone.Aside, Glyph: RowGlyph.Bad),
            "",
            ModelFacts.Row("expected", Fingerprint.Short(Glyphs, got.Expected)
                + $" {Glyphs.Dot} the file's sha256 fingerprint on Hugging Face"),
            ModelFacts.Row("got", Fingerprint.Short(Glyphs, got.Actual) + $" {Glyphs.Dot} what actually arrived"),
        ]), ModelMismatchKey);


    //keep the trailing separator on the folder that holds a file, so a row reads as a place rather than as a truncated path.
    private static string Folder(string path) =>
        System.IO.Path.GetDirectoryName(path) + System.IO.Path.DirectorySeparatorChar;

    //set _arrivedByWatch where the watch's find is adopted, so OfferMove gives the arrival's words rather than a bare question
    private WizardScreen AdoptFromWatch(FoundModel model)
    {
        _arrivedByWatch = true;
        return Adopt(model);
    }

    private WizardScreen AnswerModelEnding(string key)
    {
        //clear _droppedPartial before the switch, every option here removes the delete promise. a path added later must not leave it standing
        _droppedPartial = null;

        return key switch
        {
            //same deed, the fetch resumes when a .part is on disk
            ModelResume or ModelRefetch => StartModelFetch(_modelOffer!),
            PickAnother => Search(),
            _ => throw new InvalidOperationException(
                $"the screen '{_awaiting ?? "(none)"}' has no handler for the answer '{key}'"),
        };
    }

    //nothing is written here, the root is only a Writes intent. a folder typed and entered accepts the consent, a refusal keeps the screen
    private WizardScreen TypedWeightsRoot(string typed)
    {
        //treat an empty answer as no folder, accepting it would set an empty root as a Writes intent and the scaffold writes it
        if (string.IsNullOrWhiteSpace(typed)) return ModelConsent(_modelOffer!);

        var root = Gatto.Roles.LlamaAssetSteering.NormalizePath(typed);
        var offer = _modelOffer!;

        if (TooSmall(offer, root) is { } refusal)
        {
            _rootNote = refusal;
            return ModelConsent(offer);
        }

        Writes = Writes with { WeightsRoot = root };
        _rootNote = null;

        var accepted = offer with
        {
            //spell out System.IO.Path, this class has its own Path property and a bare name would resolve to that
            IntoDir = Gatto.Core.Acquire.ModelLocation.ForModelIn(root, offer.ModelId)
                + System.IO.Path.DirectorySeparatorChar,
        };

        //move _modelOffer and _modelView together, or the fetching screen names the old folder while the transfer goes to the new one
        _modelOffer = accepted;
        _modelView = ViewOf(accepted);
        return StartModelFetch(accepted);
    }

    //start a fetch only on the fetch key, everything else goes to TypedWeightsRoot and back to this screen. never throw here, the screen offers the typed row itself
    private WizardScreen AnswerModelConsent(string key)
    {
        //send PickAnother to Search, the one place that shows the shelf again, so this screen cannot drift from the watch screen's copy
        if (key == PickAnother) return Search();

        //read the typed mark to learn the answer is a folder. the dispatch throws on anything that is neither an option nor a folder
        if (ShelfControls.Untyped(key) is { } typed) return TypedWeightsRoot(typed);

        //clear _rootNote here rather than on emit, it describes the last answer and dies on the next real deed
        _rootNote = null;
        return StartModelFetch(_modelOffer!);
    }

    //run the fetch on a task the screen watches, a synchronous fetch leaves no clock turning and the progress bar cannot move
    private WizardScreen StartModelFetch(ModelFetchOffer offer)
    {
        _modelFetchCancel = new CancellationTokenSource();
        var ct = _modelFetchCancel.Token;
        //mint a fresh _modelFetchPause per sitting, a resume comes through here and EndFetch already dropped the previous one
        _modelFetchPause = new CancellationTokenSource();
        var pause = _modelFetchPause.Token;
        //clear _modelPaused here, every path that starts or resumes a fetch passes through this method
        _modelPaused = false;
        //measure the wait here for the arrival screen, the tick's elapsed is not the whole wait
        _modelClock = System.Diagnostics.Stopwatch.StartNew();
        //pass ct to the delegate, on Task.Run it would mean do not start if already cancelled. the delegate owns the partial
        _modelFetch = Task.Run(() => probes.FetchModel(offer, new TickSink(OnTick), ct, pause), CancellationToken.None);
        return ModelFetching();
    }

    //the fetch stopped (finished, dropped, paused or stopped by a key), its outcome picks the next screen
    private WizardScreen AfterModelFetch(string answer)
    {
        var fetch = _modelFetch!;
        _modelFetch = null;
        //accumulate rather than read, a resumed fetch is one wait in two sittings and the arrival reports the whole wait
        _modelElapsedMs += _modelClock?.ElapsedMilliseconds ?? 0;

        if (answer == ModelFetchStop)
        {
            _modelFetchCancel!.Cancel();
            //wait for the fetch to unwind, the partial is deleted inside the cancelled path, returning early reports it gone while still on disk
            try { fetch.GetAwaiter().GetResult(); } catch (Exception) { } //a stopped fetch is not a failure
            EndFetch();
            //the user declined this download, which is not leaving setup, so go back to consent. the partial is gone and the offer is honest again
            return ModelConsent(_modelOffer!);
        }

        //cancel the pause source before the join, or the join waits for a transfer nobody asked to stop
        if (answer == ModelFetchPause) _modelFetchPause!.Cancel();

        Gatto.Core.Acquire.HubFetchResult got;
        //turn a throw into a Dropped result, an unhandled throw leaves the wizard watching a finished fetch. keep this the one join and catch for every path
        try { got = fetch.GetAwaiter().GetResult(); }
        catch (Exception) { got = new(Gatto.Core.Acquire.HubFetchOutcome.Dropped); }

        //draw the paused screen only when the fetch reports Paused, the face waits for a key and a fetch can finish inside that window
        if (got.Outcome == Gatto.Core.Acquire.HubFetchOutcome.Paused)
        {
            //read the last tick after the join, ticks arrive from the fetch's own thread. keep it for the paused screen's bar, clear the rest after
            EndFetch(keepTick: true);
            _modelPaused = true;
            return ModelFetchingPaused();
        }
        //read the tick after the join, a cross-thread read of an unarrived value would hide the kept partial from the drop screen. read it before EndFetch clears it
        var last = PollTick();
        EndFetch();

        return got.Outcome switch
        {
            Gatto.Core.Acquire.HubFetchOutcome.Arrived =>
                ModelArrived(_modelView!.Value, _modelElapsedMs),
            //a drop that kept a tick can be resumed, the drop screen shows the figure
            Gatto.Core.Acquire.HubFetchOutcome.Dropped when last is { } kept => ModelDropped(kept),
            //a drop that kept nothing goes to the browser watch, the Hub was out of reach and nothing arrived. the watch can verify with the offer's fingerprints
            Gatto.Core.Acquire.HubFetchOutcome.Dropped => Watch(WatchCause.Unreachable),
            Gatto.Core.Acquire.HubFetchOutcome.Mismatch => ModelMismatch(got),
            //route this arm to the watch, the tree changed between the offer and the fetch, gatto can't prove what it pulled
            _ => Watch(WatchCause.NoFingerprint),
        };
    }

    //rescan the disk instead of composing a FoundModel from the offer, adopt refuses an incomplete set and only the disk can say it's complete
    private WizardScreen AnswerModelArrived(string key)
    {
        if (key != ModelArrivedNext) return Leave();

        var rescan = probes.Scan(_modelOffer!.IntoDir);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;
        //return to consent when the rescan cannot find the arrival it announced, the watch would render a stale cause after a verified fetch
        return _discovered.FirstOrDefault(m => Answers(m, Picked!.PickedQuant)) is { } landed
            ? Adopt(landed)
            : ModelConsent(_modelOffer!);
    }

    //offer what is already on this machine before proposing a download, an empty scan is normal rather than a failure
    private WizardScreen Discover(string? extraRoot)
    {
        Sweep(extraRoot);

        //keep the discovered list first on the plain face, it cannot bind m, hiding the list behind the key would delete it
        if (!CanSwitchSource) return DiscoveredScreen(heading: null);

        //run the search before choosing a landing, the arm below depends on its outcome and a landing chosen first would guess at it
        RunSearch(keepIfEmpty: null);

        //open the local shelf on an outage only when this machine has models, an empty machine gets the Hub's failure screen. scope it to the arrival only
        if (_searchCause is HubSearchCause.HubFailed && _discovered.Count > 0)
            return DiscoveredScreen(heading: null, notice: OutOfReach);

        return SearchScreen();
    }

    //treat a typed .gguf as the model itself, sweep its folder and adopt the scan's row (a second FoundModel here could disagree)
    private WizardScreen DiscoverTyped(string root)
    {
        if (root.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            if (System.IO.Path.GetDirectoryName(root) is { Length: > 0 } folder)
            {
                Sweep(folder);
                if (_discovered.FirstOrDefault(m =>
                        string.Equals(m.Path, root, StringComparison.OrdinalIgnoreCase)) is { } named)
                    return Adopt(named);
            }
            else
            {
                //for a bare .gguf name don't sweep a folder, the name is not a folder either, and leave _scanTyped unset
                _scanTyped = null;
                Sweep(null);
            }
        }
        else
            //sweep a non-.gguf path as itself, and a .gguf's folder, a file path as a folder hides every model
            Sweep(root);

        //answer a typed folder with the local shelf and its invitation, so an empty folder says so.
        return DiscoveredScreen(heading: null, invitation: true);
    }

    //keep the sweep and the rows it leaves together, so two screens cannot scan the same folder differently.
    private void Sweep(string? extraRoot)
    {
        var scan = probes.Scan(extraRoot);
        _discovered = scan.Found;
        _scanRoots = scan.Roots;
        //keep _scanTyped latched through later ambient sweeps, the user typed it and these rows still come from that folder
        if (extraRoot is { Length: > 0 }) _scanTyped = extraRoot;
        //filter the scan's rows to the typed folder and name only that folder in the roots, the scan merges the ambient roots in
        if (_scanTyped is { } typed)
        {
            _discovered = [.. scan.Found.Where(m => Gatto.Core.Acquire.ModelLocation.IsInside(typed, m.Path))];
            _scanRoots = [typed];
        }
    }

    //build the list over the last scan, the folder answer matches filenames against those results. set invitation only where the user asked for this shelf
    private WizardScreen DiscoveredScreen(string? heading, string? notice = null, bool invitation = false)
    {
        //the empty shelf names where gatto looked and invites a folder, the user's models may live on a drive that was never scanned
        if (invitation && _discovered.Count == 0 && _scanRoots.Count > 0)
            return Emit(new WizardScreen.Choice(
                DiscoveredKey,
                ModelTitle,
                [new ChoiceOption(Elsewhere, $"Look in another folder{Glyphs.Ellipsis}"),
                 new ChoiceOption(SearchInstead, "Find one to download instead")],
                Door: $"type a folder path{Glyphs.Ellipsis}",
                //pass no families for an empty shelf, the block takes the table's place and the chips row draws nothing
                Shelf: new ShelfView([], null, Shape, Source: ShelfSource.Local, Empty: EmptyLocalRows())),
                DiscoveredKey);

        if (_discovered.Count == 0)
        {
            //add the scan line only when rows follow it, the next screen already reports nothing and two messages read as a fault
            var searched = Search();
            if (_rows.Count > 0)
                //put this on the narration channel rather than _emitted, a row queued behind the returned screen is never read
                _narrate.Add(new WizardScreen.Info("model.none",
                    [new WizardRow("No models found on this machine yet"), .. LookedInRows()]));
            return searched;
        }

        //adopted models stay (with their have-mark), this is also the reuse picker
        var shown = new List<(int At, FoundModel Model)>();
        //no chip lit shows every family, models with no family included
        for (var i = 0; i < _discovered.Count; i++)
            if (_localFamily is not { } fam
                || Families.Load().FamilyOf(_discovered[i].Header?.Architecture) == fam)
                shown.Add((i, _discovered[i]));

        var options = new List<ChoiceOption>();
        foreach (var (at, m) in shown)
            //keep a vendor off the row, a vendor label editorialises about someone else's weights
            options.Add(new ChoiceOption(
                at.ToString(System.Globalization.CultureInfo.InvariantCulture), //index into _discovered even when a chip filters, AnswerDiscovered reads it back from there
                System.IO.Path.GetFileName(m.Path),
                $"{Gb((ulong)Math.Max(0, m.FileBytes))} {Glyphs.Dot} {System.IO.Path.GetDirectoryName(m.Path)}"));
        options.Add(new ChoiceOption(Elsewhere, $"Look in another folder{Glyphs.Ellipsis}"));
        options.Add(new ChoiceOption(SearchInstead, "Find one to download instead"));

        return Emit(new WizardScreen.Choice(
            DiscoveredKey,
            heading ?? (_discovered.Count == 1 ? "Found a model already on this machine" : $"Found {_discovered.Count} models already on this machine"),
            options,
            //draw the notice in the body above the chips, where the Hub shelf puts its discovery line, so a shelf's reason always appears there
            BodyRows: notice is { Length: > 0 } why ? [new WizardRow(why, Tone: RowTone.Aside)] : [],
            //keep the door even when a chip empties the shelf, only the filter can empty it here. a null door and shelf make the face draw a numbered list
            Door: LocalShelfDoorPlaceholderOf(Glyphs),
            //draw the local list through the same shelf view as the Hub, only the source differs
            Shelf: LocalView([.. shown.Select(s => s.Model)],
                empty: shown.Count == 0 ? LocalChipEmptyRows(_discovered.Count) : null)), DiscoveredKey);
    }

    //the fit check decides the shelf's rows. an empty result offers the ways that still work
    private WizardScreen Search(IReadOnlyList<ShelfRow>? keepIfEmpty = null)
    {
        //drop _scanTyped here, every path from the local shelf to the Hub passes through this method. a latch outliving its shelf would filter the next
        _scanTyped = null;
        RunSearch(keepIfEmpty);
        return SearchScreen();
    }

    //run the search without emitting a screen, the caller reads the outcome first and a screen no face painted would still sit in _emitted
    private void RunSearch(IReadOnlyList<ShelfRow>? keepIfEmpty)
    {
        var outcome = probes.Search(
            new HubSearchRequest(_view, _axis, _lift, RowBudget, _search, _family, _publisher));

        //keep the rows when a sort returns nothing, the sort control must never empty the list it arranges. re-sort them through the shared Arrange
        if (outcome.Rows.Count == 0 && keepIfEmpty is { Count: > 0 } kept)
        {
            //set _carriedOver where the fallback happened rather than from the result's shape, so the sentence can say the order changed but the selection did not
            _carriedOver = true;
            var badges = kept.ToDictionary(r => r.RepoId, r => r.Badge, StringComparer.OrdinalIgnoreCase);
            _rows = [.. HubSearch.Arrange(kept, _axis ?? HubSearch.OrderFor(_view),
                HubSearch.OrderFor(_view), id => badges.GetValueOrDefault(id))];
        }
        else
        {
            _carriedOver = false;
            _rows = outcome.Rows;
            _searchCause = outcome.Cause;
            _hiddenByFit = outcome.HiddenByFit;
            _hiddenByKind = outcome.HiddenByKind;
            _hiddenByFamily = outcome.HiddenByFamily;
            _hiddenOlder = outcome.HiddenOlder;
            _hiddenNewer = outcome.HiddenNewer;
        }
        //set _stoodOnAShelf from the rows that came back, so it reports the shelf the user actually has
        if (_rows.Count > 0) _stoodOnAShelf = true;
        //refresh _curatedPublisher from the new outcome even when the rows were held, a sort leaves the view unchanged
        _curatedPublisher = outcome.CuratedPublisher;
    }

    //draw the Hub shelf over whatever RunSearch last found
    private WizardScreen SearchScreen()
    {
        //keep the screen's own row budget, the engine's request cap is another component's number. the escape rows come out of the same budget
        var shown = Math.Min(_rows.Count, RowBudget);

        //read the loaded model once for the whole shelf, probing the running server per row costs a round trip per row
        var loadedId = probes.LoadedModelId();

        //read the machine once, pricing six rows against six reads gives six chances to disagree about the card
        var hardware = probes.Hardware() is { } probe
            ? Gatto.Core.Hardware.HardwareClassifier.Classify(probe) : null;

        var options = new List<ChoiceOption>();

        //a paused fetch takes the shelf's first row, in session only, read from disk so it survives a restart
        var paused = _addRoad ? probes.PausedFetches() : [];
        var resume = paused.Count > 0 ? paused[0] : null;
        if (resume is not null)
            options.Add(new ChoiceOption(ResumePausedFetch, ResumeRow(resume)));

        for (var i = 0; i < shown; i++)
            options.Add(new ChoiceOption(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                //make the option's label the identity only, the transcript echoes it and a whole table row there is unreadable
                SearchRow.Label(_rows[i])));
                //add no description to the option, the facts that change the pick are table columns and the rest is on the model's page

        //keep the point-at-a-folder option, a user whose models sit on an unswept drive has no way out but re-downloading them
        options.Add(new ChoiceOption(Elsewhere, "I already have a model, let me point at the folder"));
        //on an empty shelf with no frame offer the escape rows again, the frame's keys are not shown there. a function that is a key must not also be a row
        if (_rows.Count == 0 && !SearchFoundNothing)
        {
            //the allowlist shapes browsing, a user can still type any model by name
            options.Add(new ChoiceOption(TypeAnId, "Type a model's name from Hugging Face"));
            //gate this on the curated slug, a dropped slug already searched everyone. a widen row over a wide list is a row that does nothing
            if (_curatedPublisher is { Length: > 0 })
                options.Add(new ChoiceOption(Broaden, "Show models from every approved publisher"));
        }
        //keep the neutral empty branch, it claims nothing and covers an emptiness the engine cannot evidence


        //say what these rows are first and the order second, keep a verdict word like best off the heading
        return Emit(new WizardScreen.Choice(
            SearchKey,
            //the heading follows rows and the frame, so the typed-word sentence shows only where no shelf speaks
            Question: _rows.Count != 0 || HasLadder ? ModelTitle
                : SearchFoundNothing ? $"Nothing on Hugging Face answers \"{_search}\""
                : EmptyHeading(_searchCause),
            options,
            //a populated shelf keeps one discovery aside, the header strip says the rest. an empty shelf keeps its prose unless a frame speaks for it
            BodyRows: _rows.Count > 0 ? DiscoveryRows()
                : HasLadder ? [] : EmptyRows(_searchCause),
            //door and shelf appear together only when a family ladder is in force, whatever the row count. pass the shown slice, the cap above decided it
            Door: HasLadder ? ShelfDoorPlaceholderOf(Glyphs) : null,
            Shelf: !HasLadder ? null : new ShelfView(
                [.. _rows.Take(shown)], _curatedPublisher, Shape,
                //resolve the axis here, _axis is null until s and the view's default answers. one place decides, so header and rows cannot describe different orders
                _axis ?? HubSearch.OrderFor(_view), _lift, _hiddenByFit,
                //take the ladder from the families table, the chip order is recorded in families.json and a second copy could drift
                Families: Families.Load().Ladder, Family: _family ?? "all",
                CarriedOver: _carriedOver, HiddenByKind: _hiddenByKind,
                //the local shelf feeds this field too, Shelf.cs composes the sentence for both sides
                HiddenByFamily: _hiddenByFamily,
                //count what the budget left off from the rows this search produced, a budget-sized count could report rows that never existed
                HiddenOlder: _hiddenOlder, HiddenNewer: _hiddenNewer,
                Total: _rows.Count + _hiddenByFit + _hiddenOlder + _hiddenNewer,
                MoreBelow: _rows.Count - shown,
                //build the facts over the same enumeration as the rows, the index alignment ShelfView guards then holds by construction
                Facts: [.. _rows.Take(shown).Select(r => FactsFor(r, loadedId, hardware))],
                //the option and the table read the same resume string from here, one home read twice
                Resume: resume is null ? null : ResumeRow(resume),
                //the chips row's own searched bit, separate from the fit toggle above
                Searched: _search is { Length: > 0 },
                //leave Empty null whenever rows are on the shelf. the chip-empty line may not name a or all, neither would lift anything
                Empty: ChipFoundNothing ? ChipEmptyRows()
                    : SearchFoundNothing ? SearchEmptyRows()
                    : shown == 0 ? NothingToShowRows() : null),
            //keep the typed word in the door where the search found nothing, a populated shelf shows no draft
            Draft: SearchFoundNothing ? _search : null,
            //the outage screen says it offers the switch, the body's m row reads the same predicate
            SwitchesSource: _rows.Count == 0 && !HasLadder && _searchCause is HubSearchCause.HubFailed
                && OffersTheLocalShelf),
            SearchKey);
    }

    //rescan on arrival, a download may have finished elsewhere since the first sweep. an old scan would deny the user their own file
    private WizardScreen ToLocalShelf()
    {
        //no source field here, the shelf view has it and a second field could disagree with the screen
        Sweep(null); //the Hub has no typed folder
        //pass invitation because pressing m asks about this machine, an empty answer gets its own screen
        return DiscoveredScreen(heading: null, invitation: true);
    }

    //the block that replaces the table when a chip hides every row, keep it in the view because body rows render above the shelf
    private ShelfView LocalView(IReadOnlyList<FoundModel> shown, IReadOnlyList<string>? empty = null)
    {
        var hw = probes.Hardware() is { } probe
            ? Gatto.Core.Hardware.HardwareClassifier.Classify(probe) : null;

        //read once here, the answer comes from probing the running server
        var loadedId = probes.LoadedModelId();
        //read the loaded path too, a profile can name several files and a scan finds them all
        var loadedPath = probes.LoadedModelPath();

        return new ShelfView(
            //the badge register is keyed by file name, so a local row asks by the name on disk
            [.. shown.Select(m => hw is null
                ? LocalShelf.Unpriced(m, probes.BadgeForFile(System.IO.Path.GetFileName(m.Path)))
                : LocalShelf.Row(m, hw, badge: probes.BadgeForFile(System.IO.Path.GetFileName(m.Path))))],
            CuratedPublisher: null, Shape,
            Families: Families.Load().Ladder, Family: _localFamily ?? "all",
            Total: _discovered.Count,
            //resolve a local row through ExistingModelFor and reuse the answer, the mark and the id are two readings of one lookup
            Facts: [.. shown.Select(m =>
            {
                var id = probes.ExistingModelFor(m.Path);
                return LocalShelf.FactsFor(m, HaveFor(id, loadedId, m.Path, loadedPath), id);
            })],
            Source: ShelfSource.Local,
            Folder: _scanTyped,
            HiddenByFamily: _discovered.Count - shown.Count,
            Empty: empty);
    }

    //reuse the shared family clause. no fit branch here, the local shelf never hides rows for price
    private IReadOnlyList<string> LocalChipEmptyRows(int hiddenByFamily) =>
    [
        Gatto.Repl.Cats.EmptyOf(Glyphs),
        "",
        FamilyEmptySentence(_localFamily, heldClause: null, pricedOut: false, hiddenByFamily),
    ];

    //read every field off the row, hardware passed in once per screen, a null structure renders an empty cell and never a guess
    private Tui.ModelFacts FactsFor(ShelfRow r, string? loadedId,
        Gatto.Core.Hardware.HardwareClass? hw)
    {
        //ask the repo first, the file name only when there is no repo. carry the id too, the pane names it and a second ask could drift
        var id = probes.ModelForHubRow(r.RepoId, r.PickedQuant.FileName);
        return new(Structure: r.Structure, Experts: r.Experts,
            //a Hub row names a repo rather than a file here, so the facts carry no path
            Have: HaveFor(id, loadedId), HaveId: id,
            //quants come from the tree call the search already made, fit uses FitOf like Pick, so pane and row cannot disagree
            FileCount: r.FileCount > 0 ? r.FileCount : null,
            Files: QuantsOf(r, hw));
    }

    //price every quant the repo listed, null when the tree gave none so the pane says nothing rather than claiming there are none
    internal static IReadOnlyList<Tui.PaneFile>? QuantsOf(ShelfRow r,
        Gatto.Core.Hardware.HardwareClass? hw)
    {
        if (r.AllQuants is not { Count: > 0 } quants || hw is not { } machine) return null;

        //keep the repo's own order, the renderer sorts. drop files with no readable quant, an unnamed entry cannot be picked
        return [.. HubSearch.Candidates(quants, r.Params)
            .Select(q => (Token: Gatto.Core.Acquire.QuantToken.Of(q.FileName), q.Bytes, q.StreamedBytes))
            .Where(q => q.Token is { Length: > 0 })
            .Select(q => new Tui.PaneFile(
                q.Token!,
                q.Bytes,
                HubSearch.FitOf(q.Bytes, r.NativeCtx, machine, Tui.Pane.KvContext, r.Arch, q.StreamedBytes)))];
    }

    //apply the pick once, by replacing the row, matched by label because pane and repo order differ. an unresolvable pick leaves the old quant standing
    private void ApplyPickedQuant(string key, string quant)
    {
        if (!int.TryParse(key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var at)
            || at < 0 || at >= _rows.Count) return;

        if (_rows[at].AllQuants is not { Count: > 0 } quants) return;
        if (quants.FirstOrDefault(q =>
                string.Equals(QuantToken.Of(q.FileName), quant, StringComparison.OrdinalIgnoreCase))
            is not { } chosen) return;

        _rows = [.. _rows.Select((r, i) => i == at ? r with { PickedQuant = chosen } : r)];
    }

    //loaded is a claim about a file, so compare paths when both are known. with no path to compare, a matching id still reads loaded
    private static HaveMark HaveFor(string? modelId, string? loadedId,
        string? path = null, string? loadedPath = null)
    {
        if (modelId is not { Length: > 0 }) return HaveMark.None;
        if (!string.Equals(modelId, loadedId, StringComparison.OrdinalIgnoreCase)) return HaveMark.Added; //a loaded model is also added, loaded wins (it changes what Enter does)
        if (loadedPath is not { Length: > 0 } || path is not { Length: > 0 }) return HaveMark.Loaded;
        return SamePath(path, loadedPath) ? HaveMark.Loaded : HaveMark.OtherFile;
    }

    //compare full paths case-insensitively, a malformed path answers not the same rather than throwing into a render
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    //true when a sort's own search found nothing and the rows on screen are the old ones
    private bool _carriedOver;

    //leave the root rows raw, the face is the sanitize seam, sanitizing here would be a second home for that rule
    private IReadOnlyList<WizardRow> LookedInRows()
    {
        if (_scanRoots.Count == 0) return [];
        return [.. new WizardRow[] { "", "gatto looked in:" }, .. RootRows()];
    }

    //put the indent on the row rather than in the text, a wrapped path reads as two paths
    private IReadOnlyList<WizardRow> RootRows() =>
        [.. _scanRoots.Select(root => new WizardRow(root, Indent: 2))];

    //the words are the user's, quoted and not paraphrased, quoting is what makes the sentence checkable by the person who typed it
    private string SearchEmptySentence() => $"nothing on Hugging Face answers \"{_search}\"";

    //a chip emptied the shelf, which is not the search finding nothing. only true when a count backs the emptiness
    private bool ChipFoundNothing =>
        _rows.Count == 0 && _family is { Length: > 0 } && (_hiddenByFit > 0 || _hiddenByFamily > 0);

    //latched once the walk has stood on a shelf, not derived from the search and family, because the all chip clears both
    private bool HasLadder => _stoodOnAShelf && _searchCause is not HubSearchCause.HubFailed;

    //never cleared by a later empty search, having stood on a shelf is not undone by narrowing it to nothing
    private bool _stoodOnAShelf;

    //an unreachable Hub is excluded, the search never ran so there is no honest sentence about the word
    private bool SearchFoundNothing =>
        _rows.Count == 0 && _search is { Length: > 0 }
        && _searchCause is not HubSearchCause.HubFailed;

    //this block renders inside the shelf so the chips row stays above the cat. reuse Shelf.HiddenClause, a second spelling would drift
    private IReadOnlyList<string> ChipEmptyRows() =>
    [
        Gatto.Repl.Cats.EmptyOf(Glyphs),
        "",
        ChipSentence(),
    ];

    //the fit case wins when both numbers are set, it means this family has rows and every one was too big
    private string ChipSentence() =>
        FamilyEmptySentence(_family, HeldClause, PricedOut, _hiddenByFamily);

    //the fit clause wins, it is the one sentence the engine actually measured. the local shelf passes no fit clause, nothing there is held for price
    private string FamilyEmptySentence(string? family, string? heldClause, bool pricedOut, int hiddenByFamily)
    {
        if (heldClause is { } held)
            return pricedOut
                ? $"No {family} model fits this machine's memory {Glyphs.Dot} {held}"
                : $"No {family} model on this shelf {Glyphs.Dot} {held}";
        if (Gatto.Cli.Setup.Tui.Shelf.FamilyClause(hiddenByFamily) is { } elsewhere)
            return $"No {family} model on this shelf {Glyphs.Dot} {elsewhere}";
        return $"No {family} model on this shelf";
    }

    //what the a key brings back, or null when it brings back nothing. named once so sentences cannot ask the question differently
    private string? HeldClause =>
        Gatto.Cli.Setup.Tui.Shelf.HiddenClause(_hiddenNewer, _hiddenOlder, _hiddenByFit, Glyphs);

    //separate the pricing question from HeldClause, rows held for being newer were never priced
    private bool PricedOut => _hiddenByFit > 0;

    //this emptiness has no count behind it, so the sentence must not name a or all, neither lifts anything here
    private IReadOnlyList<string> NothingToShowRows() =>
    [
        "",
        Gatto.Repl.Cats.EmptyOf(Glyphs),
        "",
        "nothing to show on this shelf right now",
        $"another family might {Glyphs.Dot} d to search by name",
        "",
        "",
    ];

    //its blank rows lead and end the block so the door stays put when a search comes back empty
    private IReadOnlyList<string> SearchEmptyRows() =>
    [
        "",
        Gatto.Repl.Cats.EmptyOf(Glyphs),
        "",
        SearchSentence(),
        //the ways forward are things the door does, which is why it keeps the focus and the typed word
        $"another word might {Glyphs.Dot} or paste a full repo id, gatto takes those too",
        "",
        "",
    ];

    //a memory claim needs PricedOut, rows held for being newer were never priced, so name what is held and a
    private string SearchSentence() =>
        HeldClause is { } held
            ? PricedOut
                ? $"nothing answering \"{_search}\" fits this machine's memory {Glyphs.Dot} {held}"
                : $"{SearchEmptySentence()} {Glyphs.Dot} {held}"
            : SearchEmptySentence();

    //one heading per cause the search evidenced, don't say small machine when the Hub is down
    private static string EmptyHeading(HubSearchCause? cause) => cause switch
    {
        //keep shelf out of these sentences, it is our word, a register a user never saw
        HubSearchCause.NothingFits => "None of these models fit this machine",
        HubSearchCause.HubFailed => "Couldn't reach the model list",
        _ => "No models to show right now",
    };

    //all branches end with the ways forward, an empty screen with no next step is where a wizard loses someone
    private IReadOnlyList<WizardRow> EmptyRows(HubSearchCause? cause) => cause switch
    {
        //hardware line first, a user who reads the numbers sees arithmetic about their machine rather than an opinion about the models
        HubSearchCause.NothingFits =>
        [
            HardwareLine(probes.Hardware()),
            "Every model gatto looked at needs more than that.",
            NextWays,
        ],
        //say the machine is fine, otherwise people go check their wifi for nothing
        HubSearchCause.HubFailed =>
        [
            "gatto couldn't reach Hugging Face, so there is no list to show. Nothing is wrong with this machine.",
            //offer the m row only when a local shelf stands behind it and only on faces that bind m
            .. OffersTheLocalShelf
                ? new[] { DiscoveryRow(Announceable, _addRoad) with
                    { Text = $"The {Announceable} {(Announceable == 1 ? "model on this machine is" : "models on this machine are")} one m away" } }
                : [],
            NextWays,
        ],
        //the unevidenced case, wording true whichever thing happened
        _ =>
        [
            HardwareLine(probes.Hardware()),
            "That can mean nothing here fits this machine, or that the model list is out of reach.",
            NextWays,
        ],
    };

    //list the sweep's own roots, a re-derived list would describe a sweep nobody made
    private IReadOnlyList<string> EmptyLocalRows() =>
    [
        Gatto.Repl.Cats.EmptyOf(Glyphs),
        "",
        //a typed folder can be empty on a machine that has models
        _scanTyped is null
            ? "no models on this machine yet, gatto looked in:"
            : "no models in that folder, gatto looked in:",
        .. _scanRoots,
        "",
        "type a folder path below, gatto looks there and in its subfolders",
        //the one key besides the door, with the footer's verb, back only where there is a step to go back to
        CanGoBack ? "press Esc to go back" : "press Esc to leave",
    ];

    //the count is the sweep's own, so the number and the shelf behind m cannot disagree. the two roads' wording differs on purpose
    private static WizardRow DiscoveryRow(int found, bool inSession) =>
        new(inSession
                ? $"gatto found {found} model{(found == 1 ? "" : "s")} on this machine not yet on its list, m shows them"
                : $"gatto also found {found} model{(found == 1 ? "" : "s")} already on this machine, m shows them",
            Tone: RowTone.Aside, Keys: ["m"]);

    //no sweep means nothing to announce, no source switch means naming a key this face never binds
    private IReadOnlyList<WizardRow> DiscoveryRows() =>
        OffersTheLocalShelf ? [DiscoveryRow(Announceable, _addRoad)] : [];

    //whether m has a local shelf with something on it, every m row reads this one predicate
    private bool OffersTheLocalShelf => CanSwitchSource && Announceable > 0;

    //in a session the sentence counts models not yet on its list. on the setup road all count, there is no list yet
    private int Announceable => _discovered.Count(NotOnTheList);

    //reuse ExistingModelFor rather than asking the same question a second way
    private bool NotOnTheList(FoundModel m) =>
        !_addRoad || probes.ExistingModelFor(m.Path) is not { Length: > 0 };

    //state what happened and blame nothing. it names no m, the footer already advertises the way back
    internal const string OutOfReach = "Hugging Face is out of reach, showing the models on this machine.";

    //one home for the ways out of an empty shelf, three branches offering them separately would drift
    private string NextWays => _curatedPublisher is { Length: > 0 }
        ? "You can widen the list, point gatto at a model you already have, or name one directly."
        : "You can point gatto at a model you already have, or name one directly.";

    //verified-first is in the cycle but never the default (pressing s is the explicit choice)
    private static SearchOrder NextAxis(SearchOrder current) => current switch
    {
        SearchOrder.MostDownloaded => SearchOrder.RecentlyUpdated,
        SearchOrder.RecentlyUpdated => SearchOrder.MostParams,
        SearchOrder.MostParams => SearchOrder.VerifiedFirst, //no size axis, a size is known only after the tree call
        _ => SearchOrder.MostDownloaded,
    };

    private WizardScreen AnswerSearch(string key)
    {
        //resuming goes to consent rather than straight to the transfer, the user agrees to the rest of the download. the fetch reads its own .part file
        if (key == ResumePausedFetch && probes.PausedFetches() is [{ } paused, ..]
            && probes.ResumeOffer(paused) is { } resumed)
            return ModelConsent(resumed);

        //widening changes only the view and reruns the same search, there is no second path to drift
        if (key == Broaden)
        {
            _view = HubSearchView.Broadened;
            return Search();
        }

        //each control changes what the question is and re-asks it, none answers it, so every one returns Search()
        if (key == CtlPublisher) return PublisherPicker();

        //re-search rather than filter what is on screen, the publisher is part of the query and decides which orgs get a tree call
        if (key.StartsWith(PickPublisher, StringComparison.Ordinal))
        {
            if (key == PickEveryPublisher)
            {
                _publisher = null;
                _view = HubSearchView.Broadened;
            }
            else
            {
                _publisher = key[PickPublisher.Length..];
                _view = HubSearchView.Curated;
            }

            return Search();
        }

        if (key == CtlSort)
        {
            //s alone passes the rows in hand. f searches elsewhere and an empty result is a true answer, a can only add rows
            var onScreen = _rows;
            _axis = NextAxis(_axis ?? HubSearch.OrderFor(_view));
            return Search(keepIfEmpty: onScreen);
        }

        //m is a toggle, so the flow reads its own state rather than the answer
        if (key == CtlSource) return ToLocalShelf();

        //store all as null rather than a sixth family, a literal all would miss the families table and end up null by accident
        if (key.StartsWith(CtlFamily, StringComparison.Ordinal))
        {
            var chip = key[CtlFamily.Length..];
            _family = chip is "all" or "" ? null : chip;
            //the chip clears the typed search, the engine ignores family while a search is in force, without the clear the key does nothing
            _search = null;
            return Search();
        }

        if (key == CtlLift)
        {
            //a toggle of exactly two states, calling it a cycle would promise a third
            _lift = !_lift;
            return Search();
        }

        if (key == TypeAnId || key == CtlSearch)
            return Emit(new WizardScreen.Ask(
                TypedIdKey,
                "What is it called on Hugging Face?",
                Validate: t => t.Trim().Length == 0 ? "Type a name like org/model" : null,
                Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

        if (key == Elsewhere)
            return Emit(new WizardScreen.Ask(
                ScanPathKey,
                "Which folder should gatto look in?",
                Validate: t => Gatto.Roles.LlamaAssetSteering.NormalizePath(t).Length == 0 ? "Type a folder path" : null,
                Placeholder: @"Type or paste the folder, e.g. C:\models"),
                ScanPathKey);

        //read the typed text before the Numbered tail, Numbered throws on anything that is not a row index
        if (ShelfControls.Untyped(key) is { } typed) return AnswerTypedText(typed);

        return PickedFromHub(Numbered(_rows, key, SearchKey));
    }

    //remember the term before the walk, the empty screen quotes it and the shelf's controls rerun the search
    private WizardScreen SearchFor(string text)
    {
        _search = text.Length == 0 ? null : text;
        //drop the family when a search is typed, the engine did it silently and the chip stayed accented. keep the engine's guard anyway
        if (_search is not null) _family = null;
        return Search();
    }

    //one classifier for both doors, so the two typing screens share one grammar
    private WizardScreen AnswerTypedText(string typed)
    {
        if (Gatto.Cli.Setup.Tui.TypedDoor.Classify(typed) == Gatto.Cli.Setup.Tui.DoorInput.Path)
            return DiscoverTyped(Gatto.Roles.LlamaAssetSteering.NormalizePath(typed));

        return AnswerTypedId(typed);
    }

    //three outcomes, three sentences, a typo is not an outage
    private WizardScreen AnswerTypedId(string typed)
    {
        //classify what was typed before anything acts, search is the default, most of what gets typed is a search
        if (!HubUrl.TryParse(typed, out var reference)) return SearchFor(typed.Trim());

        if (reference is HubRef.Collection)
            //a collection is many models and gatto can't pick one, the sentence says what the link is
            return Emit(new WizardScreen.Ask(
                TypedIdKey,
                "That's a collection, which is many models, open one of them and paste that instead",
                Validate: t => t.Trim().Length == 0 ? "Type a name like org/model" : null,
                Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

        //use the parsed id, a pasted link resolves the repo it names
        var typedId = ((HubRef.Model)reference).RepoId;

        switch (probes.EvaluateTypedId(typedId))
        {
            case TypedIdOutcome.Ok ok:
                return PickedFromHub(ok.Row);

            case TypedIdOutcome.Malformed:
                //the refusal names the shape of a valid name
                return Emit(new WizardScreen.Ask(
                    TypedIdKey,
                    "That doesn't look like a Hugging Face name, they look like org/model, e.g. bartowski/Qwen3-8B-GGUF",
                    Validate: t => t.Trim().Length == 0 ? "Type a name like org/model" : null,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            //say it as a fact about the files, a verdict on the model is not allowed here
            case TypedIdOutcome.NoUsableQuant:
                return Emit(new WizardScreen.Ask(
                    TypedIdKey,
                    "Found it, but none of its files fit this machine, try a smaller model",
                    Validate: t => t.Trim().Length == 0 ? "Type a name like org/model" : null,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            //say what the repo is, don't blame what the user typed
            case TypedIdOutcome.NoWeights:
                return Emit(new WizardScreen.Ask(
                    TypedIdKey,
                    "Found it, but that repo publishes no weights, it holds no model to run",
                    Validate: t => t.Trim().Length == 0 ? "Type a name like org/model" : null,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            case TypedIdOutcome.Unreachable u:
                return Emit(new WizardScreen.Ask(
                    TypedIdKey,
                    u.Gated
                        ? "That one needs you to accept its licence on Hugging Face first"
                        //say "or private", the Hub answers 401 whether a repo is absent or it is private
                        : "Couldn't find that one, check the name, or it may be private",
                    Validate: t => t.Trim().Length == 0 ? "Type a name like org/model" : null,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            default:
                throw new InvalidOperationException("unreachable, TypedIdOutcome is closed");
        }
    }

    //after the pick: the download, and the watch

    //every pick ends on a screen that waits, an Info with nothing after it ends the run
    private WizardScreen PickedFromHub(ShelfRow row)
    {
        Picked = row;
        //a fresh pick starts a new watch, nothing explained yet
        _watchMark = "";
        //an offer exists only when every file has a published fingerprint, the same rule the fetch refuses on. the screen cannot offer what the fetch would decline
        if (probes.ModelOffer(row) is { } offer) return ModelConsent(offer);
        _watchCause = WatchCause.NoFingerprint;
        return DownloadScreen(row);
    }

    //the one road onto the browser watch, the cause is set on entry so no call site can forget it
    private WizardScreen Watch(WatchCause why)
    {
        _watchCause = why;
        return DownloadScreen(Picked!);
    }

    //the carried cause decides which sentence renders, each arm says only what it can claim
    private WizardScreen DownloadScreen(ShelfRow row)
    {
        var files = WatchFiles(row);
        //use the larger of the two counts, a shard set may report one member against thirteen shards, undercounting leaves a partial model
        var count = Math.Max(files.Count, row.PickedQuant.ShardCount);
        var rows = new List<WizardRow>
        {
            new(WatchCauseSentence(count, files.Count, row.PickedQuant.Bytes), RowTone.Aside),
            "",
            ModelFacts.Row("download", FileWord(files[0]), lift: files[0].Name),
        };

        //say and rather than with, every file here is the user's to fetch
        foreach (var extra in files.Skip(1))
            rows.Add(ModelFacts.Row("and", FileWord(extra), lift: extra.Name));

        //many files get the repo's file list, a single file the direct link. branch on the count, don't re-derive from names
        var url = count > 1
            ? Gatto.Core.Acquire.HubUrl.Tree(row.RepoId)
            : Gatto.Core.Acquire.HubUrl.Download(row.RepoId, row.PickedQuant.RepoPath);
        rows.Add(ModelFacts.Row("from", Trimmed(url))); //the link is the instruction, a bare file name sends people to a search engine
        rows.Add("");

        //the partial sentence replaces the standing one. a screen that just said one of two files is here owes an explanation first
        var here = files.Count(f => Arrived(f.Name));
        rows.Add(here > 0 && here < count
            ? new WizardRow(PartialSentence(files, here, count), RowTone.Aside)
            : "gatto is watching for it, you don't need to press anything.");

        //say this only where the fingerprint cannot be checked, elsewhere the arrival line already tells it
        if (_watchCause == WatchCause.NoFingerprint)
            rows.Add(new WizardRow("It will check the name and the size when a file lands, but "
                + "without a published fingerprint it can't check the bytes.", RowTone.Aside));

        rows.Add("");

        //name where it is looking from the scan's own answer, say nothing yet only while nothing has arrived
        rows.Add(new WizardRow("It is watching:",
            Tail: here > 0 ? "checking every few seconds" : "checking every few seconds, nothing yet"));
        rows.AddRange(RootRows());

        //the blank belongs to the sentence below it, the frame adds its own before the option list. two would open a gap nobody drew
        if (here == 0) rows.Add("");

        //say gatto will find the file, don't say resume, it re-walks from the start. once a file is here the sentence describes a wait that is gone
        if (here == 0)
            rows.Add(new("This can take hours on a big model. You can leave with Esc and run gatto "
                + "setup again later, gatto will find the file.", Highlight: ["gatto setup"]));

        return Emit(new WizardScreen.Choice(
            DownloadKey, ModelTitle, WatchOptions(row), BodyRows: rows, Watching: true,
            Door: $"type the path to the downloaded file or folder{Glyphs.Ellipsis}",
            ArrivedLabel: $"{Glyphs.Ok} {row.PickedQuant.FileName} arrived"), DownloadKey);
    }


    //a file already here drops its size. mark nothing until one has arrived, a list of misses reads as failure
    private string FileWord(ModelFetchFile file) =>
        !AnyArrived() ? file.Name + $" {Glyphs.Dot} " + file.Size
        : Arrived(file.Name) ? $"{Glyphs.Ok} {file.Name}, here"
        : $"{Glyphs.Bad} {file.Name} {Glyphs.Dot} {file.Size} {Glyphs.Dot} still missing";

    //read off _discovered, the poll refreshes it, so the marks cannot disagree with the disk the watch just looked at
    private bool Arrived(string fileName) =>
        _discovered.Any(m => string.Equals(
            System.IO.Path.GetFileName(m.Path), fileName, StringComparison.OrdinalIgnoreCase));

    private bool AnyArrived() =>
        Picked is { } row && WatchFiles(row).Any(f => Arrived(f.Name));

    //a missing encoder names its consequence, the model can't see. a shard set gets the general sentence, it has no such consequence to name
    private string PartialSentence(IReadOnlyList<ModelFetchFile> files, int here, int total)
    {
        var missingEncoder = files.Any(f => f.Encoder && !Arrived(f.Name));
        var count = total == 2 && here == 1
            ? "One of the two files is here."
            : $"{here} of the {total} files are here.";
        return missingEncoder
            ? count + " The model can't see images without its encoder, the link above has both; "
                + "gatto keeps watching."
            : count + " The link above has all of them; gatto keeps watching.";
    }


    //the watch waits for every file, not just the name that matched. a declined encoder is not missing
    private bool UnitHere(ShelfRow row) =>
        WatchFiles(row).Where(f => f.Encoder).All(f => _declinedEncoder || Arrived(f.Name));

    //offer it only where the missing files are encoders and something has already arrived. a set without shards cannot load
    private bool CanGoTextOnly(ShelfRow row)
    {
        var files = WatchFiles(row);
        if (!files.Any(Arrived2)) return false;
        //every file still missing must be an encoder, without its shards the model will not load
        return files.Where(f => !Arrived(f.Name)).All(f => f.Encoder)
            && files.Any(f => f.Encoder && !Arrived(f.Name));

        bool Arrived2(ModelFetchFile f) => Arrived(f.Name);
    }

    //the files the user must fetch, worded with the same composer as consent, the two screens cannot describe one unit differently
    private static IReadOnlyList<ModelFetchFile> WatchFiles(ShelfRow row)
    {
        var files = new List<ModelFetchFile>();
        foreach (var m in row.PickedQuant.Members)
            files.Add(new(m.FileName, SizeWords.Auto(m.Bytes)));
        if (Gatto.Core.Acquire.ProjectorPick.Best(row.Projectors) is { } encoder)
            foreach (var m in encoder.Members)
                files.Add(new(m.FileName, SizeWords.Auto(m.Bytes), Encoder: true));
        return files;
    }

    //why the download is theirs, in the state that is actually true. the tail is shared, only the reason differs
    private string WatchCauseSentence(int fileCount, int listed, long totalBytes)
    {
        var why = _watchCause == WatchCause.Unreachable
            ? "gatto couldn't reach Hugging Face, so this time the download is yours, everything "
              + "below is exact."
            : "Hugging Face publishes no fingerprint for these files, so the download is yours, "
              + "everything below is exact.";
        if (fileCount <= 1) return why;
        var how = fileCount == 2 ? "download both" : "download them all";
        //state the total only where the rows cannot say it, a set listed as one row has nowhere else for the size
        var total = fileCount > listed
            ? $" (total {SizeWords.Auto(totalBytes)})"
            : "";
        return why + $" It is {InWords(fileCount)} files{total}: {how} into one folder.";
    }

    //the drawn copy says both only of two, above that say the number
    private static string InWords(int n) => n == 2 ? "two" : n.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    //drop the scheme, the row is a fact to read rather than a link to click
    private static string Trimmed(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) ? url["https://".Length..] : url;

    //the answer that loses nothing comes first, above the one that abandons work already on disk
    private IReadOnlyList<ChoiceOption> WatchOptions(ShelfRow row) =>
        CanGoTextOnly(row)
            ? [new ChoiceOption(UseTextOnly, "Use it text-only"),
               new ChoiceOption(PickAnother, "Pick a different model", Advances: false)]
            : DownloadOptions;

    //watching never traps anyone, the door takes a path saved elsewhere and this option picks another model
    private static IReadOnlyList<ChoiceOption> DownloadOptions =>
    [
        //the standing typed door takes a path in place, don't bring back a row that opens a second screen for it
        new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
    ];

    //the one entry point for the watch, the flow knows the walk and the runner must not branch on a screen key
    public bool PollWatch() =>
        //a paused fetch resolves nothing, this arm must lead the ladder. without it the flow falls to asking the disk about a browser download
        _modelPaused ? false
        : _fetching is { } fetch ? fetch.IsCompleted
        //task fields answer before disk questions, a task is running or it is not, the fields below are told apart by which are set
        : _modelFetch is { } model ? model.IsCompleted
        //the check's watch resolves also when the load ask is due, the next screen decides which happened
        : _audition is { } check ? check.IsCompleted || LoadAskIsDue()
        //these tasks sit above the disk questions, a task is running or it is not
        : _proving is { } proving ? proving.IsCompleted
        : _loading is { } one ? one.IsCompleted
        : Picked is not null ? PollForDownload()
        : PollForPartialSet();

    //called by the face on its own clock, it refreshes _discovered and the answer reads that, one scan cannot disagree with a second
    public bool PollForDownload()
    {
        if (Picked is not { } row) return false;

        var rescan = probes.Scan(null);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;

        //the poll answers has anything changed. a partial set matches while Adopt refuses it, an is-it-there poll would resolve the screen the instant it appeared
        var mark = MarkOf(_discovered.FirstOrDefault(m => Answers(m, row.PickedQuant)));
        if (mark == _watchMark) return false;

        _watchMark = mark;
        return mark.Length > 0;
    }

    //the state the screens render, absent, k of n, or complete, one home because three sites read it
    private static string MarkOf(FoundModel? m) =>
        m is null ? "" : $"{m.Path}|{m.ShardsPresent}|{m.IsComplete}";

    //reset by a new pick, said once is per watch. a carried mark would swallow the first shard of the next one
    private string _watchMark = "";

    //the same watch for a set nobody picked, anchored on the set's own file name since there is no pick to match
    public bool PollForPartialSet()
    {
        if (_watchingSet is not { } waitingFor) return false;

        var rescan = probes.Scan(null);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;

        var mark = MarkOf(_discovered.FirstOrDefault(m => Answers(m, waitingFor)));
        if (mark == _watchMark) return false;

        _watchMark = mark;
        return mark.Length > 0;
    }

    //the set a partial screen is waiting to fill, shaped as the quant it would have been picked as, null elsewhere
    private HubQuant? _watchingSet;

    //the watch driven by the user rather than a clock, an arrival adopts exactly as a discovered model so the two cannot diverge
    private WizardScreen AnswerDownload(string key)
    {
        if (Picked is not { } row) return Search();          //nothing to watch for, not reachable
        //read the _discovered the poll refreshed, a second scan could disagree with the first. a vanished file just returns to the watch
        if (key == Landed)
        {
            if (_discovered.FirstOrDefault(m => Answers(m, row.PickedQuant)) is not { } arrived)
                return DownloadScreen(row);

            var check = Accept(arrived, row.PickedQuant);
            return check.Verdict switch
            {
                //still being written or unreadable both mean not yet, neither is an accusation
                WatchVerdict.StillArriving or WatchVerdict.Unreadable => DownloadScreen(row),
                WatchVerdict.NotThePublishedFile => WrongFile(row, arrived, check),
                //the whole unit must be here, a vision model missing its encoder stays on the watch
                _ when !UnitHere(row) => DownloadScreen(row),
                _ => AdoptFromWatch(arrived),
            };
        }
        if (key == PickAnother) return Search();

        //the user said the smaller unit is the unit, remember it so nothing asks again
        if (key == UseTextOnly)
        {
            _declinedEncoder = true;
            return _discovered.FirstOrDefault(m => Answers(m, row.PickedQuant)) is { } weights
                ? Adopt(weights)
                : DownloadScreen(row);
        }

        //a wrapped answer from the door is the path, normalize it with the shared rule. an empty answer falls through to the rescan and does nothing
        if (ShelfControls.Untyped(key) is { } door
            && Gatto.Roles.LlamaAssetSteering.NormalizePath(door) is { Length: > 0 } typed)
            return AnswerLandedPath(typed);

        var rescan = probes.Scan(null);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;
        var landed = _discovered.FirstOrDefault(m => Answers(m, row.PickedQuant));

        return landed is null ? DownloadScreen(row) : Adopt(landed);
    }

    //the sibling of the common-folders watch, differing only in where it looks. a miss returns to the watch, which lists the folders it searched
    private WizardScreen AnswerLandedPath(string root)
    {
        var scan = probes.Scan(root);
        _discovered = scan.Found;
        _scanRoots = scan.Roots;

        //only reached from the download watch, which returns to search when nothing is picked
        if (Picked is not { } row) return DiscoveredScreen(heading: null);

        if (_discovered.FirstOrDefault(m => Answers(m, row.PickedQuant)) is { } landed) return Adopt(landed);

        return _discovered.Count == 0
            ? DownloadScreen(row)
            : DiscoveredScreen("That file isn't there yet, these are the models gatto can see");
    }

    //the order lives in the pure flow where tests drive it. unreadable answers null, calling a locked file the wrong file would lie
    private WatchCheck Accept(FoundModel found, HubQuant picked)
    {
        //a .part beside it means the writer is not done, keep watching rather than judge a growing file
        if (probes.StillArriving(found.Path)) return new(WatchVerdict.StillArriving);

        //where a fingerprint was published it decides, a file whose bytes differ cannot have the published hash anyway
        if (picked.Sha256 is { Length: > 0 } expected)
        {
            var actual = probes.Sha256Of(found.Path);
            //a file gatto cannot hash is usually one still written without a .part, wait rather than accuse
            if (actual is not { Length: > 0 }) return new(WatchVerdict.Unreadable);

            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? new(WatchVerdict.Verified, actual, expected)
                : new(WatchVerdict.NotThePublishedFile, actual, expected);
        }

        //the size check is for single files only. a set's Bytes is a summed total, comparing it against one shard calls every correct set wrong
        if (picked.ShardCount <= 1 && found.Set is null
            && picked.Bytes > 0 && found.FileBytes > 0 && found.FileBytes != picked.Bytes)
            return new(WatchVerdict.NotThePublishedFile);

        return new(WatchVerdict.NameAndSizeOnly);
    }

    //the watch keeps watching, nothing adopted and nothing deleted. the file is the user's, unlike the fetch's mismatch which deletes what it pulled
    private WizardScreen WrongFile(ShelfRow row, FoundModel found, WatchCheck check)
    {
        //a collection expression because a spread inside an object initializer does not compile
        List<WizardRow> rows =
        [
            new("A file with the right name is here, but it isn't the published one.",
                RowTone.Aside, Glyph: RowGlyph.Bad),
            "",
            //the rows name what was actually compared, the fingerprint where published, otherwise the size. draw no row in a state where its claim cannot be true
            .. check.Expected is { Length: > 0 }
                ? (IReadOnlyList<WizardRow>)
                [
                    ModelFacts.Row("expected", Fingerprint.Short(Glyphs, check.Expected)
                        + $" {Glyphs.Dot} the file's fingerprint on Hugging Face"),
                    ModelFacts.Row("got", Fingerprint.Short(Glyphs, check.Actual)
                        + $" {Glyphs.Dot} the file in " + Folder(found.Path)),
                ]
                :
                [
                    ModelFacts.Row("expected", SizeWords.Auto(row.PickedQuant.Bytes)
                        + $" {Glyphs.Dot} the size Hugging Face lists"),
                    ModelFacts.Row("got", SizeWords.Auto(found.FileBytes)
                        + $" {Glyphs.Dot} the file in " + Folder(found.Path)),
                ],
            "",
            new WizardRow("A broken copy looks like this, downloading it again usually fixes it. "
                + "gatto keeps watching.", RowTone.Aside),
            "",
            new WizardRow("It is watching:", Tail: "checking every few seconds"),
            .. RootRows(),
        ];

        return Emit(new WizardScreen.Choice(
            DownloadKey, ModelTitle, WatchOptions(row), BodyRows: rows, Watching: true,
            Door: $"type the path to the downloaded file or folder{Glyphs.Ellipsis}",
            ArrivedLabel: $"{Glyphs.Ok} {row.PickedQuant.FileName} arrived"), DownloadKey);
    }

    //a set answers the pick by stem and shard count (shards arrive in any order), Adopt still decides it's complete
    private static bool Answers(FoundModel found, HubQuant picked)
    {
        var name = System.IO.Path.GetFileName(found.Path);
        if (string.Equals(name, picked.FileName, StringComparison.OrdinalIgnoreCase)) return true;

        return ShardName.Parse(name) is { IsSet: true } a
            && ShardName.Parse(picked.FileName) is { IsSet: true } b
            && a.Count == b.Count
            && string.Equals(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase);
    }

    //a partial set never adopts, a model missing shards will not load. the k of n count says the download is progressing
    private WizardScreen PartialSet(FoundModel model)
    {
        var set = model.Set!;         //the bang is safe because IsComplete is false only when Set is non-null
        var folder = System.IO.Path.GetDirectoryName(model.Path) ?? "";
        //arm the watch on the set on screen and arm the mark to what the screen shows. otherwise the first poll resolves the screen the instant it appears
        _watchMark = MarkOf(model);
        _watchingSet = Picked is null
            //the match reads names only, the size stays the file's own byte count rather than zeroed
            ? new HubQuant(System.IO.Path.GetFileName(model.Path), model.FileBytes, null, set.Count)
            : null;
        return Emit(new WizardScreen.Choice(
            PartialSetKey,
            $"Still arriving, {model.ShardsPresent} of {set.Count} files so far",
            [new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
            BodyRows:
            [
                new WizardRow($"This model comes as {set.Count} files, and gatto needs all of them "
                    + "in the same folder before it can use any of them."),
                "",
                //leave it raw, the face sanitizes every row it renders, sanitizing here would be the rule's second home
                new WizardRow($"  {folder}"),
                "",
                new WizardRow("gatto is watching for the rest, you don't need to press anything."),
            ], Watching: true), PartialSetKey);
    }

    //a completed set reuses the download answer, so the same matcher that found it half-arrived adopts it. a discovery-only set re-enters Discover
    private WizardScreen AnswerPartialSet(string key)
    {
        if (key == PickAnother) return Search();
        return Picked is not null ? AnswerDownload(Landed) : Discover(extraRoot: null);
    }

    //the audition offer

    //one header for the whole step, two screens spelling it separately is how a step asks two questions
    private const string CheckTitle = "Can it run commands and edit files?";

    //offer after the scaffold's write, the audition starts its server from the written config
    private WizardScreen OfferAudition(string modelId) //never on the connect road (no model to audition)
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
        return Emit(new WizardScreen.Choice(
            AuditionOfferKey,
            CheckTitle,
            [
                new ChoiceOption(Yes, "Yes, check it", Recommended: true),
                //the skip option states what it does, one load and one arrival, no tasks
                new ChoiceOption(Skip, "Skip the tasks, just make sure it answers"),
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
                    [.. Roles.Audition.Battery.V1.Select((t, i) => $"{i + 1}) {t.Label}")]),
            ]),
            AuditionOfferKey);
    }

    //state the cost before the answer, the check stops the answering model's server and the conversation re-reads after
    private WizardScreen InSessionCheckAsk(string holding, string modelId) => Emit(new WizardScreen.Choice(
        InSessionCheckKey,
        $"Check {modelId} now?",
        [
            new ChoiceOption(Yes, "Check it now"),
            //this road has no switch, so the label promises adding unchecked
            new ChoiceOption(AddUnchecked, AddUncheckedLabel, EscVerb: "add it unchecked"),
        ],
        BodyRows:
        [
            //the model is already added by the time this ask runs, the scaffold wrote first
            new WizardRow($"{modelId} is added to gatto."),
            //name servers, that is what the record holds
            new WizardRow($"The check runs it on the server, so {holding}'s server stops and "
                + $"{modelId}'s starts in its place."),
            //one row only, gatto cannot see what a client of the server does
        ]),
        InSessionCheckKey);

    private WizardScreen InSessionEnding()
    {
        //the add road never draws the summary (its first option starts gatto)
        return !_addRoad && _summaryOutcome is { } done ? SummaryScreen(done) : TerminalScreen();
    }

    //don't name a screen option back, Answer intercepts that key before the screen dispatch

    //yes releases the held server, it is the ask's own promise. no must not touch the server, its sentence says nothing changes
    private WizardScreen AnswerInSessionCheck(string key)
    {
        if (_modelId is null || key != Yes) return Done();
        probes.ReleaseHeldServer();
        return StartAudition(_modelId);
    }

    //gatto audition refuses while a model holds the server, so the sentence offers it for after and names the model that must stop first
    private WizardScreen HeldScreen(string holding, string modelId) => Emit(new WizardScreen.Choice(
        AuditionHeldKey,
        CheckTitle,
        [new ChoiceOption(AuditionHeldNext, "next"),
         new ChoiceOption(AuditionHeldLeave, "leave", Advances: false)],
        BodyRows:
        [
            new WizardRow("gatto can't check this model right now.", Glyph: RowGlyph.Warn),
            "",
            CheckFacts.Row("server", $"{holding} is running", lift: holding),
            CheckFacts.Cont($"checking {modelId} would unload it"),
            "",
            //accent the command rather than backtick it, the terminal shows the marks to the reader
            new WizardRow($"The check couldn't run, so gatto can't judge this model yet. Once "
                + $"{holding} is no longer running, type gatto audition {modelId} in a terminal to "
                + "run the same check.",
                RowTone.Aside, Highlight: [$"gatto audition {modelId}"]),
            "",
        ],
        KeysOnly: true), AuditionHeldKey);

    //the only screen on this step that may name a cause, the header arithmetic decides it before any spawn
    private WizardScreen IncompleteScreen(CheckStumble.Incomplete filed) => Emit(new WizardScreen.Choice(
        AuditionIncompleteKey,
        CheckTitle,
        [
            new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
            new ChoiceOption(CheckAgain, "Check it again", Advances: false),
        ],
        BodyRows:
        [
            new WizardRow("The model file is incomplete, gatto didn't try to load it.",
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
            new WizardRow("A download or copy that stopped early looks like this. The check couldn't "
                + "run, so gatto can't judge this model yet, a complete copy of the same file may "
                + "run fine.", RowTone.Aside),
        ]), AuditionIncompleteKey);

    //no cause here, the exhibit is the server's own last lines, one row each and wrapped, llama.cpp puts the reason at the end
    private WizardScreen ExitedScreen(CheckStumble.Exited exited) => Emit(new WizardScreen.Choice(
        AuditionExitedKey, CheckTitle, EngineStumbleOptions,
        BodyRows: EngineStumbleRows(exited, exited.LastLines, exited.LogPath)),
        AuditionExitedKey);

    private WizardScreen StoppedScreen(CheckStumble.Stopped stopped) => Emit(new WizardScreen.Choice(
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

    private IReadOnlyList<WizardRow> EngineStumbleRows(
        CheckStumble stumble, IReadOnlyList<string> lines, string? logPath)
    {
        var stopped = stumble is CheckStumble.Stopped;
        var done = PollCheck() is { Done.Count: > 0 } t ? t.Done : [];

        return
        [
            new WizardRow(stopped
                ? "The server stopped while the check was running, nothing was measured."
                : "The server exited while loading the model, nothing was measured.",
                Glyph: RowGlyph.Bad),
            "",
            .. ModelRow(),
            .. done.Count > 0
                ? (IReadOnlyList<WizardRow>)[CheckFacts.Row("done", string.Join($" {Glyphs.Dot} ", done))]
                : [],
            //an absent log is said rather than dropped, a killed server writes nothing and "no log" is itself a fact
            .. lines.Count > 0
                ? (IReadOnlyList<WizardRow>)
                  [
                      //wide, because a narrower fact block folds the quote elsewhere, a visible diff on the screen whose job is quoting
                      CheckFacts.Row("last lines", lines[0], wide: true),
                      .. lines.Skip(1).Select(l => CheckFacts.Cont(l, wide: true)),
                  ]
                : [CheckFacts.Row("last lines", "none, the server exited without writing anything")],
            .. logPath is { Length: > 0 } log
                ? (IReadOnlyList<WizardRow>)[CheckFacts.Cont($"full log: {log}", wide: true)]
                : [],
            "",
            stopped
                ? new WizardRow("The check couldn't finish, so gatto can't judge this model yet. "
                    + $"Type gatto serve start {_modelId} in a new terminal to see what the "
                    + "server does on its own.",
                    RowTone.Aside, Highlight: [$"gatto serve start {_modelId}"])
                : new WizardRow("The check couldn't run, so gatto can't judge this model yet, the "
                    + "lines above are the server's own account of what happened.", RowTone.Aside),
        ];
    }

    //not a failure, the model just takes this long, so the question is real and the watch keeps running while it asks
    private WizardScreen LoadAsk() => Emit(new WizardScreen.Choice(
        AuditionLoadAskKey,
        CheckTitle,
        [
            new ChoiceOption(KeepWaiting, "Keep waiting", Advances: false),
            new ChoiceOption(StopUnchecked, "Stop the check, use it unchecked"),
            new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
        ],
        BodyRows:
        [
            new WizardRow("The model has been loading for a while and hasn't answered yet.",
                Glyph: RowGlyph.Warn),
            "",
            .. ModelRow(),
            CheckFacts.Row("server", "running, still loading"),
            "",
            new WizardRow("A model that barely fits can take longer than this. gatto won't wait "
                + "forever without asking, keep waiting, or carry on.", RowTone.Aside),
        ],
        Watching: true,
        FullPurr: true), AuditionLoadAskKey);

    //waiting again restarts nothing, same task and same clock. the other two cancel and join, the check must release the machine before the next screen
    private WizardScreen AnswerLoadAsk(string answer)
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
    private IReadOnlyList<WizardRow> ModelRow() =>
        _modelFile is { Length: > 0 } file ? [CheckFacts.Row("model", file, file)] : [];

    //re-entered once the model and llama-server path are on disk, carrying the id the writer produced, null on the connect path
    public WizardScreen ResumeAfterWrites(string? modelId)
    {
        NeedsWritesApplied = false;
        _applied = true;      //from here on, saying nothing was written would be a lie

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
            _ => throw new InvalidOperationException("unreachable, ResumeStage is closed"),
        };
    }

    private WizardScreen AnswerAuditionOffer(string key) =>
        _modelId is null ? Done()
        : key == Yes ? StartAudition(_modelId)
        //the skip is not a decline, gatto still makes sure the model loads and answers, one load and one arrival
        : key == Skip ? StartTheOneLoad()
        : Done();

    //run the load long-running like the check, a pool worker blocked on a cold load for minutes gets replaced on a timer
    private WizardScreen StartTheOneLoad()
    {
        ResetCheck();

        var id = _modelId;
        _loading = Task.Factory.StartNew(
            () => probes.ProveIt(id, new CheckSink(OnCheck)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Waiting();
    }

    //one waiting screen for two roads, the copy differs because Esc abandons different deeds, here only the one reply
    private WizardScreen Waiting() => Emit(new WizardScreen.Choice(
        AuditionWaitingKey,
        CheckTitle,
        [new ChoiceOption(SkipStop, "stop waiting", Advances: false)],
        BodyRows:
        [
            new WizardRow("Nothing to do here, gatto starts the server, loads the model and asks it "
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
    private WizardScreen Answers(Gatto.Core.Acquire.ProveOutcome outcome) => Emit(new WizardScreen.Choice(
        AuditionAnswersKey,
        CheckTitle,
        [new ChoiceOption(AnswersNext, "next"), new ChoiceOption(AnswersLeave, "leave", Advances: false)],
        BodyRows:
        [
            new WizardRow("It loads and answers.", RowTone.Aside, Glyph: RowGlyph.Good,
                Lift: ["It loads and answers."]),
            "",
            .. outcome.TokensPerSecond is { } rate
                ? (IReadOnlyList<WizardRow>)[SpeedRow(rate), ""]
                : [],
            new WizardRow("The five tasks were skipped, so gatto hasn't judged whether it can run "
                + $"commands or edit files, type gatto audition {_modelId} in a new terminal "
                + "whenever you want that check.",
                RowTone.Aside, Highlight: [$"gatto audition {_modelId}"]),
            "",
        ],
        KeysOnly: true,
        PurredSinceWatch: true), AuditionAnswersKey);

    //stop means stop waiting, ProveIt owns one bounded request and takes no token, the load finishes into a result nobody reads
    private WizardScreen AfterTheOneLoad(string answer)
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
    private WizardScreen StartAudition(string modelId)
    {
        _auditionCancel = new CancellationTokenSource();
        var ct = _auditionCancel.Token;
        ResetCheck();
        //one wait, one clock, restarting it on keep waiting would double the time before the second question
        _checkClock ??= System.Diagnostics.Stopwatch.StartNew();

        //read Picked on this thread, the delegate runs on the pool (the repo id tags the badge for the search that offered it)
        var repoId = Picked?.RepoId;

        //ask the machine whether this model's server is up, a road flag is only a proxy. read it before StartNew or it races the task it describes
        _serverAlreadyUp = probes.ServerAlreadyUp(modelId);
        //run it LongRunning, a pool worker blocked for minutes gets replaced on a timer. hand the starter no token, it would mean don't start if cancelled
        _audition = Task.Factory.StartNew(
            () => probes.RunAudition(modelId, repoId, new CheckSink(OnCheck), ct),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Running();
    }

    //the live check screen, finished tasks are named and never ticked, its one option cancels so only the watch advances it
    private WizardScreen Running() => Emit(new WizardScreen.Choice(
        AuditionRunningKey,
        CheckTitle,
        [new ChoiceOption(AuditionStop, "stop the check", Advances: false)],
        BodyRows:
        [
            //this model's server may already be up, don't narrate it as work still to come
            new WizardRow(_serverAlreadyUp
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
    private WizardScreen AfterAudition(string answer)
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
            return Emit(new WizardScreen.Choice(
                AuditionFailedKey,
                unavailable is null
                    ? "Couldn't run the check just now"
                    : "Couldn't run the check: the server would not start.",
                [new ChoiceOption(Anyway, "Carry on")],
                //accent a command, don't backtick it (the terminal doesn't render backticks, the user reads the punctuation)
                BodyRows:
                [
                    .. unavailable is { } u
                        ? (IReadOnlyList<WizardRow>)[CheckFacts.Cont(u.Reason), ""]
                        : [],
                    //drop the gatto audition pointer on the server-stopped arm, the command refuses while another model holds the server
                    new WizardRow(unavailable is null
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

        return Emit(new WizardScreen.Choice(
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
    private WizardScreen PassScreen(AuditionCheck check, AuditionFacts f) => Emit(new WizardScreen.Choice(
        AuditionPassedKey,
        CheckTitle,
        [new ChoiceOption(AuditionPassedNext, "next"),
         new ChoiceOption(AuditionPassedLeave, "leave", Advances: false)],
        BodyRows:
        [
            new WizardRow(PassHeadline(f, Glyphs), RowTone.Aside, Glyph: RowGlyph.Good, Lift: [PassSentence]),
            "",
            //name the tasks that didn't pass from the runner's own records, the tally can't say which. a cut task didn't run, a failed one gave the wrong result
            .. check.Tasks is { } ran && ran.Any(t => !t.Pass)
                ? (IReadOnlyList<WizardRow>)[.. TaskRows([.. ran.Where(t => !t.Pass)]), ""]
                : [],
            .. StampRows(f),
            .. check.Recorded is { } on
                ? (IReadOnlyList<WizardRow>)["", CheckFacts.Row("recorded",
                    $"the shelf shows it as tool-calling verified {Glyphs.Dot} {on:yyyy-MM-dd}")]
                : [],
            //trailing blank, a KeysOnly screen has no option list, the last fact would sit on the footer rule
            "",
        ],
        KeysOnly: true,
        PurredSinceWatch: true), AuditionPassedKey);

    //only next carries on, every other key leaves (the footer promises which deed happened)
    private WizardScreen AnswerAuditionPassed(string key) =>
        key == AuditionPassedNext ? Done() : Leave();

    //one row per task, the words are TaskWords' (the CLI report draws the same sentences). the mark is semantic, a skipped task is not a failed one
    private static IReadOnlyList<WizardRow> TaskRows(IReadOnlyList<Gatto.Roles.Audition.AuditionTaskResult> tasks) =>
    [
        .. tasks.Select(t =>
        {
            var mark = t.Skipped ? RowGlyph.NotRun : t.Pass ? RowGlyph.Good : RowGlyph.Bad;
            var why = Gatto.Roles.Audition.TaskWords.Why(t);
            return new WizardRow(
                why is { Length: > 0 } said ? $"{t.Label}, {said}" : t.Label,
                //fact rows don't wrap (at 80 the longest is 79 cells, and a wrapped row breaks the mark column)
                RowTone.Aside, Indent: 2, Glyph: mark, Lift: [t.Label], Wide: true);
        }),
    ];

    //failure body: the stamp, then the mitigation. the blank row exists only when the stamp does
    private static IReadOnlyList<WizardRow> FailRows(AuditionCheck check, Gatto.Terminal.GlyphSet g)
    {
        var facts = check.Facts;
        var rows = new List<WizardRow>
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
    private WizardScreen AnswerAuditionFailed(string key) =>
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

    private WizardScreen AnswerDiscovered(string key)
    {
        //re-search the Hub rather than restoring a snapshot, a stale shelf prices against state nobody re-read
        if (key == CtlSource) return Search();

        //same chip keys for both shelves, this one writes the local field (a Hub press says nothing about this disk)
        if (key.StartsWith(CtlFamily, StringComparison.Ordinal))
        {
            var chip = key[CtlFamily.Length..];
            _localFamily = chip is "all" or "" ? null : chip;
            return DiscoveredScreen(heading: null);
        }

        if (key == Elsewhere)
            return Emit(new WizardScreen.Ask(
                ScanPathKey,
                "Which folder should gatto look in?",
                Validate: t => Gatto.Roles.LlamaAssetSteering.NormalizePath(t).Length == 0 ? "Type a folder path" : null,
                Placeholder: @"Type or paste the folder, e.g. C:\models"),
                ScanPathKey);

        //the footer draws a here, so answer it (unhandled it reaches Numbered and throws, and the toggle is shared with the Hub shelf)
        if (key == CtlLift)
        {
            _lift = !_lift;
            return DiscoveredScreen(heading: null);
        }

        if (key == SearchInstead) return Search();

        //the typed answer is a folder, read it through Untyped (a numeral typed in the door and a row index are the same string)
        if (ShelfControls.Untyped(key) is { } typedAnswer
            && Gatto.Roles.LlamaAssetSteering.NormalizePath(typedAnswer) is { Length: > 0 } typedRoot)
            return DiscoverTyped(typedRoot);

        return Adopt(Numbered(_discovered, key, DiscoveredKey));
    }

    //the one completion point for a chosen model. a found file stays put, a wizard pick must not move 30 GB
    private WizardScreen Adopt(FoundModel model)
    {
        //check completeness here and nowhere else, every route inherits the refusal. do it before the choice is announced
        if (!model.IsComplete) return PartialSet(model);

        Selected = model;

        //a file that already belongs to a model reuses its id and skips the scaffold, that's what makes a re-run idempotent
        if (probes.ExistingModelFor(model.Path) is { Length: > 0 } existing)
        {
            _modelId = existing;
            //tell them an existing model was found, it's why the next screen is not the one they expected
            _narrate.Add(new WizardScreen.Info("model.reused",
                //in /model add the user is already inside gatto, the row says added there
                [new WizardRow(_addRoad
                    ? $"{Glyphs.Ok} Already added as {existing}, using that model"
                    : $"{Glyphs.Ok} Already set up as {existing}, using that model")]));
            return Done();
        }

        //a different file holding the same id is a question only the user can answer, don't merge it with the reuse above
        var (clash, clashId) = probes.ClashFor(model.Path, Picked?.RepoId);
        if (clashId is { Length: > 0 } held)
        {
            _clashingModelId = held;
            //a second quant joins its model in silence, no screen for a question with one real answer
            if (clash == Gatto.Roles.IdClash.SameModel) return AddFileIntent(model, held);

            //the add option shows only when gatto can't tell, you can't add to a model that won't load
            return ModelCollision(held, canAdd: clash == Gatto.Roles.IdClash.CannotTell);
        }

        return OfferMove(model, replace: false);
    }

    //the arrival version of the move offer, same key and handler. move is first here, the screen just argued for it
    private WizardScreen WatchArrived(FoundModel model, MoveOffer offer)
    {
        _moveOffer = offer;
        var name = System.IO.Path.GetFileName(model.Path);
        var verified = _watchCause == WatchCause.Unreachable
            ? $"{name} arrived, byte for byte the file Hugging Face publishes."
            //when there's no hash, name what was checked instead (one headline row in two states)
            : $"{name} arrived, name and size matching what Hugging Face lists.";

        List<WizardRow> rows =
        [
            new(verified, RowTone.Aside, Glyph: RowGlyph.Good, Lift: [name]),
            "",
            ModelFacts.Row("found", model.Path),
        ];

        //name the encoder that arrived too, two files fetched and one reported would under-claim
        if (Picked is { } row)
            foreach (var extra in WatchFiles(row).Where(f => f.Encoder && Arrived(f.Name)))
                rows.Add(ModelFacts.Row("with", extra.Name + (_watchCause == WatchCause.Unreachable
                    ? $" {Glyphs.Dot} verified"
                    //reuse the headline's words here, the row and the sentence above must not drift
                    : $" {Glyphs.Dot} name and size match"), lift: extra.Name));

        rows.Add("");
        rows.Add(new WizardRow("Downloads is where files land, not where models live, a cleared "
            + "Downloads folder would take the model with it.", RowTone.Aside));

        return Emit(new WizardScreen.Choice(
            MoveKey,
            ModelTitle,
            [new ChoiceOption(Yes, $"Move files into {offer.SuggestedDir}"),
             new ChoiceOption(No, "Leave them in Downloads")],
            BodyRows: rows,
            PurredSinceWatch: true), MoveKey);
    }

    //leaving it is first and the default (a multi-GB copy is a choice), another drive says copy with the size up front
    private WizardScreen OfferMove(FoundModel model, bool replace)
    {
        _moveReplace = replace;
        //a file the watch waited for gets the arrival screen, it says what arrived before asking where it lives
        if (_arrivedByWatch && probes.MoveOfferFor(model.Path) is { } watched)
        {
            Selected = model;
            return WatchArrived(model, watched);
        }
        if (probes.MoveOfferFor(model.Path) is not { } offer) return OfferProjector(model, replace);

        _moveOffer = offer;
        var rows = new List<WizardRow>
        {
            //state the fact and why it matters, don't tell the user they should move
            new("This model is in your Downloads folder, which Windows can be set to clean out "
                + "automatically."),
        };

        if (offer.StorageSense == "armed") //storage sense informs, it never gates
            rows.Add(new WizardRow("That cleanup is currently armed on this machine.", RowTone.Aside));

        //an unmeasured size gets no size clause, 0 GB on a screen that prices the copy reads as free
        if (offer.CrossVolume)
            rows.Add(new WizardRow(
                offer.Bytes > 0
                    ? $"That folder is on another drive, so moving would COPY {SearchRow.Gb(offer.Bytes)}."
                    : "That folder is on another drive, so moving would COPY the file rather than rename it.",
                RowTone.Aside));

        return Emit(new WizardScreen.Choice(
            MoveKey,
            "Where should this model live?",
            [
                new ChoiceOption(No, "Leave it where it is"),
                new ChoiceOption(Yes, $"Move it to {offer.SuggestedDir}"),
                new ChoiceOption(Elsewhere, "Move it somewhere I choose"),
            ],
            BodyRows: rows), MoveKey);
    }

    //keep writes nothing, null would read as a stored intent, silence means leave it alone
    private WizardScreen AnswerMove(string key)
    {
        _awaiting = null;

        if (key == Yes && _moveOffer is { } offer)
        {
            Writes = Writes with { MoveTo = offer.SuggestedDir };
            return OfferProjector(Selected!, _moveReplace);
        }

        if (key == Elsewhere)
            return Emit(new WizardScreen.Ask(
                MovePathKey,
                "Which folder?",
                //the folder doesn't need to exist, adoption creates it. reject only an empty answer
                Validate: t => Gatto.Roles.LlamaAssetSteering.NormalizePath(t).Length == 0
                    ? "Type or paste a folder path"
                    : null,
                Placeholder: @"Type or paste the folder, e.g. D:\models"), MovePathKey);

        return OfferProjector(Selected!, _moveReplace);
    }

    private WizardScreen AnswerMovePath(string typed)
    {
        _awaiting = null;
        Writes = Writes with { MoveTo = typed };
        return OfferProjector(Selected!, _moveReplace);
    }

    //a projector sits beside the model, offer to add it. show what the pair costs to run, and only when adding it changes that
    private WizardScreen OfferProjector(FoundModel model, bool replace)
    {
        _projectorReplace = replace;
        //the watch already offered text-only while the encoder was on screen, don't ask the same question twice
        if (_declinedEncoder) return OfferCustomBuild(model, replace);
        //no projector goes on to the build offer, going straight to the scaffold would leave that offer unreachable
        if (probes.ProjectorFor(model.Path) is not { } found)
            return OfferCustomBuild(model, replace);

        _projector = found.Path;
        var rows = new List<WizardRow>
        {
            new($"Found {System.IO.Path.GetFileName(found.Path)} beside your model, "
                + "that is what lets a model read images."),
        };

        //warn only when the projector changes the fit, a screen that warns about everything trains users to skip warnings
        if (probes.PairFit(model.Path, found.Bytes) is { } fit && fit.Alone != fit.WithProjector)
            rows.Add(new WizardRow(
                $"Loading both together {SearchRow.FitWords(fit.WithProjector, Shape, Glyphs)}, "
                + $"the model on its own {SearchRow.FitWords(fit.Alone, Shape, Glyphs)}.", RowTone.Aside));

        return Emit(new WizardScreen.Choice(
            ProjectorKey,
            "Add vision to this model?",
            [
                //yes leads the cursor, but the screen still asks, a projector beside the weights is never armed on its own
                new ChoiceOption(Yes, "Yes, add the projector"),
                new ChoiceOption(No, "No, set it up for text"),
            ],
            BodyRows: rows), ProjectorKey);
    }

    //yes writes the projector, no writes nothing (Back's Writes restore is what stops an abandoned choice)
    private WizardScreen AnswerProjector(string key)
    {
        _awaiting = null;
        if (key == Yes && _projector is { Length: > 0 } path)
            Writes = Writes with { Projector = path };

        return OfferCustomBuild(Selected!, _projectorReplace);
    }

    //when the pinned build can't load this architecture, offer one that can, only this branch writes it. no is first and scaffolds anyway
    private WizardScreen OfferCustomBuild(FoundModel model, bool replace)
    {
        _customBuildReplace = replace;
        var arch = probes.ArchitectureOf(model.Path);
        if (!ArchNote.NeedsAnotherBuild(arch)) return ScaffoldIntent(model, replace);

        return Emit(new WizardScreen.Choice(
            CustomBuildKey,
            "Point this model at a different llama.cpp build?",
            [
                //no is first, most users have no second build and the model is written either way. on /model add the row says added
                new ChoiceOption(No, _addRoad ? "No, add it anyway" : "No, set the model up anyway"),
                new ChoiceOption(Yes, "Yes, I have one"),
            ],
            BodyRows: [.. ArchNote.AskRows(arch).Select(r => (WizardRow)r)]), CustomBuildKey);
    }

    //yes opens the path ask, no writes nothing, Back's Writes restore keeps an abandoned answer off disk
    private WizardScreen AnswerCustomBuild(string key)
    {
        _awaiting = null;
        if (key != Yes) return ScaffoldIntent(Selected!, _customBuildReplace);

        return Emit(new WizardScreen.Ask(
            CustomBuildPathKey,
            "Where is that llama-server.exe?",
            Validate: LlamaPathComplaint,
            Placeholder: LlamaPathPlaceholder), CustomBuildPathKey);
    }

    //verify a per-model binary with the same probe and sentences as the global one (a wrong shape is wrong under any key)
    private WizardScreen AnswerCustomBuildPath(string typed)
    {
        _awaiting = null;
        var result = probes.VerifyLlamaServer(typed);
        if (LlamaProbeComplaint(result.Shape, probes.ChooseLlamaAsset()?.CudartZipName is not null,
                steeredDoor: false, result.VcRuntimeAbsent, result.SiblingName)
            is { } complaint)
            return Emit(new WizardScreen.Ask(
                CustomBuildPathKey, complaint,
                Validate: LlamaPathComplaint,
                Placeholder: LlamaPathPlaceholder), CustomBuildPathKey);

        //write what actually ran, a folder answer stores the exe the probe resolved to
        Writes = Writes with { ModelLlamaServer = result.Ran ?? typed };
        //don't announce the pick again here, the answer is in the transcript right above
        return ScaffoldIntent(Selected!, _customBuildReplace);
    }

    //when the repo can't tell a second quant from a same-named model, ask the user. when adding is impossible the option is absent
    private WizardScreen ModelCollision(string existingId, bool canAdd) => Emit(new WizardScreen.Choice(
        ModelCollisionKey,
        //call the weights a file here, the word model already means the setup
        $"You already have a model called {existingId}, set up for a different file",
        [
            new ChoiceOption(UseExistingModel, "Use the model I already have"),
            //add-as-a-file is second because it loses nothing, replace is last as the only option that throws work away
            .. canAdd
                ? new[] { new ChoiceOption(AddAsFile, $"Add it as another file of {existingId}") }
                : [],
            new ChoiceOption(ReplaceModel, "Replace it with this model"),
        ],
        BodyRows:
        [
            //say what replacing costs, the model's own settings and tuning are gone
            new WizardRow($"Replacing rewrites {existingId} for the file you just chose. Anything "
                + "you tuned in that model, its settings, its system-append, is gone."),
        ]), ModelCollisionKey);

    private WizardScreen AnswerModelCollision(string optionKey)
    {
        _awaiting = null;
        if (optionKey == UseExistingModel)
        {
            //keep the existing id and skip the scaffold, the chosen file simply isn't adopted
            _modelId = _clashingModelId;
            _narrate.Add(new WizardScreen.Info("model.reused",
                [new WizardRow($"{Glyphs.Ok} Keeping {_clashingModelId} as it is")]));
            return Done();
        }

        //the user says it's the same model, so join it, don't replace it
        if (optionKey == AddAsFile) return AddFileIntent(Selected!, _clashingModelId!);

        return OfferMove(Selected!, replace: true);
    }

    //skip the create screens, this model already has its projector and server. the id is passed so the file lands in that folder
    private WizardScreen AddFileIntent(FoundModel model, string existingId)
    {
        Writes = Writes with
        {
            CreateModel = new WriteSet.Model(model.Path, Port: DefaultPort,
                Context: probes.ContextFor(model.Path), Id: existingId, AddToModelId: existingId),
        };
        //save the join separately, the WriteSet is cleared before its question is asked
        _joining = (existingId, Gatto.Core.Acquire.QuantToken.Of(model.Path) ?? "");
        NeedsWritesApplied = true;
        return Emit(WritesPending, null);
    }

    //the joined file is inactive until yes, so ask either way. if the model is serving, say the running server keeps its file
    private WizardScreen QuantFromNow(string modelId, string quant)
    {
        //say what happened before asking about it (the file is already there, the question is about next)
        _narrate.Add(new WizardScreen.Info("model.quantadded",
            [new WizardRow($"{Glyphs.Ok} {quant} is added to {modelId}.")]));

        var serving = string.Equals(probes.LoadedModelId(), modelId, StringComparison.OrdinalIgnoreCase);
        return Emit(new WizardScreen.Choice(
            QuantFromNowKey,
            SwapConfirm.QuantQuestion(modelId, quant),
            [
                //each option names its own outcome, the reader shouldn't fetch the consequence from the body
                new ChoiceOption(Yes, $"Yes, {modelId} will use {quant}"),
                new ChoiceOption(No, $"No, keep {quant} on the list unused for now"),
            ],
            BodyRows: serving
                ? [new WizardRow($"{modelId} is serving right now. This changes the setting: the "
                    + "running server keeps the file it started with until it restarts.")]
                : [new WizardRow($"{quant} is on {modelId}'s list either way. This chooses which "
                    + "file it uses.")]),
            QuantFromNowKey);
    }

    //yes records an intent for the Done pause, no keeps the file and undoes nothing
    private WizardScreen AnswerQuantFromNow(string optionKey)
    {
        _awaiting = null;
        if (optionKey == Yes && _joining is { } j)
            Writes = Writes with { ActivateFile = (j.Id, j.Quant) };
        return Done();
    }

    //the model intent in one place, replacing or not, so the collision branch can't drift on context, port or the pause

    //this records an intent and pauses for the writer. if a screen ever sits between an intent and its pause, add the back-walk test in the same commit
    private WizardScreen ScaffoldIntent(FoundModel model, bool replace)
    {
        //use the computed context, a ceiling this machine can't hold is a model that fails to load
        Writes = Writes with
        {
            CreateModel = new WriteSet.Model(model.Path, Port: DefaultPort,
                Context: probes.ContextFor(model.Path), Replace: replace,
                Source: Picked is { } row && Answers(model, row.PickedQuant) ? new Gatto.Roles.ModelSource(row.RepoId, row.PickedQuant.FileName) : null,
                PinGpu: probes.ServeOnlyGpu()),
        };
        //pause for the writer before offering anything, no screen may audition a model that isn't on disk yet
        NeedsWritesApplied = true;
        return Emit(WritesPending, null);
    }

    //shared

    //last write, it names the default and arms the pause before prove-it
    private WizardScreen Done()
    {
        //belt for the roads that scaffolded nothing, the pause applies it. on the add road fill it only when no default exists, overwriting one erases the user's choice
        if (_modelId is { } id)
            Writes = _addRoad
                ? Writes with { DefaultModelIfAbsent = id }
                : Writes with { DefaultModel = id };

        //arm the pause on every arrival, even with nothing to write, reuse and collision paths write their default here only
        _resumeStage = ResumeStage.DonePause;
        NeedsWritesApplied = true;
        return Emit(new WizardScreen.Info("default.ready", [$"Saving your choices{Glyphs.Ellipsis}"]), null);
    }

    //prove-it runs after the write so the saved sentence is true. guard its one run by counting probe calls, a ran-already flag would hide the routing bug
    private WizardScreen ProveScreen()
    {
        //the marker promises a question, paths that ask nothing withdraw it, the two paths that ask still carry it
        var firstQuestion = new WizardScreen.Info("step.first", [new WizardRow("First question")]);
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
            _narrate.Add(new WizardScreen.Info(ProveKey, [
                new WizardRow($"{_modelId} is saved. {holding} is still the running server, so "
                    + "nothing has been asked of it yet."),
                //only say it when the check step didn't already say it (the old whenever-you-want promise was false while a model holds the server)
                .. _heldSaid
                    ? (IReadOnlyList<WizardRow>)[]
                    : [new WizardRow($"Once {holding} is no longer running, type gatto audition "
                        + $"{_modelId} in a terminal to run the check.",
                        Highlight: [$"gatto audition {_modelId}"])],
                //one serve-restart sentence for both roads, the switch question the add branch promised no longer exists
                new WizardRow($"gatto serve stop, then gatto serve start {_modelId} when you "
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
                    _narrate.Add(new WizardScreen.Info(ProveKey, SuccessRows(outcome)));
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
                return Emit(new WizardScreen.Choice(
                    ProveKey,
                    ConnectCheckTitle,
                    [new ChoiceOption(OpenRepl, "next"),
                     new ChoiceOption(Finish, "leave", Advances: false)],
                    KeysOnly: true,
                    PurredMs: (long)outcome.FirstToken.TotalMilliseconds,
                    BodyRows: [
                        new WizardRow("It answers.", RowTone.Aside, Glyph: RowGlyph.Good),
                        "",
                        ProveFacts.Row("server", _connectBaseUrl ?? ""),
                        .. _connectModel is { Length: > 0 } named
                            ? (IReadOnlyList<WizardRow>)[ProveFacts.Row("model", named)]
                            : [],
                        ProveFacts.Row("context",
                            $"{_connectContext:N0} {Glyphs.Dot} " + ContextNote()),
                        .. outcome.TokensPerSecond is { } rate
                            ? (IReadOnlyList<WizardRow>)[ProveFacts.Speed(rate)]
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
        var rows = new List<WizardRow> { outcome.Detail, "" };
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
        return Emit(new WizardScreen.Choice(
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
    private WizardScreen ConnectChecking() => Emit(new WizardScreen.Choice(
        ProveKey,
        ConnectCheckTitle,
        [new ChoiceOption(ConnectCheckStop, "stop the check", Advances: false)],
        BodyRows: [
            new WizardRow("Nothing to do here, gatto asks the server one small question and waits "
                + "for the first word back.", RowTone.Aside),
            "",
            ProveFacts.Row("server", _connectBaseUrl ?? ""),
            .. _connectModel is { Length: > 0 } named
                ? (IReadOnlyList<WizardRow>)[ProveFacts.Row("model", named)]
                : [],
            ProveFacts.Row("asking", "\"Say OK.\", waiting for the first reply"),
            "",
        ],
        Watching: true,
        KeysOnly: true,
        FullPurr: true,
        Legend: "the server stays set up, unchecked"), ProveKey);

    //branch on the status code, a message text is not evidence. offer try again only where asking twice could help
    private WizardScreen ConnectCheckFailed(Gatto.Core.Acquire.ProveOutcome outcome)
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

        return Emit(new WizardScreen.Choice(
            ProveKey,
            ConnectCheckTitle,
            canRetry
                ? [new ChoiceOption(Retry, "Try again"),
                   new ChoiceOption(Finish, "Finish anyway, I'll look into it")]
                : [new ChoiceOption(Finish, "Finish anyway, I'll look into it")],
            BodyRows: [
                new WizardRow(verdict, RowTone.Aside, Glyph: RowGlyph.Bad),
                "",
                ProveFacts.Row("server", _connectBaseUrl ?? ""),
                ProveFacts.Row("said", said),
                "",
                new WizardRow(advice),
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
    private WizardScreen AnswerProve(string key)
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
    private WizardScreen AnswerSummary(string key)
    {
        RefuseUndrawn(key, "summary");
        _awaiting = null;
        StartReplWhenDone = key == OpenRepl;
        return TerminalScreen();
    }

    //the done step's questions in one method, each call site tested. in session the install question is skipped, /model add must not offer to install
    private WizardScreen DoneStep() =>
        (_addRoad ? null : InstallSegment()) ?? AfterTheInstallAsk();

    //the consent is asked last, once, an existing answer skips the ask (once comes from the probe's read)
    private WizardScreen AfterTheInstallAsk()
    {
        //the one place that can say the install question is past, both roads arrive here
        _pastTheInstallAsk = true;
        return probes.UpdateConsent() is null ? UpdateAsk() : CloseWizard();
    }

    //the summary draws after both questions, its gatto row computes from the install answer and the config path exists only past the consent pause
    private WizardScreen SummaryScreen(Gatto.Core.Acquire.ProveOutcome outcome) =>
        Emit(new WizardScreen.Choice(
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

    private WizardScreen UpdateAsk() => Emit(new WizardScreen.Choice(
        UpdateKey,
        "Should gatto check for updates?",
        [
            //no pre-selection, a highlighted recommendation on a network question pushes the user toward gatto's preference
            new ChoiceOption(UpdateYes, "Yes, check weekly"),
            new ChoiceOption(UpdateNo, "No"),
        ],
        BodyRows:
        [
            new WizardRow("Once a week, gatto asks GitHub whether a newer release exists. "
                + "Nothing about you or your machine is sent."),
            //a blank row separates the two paragraphs, without it they read as one block
            "",
            //say how to change it, a consent you can't revoke isn't consent
            new WizardRow("Say no and gatto never contacts the network on its own. "
                + "You can change it later with the update_check key in gatto.json."),
        ],
        //this screen has no default answer, the cursor must start from nothing
        NoDefault: true), UpdateKey);

    private WizardScreen AnswerUpdate(string key)
    {
        _awaiting = null;
        Writes = Writes with { UpdateCheck = key == UpdateYes };
        return CloseWizard();
    }

    //write the consent answer before the terminal renders. can't arm the pause and return Terminal at once, the runner drops that return and done prints twice
    private WizardScreen CloseWizard()
    {
        _resumeStage = ResumeStage.ConsentPause;
        NeedsWritesApplied = true;
        return Emit(new WizardScreen.Info("consent.ready", [$"Saving your answer{Glyphs.Ellipsis}"]), null);
    }

    //the run-gatto-then-slash-model line, its id from what this walk added (one test pins it literally)
    internal WizardRow AddRoadNextStep =>
        new($"run gatto, then /model {_modelId} to use it",
            Highlight: ["gatto", $"/model {_modelId}"]);

    //the closing sentence reads what the install really did, a null InstallTo means many things, _installState tells them apart
    internal WizardRow CompletionNextStep =>
        //the intent is consumed at the pause, check _installedTo too or an accepted install reads as declined
        (Writes.InstallTo ?? _installedTo) is not null
            //the new-terminal caveat is only true when the install was accepted
            ? new WizardRow(
                "A new terminal will find it; this one will not. When you're ready, open a new "
                + "terminal in your project folder and type gatto. Have fun with your local model!",
                Highlight: ["gatto"])
        : _installState switch
        {
            //already findable from any terminal, so the new-terminal caveat warns about a problem this user doesn't have
            InstallState.Installed or InstallState.AlreadyInstalledElsewhere => new WizardRow(
                "Open a terminal in your project folder and type gatto. Have fun with your local "
                + "model!", Highlight: ["gatto"]),

            //declined, so name where it runs from and offer gatto setup without pushing
            InstallState.NotInstalled or InstallState.InstalledButNotOnPath => new WizardRow(
                $"gatto stays where it is, {probes.RunningFrom()}. Run it from there; gatto setup "
                + "can install it later. Have fun with your local model!",
                Highlight: ["gatto setup"]),

            //a dev build was never installable, so nothing here was declined
            InstallState.DevBuild => new WizardRow(
                "Run gatto from where it is. Have fun with your local model!", Highlight: ["gatto"]),

            _ => new WizardRow("Have fun with your local model!"),
        };

    //the closing line, read by both the done screen and the record, so one walk can't close twice
    private WizardRow FinishedNextStep => _addRoad ? AddRoadNextStep : CompletionNextStep;

    private WizardScreen TerminalScreen()
    {
        _finished = true;
        //in-session this screen carries nothing, the swap confirm is next. standalone keeps one deliberate blank before the closing line
        return Emit(new WizardScreen.Terminal(
            "done",
            //the blank returns on the add road, it ends at a shell prompt like setup's
            StartReplWhenDone ? [] : [""],
            //the starting line is true only while a server happens to be running, don't paper over the gap till auto-serve ships
            NextStep: StartReplWhenDone ? new WizardRow($"Starting gatto{Glyphs.Ellipsis}")
                : FinishedNextStep,
            Success: true), null);
    }

    //one stamp composer for the pass, failure and completion screens, drop every row whose fact is missing
    private static IReadOnlyList<WizardRow> StampRows(AuditionFacts f)
    {
        var rows = new List<WizardRow>();
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
    private static WizardRow SpeedRow(double rate)
    {
        var shown = Math.Round(rate, 1);
        var figure = $"~{shown:0.#} tok/s";
        return new WizardRow(
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
    private IReadOnlyList<WizardRow> SuccessRows(Gatto.Core.Acquire.ProveOutcome outcome)
    {
        return Tui.Epilogue.Compose(WalkFacts(outcome), Tui.Epilogue.Face.Summary, Glyphs);
    }

    //the record from the same composer as the summary, empty when no summary was reached so the printer skips it
    internal IReadOnlyList<WizardRow> RecordRows() =>
        //a crash keeps the last frame, only a real leave or a finished walk prints a record
        _summaryOutcome is not null || _left || _finished ? RecordFacts() : [];

    //a leave records with a null outcome, CheckRow says not-run. empty before the write pause, a walk with nothing on disk has no record
    private IReadOnlyList<WizardRow> RecordFacts() =>
        _summaryOutcome is { } outcome
            ? Tui.Epilogue.Compose(WalkFacts(outcome), Tui.Epilogue.Face.Record, Glyphs)
        : _applied
            ? Tui.Epilogue.Compose(WalkFacts(null), Tui.Epilogue.Face.Record, Glyphs)
            : [];

    //whether the user left the wizard, as distinct from a run that threw (a throw keeps the last frame)
    internal bool Left => _left;

    //the sentence a leave that wrote nothing prints. gatto model names the add command, gatto setup keeps its own
    internal WizardRow? PrintedLeave =>
        _addRoad
            ? new WizardRow($"nothing added {Glyphs.Dot} gatto model whenever you're ready", Highlight: ["gatto model"])
            : LeaveStep;

    //gatto model's closing is one line under the header, like its nothing-added leave. null on setup, before the writes, and on a throw
    internal WizardRow? AddedClosing =>
        _addRoad && _applied && (_finished || _left) && _modelId is { } id
            ? new WizardRow($"{id} added{AddedHow} {Glyphs.Dot} "
                + (_proveFailed is not null && _summaryOutcome is null
                    ? "gatto doctor checks everything and says what to fix"
                    : $"run gatto, then /model {id} to use it"),
                Highlight: ["gatto doctor", "gatto", $"/model {id}"])
            : null;

    //what the user did with the check, read from the deed. a server that failed after the check outranks its verdict, the tail sends the user to doctor
    private string AddedHow =>
        _proveFailed is { StartFailed: true } && _summaryOutcome is null ? ", but its server didn't start"
        : _proveFailed is not null && _summaryOutcome is null ? ", but it didn't answer"
        : _check is { Outcome: AuditionOutcome.Passed } ? " and checked"
        : _check is { Outcome: AuditionOutcome.Failed } ? ", it struggled on the check and you kept it"
        : _check is { Outcome: AuditionOutcome.CouldNotRun } ? ", the check couldn't run"
        : _checkStopped ? ", you stopped the check"
        : _summaryOutcome?.ServedByAnother is { } holding ? $", not checked while {holding} holds the server"
        : _proved is not null ? ", it answers and the five tasks were skipped"
        : _left ? ", you left before checking it"
        : ", not checked";

    //the flow picks the closing row, null on the road whose tail is a live countdown, it writes the tail itself
    internal WizardRow? RecordClosing =>
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
            ? new Tui.Epilogue.ServerFact(url, _connectModel, $"{_connectContext:N0}",
                ContextNote(),
                //endpoint names stay in the config, the row's job is which server it talks to and that the earlier setup is kept
                _besideLocal ? "used from now on, the earlier setup is kept" : null)
            : null;

    //the machine fact row, one grammar across the four shapes. state one budget and say where it applies, promise nothing a record can't keep
    private WizardRow? MachineRow()
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
    private (WizardRow? Row, WizardRow? Folder) ModelRows()
    {
        if (_modelId is not { Length: > 0 } id) return (null, null);
        if (probes.ActiveModelFile(id) is not { } file) return (null, null);

        var facts = $"{System.IO.Path.GetFileName(file.Path)} {Glyphs.Dot} {SizeWords.Auto(file.Bytes)}";
        return (
            SummaryFacts.Row("model", _modelVerified ? facts + $" {Glyphs.Dot} verified" : facts),
            SummaryFacts.Row("", System.IO.Path.GetDirectoryName(file.Path) + System.IO.Path.DirectorySeparatorChar));
    }

    //the found road drops the backend word from the engine row, only the truth fills it
    private WizardRow? EngineRow()
    {
        if (_offer is { } offer)
            return SummaryFacts.Row("engine",
                $"llama.cpp {Gatto.Roles.LlamaAssetSteering.PinnedRelease} {Glyphs.Dot} {offer.Pick.BuildName}"
                + $" {Glyphs.Dot} {offer.Into}");

        if (_llamaPath is { Length: > 0 } exe && _foundBuild is { Length: > 0 } build)
            return SummaryFacts.Row("engine",
                $"llama.cpp {build} {Glyphs.Dot} {System.IO.Path.GetDirectoryName(exe)}"
                + System.IO.Path.DirectorySeparatorChar);

        return null;
    }

    //the check row is a verdict sentence, no numbers. the arms must be tested in order, a held server is also a walk with no audition
    private WizardRow CheckRow(Gatto.Core.Acquire.ProveOutcome? outcome, bool pointer = true)
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
    private WizardRow GattoRow()
    {
        //check the intent and the landed fact, the write set is cleared before this row draws
        if (Writes.InstallTo is { Length: > 0 } || _installedTo is { Length: > 0 }
            || Writes.PathOnly || _pathOnlyLanded
            || _installState is InstallState.Installed)
            return SummaryFacts.Row("gatto", $"{Glyphs.Ok} installed, any terminal finds it");

        return _installState switch
        {
            InstallState.DevBuild =>
                SummaryFacts.Row("gatto", "runs from a development build"),
            InstallState.AlreadyInstalledElsewhere =>
                SummaryFacts.Row("gatto",
                    $"runs from {probes.RunningFrom()}, gatto is also installed at {_installDir}"),
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


    private WizardScreen Leave()
    {
        _awaiting = null;

        //leaving means intending nothing, this clear is explicit insurance, no apply path reaches it today
        Writes = new WriteSet();
        //mark leaving before composing, LeavingRows and LeaveStep read it
        MarkLeaving();
        return Emit(new WizardScreen.Terminal(
            "left", LeavingRows, NextStep: LeaveStep, Success: true), null);
    }

    //the leave copy says what actually landed, silent before the writes, ownership after. one home because Esc shows this too

    //the leave's rows are the record on both roads
    internal IReadOnlyList<WizardRow> LeavingRows => RecordFacts();

    //the sentence is what the frame draws, comma included, a test reads it against that golden
    internal const string NothingAddedText = "Nothing added, gatto model whenever you're ready";

    //the computed leave sentence sits in the next-step slot, it replaces the generic pair instead of adding a row
    internal WizardRow? LeaveStep =>
        !_addRoad ? Tui.Epilogue.LeaveSentence(Lead, _modelId, UnreachedQuestions())
        : _applied ? null
        : new WizardRow(NothingAddedText, Highlight: ["gatto model"]);

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
    private static readonly WizardScreen WritesPending = new WizardScreen.Info("writes.pending", []);

    //stamp the strip at the one exit a screen can take. the key is an argument because the two exits know it differently
    private WizardScreen Crossing(WizardScreen s, string stripKey) =>
        s with { Strip = WalkSection.For(stripKey, Path, _addRoad) };

    private WizardScreen Emit(WizardScreen s, string? awaiting)
    {
        //every askable screen leaves with the back rule's answer, no screen author sets it
        s = WithBack(s);
        //the strip is stamped from the awaiting argument, reading the field here would price the screen on the previous position
        s = Crossing(s, awaiting ?? KeyOf(s));
        _emitted.Add(s);
        _awaiting = awaiting;
        //only an awaited screen moves the leaving point, a write-pause screen is consumed before any face sees it
        if (awaiting is { Length: > 0 }) _lastShown = awaiting;
        //only drawn options constrain an answer, a watch is answered by resolving, so it holds no list
        _awaitingOptions = s is WizardScreen.Choice { Options: { } opts, Watching: false }
            ? [.. opts.Select(o => o.Key)]
            : null;
        return s;
    }
}
