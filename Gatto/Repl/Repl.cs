using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl;

//the result of a /role switch, holding the canonical name and model id. on failure Message holds the line to print and the session is unchanged
public sealed record RoleSwitchResult(bool Success, string RoleName, string? ModelName, string? SystemText, string? Message,
    string? ThinkingName = null, SessionBaseline? Baseline = null,
    //the new role's file colours and banner rows, null where the file carries none
    (RgbColor Dark, RgbColor? Light)? RoleTint = null, IReadOnlyList<string>? RoleCat = null,
    string? FirstMessage = null,
    //set by ApplyRoleSwitch alone, true when the switch opened the conversation with the new role's first message
    bool Opened = false);

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

public sealed partial class Repl(
    AgentLoop loop, Conversation convo, string roleName, string modelName,
    SessionStore? sessions, IChatClient client, int? configContext, int? contextBudget, string cwd,
    Func<ComposedSystem> recomposeSystem, Func<string, RoleSwitchResult> switchRole,
    WildState? wild = null, Action<bool>? persistWild = null,
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
    //the commands extensions registered, read beside the built-ins at every site that names a command. null means none registered
    Gatto.Core.Commands.CommandRegistry? commands = null,
    //the finished sentence for an over-budget memory index, printed once after the banner. a later re-truncation goes through the warn sink instead
    string? memoryWarning = null,
    string? cloudNotice = null,   //said once per home, the first time a session runs on a cloud endpoint
    //the launch lines in arrival order, said after the banner: the sink's warnings on the rich face only, the wild notice on both. null says none
    IReadOnlyList<string>? launchWarnings = null,
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
    Func<Render.Chrome.PurrFrames>? purrSet = null,
    //the window title's deed, injectable so the title moment is testable without the real console. null keeps the alt screen's own default
    Gatto.Terminal.ITerminalTitle? terminalTitle = null,
    //null unless gatto reads the console itself, the only reader whose record count shows the tracer arriving
    Gatto.Terminal.InputDeafnessWatchdog? deafWatch = null,
    //what the resume carried or why it sent the conversation again, shown under the resume marker and never saved
    string? resumeLine = null,
    //the word the /model legend gives the dot, weights loaded on local rows and current on a cloud list where nothing loads
    string modelMarkLegend = "weights loaded",
    //said by bare /model on a cloud endpoint that lists no models, where a typed name is the whole switch
    string? cloudSwitchHint = null,
    //the line naming the served llama-server that stopped running, read after a lost connection. null when gatto serves nothing for this session
    Func<string?>? serverGone = null,
    //what /context counts, built by the caller from the composition and the tools since Repl cannot see the role. null leaves /context unavailable
    Func<ContextUsageState, ContextInputs>? contextInputs = null,
    //llama-server's counting routes for the exact path, null for an endpoint that has none
    ITokenCounter? tokenCounter = null,
    //the clipboard Alt+V reads images from, injectable so a test can paste. null uses the real one
    Gatto.Terminal.IClipboardImageNative? clipboardImageSource = null,
    //gatto.json turns wild on for every launch, so /wild never can end this session's wild and not the next launch's
    bool wildFromHome = false,
    Func<bool>? wildPersisted = null,   //whether the home holds a wild grant for this folder, read before /wild never clears it
    //the role a bare launch runs from this home, and the rule names a role only when it is not this one. null names every role
    string? defaultRole = null,
    //the running role's colours and banner rows, handed down so the Repl never reads a role file
    (RgbColor Dark, RgbColor? Light)? roleTint = null,
    IReadOnlyList<string>? roleCat = null,
    //the running role's opening line, committed as history whenever a conversation opens empty
    string? firstMessage = null)
{
    //the name the top rule shows, null when the running role is the default, compared as the launch resolves a name against the files
    private string? DisplayRole() => string.Equals(roleName, defaultRole, StringComparison.OrdinalIgnoreCase) ? null : roleName;

    //a bypassing command's answer while a turn is in flight, null at rest or for any other text, which then goes to the queue
    private Task<Func<int, IReadOnlyList<string>>>? BypassNow(string text, RichState s)
    {
        bool inFlight;
        lock (_ctsLock) inFlight = s.TurnCts is not null;
        if (!inFlight || !SlashCommands.BypassesQueue(text)) return null;
        var trimmed = text.Trim();
        if (trimmed == "/context") return ContextAnswerAsync(s);
        return Task.FromResult(ReadOnlyAnswer(trimmed, s));
    }

    //the prompters' bridge to the chrome, armed when the painter and renderer come up, cleared at teardown, so a prompt renders inline until then
    private readonly ChromeHandle? _chrome = chrome;

    private readonly Gatto.Core.Commands.CommandRegistry _commands = commands ?? new Gatto.Core.Commands.CommandRegistry();

    //a copy, so a warning raised while the rows print cannot change the list under the loop
    private string[] LaunchWarningsNow() => launchWarnings is null ? [] : [.. launchWarnings];

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

    //the pasted bytes by their blob path, written only when a message that holds them is sent, so a refused, deleted or abandoned paste leaves no file (under s.Gate)
    private readonly Dictionary<string, byte[]> _unsavedPaste = new(StringComparer.OrdinalIgnoreCase);

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

    //a bare Enter with this many messages already queued beeps and puts the text back in the composer
    public const int QueueCap = 10;

    //how often the resize poller reads the geometry, fast enough that a mid-turn resize cannot leave stale pixels in the content
    public const int ResizePollMs = 200;

    //the shared /role body, and the caller must keep its own roleName and modelName in sync. a null ModelName on success is a bare /role
    public static RoleSwitchResult ApplyRoleSwitch(Conversation convo, Func<string, RoleSwitchResult> switchRole, string arg, string current)
    {
        //a bare /role names the running role and switches nothing
        if (arg.Length == 0) return new RoleSwitchResult(true, current, null, null, null);
        var result = switchRole(arg);
        if (result.Success)
        {
            convo.ReplaceSystem(result.SystemText!, result.Baseline);
            convo.SetGattoRole(result.RoleName);
            //a conversation holding only an opening carried no request, so the role switched to opens it afresh
            if (convo.Messages.Skip(1).All(m => m.Role == "assistant")) convo.TruncateTo(1);
            return result with { Opened = SeedFirstMessage(convo, result.FirstMessage) };
        }
        return result;
    }

    //the opening turn is committed as history, so the first request carries it and none is made for it
    internal static bool SeedFirstMessage(Conversation convo, string? text)
    {
        if (text is null || convo.Messages.Any(m => m.Role != "system")) return false;
        convo.AddAssistant(text);
        return true;
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
        renderer.OnWarning($"the server is running {running} now, this session is on {Gatto.Core.Models.ServedModelName.Display(modelName)}"
            + (servingOnTheShelf ? $" — /model {running} to use it" : ""));
        _servingSaid = running;
    }

    //true when the estimate is strictly over the armed model's window and this crossing has not been reported
    public static bool ShouldWarnContextFit(int estimateTokens, int? budgetTokens, bool alreadyWarned) =>
        !alreadyWarned && budgetTokens is int b && b > 0 && estimateTokens > b;

    //the auto-compact body, it strips the memory scaffolding instead of banking it and leaves everything untouched on a null summary
    internal async Task<CompactionResult?> AutoCompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct, long traceParent = 0)
    {
        SetReasoningPassthrough(observer, true);             //the dimmed summary is not reasoning
        string? summary;
        var compactor = new Compactor(client, modelName, loop.RequestShape, loop.Trace);
        try
        {
            summary = await compactor.SummarizeAsync(
                convo, observer, ct, windowTokens: contextBudget, ratio: _usageState.Ratio, midTurn: true,
                reason: reason.ToString(), traceParent: traceParent);
        }
        finally { SetReasoningPassthrough(observer, false); }
        _lastAutoCompactFailure = compactor.LastFailure;     //the loop's line names it when the summary is null
        if (summary is null) return null;                    //nothing is touched, the summarizer call was the only fallible work

        //all fallible work done, adopt atomically from here
        var (result, stripped) = Compactor.RebuildMidTurn(summary, convo, currentUserPrompt, observer, sessions, recomposeSystem, _glyphs.Dot);
        _lastCompactSummary = stripped;                       //the rich loop shows it as the new session's lead, as after /compact
        _offered = false;                                     //a fresh session may cross 85% again
        _usageState.Clear();                                  //cleared so a re-fire needs fresh over-threshold evidence
        return result;
    }

    //adapts AutoCompactAsync to the ICompactionHandler seam, nested so it can reach the enclosing Repl's fields
    private sealed class AutoCompactionHandler(Repl repl) : ICompactionHandler
    {
        public Task<CompactionResult?> CompactAsync(
            CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct)
            => repl.AutoCompactAsync(reason, currentUserPrompt, observer, ct);

        public Task<CompactionResult?> CompactAsync(
            CompactionReason reason, string currentUserPrompt, ITurnObserver observer, long traceParent, CancellationToken ct)
            => repl.AutoCompactAsync(reason, currentUserPrompt, observer, ct, traceParent);

        public string? LastFailure => repl._lastAutoCompactFailure;
    }

    //why the last automatic summary came back null, read by the loop's line that turns auto-compaction off
    private string? _lastAutoCompactFailure;

    //emits the 85% offer after a turn when the crossing was seen, the footer's percent is trusted over the estimate
    private void MaybeOfferCompact(ITurnObserver renderer, CtxState? ctx = null)
    {
        var crossed = ctx?.Percent is int p ? p >= 85
            : ShouldOfferCompact(ContextBudget.Estimate(convo.Messages), contextBudget, _offered);
        if (crossed && !_offered)
        {
            renderer.OnWarning(CommandText.CompactOfferLineFor(loop.ArmedAutoCompactAt));
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
                : $"{Gatto.Core.Models.ServedModelName.Display(modelName)} holds {effective}";
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

    //the prefill's denominator, the chars÷4 estimate lifted by the ratio the server measured
    internal static long ScaledPromptEstimate(int estimate, double ratio) =>
        estimate <= 0 ? 0 : (long)Math.Round(estimate * Math.Max(1.0, ratio));

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
        IReadOnlyList<ImageRef>? images = null, Func<string?>? serverGone = null)
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
            //a recorded server that died names the cause and both commands, so its line replaces the client's error instead of repeating the start
            renderer.OnWarning(serverGone?.Invoke() ?? ex.Message);
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
    internal static CommandEchoItem BannerItem(Theme theme, string roleName, IReadOnlyList<string>? roleCat, string modelName, string? versionLine,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        var rows = new List<string>();
        //the cat comes from the glyph set too, so it is a call rather than a literal. a legacy console can't draw it, and a census can't see the call
        foreach (var catLine in Cats.For(roleCat, g).Split('\n')) rows.Add(theme.Paint(catLine, Theme.Accent));
        rows.Add("");   //blank row between the cat art and the info lines
        //the caller builds the version tail, Repl cannot see Gatto.Cli where the dev-build definition lives, so GattoApp resolves it once and passes the string down
        var ver = versionLine ?? ("v" + Gatto.Core.GattoVersion.String
            + (Gatto.Core.GattoVersion.Build is { Length: > 0 } b ? " " + g.Dot + " build " + b : ""));
        var sep = " " + g.Dot + " ";
        rows.Add(theme.Paint("gatto", Theme.Accent, bold: true) + theme.Paint(sep, Theme.Dim)
            + theme.Paint(roleName, theme.RoleTint(roleName)) + theme.Paint(sep, Theme.Dim)
            + Gatto.Core.Models.ServedModelName.Display(modelName)
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

    //the face a session runs, GattoApp asks it too, since only the rich face covers what stderr printed before the banner
    public static bool RichFace(TermCaps caps, bool altScreen) => caps.Rich && altScreen;

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

    //called once, when the session first waits for input, which is where the launch span closes
    public Action? OnReady { get; set; }

    private void Ready()
    {
        var ready = OnReady;
        OnReady = null;
        ready?.Invoke();
    }

    public async Task<int> RunAsync(CancellationToken appCt)
    {
        //a session's arming site, -p arms its own and run_agent only elides. a project file can only veto, an edit builds the behaviour driver
        var armedAt = ArmedAutoCompactAt(autoCompact, cwd);
        loop.EnableAutoCompact(armedAt is null ? null : new AutoCompactionHandler(this), _usageState, armedAt);

        var caps = TermCaps.Detect();
        //a rich TTY still takes the plain renderer when the user turns the alt screen off
        return RichFace(caps, altScreen)
            ? await RunRichAsync(caps, appCt)
            : await RunPlainAsync(appCt);
    }
}
