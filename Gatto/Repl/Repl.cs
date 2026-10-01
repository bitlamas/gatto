using System.Runtime.ExceptionServices;
using System.Text;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;

namespace Gatto.Repl;

//the result of a /role switch, holding the canonical name and model id. on failure Message holds the line to print and the session is unchanged
public sealed record RoleSwitchResult(bool Success, string RoleName, string? ModelName, string? SystemText, string? Message,
    string? ThinkingName = null, SessionBaseline? Baseline = null);

//the outcome of a /model switch. the mismatch field names loaded weights only when they disagree, and the context field is the window the server allocated
public sealed record ModelSwitchResult(
    bool Success, string ModelId, string? SystemText, int? ContextBudget,
    string? Message, string? ServingMismatch, int? ProbedNCtx, string? ThinkingName = null,
    //the new model reasons through a toggle, so the footer reads thinking on|off and a /model switch can change that shape
    bool ThinkingIsToggle = false,
    //the new model has no reasoning switch at all, so bare /effort checks this before offering a level
    bool ThinkingIsUnavailable = false,
    SessionBaseline? Baseline = null);

//one probe answer: a null mismatch means agreement or an unknown reading. vision blocks only on a definite false, and the shelf flag says the name can be typed
public sealed record ServingProbe(string? Mismatch, int? NCtx,
    Gatto.Core.Client.ThinkCapability Capability = Gatto.Core.Client.ThinkCapability.None,
    bool? Vision = null, bool MismatchIsOnTheShelf = false);

//the result of an /effort switch, with the clamped level name for the footer. the message field is the line to print, including whether the level was saved
public sealed record EffortResult(bool Success, string? LevelName, string Message, ThinkingMark? Thinking = null);

//the semaphore count equals queued messages minus unredeemed permits. the take returns null from an empty queue, so no message is handed out twice
internal sealed class DispatchQueue(int cap)
{
    private readonly object _lock = new();
    private readonly Queue<string> _q = new();
    private readonly SemaphoreSlim _signal = new(0);

    public int Count { get { lock (_lock) return _q.Count; } }

    //false means the cap is full, and the caller beeps and re-seeds the composer with the text
    public bool TryEnqueue(string text)
    {
        lock (_lock)
        {
            if (_q.Count >= cap) return false;
            _q.Enqueue(text);
            _signal.Release();   //inside the lock, so a message and its permit appear together
            return true;
        }
    }
    //take every queued message and consume one permit each, in queue order, under one lock. taking and reporting in one call is what makes a half-pull impossible
    public IReadOnlyList<string> DrainAll()
    {
        lock (_lock)
        {
            var taken = _q.ToList();
            _q.Clear();
            for (var i = 0; i < taken.Count; i++) _signal.Wait(0);   //never blocks, since a permit exists for each message
            return taken;
        }
    }

    //the queued message texts, oldest first, the identity source for the chrome and the pull. the strings are the same instances TryEnqueue was handed
    public IReadOnlyList<string> Texts()
    {
        lock (_lock) return _q.ToList();
    }

    //wait for a permit and take the oldest queued message. null means that permit's message was pulled back into the composer, so the caller skips the round
    public async Task<string?> TakeAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);
        OnPermitTakenForTest?.Invoke();   //the hook is null outside tests
        lock (_lock) return _q.Count > 0 ? _q.Dequeue() : null;
    }

    //a test seam that fires between the permit and the dequeue, where the null return happens. it lets a test drive the Esc against dispatcher race on purpose
    internal Action? OnPermitTakenForTest;
}

public sealed class Repl(
    AgentLoop loop, Conversation convo, string roleName, string modelName,
    SessionStore? sessions, IChatClient client, int? configContext, int? contextBudget, string cwd,
    Func<ComposedSystem> recomposeSystem, Func<bool?> toggleAuto, Func<string, RoleSwitchResult> switchRole,
    Action? resetGrounding = null, WildState? wild = null, Action<bool>? persistWild = null,
    HookBus? hooks = null, ThemeMode themeMode = ThemeMode.Dark,
    Action<string>?[]? subagentProgressSink = null, string? resumedFrom = null, ReasoningMode reasoning = ReasoningMode.Collapsed,
    string? thinkingName = null,
    //the second argument says whether the level is also written as this model's default_effort. true for a typed /effort or the picker's Enter, false for its s
    Func<string, bool, EffortResult>? setEffort = null,
    //the picker rows for bare /effort, one per level the model's own thinking map declares. null or an empty list both fall back to the plain usage line
    Func<IReadOnlyList<PickerItem>>? listEfforts = null,
    //the composed version line, built by the caller since Cli owns the dev-build definition. null falls back to the undecorated form
    string? versionLine = null,
    InputPump? pump = null, ChromeHandle? chrome = null,
    //the /model seam, since Repl never references Gatto.Roles. its second argument says the swap is already confirmed by the add flow
    Func<string, bool, ModelSwitchResult>? switchModel = null,
    //the wizard is handed in, so Repl never sees Setup, and it binds this session's prompter. null means d is never offered, the key and the deed arrive together
    Func<string, string?>? setDefault = null,
    //the files this model holds, for bare /model quant, crossing the seam as quant and active pairs. null means the session cannot know, unlike an empty list
    Func<IReadOnlyList<(string Quant, bool Active)>>? modelFiles = null,
    //switch which file the model uses, with the confirm left to the caller. the ask needs the prompter and the serving state, both the caller's
    Func<string, string>? switchQuant = null,
    //the same seam for the remove verb, separate from the switch since one changes which file is used and the other forgets one exists
    Func<string, string>? removeQuant = null,
    //the probe answer taken before this Repl exists, run through the same DescribeProbe /model uses so the two agree. the closure repeats that probe once per turn
    ServingProbe? launchServing = null, Func<ServingProbe>? probeServing = null,
    //the picker rows, handed in since Repl cannot enumerate models itself. every row is a model, and the caller supplies the warning when there is one
    Func<IReadOnlyList<PickerItem>>? listModels = null, IListPicker? picker = null,
    //the live slots reader for a local endpoint, so the purr shows real prefill and decode. null for a remote endpoint, where only a rough estimate exists
    Gatto.Core.Client.ISlotsReader? slotsReader = null,
    //the warning sink, handed in so the rich loop can route diagnostics to its system line instead of corrupting the alt-screen frame. null skips the routing
    Gatto.Core.WarningSink? warn = null,
    //false for altScreen keeps a rich terminal on the plain renderer, and true for dumpOnExit prints the whole transcript at exit
    bool altScreen = true, bool dumpOnExit = false,
    //the effective toggle capability, true even when a binary thinking map sniffs as levels, and the footer then reads thinking on|off
    bool thinkingIsToggle = false,
    //the model has no reasoning switch at all, so bare /effort says so instead of offering a level it cannot act on
    bool thinkingIsUnavailable = false,
    //mouseEnabled enters console input mode and registers the mouse sink, and wheelLines feeds the painter. modeControl is the seam, null uses the real control
    bool mouseEnabled = false, int wheelLines = 3, Gatto.Terminal.IConsoleModeControl? modeControl = null,
    //a selection is always copied by Ctrl+C, copyOnSelect also copies at drag release, and a null clipboard uses the real one
    bool copyOnSelect = false, Gatto.Terminal.IClipboard? clipboard = null,
    //the one session file this launch resumes, handed in rather than re-derived so the transcript rebuild cannot disagree with the context load
    string? resumedPath = null,
    //the proactive compact threshold from the config, null when auto_compact is off. the first statement of the run installs it on the loop with the handler
    double? autoCompact = null,
    //the real permission store rather than a closure, since Repl already references its namespace. null degrades to unavailable, and the store is mutated directly
    PermissionStore? permissions = null,
    //what gatto really advertises this session, with each tool's owning script. null makes the command unavailable rather than showing an empty list
    Func<IReadOnlyList<ToolInfo>>? listTools = null,
    //each committed extension's one line of standing policy, shown under /tools beneath its tools. a closure beside listTools, and null leaves the policy rows out
    Func<IReadOnlyList<(string Extension, string Line)>>? listPolicy = null,
    //the finished sentence for an over-budget memory index, printed once after the banner. a later re-truncation goes through the warn sink instead
    string? memoryWarning = null,
    string? cloudNotice = null,   //said once per home, the first time a session runs on a cloud endpoint
    bool cloud = false,           //the footer's mark before the model, fixed for the session since the endpoint is
    //the provider's summed cost and latest quota, read at each footer rebuild, and null shows neither
    Func<(decimal? Cost, Gatto.Core.Client.QuotaReading? Quota)>? readUsage = null,
    //hands the rich loop's any-thread repaint to the usage feed, so a new quota shows without waiting for a key
    Action<Action>? onUsageChanged = null,
    //the memory write half over the compact seam, banking candidates and composing the index. null marks memory off, and only a user-chosen compact reaches it
    Gatto.Core.Memory.PiggybackSeam? piggyback = null,
    //the rich loop's output surface, injectable so a test can drive the loop headlessly, and null uses the real console surface
    Gatto.Terminal.ITermSurface? termSurface = null,
    //the prompters' link to the current turn's cancellation. the handle is armed with a live read of the turn token, and null leaves Esc cancelling one call
    TurnAbortHandle? turnAbort = null,
    //a non-null notice means gatto does not manage the server, and the sentence is written where the endpoint kind is known. null means a model session
    string? unmanagedNotice = null,
    //the connect wording of the image refusal, since the model sentence ends with advice a connect session cannot take
    string? unmanagedVisionNotice = null,
    //the actionable tail for a model that does not name the projector sitting beside it. null means there is nothing to point at, so the refusal keeps its ending
    string? visionFixHint = null,
    //the glyph set this run speaks, resolved once in Cli and handed down so two painters cannot disagree about the host. null means the Unicode set
    Gatto.Terminal.GlyphSet? glyphs = null,
    //the purr set this session draws each turn, null meaning pool index 0. the draw happens in GattoApp, so no golden depends on a coin toss
    Func<Render.PurrFrames>? purrSet = null,
    //the window title's deed, injectable so the title moment is testable without the real console. null keeps the alt screen's own default
    Gatto.Terminal.ITerminalTitle? terminalTitle = null,
    //null unless gatto reads the console itself, the only reader whose record count shows the tracer arriving
    Gatto.Terminal.InputDeafnessWatchdog? deafWatch = null,
    //what the resume carried or why it sent the conversation again, shown under the resume marker and never saved
    string? resumeLine = null,
    //the word the /model legend gives the dot, weights loaded on local rows and current on a cloud list where nothing loads
    string modelMarkLegend = "weights loaded",
    //said by bare /model when the endpoint lists no models, so the picker never shows rows that endpoint cannot take
    string? noModelList = null)
{
    //the prompters' bridge to the chrome, armed when the painter and renderer come up, cleared at teardown, so a prompt renders inline until then
    private readonly ChromeHandle? _chrome = chrome;

    //the run's glyph set, resolved once at launch and handed in so every painter speaks one set of marks
    private readonly Gatto.Terminal.GlyphSet _glyphs = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;

    //handed to the painter the rich loop builds, which passes it to its AltScreen
    private readonly Gatto.Terminal.ITerminalTitle? _terminalTitle = terminalTitle;

    private readonly object _ctsLock = new();
    private bool _offered;   //the 85% offer fires once per session, a /new or /compact lets it fire again
    private string? _lastCompactSummary;   //the newest /compact summary, which the rich loop prints as the session lead

    //what the last /compact banked into project memory, held as state because CompactedLine runs after HandleCompactAsync returns
    private (int Appended, int Dropped) _lastCompactBanked;

    //the footer percent read before this compaction mutates anything, so CompactedLine can report the transition
    private int? _lastCompactBeforePercent;

    //images the compaction dropped because a summary holds only text, counted so CompactedLine can report the loss
    private int _lastCompactImagesDropped;

    //images pasted with Alt+V and not yet sent, merged with the files a message names and cleared once it goes

    //pasted images waiting for a send, keyed by the number in their [Image #N] placeholder (every access is under s.Gate)
    private readonly Dictionary<int, ImageRef> _pendingPaste = new();

    //hands a refused message back to the live composer, inserting it because the composer is already mid-Read when the refusal is known
    private Action<string>? _restoreComposer;

    //asks whether a large batch of images should go, and how durably the answer holds. null outside the rich loop, which has no way to ask
    private Func<int, int, ConfirmAttachAnswer>? _confirmAttach;

    //test seam that lets the confirm flow be driven without a terminal. production leaves it null and builds a real SelectPrompt
    internal Func<SelectSpec, SelectOutcome>? ConfirmAttachForTest;

    //reads and writes the project's standing answer on the count question, separate from _confirmAttach so the byte backstop is checked first
    private Func<bool>? _attachManyGranted;
    private Action? _grantAttachMany;

    //what the composer header says when an Alt+V had nothing to paste, and only refusals go here
    private string? _pasteHint;

    //when a transient paste hint stops showing, and null means the hint has no expiry
    private DateTime? _pasteHintUntil;

    //arms a one-shot repaint so a transient hint disappears on its own, the paste handler is wired before the loop's hint timer exists
    private Action<int>? _armHintExpiry;

    //how many images this session has pasted, the number in each [Image #N] placeholder
    private int _pastedCount;

    //set once the rich loop wires paste, null on the plain path, which has no Alt+V to advertise
    private Gatto.Terminal.IClipboardImageNative? _clipboardImages;

    //index lines that did not fit the memory budget and never reached the summarizer, counted so CompactedLine can report them
    private int _lastSuppressionUnfed;

    //this compaction's memory bank outcome, written onto the session lead and reset at the top of every compact
    private Gatto.Core.Memory.MemoryBankOutcome? _lastCompactOutcome;

    //the last BankMemory failure line, kept per attempt so the rotation can replay it after the reset erases the row
    private string? _lastBankFailure;

    //server-reported context truth for the auto-compact threshold and the summarizer ratio, cleared on every session rotation or model change
    private readonly ContextUsageState _usageState = new();

    //the context-fit warning latch, cleared when the estimate drops back under the armed model's budget so a new crossing warns again
    private bool _contextFitWarned;

    //the mismatch id the last serving warning named, so a new server still speaks instead of a bool going quiet
    private string? _servingSaid;

    //whether the serving model is one the user can name with /model, set from the same probe answer as serving
    private bool servingOnTheShelf;


    //the model the server has loaded when it disagrees with the armed model, null on agreement or an unanswered probe
    private string? serving = launchServing?.Mismatch;

    //the window the server actually allocated (null when the probe could not answer), read as min(declared, probed) by the fit warning
    private int? probedNCtx = launchServing?.NCtx;

    //whether the armed model's reasoning switch is a toggle, kept as a field because /model can change the shape in another method
    private bool thinkingToggle = thinkingIsToggle;

    //whether the armed model has no reasoning switch at all, bare /effort reads it before offering a picker or a level
    private bool thinkingUnavailable = thinkingIsUnavailable;

    //test seam for the armed model id, the same field a /model or /role switch mutates
    internal string ModelNameForTest => modelName;

    //test seam for the serving mismatch id, null when the armed model and the server's loaded weights agree
    internal string? ServingForTest => serving;

    //test seam for the live conversation, so a test can check the rebuilt system prompt a compact adopted
    internal Conversation ConversationForTest => convo;

    private const double OfferFraction = 0.85;

    //the 85% offer line, a function because the next step it names depends on the auto-compact setting
    public static string CompactOfferLineFor(double? autoCompactAt) =>
        autoCompactAt is double t
            ? $"context 85% full — gatto auto-compacts at {(int)Math.Round(t * 100)}%; /compact now if you would rather pick the moment"
            : "context 85% full — /compact summarizes this session and continues fresh (auto-compact is off)";

    //a bare Enter with this many messages already queued beeps and puts the text back in the composer
    public const int QueueCap = 10;

    //how often the resize poller reads the geometry, fast enough that a mid-turn resize cannot leave stale pixels in the content
    public const int ResizePollMs = 200;

    //the usage line a bare /remember prints, no turn is dispatched
    internal const string RememberUsage =
        "usage: /remember <text> — bank a note to project memory, in the model's own words";

    //prompt sugar that sends the text verbatim in a turn asking the model to bank it with memory_write, so no new write path exists
    internal static string RememberPrompt(string text) =>
        "Bank the following to project memory with memory_write, choosing a slug for it (reuse an " +
        "existing slug if this revises a fact already there), stated compactly in your own words: " + text;

    //the shared /role body, an empty arg is a usage error and the caller must keep its own roleName and modelName in sync
    public static RoleSwitchResult ApplyRoleSwitch(Conversation convo, Func<string, RoleSwitchResult> switchRole, string arg)
    {
        if (arg.Length == 0) return new RoleSwitchResult(false, "", null, null, "usage: /role <name>");
        var result = switchRole(arg);
        if (result.Success) { convo.ReplaceSystem(result.SystemText!, result.Baseline); convo.SetGattoRole(result.RoleName); }
        return result;
    }

    //true when a budget is enforced, the estimate is at or above 85% of it, and the offer has not already fired
    public static bool ShouldOfferCompact(int estimateTokens, int? budgetTokens, bool alreadyOffered) =>
        !alreadyOffered && budgetTokens is int b && b > 0 && estimateTokens >= b * OfferFraction;

    //says that another model is serving, once per transition and before the fit warning, naming no cause
    private void MaybeSaySomethingElseIsServing(ITurnObserver renderer)
    {
        if (serving is not { Length: > 0 } running) { _servingSaid = null; return; }
        if (string.Equals(running, _servingSaid, StringComparison.Ordinal)) return;

        //the /model remedy appears only when the running id names a shelf model, a file stem that no shelf row claims is dropped
        renderer.OnWarning($"the server is running {running} now, this session is on {modelName}"
            + (servingOnTheShelf ? $" — /model {running} to use it" : ""));
        _servingSaid = running;
    }

    //true when the estimate is strictly over the armed model's window and this crossing has not been reported
    public static bool ShouldWarnContextFit(int estimateTokens, int? budgetTokens, bool alreadyWarned) =>
        !alreadyWarned && budgetTokens is int b && b > 0 && estimateTokens > b;

    //the last assistant message with text and the nearest user text before it, null when the model has not answered in text
    public static (string User, string Assistant)? LastTextExchange(Conversation convo)
    {
        var msgs = convo.Messages;
        var aIdx = -1;
        for (var i = msgs.Count - 1; i >= 0; i--)
            if (msgs[i].Role == "assistant" && msgs[i].Content is { Length: > 0 }) { aIdx = i; break; }
        if (aIdx < 0) return null;
        for (var i = aIdx - 1; i >= 0; i--)
            if (msgs[i].Role == "user" && msgs[i].Content is { Length: > 0 } u)
                return (u, msgs[aIdx].Content!);
        return null;
    }

    //the shared /compact body, and a null summary leaves the conversation and its session file untouched
    public static async Task<Conversation?> CompactAsync(
        Compactor compactor, Conversation convo, SessionStore? sessions,
        Func<ComposedSystem> recomposeSystem, ITurnObserver observer, CancellationToken ct,
        int? contextBudget = null, Action<string>? onSummary = null,
        Action<MemoryPiggyback.Extraction>? bankCandidates = null, string? doNotRepeat = null)
    {
        var oldPath = sessions?.CurrentPath;          //read before StartNew, which clears the path
        var lastExchange = LastTextExchange(convo);
        var rawSummary = await compactor.SummarizeAsync(
            convo, observer, ct, windowTokens: contextBudget, doNotRepeat: doNotRepeat);
        if (rawSummary is null)
        {
            observer.OnWarning("compact failed — session unchanged");
            return null;                              //null tells the caller to abort, the old session stays live
        }
        //project memory banks here because the user chose this boundary, and the stripped text is what the rest of the method sees
        var extraction = MemoryPiggyback.Extract(rawSummary);
        var summary = extraction.StrippedSummary;
        if (extraction.Candidates.Count > 0 || extraction.Dropped > 0)
            bankCandidates?.Invoke(extraction);
        //the session_summary hook fires here with no extra model call, before the rotation so the caller emits on the current conversation
        onSummary?.Invoke(summary);
        //the compact block stays final, a later block would be swallowed into the resume graft --continue builds
        var composed = recomposeSystem();             //a fresh read of the context files on disk, so the prompt matches what is there now
        var systemText = composed.Text + "\n\n" + Compactor.BuildContext(summary, oldPath ?? "(unsaved session)", lastExchange);
        sessions?.StartNew();                         //later saves open a new file, so the old transcript is left alone
        var fresh = new Conversation(systemText, baseline: composed.Baseline);
        sessions?.Save(fresh);
        return fresh;
    }

    //runs CompactAsync and, on success, adopts the fresh conversation and lets the 85% offer fire again
    internal async Task<bool> HandleCompactAsync(ITurnObserver renderer, CancellationToken ct, CtxState? ctx = null)
    {
        _lastCompactBanked = (0, 0);   //reset per compact, so a later confirmation cannot repeat an earlier count
        _lastCompactBeforePercent = ctx?.Percent;   //the percent the footer showed, read before anything here mutates
        _lastCompactImagesDropped = convo.Messages.Sum(m => m.Images?.Count ?? 0);   //counted before the rebuild, when the images are still in history
        _lastCompactOutcome = null;    //reset per compact, like the counts above
        _lastBankFailure = null;       //reset per compact, so a failure from an earlier one cannot leak
        _lastSuppressionUnfed = 0;     //how many index lines were dropped for budget, set below and reset per compact

        //the index is composed from disk because the cached system message misses facts written mid-session, and Compose is total so no try/catch is needed
        string? doNotRepeat = null;
        if (piggyback is not null)
        {
            var index = piggyback.ComposeIndex();
            doNotRepeat = Compactor.SuppressionAddendum(index);
            _lastSuppressionUnfed = index.TruncatedLines;
        }
        //build the Compactor here, /role can change modelName and a captured one would keep summarizing with the stale launch model
        string? capturedSummary = null;
        Conversation? fresh;
        SetReasoningPassthrough(renderer, true);   //the dimmed summary streams as reasoning, so the preview filter must pass it through
        try
        {
            fresh = await CompactAsync(new Compactor(client, modelName), convo, sessions, recomposeSystem, renderer, ct,
                contextBudget: contextBudget, onSummary: s => capturedSummary = s,
                doNotRepeat: doNotRepeat,
                bankCandidates: ex =>
                {
                    var banked = BankMemory(ex.Candidates, renderer).Banked;
                    _lastCompactBanked = (banked, ex.Dropped);
                    //recorded even when nothing banked, a zero with heading_found:true is the case that matters
                    _lastCompactOutcome = new MemoryBankOutcome(
                        ex.HeadingFound, ex.Candidates.Count, banked, ex.Dropped);
                });
        }
        finally { SetReasoningPassthrough(renderer, false); }
        if (fresh is null) return false;
        _lastCompactSummary = capturedSummary;   //the rich loop shows it as the new session's lead
        //the session_summary hook fires here with the summary just produced and no extra model call, before convo rotates
        if (capturedSummary is not null && hooks is not null)
            await hooks.EmitSessionSummaryAsync(new HookPayload(
                Summary: new SummaryInfo(capturedSummary, DateTimeOffset.Now, modelName, GistOfConversation())));
        convo = fresh;
        _offered = false;   //a fresh session may cross 85% again
        resetGrounding?.Invoke();   //a fresh session has no checked restatement, so grounding must be re-armed
        _usageState.Clear();   //the old reading describes a conversation that no longer exists
        ctx?.RecordUsedTokens(null);   //clears the context figure too, there is none on the plain path
        return true;
    }

    //the auto-compact body, it strips the memory scaffolding instead of banking it and leaves everything untouched on a null summary
    internal async Task<CompactionResult?> AutoCompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct)
    {
        var oldPath = sessions?.CurrentPath;                 //read before StartNew, which clears the path
        SetReasoningPassthrough(observer, true);             //the dimmed summary is not reasoning
        string? summary;
        try
        {
            summary = await new Compactor(client, modelName).SummarizeAsync(
                convo, observer, ct, windowTokens: contextBudget, ratio: _usageState.Ratio, midTurn: true);
        }
        finally { SetReasoningPassthrough(observer, false); }
        if (summary is null) return null;                    //nothing is touched, the summarizer call was the only fallible work

        //strip the memory section here, a mid-turn compaction never banks and its scaffolding must stay out of the rebuilt prefix
        summary = MemoryPiggyback.Extract(summary).StrippedSummary;

        //all fallible work done, adopt atomically from here
        sessions?.StartNew();                                //later saves open a new file, so the old transcript is left alone
        //the compact block must stay the final append, --continue slices from the last ContextMarker to the end when it grafts a session
        var composed = recomposeSystem();                    //a fresh read of the context files on disk, so the prompt matches what is there now
        var systemText = composed.Text + "\n\n" + Compactor.BuildContext(summary, oldPath ?? "(unsaved session)", lastExchange: null);
        _lastCompactSummary = summary;                        //the rich loop shows it as the new session's lead, as after /compact
        _offered = false;                                     //a fresh session may cross 85% again
        resetGrounding?.Invoke();                             //a fresh session has no checked restatement, so grounding must be re-armed
        _usageState.Clear();                                  //cleared so a re-fire needs fresh over-threshold evidence
        //the continuation keeps this turn's images, they are the material of the task being continued rather than history that becomes text
        var carried = convo.Messages.LastOrDefault(m => m.Role == "user")?.Images;
        //the dropped count is the total minus what the continuation kept, so the line reports what actually happened
        var totalImages = convo.Messages.Sum(m => m.Images?.Count ?? 0);
        var carriedCount = carried?.Count ?? 0;
        var droppedImages = totalImages - carriedCount;
        if (droppedImages > 0 || carriedCount > 0)
            observer.OnWarning(string.Join($" {_glyphs.Dot} ", new[]
            {
                droppedImages > 0 ? $"{droppedImages} image{(droppedImages == 1 ? "" : "s")} dropped" : null,
                carriedCount > 0 ? $"{carriedCount} carried forward" : null,
            }.Where(x => x is not null)));
        var messages = new[]
        {
            new ChatMessage("user", Compactor.BuildContinuationPrompt(currentUserPrompt), Images: carried),
        };
        return new CompactionResult(systemText, messages, composed.Baseline);    //no session_summary hook fires for a mid-turn compaction, the user did not choose this boundary
    }

    //adapts AutoCompactAsync to the ICompactionHandler seam, nested so it can reach the enclosing Repl's fields
    private sealed class AutoCompactionHandler(Repl repl) : ICompactionHandler
    {
        public Task<CompactionResult?> CompactAsync(
            CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct)
            => repl.AutoCompactAsync(reason, currentUserPrompt, observer, ct);
    }

    //emits the 85% offer after a turn when the crossing was seen, the footer's percent is trusted over the estimate
    private void MaybeOfferCompact(ITurnObserver renderer, CtxState? ctx = null)
    {
        var crossed = ctx?.Percent is int p ? p >= 85
            : ShouldOfferCompact(ContextBudget.Estimate(convo.Messages), contextBudget, _offered);
        if (crossed && !_offered)
        {
            renderer.OnWarning(CompactOfferLineFor(loop.ArmedAutoCompactAt));
            _offered = true;
        }
    }

    //warns when the conversation is already over the armed model's window, and a turn over budget is still sent
    private void MaybeWarnContextFit(ITurnObserver renderer)
    {
        //the binding window is the smaller of the declared and the probed context, either one alone when only it is known
        int? effective = (contextBudget, probedNCtx) switch
        {
            (int declared, int probed) => Math.Min(declared, probed),
            (int declared, null) => declared,
            (null, int probed) => probed,
            _ => null,
        };

        var estimate = ContextBudget.Estimate(convo.Messages);
        if (ShouldWarnContextFit(estimate, effective, _contextFitWarned))
        {
            //name the server's window when it is the smaller one, the model's declared size would be a lie here
            var holds = probedNCtx is int p && (contextBudget is not int d || p < d)
                ? $"the server's actual window is {effective}"
                : $"{modelName} holds {effective}";
            renderer.OnWarning(
                $"history is ~{estimate / 1000}k tokens; {holds} — run /compact before your next message");
            _contextFitWarned = true;
        }
        else if (effective is int b && estimate <= b)
        {
            _contextFitWarned = false;   //back under budget, so a future crossing may warn again
        }
    }

    //probes the server once per turn before the request goes, and assigns the result even when it is null so a stale chip clears
    private void RefreshServingBeforeTurn(ITurnObserver renderer)
    {
        if (probeServing is not null)
        {
            var probe = probeServing();
            serving = probe.Mismatch;
            servingOnTheShelf = probe.MismatchIsOnTheShelf;
            probedNCtx = probe.NCtx;
        }
        MaybeSaySomethingElseIsServing(renderer);
        MaybeWarnContextFit(renderer);
    }

    //the line the /auto handlers print after the toggle, null when the role never armed checkpoints and there is nothing to toggle

    //the lines below are bare sentences, no parentheses, the rich loop commits each as a warning row
    internal static string AutoLine(bool? checkpointsEnabledAfterToggle) => checkpointsEnabledAfterToggle switch
    {
        null => "no checkpoints armed for this role — /auto has no effect",
        true => "checkpoints on — git commit pauses for review",
        false => "auto mode on — git commit checkpoints are skipped for this session",
    };

    //the state line /wild prints, plain text with no combining marks so the width math stays exact

    //matches a trimmed line against a bare slash command or that command plus an argument, both loops parse every command through here
    internal static bool TryMatchCommand(string trimmed, string name, out string arg)
    {
        if (trimmed == name)
        {
            arg = "";
            return true;
        }
        if (trimmed.Length > name.Length && trimmed[name.Length] == ' '
            && trimmed.StartsWith(name, StringComparison.Ordinal))
        {
            arg = trimmed[name.Length..].Trim();
            return true;
        }
        arg = "";
        return false;
    }

    internal static string WildLine(bool on, Gatto.Terminal.GlyphSet? glyphs) => on
        ? $"wild mode on — permission prompts and checkpoints are OFF for this session  {(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Wild}"
        : "wild mode off — permission prompts and checkpoints are back on";

    internal const string WildUsage =
        "usage: /wild [always|never] — toggle wild mode; always/never also persists for this project";

    //shared /wild dispatch, the persistence variants return two lines and the rich caller commits one row per line

    //no sanitize guard here, every string it returns is a compile-time literal, add one if it ever interpolates text gatto did not author
    private string HandleWild(string arg)
    {
        if (wild is null) return "wild mode is unavailable in this session";
        switch (arg)
        {
            case "":
                wild.On = !wild.On;
                return WildLine(wild.On, _glyphs);
            case "always":
                wild.On = true;
                persistWild?.Invoke(true);
                return WildLine(true, _glyphs) + "\npersisted: \"wild\": true in .gatto\\permissions.json — /wild never removes it";
            case "never":
                wild.On = false;
                persistWild?.Invoke(false);
                return WildLine(false, _glyphs) + "\npersistence removed from .gatto\\permissions.json";
            default:
                return WildUsage;
        }
    }

    //the prefill's denominator, the chars÷4 estimate lifted by the ratio the server measured
    internal static long ScaledPromptEstimate(int estimate, double ratio) =>
        estimate <= 0 ? 0 : (long)Math.Round(estimate * Math.Max(1.0, ratio));

    internal const string ToolsUnavailable = "the tool list is unavailable in this session";

    //every tool gatto advertises this turn with the extension that armed it, taken from the harness registration

    //sanitizing happens inside TableLayout before it measures, so nothing stripped can push the columns out of line

    //two columns with the origin inline in parentheses, a third column would strand it far from the row it belongs to

    //rows group by origin, built-ins first then extensions ordinal, with a policy line after that extension's last tool
    internal static TableSpec ToolsSpec(
        IReadOnlyList<ToolInfo> tools, IReadOnlyList<(string Extension, string Line)> policy,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        var prefix = PolicyPrefixOf(glyphs ?? Gatto.Terminal.GlyphSet.Unicode);
        var ordered = tools
            .OrderBy(t => t.Origin is null ? 0 : 1)
            .ThenBy(t => t.Origin ?? "", StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            rows.Add(new[]
            {
                t.Name,
                FirstLine(t.Description) + (t.Origin is null ? "" : "  (" + t.Origin + ")"),
            });

            //the last row of this origin's group, the next row belongs to another extension or there is none
            if (t.Origin is null) continue;
            var lastOfGroup = i + 1 == ordered.Count
                || !string.Equals(ordered[i + 1].Origin, t.Origin, StringComparison.Ordinal);
            if (!lastOfGroup) continue;
            foreach (var (_, line) in policy.Where(p => string.Equals(p.Extension, t.Origin, StringComparison.Ordinal)))
                rows.Add(new[] { "", prefix + line });
        }

        //a policy line for an extension that armed no tool, it joins no group and no built-in can be keyed to it
        var armed = new HashSet<string>(
            ordered.Where(t => t.Origin is not null).Select(t => t.Origin!), StringComparer.Ordinal);
        foreach (var (ext, line) in policy.Where(p => !armed.Contains(p.Extension))
                                          .OrderBy(p => p.Extension, StringComparer.Ordinal))
            rows.Add(new[] { "", prefix + line + "  (" + ext + ")" });

        return new(new[] { "tool", "description" }, new[] { ColumnAlign.Left, ColumnAlign.Left }, rows);
    }

    //the arrow and the capital P separate the policy row from the description above it, TableSpec has no style channel

    //a function of the glyph set, since a const cannot read one, and it is a render-only prefix that nothing compares against
    internal static string PolicyPrefixOf(Gatto.Terminal.GlyphSet g) => $"{g.Continue} Policy: ";

    //the policy rows both /tools sites read, one method so the two renders cannot differ on what they were handed
    private IReadOnlyList<(string Extension, string Line)> PolicyRows() =>
        listPolicy?.Invoke() ?? Array.Empty<(string, string)>();

    internal static string ToolsHeadline(int count) =>
        count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (count == 1 ? " tool armed this session" : " tools armed this session");

    //the plain loop's projection of the same spec, borderless, so both loops render through one core

    //the empty check reads both lists, an extension with policy and no tools still exists, while the headline counts tools only
    internal static string RenderToolsListFor(
        IReadOnlyList<ToolInfo> tools, IReadOnlyList<(string Extension, string Line)> policy,
        Theme theme, int width, Gatto.Terminal.GlyphSet? glyphs)
    {
        if (tools.Count == 0 && policy.Count == 0) return "no tools are armed this session";
        var sb = new StringBuilder(ToolsHeadline(tools.Count));
        foreach (var r in TableLayout.AlignedRows(ToolsSpec(tools, policy, glyphs), theme, width))
            sb.Append('\n').Append("  ").Append(r.Text);
        return sb.ToString();
    }

    //the plain path never paints, so a plain theme is the right one to measure with
    private string RenderToolsList()
    {
        if (listTools is null) return ToolsUnavailable;
        var width = 80;
        try { if (Console.WindowWidth > 20) width = Console.WindowWidth - 4; } catch { } //no console, so the 80 default stands
        return RenderToolsListFor(listTools(), PolicyRows(), new Theme(TermCaps.Plain), width, _glyphs);
    }

    //the first line of a description, with the keys of a colon-led list folded in so the row still reads as finished
    private static string FirstLine(string description)
    {
        var text = description.Replace("\r\n", "\n").Trim();
        var nl = text.IndexOf('\n');
        if (nl < 0) return text;

        var first = text[..nl].TrimEnd();
        if (!first.EndsWith(':')) return first;

        var keys = text[(nl + 1)..].Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Split(':')[0].Trim())
            .Where(k => k.Length > 0)
            .ToList();

        return keys.Count > 0 ? first + " " + string.Join(", ", keys) + "." : first[..^1];
    }

    internal const string PermissionsUsage =
        "usage: /permissions [all | revoke <n>] — list or revoke standing grants";

    //how many grants the bare listing shows before it offers /permissions all, and numbering still runs against the full set
    internal const int PermissionsListCap = 10;

    //the revoke hint, shown only when there is something to revoke, and it uses the same <n> the usage line does
    internal const string PermissionsHint =
        @"/permissions revoke <n> removes one — stored in .gatto\permissions.json";

    internal const string PermissionsUnavailable = "permissions are unavailable in this session";

    internal const string PermissionsEmpty =
        "no standing grants yet — every shell command, write, and extension tool call still prompts";

    private static string PermissionKindLabel(PermissionKind kind) => kind switch
    {
        PermissionKind.ShellPrefix => "shell",
        PermissionKind.WriteDir => "write",
        PermissionKind.Tool => "tool",
        _ => "?",
    };

    //the wild row, /wild and Shift+Tab for this session first, the per-project persistence second
    private static string WildRow(bool on) =>
        "wild mode: " + (on ? "on" : "off")
        + " — /wild or Shift+Tab toggles it for this session; /wild always|never persists it per project";

    //only the revoke confirmation uses this now, the listing measures its own kind column and a prose line has no column to align
    private static string FormatKindAndText(PermissionKind kind, string text) =>
        PermissionKindLabel(kind).PadRight(6) + text;

    //every grant numbered off the same ordering RevokeAt consumes, plus the wild row at the end

    //the grants as three columns with no headers, numbered off the same ordering RevokeAt consumes and padded to the full count
    internal static TableSpec PermissionsSpec(PermissionStore store, bool all)
    {
        var entries = store.ListEntries();
        var numberWidth = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var shown = all ? entries.Count : Math.Min(entries.Count, PermissionsListCap);
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < shown; i++)
            rows.Add(new[]
            {
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(numberWidth) + ".",
                PermissionKindLabel(entries[i].Kind),
                entries[i].Text,
            });
        return new TableSpec(
            new[] { "", "", "" },
            new[] { ColumnAlign.Left, ColumnAlign.Left, ColumnAlign.Left },
            rows);
    }

    private static string RenderPermissionsList(PermissionStore store, bool all = false,
        Gatto.Terminal.GlyphSet? glyphs = null)
    {
        var entries = store.ListEntries();
        var sb = new StringBuilder();
        if (entries.Count == 0)
        {
            sb.Append(PermissionsEmpty).Append('\n');
        }
        else
        {
            //the width comes from the full count, since a hidden entry's number is still a revoke key a user can type
            var shown = all ? entries.Count : Math.Min(entries.Count, PermissionsListCap);
            foreach (var r in TableLayout.AlignedRows(PermissionsSpec(store, all), new Theme(TermCaps.Plain), 200))
                sb.Append(r.Text).Append('\n');
            if (shown < entries.Count)
                sb.Append(($"{(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ellipsis} ")).Append((entries.Count - shown).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" more — /permissions all").Append('\n');
        }
        sb.Append(WildRow(store.Wild));
        if (entries.Count > 0) sb.Append('\n').Append(PermissionsHint);
        return sb.ToString();
    }

    //shared /permissions dispatch, a bad revoke argument is a friendly error that changes nothing
    private string HandlePermissions(string arg)
    {
        if (permissions is null) return PermissionsUnavailable;
        if (arg.Length == 0) return RenderPermissionsList(permissions, glyphs: _glyphs);
        if (string.Equals(arg, "all", StringComparison.Ordinal))
            return RenderPermissionsList(permissions, all: true, glyphs: _glyphs);

        var parts = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "revoke", StringComparison.Ordinal))
            return PermissionsUsage;

        var numberText = parts[1].Trim();
        if (!int.TryParse(numberText, out var n))
            //int.TryParse fails the same way for overflow and for garbage, so a numeric value that does not fit is answered as an out-of-range index
            return LooksNumeric(numberText)
                ? $"no grant numbered {numberText} — /permissions lists the current grants"
                : $"'{numberText}' is not a number — {PermissionsUsage}";

        var removed = permissions.RevokeAt(n - 1);   //the listing numbers grants from 1, the store counts from 0
        if (removed is null)
            return $"no grant numbered {n} — /permissions lists the current grants";

        return "revoked: " + FormatKindAndText(removed.Kind, removed.Text);
    }

    //an optionally signed run of ASCII digits, which is a different question from whether the value fits in an int
    private static bool LooksNumeric(string s)
    {
        var i = s.Length > 0 && (s[0] == '+' || s[0] == '-') ? 1 : 0;
        if (i >= s.Length) return false;
        for (; i < s.Length; i++)
            if (!char.IsAsciiDigit(s[i])) return false;
        return true;
    }

    //the /compact confirmation line, silent about zero banked and always reporting a non-zero drop count

    //the chars÷4 percent of the live conversation, the plain path's stand-in for the footer. it is only ever compared with itself
    private int? EstimatedPercent() =>
        contextBudget is int b && b > 0
            ? (int)Math.Round(100.0 * ContextBudget.Estimate(convo.Messages) / b)
            : null;

    internal string CompactedLine(CtxState? ctx = null)
    {
        //both figures come from the footer's own CtxState, sampled before and after, so the number told is the number shown
        var after = ctx?.Percent ?? EstimatedPercent();
        var line = (_lastCompactBeforePercent, after) switch
        {
            (int before, int a) => $"compacted {_glyphs.Dot} ctx {before}% {_glyphs.Right} {a}%",
            (_, int a) => $"compacted {_glyphs.Dot} ctx {a}%",
            _ => "compacted",
        };
        //a compacted history is text, so report how many images went with it
        if (_lastCompactImagesDropped > 0)
            line += $" {_glyphs.Dot} {_lastCompactImagesDropped} image{(_lastCompactImagesDropped == 1 ? "" : "s")} dropped";
        var (appended, dropped) = _lastCompactBanked;
        if (appended > 0) line += $" {_glyphs.Dot} {appended} memory candidates banked";
        if (dropped > 0)
            line += appended > 0
                ? $", {dropped} dropped over cap"
                : $" {_glyphs.Dot} {dropped} memory candidates dropped over cap";
        //a partial suppression is never silent, the line says how many facts were not fed
        if (_lastSuppressionUnfed > 0)
            line += $" {_glyphs.Dot} {Plural.Of(_lastSuppressionUnfed, "fact")} over budget not fed to the summarizer";
        return line;
    }

    //a failed bank warns and reports zero instead of taking down the compact boundary it hangs off
    private (int Banked, int Skipped) BankMemory(IReadOnlyList<string> candidates, ITurnObserver observer)
    {
        _lastBankFailure = null;   //cleared per attempt, so a rotation cannot repeat an earlier failure
        if (piggyback is null || candidates.Count == 0) return (0, 0);
        try { return piggyback.Bank(candidates); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            //recorded as well as warned, the observer channel alone loses this on a rotation
            _lastBankFailure = $"memory not updated — {ex.Message}";
            observer.OnWarning(_lastBankFailure);
            return (0, 0);
        }
    }

    //the line /role prints after a successful switch
    internal static string RoleLine(string role) => "role: " + role;

    //the line /model prints after a successful switch
    internal static string ModelLine(string model) => "model: " + model;

    internal const string NewConversationLine = "new conversation";

    internal const string RoleSwitchFailed = "role switch failed";
    internal const string EffortUnavailable = "effort switching is unavailable in this session";
    internal const string ModelSwitchFailed = "model switch failed";
    internal const string ModelSwitchUnavailable = "model switching is unavailable in this session";
    //the word a user types to add a model, one const so the dispatch and the help row agree
    internal const string AddModelArg = "add";

    //new answers exactly as add does, so the word is not read as a model id
    internal const string NewModelArg = "new";

    //the sentence that sends the user to another terminal and back, all three clauses are needed
    internal const string AddMovedText =
        "adding a model needs its own terminal — run gatto model there, then /model to switch to it";

    //the word that names a model's files, chosen so it cannot collide with a model id
    internal const string QuantArg = "quant";

    //the word that forgets a file, a const beside QuantArg so nothing spells it differently
    internal const string RemoveArg = "rm ";

    //the model's files and which one it uses, three separate answers so a session without a profile never claims a count
    internal static string QuantLines(Func<IReadOnlyList<(string Quant, bool Active)>>? files,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        if (files is null) return "this session has no model profile to read files from";

        var held = files();
        if (held.Count == 0) return "this model names no files";

        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        var rows = held.Select(f => f.Active ? $"  {g.Loaded} {f.Quant}   in use" : $"    {f.Quant}");
        var tail = held.Count == 1
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}/model quant <name> to use another one";
        return string.Join(Environment.NewLine, rows) + tail;
    }

    //bare /model shows the list and returns the picked id, null when cancelled, with Answered set only by the add flow's question
    private (string? Id, bool Answered) ShowModelPicker(ITurnObserver renderer)
    {
        if (listModels is null || picker is null) return (null, false);

        //d writes the default and the rows are rebuilt from what the write left, so the word cannot outlive a failed write
        int? openAt = null;
        while (true)
        {
            var items = listModels().ToList();
            if (items.Count == 0)
            {
                //send the user to gatto model, which is where a model is added now
                renderer.OnWarning("no models — run gatto model to add one");
                return (null, false);
            }

            //the d key is declared only when there is somewhere to write it, a picker cannot report a key it never got
            var outcome = picker.Pick("select a model", items,
                markLegend: setDefault is null ? modelMarkLegend : modelMarkLegend + "   d set as default",
                controlKeys: setDefault is null ? null : DefaultKey,
                openAt: openAt);

            if (outcome is PickOutcome.Control { Key: 'd', Cursor: var at })
            {
                //every row is a model, so the only guard is the cursor's range, the picker can name any row it was on
                if (at >= 0 && at < items.Count && setDefault!(items[at].Id) is { } problem)
                    renderer.OnWarning(problem);
                openAt = at;
                continue;
            }

            if (outcome.PickedRow is not { } chosen)
            {
                //a plain terminal could not select from the list it printed, so tell the user how to switch
                if (!IsRichTerminal()) renderer.OnWarning("type /model <name> to switch");
                return (null, false);
            }

            //a pick is always a switch, the add row is gone and gatto model is the way to add one
            return (items[chosen].Id, false);
        }
    }

    //the key that writes the default, allocated once rather than per repaint
    private static readonly char[] DefaultKey = ['d'];


    //the bare /effort line, the level in force plus the usage, and unavailable wins over every other answer
    internal static string EffortUsage(string? thinking, bool isToggle, bool unavailable) => unavailable
        ? "this model doesn't support changing the reasoning mode."
        : isToggle
            ? $"{thinking ?? "thinking off"}, usage: /effort none|on (this model has two reasoning options)"
            : thinking is null
                ? "effort: not configured, no thinking map; usage: /effort none|low|medium|high|xhigh|max"
                : $"effort: {thinking}, usage: /effort none|low|medium|high|xhigh|max";

    //s applies the row for this session only and writes no profile, Enter is the persisting pick
    private static readonly char[] EffortSessionOnlyKey = ['s'];

    //the /effort body both call sites share, a typed level always persists and the picker's s key is the only session-only way
    private string? HandleEffortCommand(string arg)
    {
        if (arg.Length == 0)
        {
            if (IsRichTerminal() && listEfforts is not null && picker is not null)
            {
                var items = listEfforts().ToList();
                if (items.Count > 0)
                {
                    var outcome = picker.Pick("select an effort level", items,
                        markLegend: "current   s session only", controlKeys: EffortSessionOnlyKey);
                    var picked = InterpretEffortPick(items, outcome);
                    return picked is { } p ? ApplyEffort(p.LevelArg, p.Persist) : null;
                }
            }
            return EffortUsage(thinkingName, thinkingToggle, thinkingUnavailable);
        }
        return ApplyEffort(arg, persist: true);
    }

    //a pure translation of the picker's answer, Enter persists, the s key does not and a cancelled pick applies nothing
    internal static (string LevelArg, bool Persist)? InterpretEffortPick(
        IReadOnlyList<PickerItem> items, PickOutcome outcome)
    {
        if (outcome is PickOutcome.Control { Key: 's', Cursor: var at })
            return at >= 0 && at < items.Count ? (items[at].Id, false) : null;
        return outcome.PickedRow is { } chosen ? (items[chosen].Id, true) : null;
    }

    //the one seam all three effort changes go through, so a typed level and a picked one cannot resolve differently
    private string ApplyEffort(string levelArg, bool persist)
    {
        if (setEffort is null) return EffortUnavailable;
        var result = setEffort(levelArg, persist);
        if (result.Success) thinkingName = result.LevelName;
        if (result.Success && result.Thinking is { } thinking) convo.SetThinking(thinking);
        return result.Message;
    }





    //the gist: control characters stripped, whitespace collapsed and the text cut to 80 cells
    public static string MakeGist(string raw)
    {
        var collapsed = new System.Text.StringBuilder();
        var pendingSpace = false;
        foreach (var r in raw.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r) || Rune.IsControl(r))
            {
                if (collapsed.Length > 0) pendingSpace = true;   //the collapsed text never starts with a space
            }
            else
            {
                if (pendingSpace) { collapsed.Append(' '); pendingSpace = false; }
                collapsed.Append(r.ToString());
            }
        }

        var outSb = new System.Text.StringBuilder();
        var cells = 0;
        foreach (var r in collapsed.ToString().EnumerateRunes())
        {
            var w = UnicodeWidth.OfRune(r);
            if (cells + w > 80) break;
            outSb.Append(r.ToString());
            cells += w;
        }
        return outSb.ToString();
    }

    //a short label for the conversation, its first user turn read from the top of the transcript
    private string GistOfConversation()
    {
        for (var i = 0; i < convo.Count; i++)
            if (convo.Messages[i].Role == "user" && convo.Messages[i].Content is { } c)
                return MakeGist(c);
        return "";
    }

    //set the passthrough around every summarizer call, otherwise the hide-reasoning filter swallows the summary, which isn't model reasoning
    private static void SetReasoningPassthrough(ITurnObserver observer, bool on)
    {
        switch (observer)
        {
            case StreamRenderer sr: sr.PassthroughReasoning = on; break;
            case PlainRenderer pr: pr.PassthroughReasoning = on; break;
        }
    }






    //roll back to the mark when only the user message was appended, a retry must not double it, else keep and persist the partial turn
    public static async Task RunTurnGuardedAsync(
        AgentLoop loop, Conversation convo, string input, ITurnObserver renderer,
        SessionStore? sessions, CancellationToken ct, TranscriptModel? model = null,
        IReadOnlyList<ImageRef>? images = null)
    {
        var mark = convo.Count;
        //armed for this turn only. the loop writes the tool request before the tool runs, so a hung tool leaves that call in the session file
        loop.OnRoundPersisted = sessions is null ? null : c => SaveSession(sessions, model, c);
        try
        {
            await loop.RunTurnAsync(convo, input, renderer, ct, images);
            SaveSession(sessions, model, convo);
        }
        catch (GattoConnectionException ex)
        {
            if (convo.Count <= mark + 1)
                convo.TruncateTo(mark);   //only the user message is dangling, safe to drop
            else
                SaveSession(sessions, model, convo);   //these tool exchanges already ran, keep them in the session file
            renderer.OnWarning(ex.Message);
        }
        catch (OperationCanceledException)
        {
            //a Ctrl+C during a reactive rescue arrives as OperationCanceledException, absorb it like a connection failure or the whole session goes down with exit 1
            if (convo.Count <= mark + 1)
                convo.TruncateTo(mark);
            else
                SaveSession(sessions, model, convo);
            renderer.OnWarning("turn cancelled");
        }
        finally
        {
            loop.OnRoundPersisted = null;
        }
    }

    //the only place a session StreamRenderer is built, so the model is always painter.Model and the tail closure holds the caller's live convo field

    //transient and never serialized (it would stack on every resume), rebuilt on launch and /new, built here so the wording can't drift

    //internal for the test seam, a guard on the banner must render the banner rather than the cat on its own
    internal static CommandEchoItem BannerItem(Theme theme, string roleName, string modelName, string? versionLine,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        var rows = new List<string>();
        //the cat comes from the glyph set too, so it is a call rather than a literal. a legacy console can't draw it, and a census can't see the call
        foreach (var catLine in Cats.For(roleName, g).Split('\n')) rows.Add(theme.Paint(catLine, Theme.Accent));
        rows.Add("");   //blank row between the cat art and the info lines
        //the caller builds the version tail, Repl cannot see Gatto.Cli where the dev-build definition lives, so GattoApp resolves it once and passes the string down
        var ver = versionLine ?? ("v" + Gatto.Core.GattoVersion.String
            + (Gatto.Core.GattoVersion.Build is { Length: > 0 } b ? " " + g.Dot + " build " + b : ""));
        var sep = " " + g.Dot + " ";
        rows.Add(theme.Paint("gatto", Theme.Accent, bold: true) + theme.Paint(sep, Theme.Dim)
            + theme.Paint(roleName, theme.RoleTint(roleName)) + theme.Paint(sep, Theme.Dim) + modelName
            + theme.Paint(sep, Theme.Dim) + theme.Paint(ver, Theme.Dim));
        //the up arrow is not in the glyph table yet, so it comes from g.UpKey. the hint names the key a keyboard has rather than a caret
        rows.Add(theme.Paint($"/quit exits{sep}/new resets{sep}Ctrl+C twice exits{sep}{g.UpKey} history",
            Theme.Dim));
        return new CommandEchoItem(rows, null) { Transient = true };
    }

    //replay carry then the held warnings after the reset, before the confirmation line (the reset discards rows raised before it)
    internal static void RotateDisplay(
        TranscriptModel model, StreamRenderer renderer, TranscriptItem? lead, string line,
        Gatto.Core.WarningSink.HeldWarnings? held, IReadOnlyList<string> carry)
    {
        model.Reset();
        if (lead is not null) model.Append(lead);
        foreach (var row in carry) renderer.CommitSystem(row);
        held?.Release();
        renderer.CommitSystem(line);
    }

    //the recompose runs inside this call, so the caller opens the hold before it and keeps ownership, one opened here would be too late
    internal static void RotateForNew(
        Func<ComposedSystem> recomposeSystem, Gatto.Core.WarningSink.HeldWarnings? held, Action<Conversation> adopt,
        TranscriptModel model, StreamRenderer renderer, TranscriptItem? lead, string line,
        IReadOnlyList<string> carry)
    {
        var composed = recomposeSystem();
        adopt(new Conversation(composed.Text, baseline: composed.Baseline));
        RotateDisplay(model, renderer, lead, line, held, carry);
    }

    internal static StreamRenderer BuildSessionRenderer(
        ChromePainter painter, ITermSurface surface, Theme theme, string roleName, ChromeTicker ticker,
        object gate, Action<Usage>? onUsage, ReasoningMode reasoning, Func<ChatMessage?> convoTail,
        GlyphSet? glyphs) =>
        new(painter, surface, theme, roleName, ticker, gate, onUsage, reasoning,
            model: painter.Model, convoTail: convoTail, glyphs: glyphs);

    //long enough to read a short sentence, and gone before it starts to look stuck
    private const int PasteHintSeconds = 4;

    //the live paste hint, or null once a transient one has expired, the image-count hint never expires
    private string? LivePasteHint()
    {
        if (_pasteHint is null) return null;
        if (_pasteHintUntil is DateTime until && DateTime.UtcNow >= until)
        {
            _pasteHint = null;
            _pasteHintUntil = null;
            return null;
        }
        return _pasteHint;
    }

    //the focus key hint when an item is focused, otherwise a one-shot teaser the first time a reasoning block collapsed, taught latches it
    internal static string? ComputeFocusHint(bool focused, bool anyReasoningCollapsed, ref bool taught,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        if (focused) return FocusKeys.HintOf(glyphs);
        if (!taught && anyReasoningCollapsed) { taught = true; return FocusKeys.HintOf(glyphs); }
        return null;
    }

    //with a display model write the full transcript so system and echo lines survive --continue, without one write the chat records, which still resume
    private static void SaveSession(SessionStore? sessions, TranscriptModel? model, Conversation convo)
    {
        if (sessions is null) return;
        if (model is not null) TranscriptStore.Save(model, convo, sessions);
        else sessions.Save(convo);
    }

    //whether this console draws the rich renderer, GattoApp picks its permission prompter with the same test, so the two call sites must not drift
    public static bool IsRichTerminal() => TermCaps.Detect().Rich;

    //repaint a restored transcript through the renderer channels, sending each tool call and its result as a pair, and change nothing
    public static void ReplayTranscript(
        IReadOnlyList<ChatMessage> messages, ITurnObserver observer, Action<string> echoUser)
    {
        var pending = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user" when m.Content is { Length: > 0 }:
                    echoUser(m.Content);
                    break;
                case "assistant":
                    if (m.Content is { Length: > 0 } text)
                        observer.OnTextDelta(text.EndsWith('\n') ? text : text + "\n");
                    if (m.ToolCalls is not null)
                        foreach (var c in m.ToolCalls) pending[c.Id] = c;
                    break;
                case "tool":
                    if (!pending.Remove(m.ToolCallId ?? "", out var call))
                    {
                        observer.OnToolResult(new ToolCall(m.ToolCallId ?? "", "(tool)", "{}"),
                            new ToolResult(m.Content ?? "", m.IsError, m.Gloss, m.View));
                        break;
                    }
                    observer.OnToolCallStart(call);
                    observer.OnToolResult(call, new ToolResult(m.Content ?? "", m.IsError, m.Gloss, m.View));
                    break;
            }
        }
    }

    //the configured threshold unless a project file vetoes it from the drive root down, null means not armed. pass configured: true, a project can only veto
    internal static double? ArmedAutoCompactAt(double? configured, string cwd) =>
        configured is not null
        && Gatto.Core.Home.ProjectFileConfig.EffectiveAutoCompact(cwd, configured: true)
            ? configured
            : null;

    public async Task<int> RunAsync(CancellationToken appCt)
    {
        //the one arming site, so -p and run_agent stay elision-only. a project file can only veto here, and whoever edits this line builds the behaviour driver
        var armedAt = ArmedAutoCompactAt(autoCompact, cwd);
        loop.EnableAutoCompact(armedAt is null ? null : new AutoCompactionHandler(this), _usageState, armedAt);

        var caps = TermCaps.Detect();
        //a rich TTY still takes the plain renderer when the user turns the alt screen off
        return caps.Rich && altScreen
            ? await RunRichAsync(caps, appCt)
            : await RunPlainAsync(appCt);
    }

    //plain loop
    private async Task<int> RunPlainAsync(CancellationToken appCt)
    {
        Console.WriteLine(Cats.For(roleName, glyphs: _glyphs));
        //the plain banner joins with Dot, the same separator as every other row
        var sep = $" {_glyphs.Dot} ";
        Console.WriteLine($"gatto{sep}{roleName}{sep}{modelName}{sep}v{Gatto.Core.GattoVersion.String}");
        Console.WriteLine("(/quit to exit, /new to reset, Ctrl+C aborts a turn)");
        //the same system line the rich path commits, printed literally here, plain has no renderer to hang the marker on
        if (memoryWarning is not null) Console.WriteLine($"{_glyphs.Sharp} " + memoryWarning);
        if (cloudNotice is not null) Console.WriteLine($"{_glyphs.Sharp} " + cloudNotice);

        var renderer = new PlainRenderer(reasoning, _glyphs);
        //the resumedFrom flag on its own still prints the marker, a compact-then-quit session has no turns to replay
        if (resumedFrom is not null)
        {
            Console.WriteLine($"(resumed from {resumedFrom})");
            if (resumeLine is not null) Console.WriteLine(resumeLine);
            ReplayTranscript(convo.Messages, renderer, u => Console.WriteLine("\n> " + u));
            Console.WriteLine();
        }
        //plain prints subagent progress as a bracketed line like its [tool] rows, and the sink is a one-slot holder filled once a renderer exists
        if (subagentProgressSink is { Length: > 0 })
            subagentProgressSink[0] = m => Console.WriteLine("[agent] " + m);
        CancellationTokenSource? turnCts = null;
        Console.CancelKeyPress += (_, e) =>
        {
            lock (_ctsLock)
            {
                if (turnCts is not null)
                {
                    e.Cancel = true;            //first Ctrl+C aborts the turn and keeps the REPL
                    try { turnCts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            //with no turn in flight Ctrl+C keeps its default, the process exits
        };

        while (!appCt.IsCancellationRequested)
        {
            Console.Write("\n> ");
            var line = Console.ReadLine();
            if (line is null) break;
            if (line.Trim().Length == 0) continue;

            //both faces call this gate before the command chain and before the send, so a mistyped command is refused in one place
            var gate = GateSlash(line);
            if (gate.Refused)
            {
                Console.WriteLine(gate.Refusal);
                continue;
            }
            line = gate.Text;
            var trimmedLine = gate.Literal ? "" : line.Trim();

            if (trimmedLine == "/quit") break;
            if (trimmedLine == "/help")
            {
                Console.WriteLine(SlashCommands.RenderPlain());
                continue;
            }
            if (trimmedLine == "/new")
            {
                var composed = recomposeSystem();              //recomposeSystem rereads the context files, so a reset picks up any edits
                convo = new Conversation(composed.Text, baseline: composed.Baseline);
                sessions?.StartNew();                  //the next save starts a new session file and leaves the old transcript alone
                _offered = false;                      //a fresh session, so the 85% offer can fire again
                resetGrounding?.Invoke();              //a fresh session re-arms grounding, so it restates again
                _usageState.Clear();                   //the old usage reading describes the conversation that just ended
                loop.ResetStripWarning();              //a fresh session reports its own first leak
                Console.WriteLine(NewConversationLine);
                continue;
            }
            if (trimmedLine == "/auto")
            {
                Console.WriteLine(AutoLine(toggleAuto()));
                continue;
            }
            if (TryMatchCommand(trimmedLine, "/wild", out var wildArg))
            {
                Console.WriteLine(HandleWild(wildArg));
                continue;
            }
            if (trimmedLine == "/tools")
            {
                //sanitize each row, a tool name and description can come from an extension script, which is not gatto's text
                foreach (var row in RenderToolsList().Split('\n'))
                    Console.WriteLine(TermText.Sanitize(row));
                continue;
            }
            if (TryMatchCommand(trimmedLine, "/permissions", out var permArg))
            {
                //sanitizing drops newlines, so split the rows first, and the listing re-emits stored grant text from model-authored arguments
                foreach (var l in HandlePermissions(permArg).Split('\n'))
                    Console.WriteLine(TermText.Sanitize(l));
                continue;
            }
            if (trimmedLine == "/compact")
            {
                lock (_ctsLock) { turnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt); }
                var ok = false;
                try { ok = await HandleCompactAsync(renderer, turnCts.Token); }
                finally
                {
                    lock (_ctsLock) { turnCts?.Dispose(); turnCts = null; }
                    Console.WriteLine();
                }
                if (ok) Console.WriteLine(CompactedLine());
                continue;
            }
            if (trimmedLine == "/role" || trimmedLine.StartsWith("/role ", StringComparison.Ordinal))
            {
                var arg = trimmedLine.Length > 5 ? trimmedLine[5..].Trim() : "";
                var result = ApplyRoleSwitch(convo, switchRole, arg);
                if (result.Success)
                {
                    roleName = result.RoleName;
                    modelName = result.ModelName!;
                    thinkingName = result.ThinkingName;   //null when the new role has no map, which hides the chip
                }
                Console.WriteLine(result.Success ? RoleLine(roleName) : result.Message);
                continue;
            }
            if (trimmedLine == "/model" || trimmedLine.StartsWith("/model ", StringComparison.Ordinal))
            {
                //the same body the rich dispatch runs, this site keeps its own sink and control-flow keyword, there is no chrome to refresh here
                if (HandleModelCommand(trimmedLine, renderer, Console.WriteLine, out var notice))
                {
                    Console.WriteLine(notice ?? ModelLine(modelName));
                    //the context-fit check runs at the switch itself, so the warning appears before the next send
                    MaybeWarnContextFit(renderer);
                }
                continue;
            }
            if (trimmedLine == "/effort" || trimmedLine.StartsWith("/effort ", StringComparison.Ordinal))
            {
                var arg = trimmedLine.Length > 7 ? trimmedLine[7..].Trim() : "";
                if (HandleEffortCommand(arg) is { } effortLine) Console.WriteLine(effortLine);
                continue;
            }
            if (TryMatchCommand(trimmedLine, "/remember", out var rememberArg))
            {
                if (rememberArg.Length == 0)
                {
                    Console.WriteLine(RememberUsage);
                    continue;
                }
                line = RememberPrompt(rememberArg);   //the prompt goes through the normal turn below, as if typed
            }
            if (trimmedLine == "/init") line = InitPrompt.Text;   //the prompt goes through the normal turn below, as if typed

            RefreshServingBeforeTurn(renderer);   //re-probe the server and check the context fit before the request is sent

            lock (_ctsLock)
            {
                turnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            }
            renderer.BeginTurn();   //reset the hide-reasoning state, a Ctrl+C mid-reasoning must not leave it set
            try
            {
                await RunTurnGuardedAsync(loop, convo, line, renderer, sessions, turnCts.Token);
            }
            finally
            {
                lock (_ctsLock)
                {
                    turnCts?.Dispose();
                    turnCts = null;
                }
                Console.WriteLine();
            }
            MaybeOfferCompact(renderer);
        }
        return 0;
    }

    //rich loop, the chrome is always the buffer suffix owned by ChromePainter, and each finished row goes to scrollback above it

    //internal so the harness can pass in a synthetic rich TermCaps, Detect() answers plain whenever stdout is redirected
    internal async Task<int> RunRichAsync(TermCaps caps, CancellationToken appCt)
    {
        //the pump starts here and only here, the composer needs it running continuously, and the fallback covers a directly built Repl
        var keys = pump ?? new InputPump(new KeyInputSource(new ConsoleKeySource()));
        keys.Start();
        var surface = termSurface ?? new ConsoleSurface();
        var theme = new Theme(caps, themeMode);
        var ctx = new CtxState { ContextWindow = configContext ?? await client.TryGetContextLengthAsync(appCt) };

        //every OnUsage feeds CtxState.RecordUsage and adds to the running up and down totals the status line draws
        long tokensUp = 0, tokensDown = 0;
        void OnUsageAccumulate(Usage u)
        {
            ctx.RecordUsage(u);
            //don't record the prompt tokens here, the same Usage already reaches _usageState through AgentLoop and the footer reads its figure from there
            tokensUp += u.PromptTokens;
            tokensDown += u.CompletionTokens;
        }

        //one lock for the session, the painter, ticker and renderer all serialize on it, and every live pixel goes through the painter under it
        var gate = new object();
        //the ChromePainter drives the ViewportCompositor and pins the chrome to the bottom, and StreamRenderer fills the transcript model
        var model = new TranscriptModel(roleName);
        var queue = new DispatchQueue(QueueCap);
        //the picker is retired, so the painter needs no queue reader and no pick sink
        var painter = new ChromePainter(surface, theme, gate, model, wheelLines,
            glyphs: _glyphs,
            terminalTitle: _terminalTitle,
            //a mouse composer gesture goes down the same channel as keys, this is the only caller of EnqueueComposerGesture and it runs under the gate
            onComposerSelect: (line, col, kind) => keys.EnqueueComposerGesture(new ComposerInput.Select(line, col, kind)))
            { RoleForTint = roleName };
        var scroll = painter.Scroll;
        var altScreen = painter.AltScreen;
        //the mouse-capture console mode, entered with the alt screen and restored on every path it goes away, null means no capture
        var inputMode = mouseEnabled
            ? new Gatto.Terminal.ConsoleInputMode(modeControl ?? new Gatto.Terminal.Win32ConsoleModeControl())
            : null;
        var ticker = new ChromeTicker(painter, gate, slotsReader);
        //the turn clock stops while a prompt is up, hooked at the pump where every prompt reads its key through PushFocus
        keys.SetModalWaitSink(open =>
        {
            if (open) { ticker.BeginUserWait(); deafWatch?.Arm(); }
            else { ticker.EndUserWait(); deafWatch?.Disarm(); }
        });
        var renderer = BuildSessionRenderer(painter, surface, theme, roleName, ticker, gate,
            OnUsageAccumulate, reasoning, () => convo.Messages.Count > 0 ? convo.Messages[^1] : null,
            _glyphs);
        //scroll keys move the viewport and the pump consumes them even while a modal prompt holds focus, each paint runs under the gate
        keys.SetScrollSink(sk =>
        {
            lock (gate)
            {
                var w = surface.Width;
                var h = painter.ViewportRows();   //transcript height above the pinned chrome, so Home and PageUp reach the banner
                switch (sk)
                {
                    case ScrollKey.PageUp: scroll.PageUp(w, h); break;
                    case ScrollKey.PageDown: scroll.PageDown(w, h); break;
                    case ScrollKey.Top: scroll.Home(w, h); break;
                    case ScrollKey.Bottom: scroll.End(); break;
                }
                painter.Repaint();
            }
        });
        //the focus sink registers below, after s.Refresh is set, it must call s.Refresh() so the footer hint follows focus at once
        var history = new History(Path.Combine(GattoHome.Resolve(), "history.txt"));
        var editor = new LineEditor(keys.Composer, history, () => surface.Width);
        //the count question is armed here where its dependencies live, and the standing answer is read once at launch
        var projectSettings = ProjectSettings.Load(Gatto.Core.Memory.MemoryDir.FindProjectRoot(cwd));
        _attachManyGranted = () => projectSettings.AttachManyGranted;
        _grantAttachMany = projectSettings.GrantAttachMany;
        _confirmAttach = (count, _) =>
        {
            var spec = ConfirmAttach.Spec(count);
            var outcome = ConfirmAttachForTest is { } forTest
                ? forTest(spec)
                : new SelectPrompt(surface, theme, new ConsoleKeySource(), keys, _chrome, _glyphs).Show(spec);
            return ConfirmAttach.Interpret(outcome);
        };
        //the two-press controller behind Esc Esc and Ctrl+C Ctrl+C, declared ahead of RefreshStatus because it threads its live hint onto the top rule
        var chords = new ArmedChord();
        //a toggle-capable model's footer reads thinking on or off, so take the effective toggle-ness from GattoApp rather than the capability alone
        var status = new StatusInfo(cwd, modelName, roleName, ctx,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Wild: wild?.On == true,
            Thinking: thinkingName, Serving: serving, ThinkingToggle: thinkingToggle, Cloud: cloud);
        var frame = new InputFrame(surface, theme, roleName, status, _glyphs);
        var focusTaught = false;   //the one-shot teaser for the first collapsed reasoning block, set once it has fired

        //feeds the ctx estimate and rebuilds the status frame, and the painter is retargeted here so it never composes against a stale one
        void RefreshStatus()
        {
            //the one context figure, the same expression the auto-compact checkpoint evaluates, and it must precede the estimate fallback
            ctx.RecordUsedTokens(ContextBudget.UsedTokens(convo, _usageState));
            ctx.RecordEstimate(ContextBudget.Estimate(convo.Messages), contextBudget);
            //the Alt+V hint appears while the clipboard holds an image, takes precedence over the focus hint, and the check is safe on every refresh
            var pasteHint = _clipboardImages?.Available() is not null
                and not Gatto.Terminal.ClipboardImageKind.None
                ? "Alt+V to paste the image on your clipboard"
                : null;
            var focusHint = pasteHint ?? ComputeFocusHint(painter.Focus.Focused is not null,
                painter.Model.Items.OfType<ReasoningItem>().Any(r => r.Collapsed), ref focusTaught, _glyphs);
            var usage = readUsage?.Invoke() ?? (null, null);
            status = status with
            {
                Model = modelName, Role = roleName, Branch = GitBranch.Read(cwd),
                TokensUp = tokensUp, TokensDown = tokensDown, Wild = wild?.On == true, Thinking = thinkingName,
                Serving = serving, ThinkingToggle = thinkingToggle, FocusHint = focusHint,
                //an expired arm yields null at repaint so the chord hint vanishes on its own, and the paste hint wins over it
                ChordHint = LivePasteHint() ?? chords.Hint,
                Quota = usage.Quota, Cost = usage.Cost,
            };
            frame = new InputFrame(surface, theme, roleName, status, _glyphs);
            //the retarget takes the gate too, the ticker repaints from its own thread while this runs on the dispatcher's
            lock (gate) { painter.Frame = frame; painter.RoleForTint = roleName; }
        }

        //boxed so every dispatch path sees the current renderer and frame, a loop holding a stale local would paint the old role's tint
        var s = new RichState
        {
            Surface = surface, Theme = theme, Glyphs = _glyphs, Renderer = renderer, Frame = frame,
            OnUsage = OnUsageAccumulate, Gate = gate, Painter = painter, Ticker = ticker, Ctx = ctx,
        };
        //bound through s.Renderer so a /role swap can't orphan it, the sink is a holder because the tool is built before any renderer exists
        if (subagentProgressSink is { Length: > 0 })
            subagentProgressSink[0] = m => s.Renderer.OnSubagentProgress(m);
        s.Refresh = () => { RefreshStatus(); s.Frame = frame; };
        s.Refresh();
        //stashed where this local and s can both reach it, idempotent and null when mouse is off
        s.RestoreMouseMode = () => inputMode?.Restore();

        //global like scroll, registered once after s.Refresh so the sink can call it, and the painter owns focus so a /role swap keeps it
        keys.SetFocusSink(fk =>
        {
            lock (gate)
            {
                var w = surface.Width;
                var h = painter.ViewportRows();
                switch (fk)
                {
                    case FocusKey.Prev: painter.Focus.Prev(w, h); break;
                    case FocusKey.Next: painter.Focus.Next(w, h); break;
                    case FocusKey.Toggle: painter.Focus.ToggleFocused(w, h); break;
                }
                s.Refresh();          //rebuild the status and frame so the focus hint follows at once, the gate is re-entrant
                painter.Repaint();    //the refresh above retargets the frame without painting, so paint here
            }
        });

        //the Shift+Tab key runs the same HandleWild("") the slash command does so the two can't drift, and a keystroke never writes permissions.json
        keys.SetWildSink(() =>
        {
            lock (gate)
            {
                //one CommitSystem call per line, a newline inside one call is stripped and two rows mash together
                foreach (var line in HandleWild("").Split('\n'))
                    s.Renderer.CommitSystem(line);
                s.Refresh();          //the footer's wild chip must reflect the flip
                painter.Repaint();
            }
        });

        //the one row a deaf reader prints, committed under the gate like every other row a background thread writes.
        deafWatch?.SetDeafSink(row =>
        {
            lock (gate)
            {
                s.Renderer.CommitSystem(row);
                painter.Repaint();
            }
        });

        //a failed copy leaves the selection alone, Win32Clipboard.TrySet never throws
        var clip = clipboard ?? new Gatto.Terminal.Win32Clipboard();
        void Copy()
        {
            lock (gate)
            {
                //pass the painted chrome rows every time so a composer or queue drag copies too, harmless when the live span is the transcript
                var text = painter.Selection.CopyText(surface.Width, theme, painter.ChromeRowInfo, _glyphs);
                if (text is { Length: > 0 }) clip.TrySet(text);
            }
        }
        //no wiring needed, the compositor reads the painter's SelectionController each paint and clears itself on reflow

        //mouse events are global and work under a modal, only a Press refreshes since each refresh reads the git branch, and every event repaints
        if (mouseEnabled)
            keys.SetMouseSink(me =>
            {
                lock (gate)
                {
                    painter.Mouse.Handle(me, surface.Width, painter.ViewportRows());
                    if (me.Kind == MouseKind.Press) s.Refresh();
                    painter.Repaint();
                }
                //the HasSelection flag is the one predicate that covers both the transcript and the chrome domains, don't test them separately
                if (copyOnSelect && me.Kind == MouseKind.Release && painter.Selection.HasSelection) Copy();
            });

        //registered unconditionally, Esc needs this path in a mouse: false session, and the copy arms are no-ops without a mouse
        keys.SetCancelSink(BuildCancelSink(painter, gate, () => s.TurnCts, Copy,
            () => s.ComposerEmpty?.Invoke() ?? true));

        //use the live read () => s.TurnCts here and in the cancel sink, the field is replaced at every turn boundary
        if (turnAbort is not null) turnAbort.Current = () => s.TurnCts;


        //a prompt draws as chrome above a dimmed composer from here on, the teardown nulls both handles again
        if (_chrome is not null) { _chrome.Painter = painter; _chrome.Renderer = renderer; }

        //exit hooks register before either entry so a crash there still restores, and from here every visible row is drawn by the compositor
        inputMode?.RegisterExitHooks();
        inputMode?.Enter();
        altScreen.RegisterExitHooks();
        //the tab title is composed from the same roleName the banner prints, so the two cannot drift
        altScreen.Enter(TerminalTitle.For(roleName));

        //the banner is the transcript's first item
        model.Append(BannerItem(theme, roleName, modelName, versionLine, _glyphs));

        //the memory-truncation notice, a system row right after the banner and above any resumed transcript
        if (memoryWarning is not null) renderer.CommitSystem(memoryWarning);
        if (cloudNotice is not null) renderer.CommitSystem(cloudNotice);

        //fire on resumedFrom alone, a compact-then-quit session has no chat turns even though its context was loaded
        if (resumedFrom is not null)
        {
            //re-seed the live convo with the rebuilt instances so the model's anchors point at the same messages the next save writes
            if (resumedPath is { } priorPath)
            {
                var (rebuiltConvo, rebuiltModel) =
                    TranscriptStore.Rebuild(File.ReadLines(priorPath), theme, roleName, _glyphs);
                var sysCount = convo.Count > 0 && convo.Messages[0].Role == "system" ? 1 : 0;
                convo.TruncateTo(sysCount);   //drop the chat-only instances GattoApp loaded and keep the fresh system message
                //keep the fresh system message, exclude the rebuilt record, and don't simplify BuildLines to a reference check, a system-role event still leads
                convo.Load(rebuiltConvo.Messages.Where(m => m.Role != "system"));   //the same instances the model's anchors point at
                foreach (var item in rebuiltModel.Items) model.Append(item);
            }
            else   //resumedFrom without a resumedPath, so replay the chat records only
            {
                ReplayTranscript(convo.Messages, renderer, u => renderer.CommitUser(u.Split('\n')));
                renderer.EndTurn();
            }
            //the divider goes after the restored transcript so it sits just above the composer, and being transient it never serializes
            model.Append(new CommandEchoItem(new[] { theme.Paint($"— resumed from {resumedFrom} —", Theme.Dim) }, null) { Transient = true });
            if (resumeLine is not null)
                model.Append(new CommandEchoItem(Array.Empty<string>(), null) { Transient = true, DimText = resumeLine });
        }
        painter.Repaint();   //first paint, so the banner and empty composer are up before the loop starts

        //on the mouse-on path this handler is dormant for Ctrl+C and only catches Ctrl+Break, so don't delete it, the plain path still needs it
        var lastAtRestCtrlC = 0L;
        Console.CancelKeyPress += (_, e) =>
        {
            lock (_ctsLock)
            {
                e.Cancel = true;
                var now = Environment.TickCount64;
                switch (DecideCtrlC(s.TurnCts is not null, now, lastAtRestCtrlC))
                {
                    case CtrlCAction.AbortTurn:
                        try { s.TurnCts!.Cancel(); } catch (ObjectDisposedException) { }
                        break;
                    case CtrlCAction.Quit:
                        s.CancelLoop?.Invoke();
                        break;
                    default:   //the HintExit rung, arm the 2s window and show the transient rule hint
                        lastAtRestCtrlC = now;                    //keep this, the next press reads it to decide a quit
                        chords.Fire(Chord.Quit, now);            //armed only for its hint string, the quit path is DecideCtrlC's
                        s.OnHintChanged?.Invoke();               //repaint the rule and schedule the expiry, the same machinery the mouse path uses
                        break;
                }
            }
        };

        return await RunLinearLoopAsync(s, editor, queue, chords, clip, appCt);
    }

    //a turn in flight aborts, at rest a single Ctrl+C only hints and a second within CtrlCDoubleTapMs quits
    internal const long CtrlCDoubleTapMs = 2000;
    internal enum CtrlCAction { AbortTurn, HintExit, Quit }
    internal static CtrlCAction DecideCtrlC(bool turnInFlight, long nowMs, long lastAtRestMs) =>
        turnInFlight ? CtrlCAction.AbortTurn
        : lastAtRestMs > 0 && nowMs - lastAtRestMs <= CtrlCDoubleTapMs ? CtrlCAction.Quit
        : CtrlCAction.HintExit;

    //the Esc rung clears a live selection, a non-empty composer passes through ahead of a turn abort. the Ctrl+C rung copies the selection and keeps it
    internal enum CancelAction { PassThrough, ClearSelection, AbortTurn, Copy }
    internal static CancelAction DecideCancel(ConsoleKey key, bool hasSelection, bool turnInFlight,
        bool composerEmpty) =>
        key == ConsoleKey.Escape
            ? (hasSelection ? CancelAction.ClearSelection
               : !composerEmpty ? CancelAction.PassThrough   //a composer with text passes through so the editor's Esc Esc can clear it, ahead of a turn abort
               : turnInFlight ? CancelAction.AbortTurn
               : CancelAction.PassThrough)                    //the ladder ends here, an empty composer's Esc drops nothing
            //the Ctrl+C rung copies a live selection and leaves it, else aborts a turn in flight, else passes through to the composer's quit chord
            : (hasSelection ? CancelAction.Copy
               : turnInFlight ? CancelAction.AbortTurn
               : CancelAction.PassThrough);

    //turnCts and composerEmpty are read live at each invocation so a /role swap or a keystroke sees the current state, registered unconditionally
    internal static Func<ConsoleKeyInfo, KeyDisposition> BuildCancelSink(
        ChromePainter painter,
        object gate, Func<CancellationTokenSource?> turnCts, Action copy, Func<bool> composerEmpty) => k =>
    {
        bool hasSel, composerBlank;
        lock (gate) { hasSel = painter.Selection.HasSelection; composerBlank = composerEmpty(); }
        switch (DecideCancel(k.Key, hasSel, turnCts() is not null, composerBlank))
        {
            case CancelAction.ClearSelection:
                lock (gate) { painter.Selection.Clear(); painter.Repaint(); }
                return KeyDisposition.Consumed;
            case CancelAction.AbortTurn:
                try { turnCts()?.Cancel(); } catch (ObjectDisposedException) { }
                return KeyDisposition.Consumed;
            case CancelAction.Copy:
                copy();
                return KeyDisposition.Consumed;
            default:
                return KeyDisposition.PassThrough;
        }
    };

    //clears the transcript and chrome selection under the same gate every mouse write takes, and does nothing once teardown has run
    internal static Action BuildComposerSelectionSetHook(ChromePainter painter, object gate, Func<bool> stopped) => () =>
    {
        lock (gate) { if (!stopped()) { painter.Selection.Clear(); painter.Repaint(); } }
    };

    //the insert alone writes a buffer nothing paints while the composer task is parked, so redraw, and do nothing after teardown
    internal static Action<string> BuildRestoreComposer(
        object gate, LineEditor editor, Action<EditorView> redraw, Func<bool> stopped) => text =>
    {
        lock (gate)
        {
            if (stopped()) return;
            editor.InsertAtCursor(text);
            redraw(editor.Snapshot());
        }
    };

    //the composer's own task feeds a FIFO the dispatcher consumes, and every paint takes the gate while editor.Read waits outside it
    private async Task<int> RunLinearLoopAsync(RichState s, LineEditor editor, DispatchQueue queue,
        ArmedChord chords, Gatto.Terminal.IClipboard clip, CancellationToken appCt)
    {
        var idleView = new EditorView(new List<string> { "" }, 0, 0);
        var lastView = idleView;         //written and read only under the gate, see RedrawLocked
        //reads lastView by reference so it sees the current composer contents, and it is set before the first dispatch
        s.ComposerEmpty = () => lastView.Lines.Count == 1 && lastView.Lines[0].Length == 0;
        var stopped = false;             //teardown latch, written and read only under the gate
        var degraded = false;            //renderer degrade latch, written and read only under the gate
        //the pump died, console gone or a dispatch that never recovered, read once at the exit epilogue so the session can say why it ended
        var pumpDied = false;
        Exception? fault = null;         //the first fault wins, from the composer task, the ticker or the resize poller

        //route diagnostics to the current renderer's system line, stderr would corrupt the alt screen, and drop them once stopped is set
        using var warnScope = warn?.Route(m => { lock (s.Gate) { if (!stopped) s.Renderer.OnWarning(m); } });

        //ends the session when the pump dies or Ctrl+C is pressed at rest, otherwise the dispatcher parks on the queue forever
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var loopCt = loopCts.Token;
        s.CancelLoop = () => { try { loopCts.Cancel(); } catch (ObjectDisposedException) { } };

        //caller holds the gate, which is the ordering for lastView, and every repaint is unconditional
        void RedrawLocked(EditorView v)
        {
            lastView = v;
            if (stopped) return;     //skip the repaint once teardown has run
            if (degraded)
            {
                //after a degrade Repaint() is a no-op, so echo the composer inline or the user types blind while Enter still sends
                EchoComposerInline(s.Surface, v, s.Glyphs);
                return;
            }
            s.Painter.State.Composer = v;
            s.Painter.Repaint();
        }

        //a degrade ends the alt screen but not the session, so give the shell's mouse mode back now, and the call is idempotent
        s.OnDegraded = () => { degraded = true; s.RestoreMouseMode?.Invoke(); };
        s.Renderer.OnDegraded = s.OnDegraded;
        //the Alt+V key pastes an image and names Ctrl+V when there is nothing to paste, Ctrl+V itself belongs to the terminal
        var clipboardImages = new Win32ClipboardImageNative();
        _clipboardImages = clipboardImages;   //the footer's Alt+V hint reads this field too
        //this runs on the editor thread mid-turn, so never read convo here, dedupe and caps happen at submit
        editor.OnPasteImage = () =>
        {
            var r = ImagePaste.Paste(clipboardImages, Path.Combine(GattoHome.Resolve(), "blobs"));
            lock (s.Gate)
            {
                //nothing stashed and nothing painted once the session is stopped
                if (stopped) return;
                foreach (var c in r.Candidates)
                {
                    var n = ++_pastedCount;
                    //the display name is the placeholder the user sees, one naming scheme for the picture
                    _pendingPaste[n] = c.DisplayName is null
                        ? c with { DisplayName = ImageAttach.Placeholder(n) }
                        : c;
                    //the placeholder in the text is the attachment, where it can be seen, moved and deleted
                    editor.InsertAtCursor(ImageAttach.Placeholder(n));
                }
                //feedback goes on the composer header, and nothing is said on success since the placeholders already show what is attached
                if (r.Candidates.Count == 0)
                {
                    _pasteHint = r.Notice;
                    _pasteHintUntil = DateTime.UtcNow.AddSeconds(PasteHintSeconds);
                    //the frame needs a wake-up when the hint expires, RefreshStatus only runs on events, a timer that repaints is enough
                    _armHintExpiry?.Invoke(PasteHintSeconds * 1000 + 100);
                }
                s.Refresh();
                s.Painter.Repaint();
            }
        };

        //a tool result arriving moves the context figure, so the footer repaints through the loop's existing shared repaint
        s.OnContextChanged = RepaintChrome;
        s.Renderer.OnContextChanged = s.OnContextChanged;

        void Redraw(EditorView v) { lock (s.Gate) RedrawLocked(v); }
        void RedrawLast() { lock (s.Gate) RedrawLocked(lastView); }

        //armed before the first dispatch, where the editor and Redraw both live, so the first refusal can restore the text
        _restoreComposer = BuildRestoreComposer(s.Gate, editor, Redraw, () => stopped);

        //the repaint any thread can call, take the gate, rebuild the frame, repaint, and skip it once stopped
        void RepaintChrome() { lock (s.Gate) { if (!stopped) { s.Refresh(); s.Painter.Repaint(); } } }
        onUsageChanged?.Invoke(RepaintChrome);
        //one reusable timer re-armed on each press so a hint vanishes by itself after the window, disposed at teardown
        var hintTimer = new System.Threading.Timer(_ => RepaintChrome(), null, Timeout.Infinite, Timeout.Infinite);
        //the timer only wakes the frame, and each hint's own staleness check drops it when RefreshStatus runs
        _armHintExpiry = ms => hintTimer.Change(ms, Timeout.Infinite);
        void OnHintChanged()
        {
            RepaintChrome();
            //re-arm only when a press left a chord armed, and the extra margin makes the shot fire after expiry
            if (chords.Armed != Chord.None)
                hintTimer.Change(CtrlCDoubleTapMs + 50, Timeout.Infinite);
        }
        //exposed through s so the mouse-off Ctrl+C handler on another thread can show the same hint
        s.OnHintChanged = OnHintChanged;

        //the one pusher, called when the queue changes, so the chrome and the queue cannot part
        void PublishQueueLocked() => s.Painter.State.QueueTexts = queue.Texts();

        //one fault slot shared with the composer and resize tasks, the first fault wins, and the cancel is punted off the timer thread
        void OnChromeFault(Exception ex)
        {
            Interlocked.CompareExchange(ref fault, ex, null);
            _ = Task.Run(() => { try { loopCts.Cancel(); } catch (Exception) { } }, CancellationToken.None);
        }
        s.Ticker.OnFault = OnChromeFault;

        //resize poller, nothing else notices a resize while the composer parks on the pump, so it repaints once the drag settles
        var resize = new ResizeWatch(s.Surface.Width, s.Surface.Height);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!loopCt.IsCancellationRequested)
                {
                    await Task.Delay(ResizePollMs, loopCt);
                    if (resize.Sample(s.Surface.Width, s.Surface.Height)) RedrawLast();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnChromeFault(ex); }
        }, CancellationToken.None);

        //the composer task
        _ = Task.Run(() =>
        {
            string? rejected = null;   //only this task writes and reads it
            try
            {
                while (!loopCt.IsCancellationRequested)
                {
                    string text;
                    try
                    {
                        text = editor.Read(Redraw, rejected,
                            //the whole queue comes back at once with its permits, and an empty answer lets the editor recall history
                            pullQueue: () =>
                            {
                                lock (s.Gate)
                                {
                                    var taken = queue.DrainAll();
                                    if (taken.Count > 0 && !stopped) PublishQueueLocked();
                                    return taken;
                                }
                            },
                            chords: chords,
                            onQuit: () => s.CancelLoop?.Invoke(),
                            onHintChanged: OnHintChanged,
                            //a Ctrl+C with Ctrl+A's selection live copies through the same clipboard instance as a drag, no gate needed
                            onCopyText: text => clip.TrySet(text),
                            //every composer-selection set-site clears any live transcript or chrome span, so one selection domain is live at most
                            onComposerSelectionSet: BuildComposerSelectionSetHook(s.Painter, s.Gate, () => stopped));
                    }
                    catch (ComposerPaintException) { throw; }   //a paint bug, rethrown so it is not mistaken for pump death
                    catch (Exception)
                    {
                        //nothing can be typed again, so end the session rather than leave the dispatcher blocked on a queue nobody fills
                        pumpDied = true;
                        return;
                    }
                    rejected = null;
                    if (text.Trim().Length == 0) continue;
                    if (queue.TryEnqueue(text))
                    {
                        //one repaint clears the composer and shows the new queued row
                        lock (s.Gate) { PublishQueueLocked(); RedrawLocked(idleView); }
                    }
                    else
                    {
                        rejected = text;            //handed back to the next Read, which reseeds the composer
                        //cap hit, beep and keep the text, guarded by stopped so no bell is written into an erased chrome
                        lock (s.Gate) { if (!stopped) s.Surface.Write("\a"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //a clean shutdown is not a fault
            }
            catch (Exception ex)
            {
                //a fault out of this body is stashed and re-raised on the dispatcher instead of dying unobserved, and the first fault wins
                Interlocked.CompareExchange(ref fault, ex, null);
            }
            finally
            {
                //any exit of this task ends the session, the composer is the only producer and the dispatcher would park forever otherwise
                try { loopCts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }, CancellationToken.None);

        //the dispatcher paints too, and no exit from this loop skips chrome teardown
        try
        {
            while (!loopCt.IsCancellationRequested)
            {
                string? input;
                try { input = await queue.TakeAsync(loopCt); }
                catch (OperationCanceledException) { break; }
                //null means Esc dropped this message after its permit, so the counts agree again and the loop just repaints
                if (input is null) { lock (s.Gate) { PublishQueueLocked(); RedrawLocked(lastView); } continue; }

                if (await DispatchLinearAsync(input, s, queue, appCt)) break;
                lock (s.Gate) { PublishQueueLocked(); RedrawLocked(lastView); }   //fresh status and ctx after the turn
            }
        }
        finally
        {
            //cancel the loop before loopCts is disposed, a /quit exit breaks the while without cancelling and the resize poller's next Task.Delay would throw
            try { loopCts.Cancel(); } catch (ObjectDisposedException) { }
            hintTimer.Dispose();            //stop a pending composer-hint expiry shot
            TeardownChrome(s.Gate, s.Painter, s.Ticker, _chrome, () => stopped = true, Volatile.Read(ref fault));
            s.RestoreMouseMode?.Invoke();   //restore the shell's mouse and QuickEdit mode (idempotent, the degrade path and the exit hooks call it too)
        }
        //the alt screen is gone with the transcript, so Ended tells the exit block whether to reprint it or offer a resume
        Ended = EndOf(degraded, dumpOnExit, sessions?.CountForCwd() > 0, pumpDied);
        if (!degraded) ExitEpilogue(s.Surface, s.Painter.Model, s.Theme, dumpOnExit, pumpDied, s.Glyphs);
        return 0;
    }

    //how the session ended, for the exit block the launcher prints once this loop has returned. it stays null on the plain loop, which keeps its own exit as it was.
    public SessionEnd? Ended { get; private set; }

    //the resume line needs a session to resume and a transcript that was not reprinted (a reprinted one is already the record)
    internal static SessionEnd EndOf(bool degraded, bool dumpOnExit, bool hasSessions, bool inputDied) =>
        new(Farewell: !degraded && !inputDied, Resume: !degraded && !dumpOnExit && hasSessions);

    //the transcript's exit on the restored main screen, it reports an input death or reprints the whole transcript on request
    internal static void ExitEpilogue(ITermSurface surface, TranscriptModel model, Theme theme, bool dumpOnExit,
        bool inputDied = false, Gatto.Terminal.GlyphSet? glyphs = null)
    {
        var width = surface.Width > 0 ? surface.Width : 80;
        if (inputDied)
            surface.Write(theme.Paint("gatto hit an input error, and your session is saved.", Theme.Warn) + "\n");
        if (!dumpOnExit) return;

        var sb = new System.Text.StringBuilder();
        foreach (var row in model.Items.SelectMany(i => i.Render(width, theme, glyphs)))
            sb.Append(row).Append(Ansi.Reset).Append('\n');
        if (sb.Length > 0) surface.Write(sb.ToString());
    }

    //this bracket starts and stops the purr, the body cannot end it early. the renderer is a Func, a /role swap mid-turn must still close the current turn
    internal static async Task<bool> DispatchBracketAsync(
        object gate, ChromePainter painter, ChromeTicker ticker, Func<StreamRenderer> renderer,
        IReadOnlyList<string> queued, IReadOnlyList<string> userLines, string kaomoji, Func<Task<bool>> body,
        long promptEstimate = 0, Func<Render.PurrFrames>? purrSet = null, string? classifyAs = null)
    {
        lock (gate)
        {
            painter.State.QueueTexts = queued;     //the dispatched message has left the queue
            renderer().CommitUser(userLines);      //the ❯ echo becomes scrollback
            renderer().BeginTurn();
            //only a submission that reaches the model purrs. classify the typed text, a typed //help reaches the model while the echo reads /help
            if (SlashCommands.ReachesModel(classifyAs ?? string.Join("\n", userLines)))
                //the purr is drawn once per turn, the accordion must not change word mid-breath. the chooser is injected, a session without one draws the same frames
                ticker.StartTurn(kaomoji, promptEstimate,
                    purrSet is null ? Render.PurrFrames.Full : purrSet());
        }
        try { return await body(); }
        finally
        {
            //call EndTurn before StopTurn, a Ctrl+C mid-tool leaves the tool row and its permission answer in the state and StopTurn would erase them
            renderer().EndTurn();
            //read the completion row before StopTurn stops the clock, and repaint there only when there is none so the composer stays put
            var purred = ticker.CompletionRow();
            ticker.StopTurn(repaint: purred is null);
            if (purred is not null) renderer().CommitCompletion(purred);
        }
    }


    //retire the ticker under the gate before the painter erases anything, a tick after that would paint onto a cleaned screen
    internal static void TeardownChrome(
        object gate, ChromePainter painter, ChromeTicker ticker, ChromeHandle? chrome,
        Action markStopped, Exception? fault)
    {
        try
        {
            lock (gate)
            {
                markStopped();
                ticker.StopTurn();          //idempotent, and its own paint faults go to OnFault
                ticker.OnFault = null;
                if (chrome is not null) { chrome.Painter = null; chrome.Renderer = null; }
                painter.Teardown();          //idempotent, it erases the chrome and never paints again
            }
        }
        catch when (fault is not null) { } //the console is sick, the fault below is the real story
        if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
    }

    //rewrites the current composer line at the cursor row when the painter is dead, a one-line fallback rather than a frame
    internal static void EchoComposerInline(ITermSurface surface, EditorView v, Gatto.Terminal.GlyphSet? glyphs)
    {
        var line = v.Lines.Count > 0 ? v.Lines[Math.Clamp(v.CursorLine, 0, v.Lines.Count - 1)] : "";
        surface.Write("\r" + Ansi.ClearLine + (glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Prompt + " "
            + TermText.Sanitize(line));
    }

    //the rich session's mutable wiring, the reference types a /role swap or a status rebuild replaces live here so no caller holds a stale local
    private sealed class RichState
    {
        //the interface, so the session state does not name the concrete surface
        public ITermSurface Surface = null!;
        public Theme Theme = null!;

        //the one glyph set for the session, a rebuild hands the new painter the same set rather than resolving one again
        public Gatto.Terminal.GlyphSet Glyphs = Gatto.Terminal.GlyphSet.Unicode;
        public StreamRenderer Renderer = null!;
        public InputFrame Frame = null!;
        public Action Refresh = null!;
        public Action<Usage> OnUsage = null!;
        public CancellationTokenSource? TurnCts;

        //the one CtxState for the session, fed by RefreshStatus every turn so the compact offer reads the same instance instead of rebuilding one
        public CtxState? Ctx;

        //the one lock every painter serializes on, set once and handed to the /role replacement renderer so a swap keeps the same object
        public object Gate = null!;

        //the session's one painter and one turn ticker, both outlive a /role swap since the replacement renderer is built around them
        public ChromePainter Painter = null!;
        public ChromeTicker Ticker = null!;

        //cancels the linear loop on Ctrl+C at rest, set by RunLinearLoopAsync which owns the loop's token source
        public Action? CancelLoop;

        //the loop's degrade latch, a /role swap must re-point it at the replacement renderer or a degrade after the swap never reaches the loop
        public Action? OnDegraded;

        //the mid-turn footer refresh, a /role swap must re-point it at the replacement renderer or the footer stops moving after the change
        public Action? OnContextChanged;

        //restores the shell's mouse and QuickEdit mode, held here since the inputMode local is out of scope at the degrade latch and teardown
        public Action? RestoreMouseMode;

        //the composer hint repaint, held here so the Ctrl+C handler outside the loop can run the same repaint and expiry
        public Action? OnHintChanged;

        //the live composer-empty predicate, a closure over the published view so every read sees the current contents (null means empty until the loop starts)
        public Func<bool>? ComposerEmpty;
    }

    //takes the attach decision above the turn bracket, a refusal from inside the body comes after the echo and the purr
    private async Task<bool> DispatchLinearAsync(string input, RichState s, DispatchQueue queue, CancellationToken appCt)
    {
        //refuses an unknown command above the bracket, and clears the composer instead of handing the text back since a restore races the composer's own paint
        var submitted = input;   //what was typed, the bracket classifies this rather than the stripped text
        var gate = GateSlash(input);
        if (gate.Refused)
        {
            s.Renderer.CommitSystem(gate.Refusal!);
            return false;
        }
        input = gate.Text;   //a typed // has lost its first slash, everything below sees the stripped text

        var attach = AttachDecision.None;
        //the one place attachments are acquired, and the literal flag keeps a //help message on this arm since the stripped text reads as a command
        if (gate.Literal || !IsSlashCommand(input))
        {
            attach = PrepareAttachments(input, s);
            if (attach.Refused)
            {
                //commit only the cause's own sentence, the user still has to resend this message
                foreach (var line in attach.RefusalLines!) s.Renderer.CommitSystem(line);
                _restoreComposer?.Invoke(input);
                return false;
            }
            //clear the pending pastes only on the path that sends, so a command typed after an image keeps the image attached
            lock (s.Gate) _pendingPaste.Clear();
        }
        return await DispatchBracketAsync(s.Gate, s.Painter, s.Ticker, () => s.Renderer,
            queue.Texts(), input.Split('\n'), Cats.Face(_glyphs),
            () => DispatchLinearBodyAsync(input, s, attach, appCt, gate.Literal),
            //what the model is about to re-read, measured before the turn appends anything
            promptEstimate: ScaledPromptEstimate(ContextBudget.Estimate(convo.Messages), _usageState.Ratio),
            purrSet: purrSet, classifyAs: submitted);
    }

    //single-sourced from SlashCommands.All with the matcher the dispatch chain uses, so this and the chain cannot disagree about what is a command
    internal static bool IsSlashCommand(string input)
    {
        var trimmed = input.Trim();
        foreach (var cmd in SlashCommands.All)
            if (TryMatchCommand(trimmed, cmd.Name, out _)) return true;
        return false;
    }

    //the gate's verdict for one submitted line. the escape flag keeps the line out of the command chain, and a refusal means nothing is sent
    internal readonly record struct SlashGate(string Text, bool Literal, string? Refusal)
    {
        internal bool Refused => Refusal is not null;
    }

    //the gate every submitted line passes, a // escape loses one slash and the refusal reads the first token against the declared names
    internal static SlashGate GateSlash(string input)
    {
        var trimmed = input.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return new SlashGate(trimmed[1..], Literal: true, null);

        var token = FirstToken(trimmed);
        return IsCommandShaped(token) && !IsDeclaredCommand(token)
            ? new SlashGate(input, false, UnknownCommandLine(token))
            : new SlashGate(input, false, null);
    }

    //a colon in this message, a census pins the exact em dash count so a new one would hide behind the bump
    internal static string UnknownCommandLine(string token) =>
        $"{token} is not a command: /help lists them, or start the line with // to send it as text";

    private static string FirstToken(string trimmed)
    {
        for (var i = 0; i < trimmed.Length; i++)
            if (char.IsWhiteSpace(trimmed[i])) return trimmed[..i];
        return trimmed;
    }

    //only a bare /word of letters reads as a command, a digit, an inner slash or punctuation means the user is writing prose
    private static bool IsCommandShaped(string token)
    {
        if (token.Length < 2 || token[0] != '/') return false;
        for (var i = 1; i < token.Length; i++)
            if (!char.IsAsciiLetter(token[i])) return false;
        return true;
    }

    private static bool IsDeclaredCommand(string token)
    {
        foreach (var cmd in SlashCommands.All)
            if (string.Equals(cmd.Name, token, StringComparison.Ordinal)) return true;
        return false;
    }

    //what a message attaches and why it may not send, the notices commit inside the turn and a refusal line stops the message
    private readonly record struct AttachDecision(
        IReadOnlyList<ImageRef>? Images,
        IReadOnlyList<ImageAttach.Notice> Notices,
        IReadOnlyList<string>? RefusalLines)
    {
        //nothing to attach and nothing to say, a slash command or a message with no images
        internal static AttachDecision None { get; } = new(null, Array.Empty<ImageAttach.Notice>(), null);

        internal static AttachDecision Send(IReadOnlyList<ImageRef>? images, IReadOnlyList<ImageAttach.Notice> notices) =>
            new(images, notices, null);

        internal static AttachDecision Refuse(params string[] lines) =>
            new(null, Array.Empty<ImageAttach.Notice>(), lines);

        internal bool Refused => RefusalLines is not null;
    }

    //decides what this message attaches and commits nothing, so the caller owns the user-visible consequences and the pastes survive a refusal
    private AttachDecision PrepareAttachments(string input, RichState s)
    {
        //only images whose placeholder is still in the text attach, the placeholders stay in the message so the model knows where each picture sits
        IReadOnlyList<ImageRef> pasted;
        lock (s.Gate)
        {
            pasted = ImageAttach.ReferencedIn(input, _pendingPaste);
            _pasteHint = null;
            _pasteHintUntil = null;
        }
        //the attached set comes from the live conversation, an accumulated one duplicates after a restore. the notices merge in one place, ImageAttach
        var guarded = ImageAttach.Detect(input, cwd, pasted, ImageAttach.ShasIn(convo.Messages),
            AttachLimits.Default);
        var notices = guarded.Notices;

        //block the notices by kind rather than by their wording, and report all of them since a message can name two ambiguous files
        var blocked = notices.Where(n => n.Kind == ImageAttach.NoticeKind.Blocked)
                             .Select(n => n.Text).ToArray();
        if (blocked.Length > 0) return AttachDecision.Refuse(blocked);

        var collected = guarded.Attached;
        //refuse the attach when the model cannot see, a server without the projector answers HTTP 500 and the whole turn would die
        if (collected.Count > 0 && VisionUnavailable() is string why) return AttachDecision.Refuse(why);

        //the count question is checked last and a session with nobody to ask sends the batch, it is a courtesy rather than a wall
        if (guarded.NeedsConfirmation && !(_attachManyGranted?.Invoke() ?? false) && _confirmAttach is { } ask)
        {
            var answer = ask(collected.Count, collected.Count * ImageAttach.EstimatedTokensPerImage);
            //a No answer commits nothing, the count question was the sentence and a row about it would only repeat it
            if (answer == ConfirmAttachAnswer.No) return AttachDecision.Refuse();
            if (answer == ConfirmAttachAnswer.YesAlways) _grantAttachMany?.Invoke();
        }

        //null when there are no images, an empty list would be a second way of saying nothing
        return AttachDecision.Send(collected.Count > 0 ? collected : null, notices);
    }

    //the reason an image cannot go to the answering model, or null when it can. only a definite false blocks an attach, unknown means no probe answered
    private string? VisionUnavailable() =>
        //a definite false only, a probe that could not answer leaves the bit null and an unknown must not block an attach
        probeServing?.Invoke() is { Vision: false }
            //on a session gatto does not serve the tail says nothing about switching models, /model refuses there
            ? unmanagedVisionNotice
              //when the encoder is already beside the model the tail names the one-key fix, otherwise the generic ending stands
              ?? "this model can't see images — message not sent. "
                 + (visionFixHint ?? "Remove the image tokens to send it anyway, or /model to one that can.")
            : null;


    //the one body behind /model, it returns true only when a switch happened and hands the caller the line to say in its place
    private bool HandleModelCommand(string trimmed, ITurnObserver picker, Action<string> say,
        out string? switchNotice)
    {
        switchNotice = null;
        //refuse before the picker, a connect endpoint does not swap models and a picker there could arm one against a server gatto does not manage
        if (unmanagedNotice is { Length: > 0 } unmanaged)
        {
            say(unmanaged);
            return false;
        }

        var arg = trimmed.Length > 6 ? trimmed[6..].Trim() : "";

        //set only when the wizard's done step answered, in the same branch as the id so the two cannot part
        var answered = false;

        //both verbs parse and answer with a line since the shelf moved to gatto model, and nothing about the session changed so they answer false
        if (arg == AddModelArg || arg == NewModelArg)
        {
            say(AddMovedText);
            return false;
        }


        //the bare /model quant lists the files the model holds, it is read-only and answers false so the caller must not refresh the chip
        if (arg == QuantArg)
        {
            say(QuantLines(modelFiles, _glyphs));
            return false;
        }

        //the quant switch answers false so the caller does not recompose, and quant rm is matched before it or rm reads as a file name
        if (arg.StartsWith(QuantArg + " " + RemoveArg, StringComparison.Ordinal))
        {
            var name = arg[(QuantArg.Length + RemoveArg.Length + 1)..].Trim();
            say(name.Length == 0 ? $"which file? /model {QuantArg} {RemoveArg} <name>"
                : removeQuant is null ? "removing a file isn't available in this session"
                : removeQuant(name));
            return false;
        }

        if (arg.StartsWith(QuantArg + " ", StringComparison.Ordinal))
        {
            var name = arg[(QuantArg.Length + 1)..].Trim();
            say(name.Length == 0 ? QuantLines(modelFiles, _glyphs)
                : switchQuant is null ? "switching files isn't available in this session"
                : switchQuant(name));
            return false;
        }

        if (arg.Length == 0 && listModels is null && noModelList is { } none)
        {
            say(none);
            return false;
        }

        if (arg.Length == 0)
        {
            var (chosen, chosenWasAnswered) = ShowModelPicker(picker);
            if (chosen is null) return false;   //cancelled, or a plain terminal which printed the list
            arg = chosen;
            answered = chosenWasAnswered;
        }
        if (switchModel is null)
        {
            say(ModelSwitchUnavailable);
            return false;
        }
        var result = switchModel(arg, answered);
        if (!result.Success)
        {
            say(result.Message ?? ModelSwitchFailed);
            return false;
        }

        switchNotice = result.Message;
        modelName = result.ModelId;
        contextBudget = result.ContextBudget;
        serving = result.ServingMismatch;
        probedNCtx = result.ProbedNCtx;
        thinkingName = result.ThinkingName;   //null when the new model has no map, the chip hides
        thinkingToggle = result.ThinkingIsToggle;   //a /model switch can change the reasoning shape (binary or levels)
        thinkingUnavailable = result.ThinkingIsUnavailable;
        if (result.SystemText is not null) convo.ReplaceSystem(result.SystemText, result.Baseline);
        else if (result.Baseline is not null && convo.Messages[0].Content is { } sameText) convo.ReplaceSystem(sameText, result.Baseline);   //same text, new baseline, so the record names the model now in use
        _usageState.Clear();   //the old reading describes a conversation and model pairing that is gone
        return true;
    }

    //the command chain and the model turn, true ends the session. every status line is a ♯ row, the bracket owns the echo, turn and purr
    private async Task<bool> DispatchLinearBodyAsync(string input, RichState s, AttachDecision attach,
        CancellationToken appCt, bool literal = false)
    {
        var trimmed = literal ? "" : input.Trim();

        if (trimmed == "/quit") return true;
        if (trimmed == "/help")
        {
            //the help block lists the commands you type and then the keys you press, gutter rows with no ♯ marker
            s.Renderer.CommitPlain(SlashCommands.RenderRich(s.Theme) + "\n\n" + Shortcuts.RenderRich(s.Theme, glyphs: _glyphs));
            return false;
        }
        if (trimmed == "/new")
        {
            //hold the warning sink first, the recompose below and any extension hook can both raise a diagnostic
            using var held = warn?.Hold();
            //a ♯ row committed here would be erased by the rotation below, which resets the display model
            var carry = new List<string>();
            _lastBankFailure = null;   //only this rotation's failure may be carried, BankMemory may not run at all
            //the recompose, the bookkeeping and the display reset are one seam, a separate reset would discard the ♯ rows the recompose just committed
            RotateForNew(recomposeSystem, held,
                adopt: fresh =>
                {
                    convo = fresh;
                    sessions?.StartNew();
                    _offered = false;                      //a fresh session, so the 85% offer may fire again
                    resetGrounding?.Invoke();              //a fresh session, so grounding is re-armed and restates again
                    _usageState.Clear();                   //the old reading describes a conversation that is gone
                    loop.ResetStripWarning();              //a fresh session reports its own first leak
                    s.Ctx?.RecordUsedTokens(null);         //the same for the context figure
                    s.Refresh();                           //a fresh conversation, so the ctx estimate resets too
                },
                s.Painter.Model, s.Renderer, BannerItem(s.Theme, roleName, modelName, versionLine, s.Glyphs),
                NewConversationLine, carry);
            s.Painter.Repaint();
            return false;
        }
        if (trimmed == "/auto")
        {
            s.Renderer.CommitSystem(AutoLine(toggleAuto()));
            return false;
        }
        if (TryMatchCommand(trimmed, "/wild", out var wildArg))
        {
            //one ♯ row per line, CommitSystem strips a newline so two sentences in one call would mash together
            foreach (var line in HandleWild(wildArg).Split('\n'))
                s.Renderer.CommitSystem(line);
            s.Refresh();                                   //the wild toggle changed, the footer chip must follow
            return false;
        }
        if (TryMatchCommand(trimmed, "/permissions", out var permArg))
        {
            //one ♯ row per line again, the listing has a row per grant plus the wild row
            foreach (var line in HandlePermissions(permArg).Split('\n'))
                s.Renderer.CommitSystem(line);
            return false;
        }
        if (trimmed == "/tools")
        {
            if (listTools is null) { s.Renderer.CommitSystem(ToolsUnavailable); return false; }
            var tools = listTools();
            var policy = PolicyRows();
            //read both lists, a pure-policy extension is armed and calling it no tools would deny it
            if (tools.Count == 0 && policy.Count == 0)
            { s.Renderer.CommitSystem("no tools are armed this session"); return false; }
            s.Renderer.CommitSystem(ToolsHeadline(tools.Count));
            s.Renderer.CommitListing(ToolsSpec(tools, policy, s.Glyphs));
            return false;
        }
        if (trimmed == "/compact")
        {
            lock (_ctsLock) { s.TurnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt); }
            var ok = false;
            //the same hold /new takes, opened around the whole handler since the recompose sits deep inside CompactAsync
            using var held = warn?.Hold();
            try { ok = await HandleCompactAsync(s.Renderer, s.TurnCts!.Token, s.Ctx); }
            finally { lock (_ctsLock) { s.TurnCts?.Dispose(); s.TurnCts = null; } }
            s.Refresh();                                   //tokens moved either way, summarizing is a real turn
            if (ok)
            {
                //reset the display model so it matches the fresh conversation, otherwise the pre-compact transcript stays on screen and the screen and the file diverge
                RotateDisplay(s.Painter.Model, s.Renderer,
                    _lastCompactSummary is { Length: > 0 } summary
                        ? new SessionLeadItem(summary, After: null) { Memory = _lastCompactOutcome }
                        : null,
                    CompactedLine(s.Ctx), held,
                    _lastBankFailure is { } bankFailure ? new[] { bankFailure } : Array.Empty<string>());
                SaveSession(sessions, s.Painter.Model, convo);
                s.Painter.Repaint();
            }
            return false;
        }
        if (trimmed == "/role" || trimmed.StartsWith("/role ", StringComparison.Ordinal))
        {
            var arg = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
            var result = ApplyRoleSwitch(convo, switchRole, arg);
            if (result.Success)
            {
                roleName = result.RoleName;
                modelName = result.ModelName!;
                thinkingName = result.ThinkingName;   //null when the new role has no map, the chip hides
                //set the model's role so future assistant blocks are tinted, blocks already committed keep theirs
                s.Painter.Model.Role = roleName;
                //the tab follows the role too, until the folder title is in
                s.Painter.AltScreen.Retitle(TerminalTitle.For(roleName));
                //rebuild the objects that captured roleName and modelName at construction, and reuse the painter and ticker so the purr never blinks across the swap
                s.Refresh();                              //this also retargets painter.Frame and RoleForTint
                //build the renderer through the launch builder, and let the tail closure read the convo field since /new and /compact reassign it
                var next = BuildSessionRenderer(s.Painter, s.Surface, s.Theme, roleName, s.Ticker, s.Gate,
                    s.OnUsage, reasoning, () => convo.Messages.Count > 0 ? convo.Messages[^1] : null,
                    s.Glyphs);
                next.AdoptTranscriptState(s.Renderer);     //hand the new renderer what the scrollback last ended with
                next.OnDegraded = s.OnDegraded;            //the loop's degrade latch too (both or neither)
                next.OnContextChanged = s.OnContextChanged;   //the mid-turn footer refresh too (both or neither)
                s.Renderer = next;
                //re-point the chrome handle at the new renderer, a stale one would leave the prompters orphaned
                if (_chrome is not null) { _chrome.Painter = s.Painter; _chrome.Renderer = s.Renderer; }
                s.Renderer.CommitSystem(RoleLine(roleName));
            }
            else
            {
                s.Renderer.CommitSystem(result.Message ?? RoleSwitchFailed);
            }
            return false;
        }
        if (trimmed == "/model" || trimmed.StartsWith("/model ", StringComparison.Ordinal))
        {
            //one body for both entries, what the two sites used to duplicate lives in HandleModelCommand and what stays here only this site can do
            if (HandleModelCommand(trimmed, s.Renderer, s.Renderer.CommitSystem, out var notice))
            {
                s.Ctx?.RecordUsedTokens(null);   //the old figure describes a pairing that is gone
                //no renderer swap here, a model change never touches roleName or RoleForTint and Refresh alone repaints the mismatch chip
                s.Refresh();
                s.Renderer.CommitSystem(notice ?? ModelLine(modelName));
                //check the context fit at the switch, an over-budget history should be compactable before the user sends a message
                MaybeWarnContextFit(s.Renderer);
            }
            return false;
        }
        if (trimmed == "/effort" || trimmed.StartsWith("/effort ", StringComparison.Ordinal))
        {
            var arg = trimmed.Length > 7 ? trimmed[7..].Trim() : "";
            if (HandleEffortCommand(arg) is { } line)
            {
                s.Renderer.CommitSystem(line);
                s.Refresh();   //the footer's level chip must reflect the switch at once
            }
            return false;
        }
        if (TryMatchCommand(trimmed, "/remember", out var rememberArg))
        {
            if (rememberArg.Length == 0)
            {
                s.Renderer.CommitSystem(RememberUsage);
                return false;
            }
            input = RememberPrompt(rememberArg);   //sent through the normal turn below, as if the user had typed it
        }
        if (trimmed == "/init") input = InitPrompt.Text;   //sent through the normal turn below, as if the user had typed it

        RefreshServingBeforeTurn(s.Renderer);   //re-probe and check the context fit before the request goes out
        s.Refresh();   //the chip (or its clearing) must reach the footer before the turn streams

        //announce the notices here inside the turn so they read under the message, a refusal never reaches this body at all
        foreach (var notice in attach.Notices)
            s.Renderer.CommitSystem(notice.Text);

        lock (_ctsLock) { s.TurnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt); }
        var turnCt = s.TurnCts.Token;
        bool turnCancelled;
        deafWatch?.Arm();
        try
        {
            await RunTurnGuardedAsync(loop, convo, input, s.Renderer, sessions, turnCt, s.Painter.Model, attach.Images);
            turnCancelled = turnCt.IsCancellationRequested;
        }
        finally
        {
            deafWatch?.Disarm();
            lock (_ctsLock) { s.TurnCts?.Dispose(); s.TurnCts = null; }
        }
        //name the session's folder in the tab once a turn ran to its end, a cancelled turn is not that boundary
        if (!turnCancelled) s.Painter.AltScreen.SetFinalTitle(TerminalTitle.Project(cwd));
        s.Refresh();                                   //a turn may have committed or switched branches, and tokens moved
        MaybeOfferCompact(s.Renderer, s.Ctx);           //the 85% offer goes through the warning sink and becomes a ♯ row
        return false;
    }
}
