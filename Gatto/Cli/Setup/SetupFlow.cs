using Gatto.Cli.Install;
using Gatto.Cli.Setup.Shelves;
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

//keep the two shas beside the verdict, so the screen shows the pair the check compared rather than hashing the file again.
internal readonly record struct WatchCheck(
    WatchVerdict Verdict, string? Actual = null, string? Expected = null);

//five states rather than a bool, still arriving, unreadable and wrong file are three facts, collapsing them accuses a half-written file
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

//keep the flow pure and resumeless, a user who leaves mid-way leaves nothing behind. every fact from ISetupProbes, every word out as a SetupScreen
internal sealed partial class SetupFlow(ISetupProbes probes)
{
    //keep screen keys stable for tests, the copy beside them changes but the key can't. write keys as literals, the census greps raw source for them, and it greps comments too, so a comment must not quote the constructor pattern

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
    //the shelf drawn while its scan and its search run, a watching screen that the load's own end resolves
    public const string ShelfLoadingKey = "model.loading";
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
    //render back as a row rather than a keystroke, so both askable kinds stay uniform without touching SelectPrompt. don't name a screen option back, Answer intercepts that key before the screen dispatch
    public const string BackKey = "back";

    //a typed screen's answer that leaves the wizard, where a bare null still means back for a face with no leave of its own
    public const string LeaveKey = "leave";

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
    public const string TypeAnId = "typeid";
    //prefix the shelf's typed answer, a typed number and a row index are the same string
    public const string CtlTyped = "shelf.typed:";
    public const string CtlLift = "shelf.lift";
    public const string CtlSearch = "shelf.search";
    //the params header's click, which re-arranges the rows in hand and asks the engine nothing
    public const string CtlParams = "shelf.params";

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

    //both check screens of gatto model end with it: the model is added and the shelf it came from returns for another
    public const string AddAnother = "model.add.another";
    internal const string AddAnotherLabel = "Add another model";

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

    //set the row budget from the runner, it holds both the flow and the face, since a second derivation fetches rows the screen can't show. keep the default for a flow driven by a test
    internal int RowBudget { get; set; } = HubSearch.DefaultRowBudget;

    //compose the flow's copy in the glyph set the runner passes, a flow that read it itself could disagree. tests that build bare default to Unicode
    internal Gatto.Terminal.GlyphSet Glyphs { get; set; } = Gatto.Terminal.GlyphSet.Unicode;

    //gate every sentence that names the m key on the face's ability to switch source. set it from the runner, the flow and the face are built apart
    internal bool CanSwitchSource { get; set; }

    //set FaceShowsChoiceBodyRows from the face like the source switch, and default it false so the narration draws unless the face says otherwise
    internal bool FaceShowsChoiceBodyRows { get; set; }

    //set from the runner when the face draws a loading shelf, so the scan and the search run behind a frame. a flow a test drives directly stays synchronous
    internal bool LoadsShelfInBackground { get; set; }

    //set from the runner, so the first arrival at the model step waits on a screen of its own rather than on the empty shelf
    internal bool OpensOnLoadingScreen { get; set; }

    //latched at the first arrival, since a later load keeps the shelf on screen
    private bool _opened;

    //use this port as the fallback for a fresh machine (no local endpoint exists to derive one from)
    public const int DefaultPort = 1235;

    private readonly List<SetupScreen> _emitted = [];

    //step markers are named rather than counted, each announced as one word where its step begins. an info screen asks no question, so its marker commits and stays

    //keep the narration in its own list, an info added to _emitted does not render and draining it would draw every key at once
    private readonly List<SetupScreen.Info> _narrate = [];

    //return the narration since the last screen and clear it, so each marker renders where the flow reached it.
    public IReadOnlyList<SetupScreen.Info> TakeNarration()
    {
        if (_narrate.Count == 0) return [];
        //stamp the marker when it is drained, so the user sees what was true when it is shown
        var taken = _narrate.Select(n => (SetupScreen.Info)Crossing(n, n.Key)).ToArray();
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
        //a shelf still loading stops asking the Hub, since nobody will read what it brings
        CancelLoad();
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

    //the sha256 the run already holds for one file, from a verified fetch or a read, so the profile records it without reading the file again
    private (string Path, string Sha256, long? Size, DateTime? Written)? _knownHash;

    //keep the last sweep's roots out of the back-step snapshot (a stale list would describe a sweep that did not happen)
    private IReadOnlyList<string> _scanRoots = [];

    //fill the folder slot only when the user typed a folder, the ambient sweep reads several roots and naming one would be a lie
    private string? _scanTyped;

    //record which screen armed the pause, inferring the route from the model id would loop back into the arming screen
    private enum ResumeStage { ScaffoldPause, DonePause, ConsentPause, AnotherPause }

    private ResumeStage _resumeStage = ResumeStage.ScaffoldPause;

    //record that default_model reached disk, the leaving sentence must come from the completed write. set it only after the done-pause's write applies
    private bool _defaultLanded;
    private IReadOnlyList<ModelRow> _rows = [];
    //hold why the last search was empty when the engine can say, and read it with the row count (null covers both cases)
    private HubSearchCause? _searchCause;
    //default the fit filter to on, and say what it hid (a is what lifts it)
    private bool _lift;

    //the repo a typed id looked up while its one-row shelf is showing, null once any search replaces it
    private string? _lookedUp;

    //the families the Hub shelf lists, the landing pair at open
    private IReadOnlySet<string> _lit = new HashSet<string>(Families.Load().Landing, StringComparer.OrdinalIgnoreCase);

    //every family on the chip row, which is what all lights
    private static IReadOnlySet<string> EveryFamily() =>
        new HashSet<string>(Families.Load().Ladder.Where(f => f != "all"), StringComparer.OrdinalIgnoreCase);

    //the one chip the face lights for a lit set: the family itself, all for every family, none for a pair
    private string? LitChip() =>
        _lit.Count == 1 ? _lit.First() : _lit.SetEquals(EveryFamily()) ? "all" : null;

    //the families lit when the shelf opens, and what the chip row comes back to when it would be empty
    private static IReadOnlySet<string> LandingPair() =>
        new HashSet<string>(Families.Load().Landing, StringComparer.OrdinalIgnoreCase);

    //a chip toggles its family, all lights every family and a second all brings the landing pair back. the row is never empty
    private void Light(string chip)
    {
        var every = EveryFamily();
        if (chip is "all" or "")
        {
            _lit = _lit.SetEquals(every) ? LandingPair() : every;
            return;
        }
        var next = new HashSet<string>(_lit, StringComparer.OrdinalIgnoreCase);
        if (!next.Remove(chip)) next.Add(chip);
        _lit = next.Count == 0 ? LandingPair() : next;
    }

    //the params sort as the user last clicked it, null while the engine's own order stands
    private bool? _smallestFirst;

    //the rows in hand by total parameters, the direction the click chose, a row with no total last
    private IReadOnlyList<ModelRow> Sorted(IReadOnlyList<ModelRow> rows) =>
        SortedBy(rows, r => r.Params is { } p ? p : ModelStructure.SizeMagnitude(r.ParamsLabel), r => r.Model);

    //the sort over anything that carries a total and a name, so the local shelf's options and facts move with their rows. each key is read once
    private IReadOnlyList<T> SortedBy<T>(IReadOnlyList<T> items, Func<T, double?> totalOf, Func<T, string> nameOf)
    {
        if (_smallestFirst is not { } smallest) return items;
        var keyed = items.Select(x => (Item: x, Total: totalOf(x), Name: nameOf(x))).ToList();
        var ordered = keyed.OrderBy(k => k.Total is null);
        ordered = smallest ? ordered.ThenBy(k => k.Total ?? 0) : ordered.ThenByDescending(k => k.Total ?? 0);
        return [.. ordered.ThenBy(k => k.Name, StringComparer.Ordinal).Select(k => k.Item)];
    }

    //keep the local shelf's chip in its own field, one shared family could empty the local shelf when the user narrows the Hub
    private string? _localFamily;

    //keep the searched words, the empty screen quotes them and a re-run searches the same. clear them when a family chip is pressed, the engine cannot hold both
    private string? _search;

    //keep the hidden-by-kind count from the engine, since nothing on the shelf undoes it
    private int _hiddenByKind;
    //whether a would show more than this shelf, as the engine said, so the count row promises a only when it can keep the promise
    private bool _moreBehind;

    //the last search's regime counts and why it stopped short, which the count row reads
    private ShelfOutcome? _counted;
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
    private string? _checkReading;

    //number finished tasks the way the offer numbered them (the runner counts the same list in the same order)
    private void OnCheck(Gatto.Roles.Audition.AuditionProgress p)
    {
        lock (_checkGate)
            switch (p.Stage)
            {
                //the read ends when the battery starts, the row it drew gives way to the task count
                case Gatto.Roles.Audition.AuditionStage.Reading:
                    _checkReading = p.Text;
                    break;
                case Gatto.Roles.Audition.AuditionStage.Started:
                    _checkReading = null;
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
            _checkReading = null;
        }
    }

    //null when nothing has been reported yet, so the screen draws no rows
    public CheckTick? PollCheck()
    {
        lock (_checkGate)
            return _checkTotal == 0 && _checkDone.Count == 0 && !_serverUp && !_stillLoading && _checkReading is null
                ? null
                : new CheckTick(_checkIndex, _checkTotal, _checkTask, [.. _checkDone], _serverUp,
                    _stillLoading, _checkReading);
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

    //hold the model id the chosen file collides with, from the screen to its answer, per run
    private string? _clashingModelId;

    //hold the joined model and quant after the pause (the write set is cleared and the done step still needs a subject)
    private (string Id, string Quant)? _joining;
    //hold the install target from the segment to its answer
    private string? _installDir;

    //findability repair only, no copy needed (the installed copy is newer than this image or unreadable)
    private bool _repairOnly;

    //hold the confirmed URL, context and endpoint name across the screens before the write pause (the flow keeps no resume state)
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
    //store the whole check rather than its facts (the completion screen can't infer a pass from facts a failed audition also measures). null means the check was skipped or could not run, treat it as we did not look
    private AuditionCheck? _check;
    //record that the writes were applied, set only where they are applied
    private bool _applied;

    //hold the chosen path, unknown until the fork is answered
    public SetupPath Path { get; private set; } = SetupPath.Unknown;

    //the strip's standing comes from the waiting screen, exposed for the flow's own tests
    public IReadOnlyList<StripSection> Strip => WalkSection.For(_awaiting, Path);

    //give a screen that asks nothing its own position, done is the last section of the strip
    private static string KeyOf(SetupScreen s) => s switch
    {
        SetupScreen.Choice c => c.Key,
        SetupScreen.Ask a => a.Key,
        SetupScreen.Info i => i.Key,
        SetupScreen.Progress p => p.Key,
        SetupScreen.Terminal t => t.Key,
        _ => throw new InvalidOperationException("unreachable, SetupScreen is closed"),
    };

    //every emitted screen, oldest first (the tests' window onto the flow)
    public IReadOnlyList<SetupScreen> Emitted => _emitted;

    //what this run intends to write, empty until a path completes. nobody applies it here
    public WriteSet Writes { get; private set; } = new();

    //the model this run will use, once one is chosen
    public FoundModel? Selected { get; private set; }

    //the shelf row the user chose when the model comes from the Hub. the browser does the download, this flow only records the choice
    public ModelRow? Picked { get; private set; }

    //whether the user asked to go straight into gatto when setup ends (the caller does the launching)
    public bool StartReplWhenDone { get; private set; }

    //true when the run's writes must be applied before the next screen. the runner applies them, then calls ResumeAfterWrites
    public bool NeedsWritesApplied { get; private set; }

    //don't snapshot the transcript, write-boundary flags or probe latches, a step back must not unshow a screen, undo a write or re-arm a start
    private sealed record Snapshot(
        string? Awaiting, ConnectProbe? Found, IReadOnlyList<FoundModel> Discovered,
        //store the typed folder beside the rows it filtered (a user choice, going back restores both together)
        string? ScanTyped,
        IReadOnlyList<ModelRow> Rows, HubSearchCause? SearchCause,
        //the lift travels with going back, the next search reads it. shelf counts stay out, a search runs before each render and rewrites them
        bool Lift,
        //the search, the lit families and the params sort stay live rather than snapshotted. back must show the shelf as the user left it
        string? ModelId, string? LlamaPath, AuditionCheck? Check,
        string? ConnectBaseUrl, int ConnectContext, string? ConnectEndpointName,
        bool ConnectBesideLocal,
        SetupPath Path, WriteSet Writes, FoundModel? Selected, ModelRow? Picked,
        bool StartReplWhenDone, SetupScreen Screen,
        //whether this step's screen came from a control that hit no list. going back restores the flag, so a control pressed there still undoes the step
        bool ControlStep,
        //the repo a typed id landed on, so going back shows that one-row shelf again rather than re-running a search
        string? LookedUp);

    //a stack of snapshots, no persistence and no resume (replaying would re-run probes, which are neither free nor idempotent)
    private readonly Stack<Snapshot> _back = new();

    private bool CanGoBack => _back.Count > 0 && !_backClosed;

    //the back stack closes at the first apply, and a second round opens it again, since a step back never undoes a write. _applied keeps the closing record
    private bool _backClosed;

    //the screen the flow waits on, the last emitted one (every branch emits its screen last)
    private SetupScreen? LastScreen => _emitted.Count > 0 ? _emitted[^1] : null;

    private Snapshot Capture(SetupScreen screen) => new(
        _awaiting, _found, _discovered, _scanTyped, _rows, _searchCause, _lift,
        _modelId, _llamaPath, _check,
        _connectBaseUrl, _connectContext, _connectEndpointName, _besideLocal,
        Path, Writes, Selected, Picked, StartReplWhenDone, screen, _controlStep, _lookedUp);

    //step back to the previous screen with the state it was answered under. after a write the step refuses with a screen, so the flow shows it and a test can open it
    private SetupScreen Back()
    {
        if (!CanGoBack)
            //second guard, reachable when a face offers back without advertising AllowBack. it renders a sentence (silence would teach the user their key is broken)
            return Emit(new SetupScreen.Info("back.refused", [new SetupRow(BackRefused)]), _awaiting);

        //a step back keeps the partial the drop screen offered to delete, so a later leave cannot delete what no screen it shows has priced, and a resume finds it
        _droppedPartial = null;

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
        _lift = s.Lift;
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
        _lookedUp = s.LookedUp;

        //recompose the shelf from live fields and re-run the search, a chips row could otherwise show a filter the rows lack. a looked-up row has no search to re-run
        if (s.Screen is SetupScreen.Choice { Key: SearchKey }) return _lookedUp is not null ? SearchScreen() : Search();

        //the screen is emitted again, the transcript keeps both visits (both happened)
        return Emit(WithBack(s.Screen), s.Awaiting);
    }

    //true for the two screens of one folder search: the folder question and a local shelf filtered by a typed folder
    private static bool IsFolderSearchStep(Snapshot s) =>
        s.Screen is SetupScreen.Ask { Key: ScanPathKey }
        || (s.Screen is SetupScreen.Choice { Key: DiscoveredKey } && s.ScanTyped is not null);

    //set AllowBack from the one rule (no screen author decides it, no face guesses it)
    private SetupScreen WithBack(SetupScreen screen) => screen switch
    {
        SetupScreen.Choice c => c with { AllowBack = CanGoBack },
        SetupScreen.Ask a => a with { AllowBack = CanGoBack },
        _ => screen,     //the Info, Progress and Terminal screens ask nothing, they can't offer a back row
    };

    //enter the model segment of the same flow (no second flow). skip the engine binary and hardware line, a live session already runs a server
    public SetupScreen StartAtModelSegment()
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

    public SetupScreen Start() => Emit(Welcome(), WelcomeKey);

    public SetupScreen Answer(string answer)
    {
        //handle the back key before the dispatch, a handler could read it as an option. route on the key alone, deferring to AllowBack let a handler end the wizard on it
        if (answer == BackKey && _awaiting is not null)
        {
            //going back from a loading shelf drops the load, or it would land on top of the screen that was restored
            if (_awaiting == ShelfLoadingKey)
            {
                CancelLoad();
                //a control's step waits on its landing, and a control that lands a list keeps no step, so b goes where it goes after that landing
                if (_stepAwaitsLoad) _back.Pop();
                _stepAwaitsLoad = false;
            }
            return Back();
        }

        //unwrap the pane's pick before the dispatch, the row's own key continues as if no pane existed and consumers read the row's file. a local pick is a path, kept for the row's answer to resolve
        _localChoice = null;
        if (ShelfControls.Unpick(answer) is { } picked)
        {
            if (_awaiting == DiscoveredKey) _localChoice = LocalShelf.PathOf(picked.File);
            else ApplyPickedFile(picked.Key, picked.File);
            answer = picked.Key;
        }

        //capture the step being left before a handler changes a field. a control that stays on the shelf pushes nothing, except when the screen it reaches holds no list
        var control = ShelfControls.IsControl(answer) || (_addRoad && IsAddress(answer));   //an address on gatto model is answered in place, a control
        if (control && _controlStep) { _back.Pop(); _controlStep = false; }
        var stepped = _awaiting is not null && !_backClosed && LastScreen is { } current
            && current is not SetupScreen.Choice { Watching: true }   //a watch ends with its task, so a later back to it shows a stop for work that finished
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
    private SetupScreen KeepStepIfNoList(SetupScreen landed)
    {
        //a loading shelf does not know yet whether a list will come, so the step is kept and the landing decides
        if (landed is SetupScreen.Choice { Key: ShelfLoadingKey } loading)
        {
            _stepAwaitsLoad = true;
            return HonestBack(loading);
        }

        if (landed is SetupScreen.Choice { Key: SearchKey or DiscoveredKey } c
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

    //a control's step waiting on its load is not one b returns to, so the loading shelf offers back only when a step lies behind it
    private SetupScreen HonestBack(SetupScreen.Choice loading)
    {
        var honest = loading with { AllowBack = _back.Count > 1 && !_backClosed };
        if (_emitted.Count > 0 && ReferenceEquals(_emitted[^1], loading)) _emitted[^1] = honest;
        return honest;
    }

    private SetupScreen Dispatch(string answer)
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
            MovePathKey => AnswerMovePath(Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(answer)),
            CustomBuildKey => AnswerCustomBuild(answer),
            CustomBuildPathKey =>
                AnswerCustomBuildPath(Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(answer)),
            ModelPickKey => FinishConnect(_connectEndpointName!, answer),
            ModelNameKey => FinishConnect(_connectEndpointName!, answer.Trim()),
            DiscoveredKey => AnswerDiscovered(answer),
            ScanPathKey => DiscoverTyped(Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(answer)),
            SearchKey => AnswerSearch(answer),
            ShelfLoadingKey => AnswerShelfLoading(answer),
            //route the typed-id ask through the same classifier, it gets the path handling too
            TypedIdKey => AnswerTypedText(answer),
            //guard the watch's own keys or a typed folder, same reason as the guards above
            DownloadKey when answer is Landed or PickAnother or UseTextOnly or Elsewhere
                || ShelfControls.Untyped(answer) is not null => AnswerDownload(answer),
            //guard the consent screen's own keys or a typed folder, any other answer falls to the throw
            ModelConsentKey when answer is ModelFetchNow or PickAnother
                || ShelfControls.Untyped(answer) is not null => AnswerModelConsent(answer),
            ModelFetchingKey => AfterModelFetch(answer),
            ReadingKey => AfterReading(answer),
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
                VerifyLlama(Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(typedPath)),
            //the LlamaPathKey screen is an ask, its answer is free text and needs no unwrap
            LlamaPathKey => VerifyLlama(Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(answer)),

            //a caller answering when nothing waits is a caller bug, so this arm throws. the message names the screen, a waiting screen with no dispatch arm also arrives here
            _ => throw new InvalidOperationException(
                $"the screen '{_awaiting ?? "(none)"}' has no handler for the answer '{answer}'"),
        };
    }

    private SetupScreen Emit(SetupScreen s, string? awaiting)
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
        _awaitingOptions = s is SetupScreen.Choice { Options: { } opts, Watching: false }
            ? [.. opts.Select(o => o.Key)]
            : null;
        return s;
    }
}
