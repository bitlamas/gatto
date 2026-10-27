//drive the real Repl loop headlessly, with an injected terminal surface. it pins only what the live loop shows, so decisions stay in the seam tests.
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Prompts;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;

namespace Gatto.Tests.Fakes;

//reveal the next key only after the editor samples KeyAvailable and reads false. a script queued at once reads as a paste, which the editor swallows.
public sealed class ScriptedKeySource : IKeySource
{
    private readonly object _lock = new();
    private readonly Queue<ConsoleKeyInfo> _keys = new();

    //set once a key leaves, cleared when the editor samples availability. until then no key is revealed and KeyAvailable answers false
    private bool _awaitingSample;

    //a timeout while this is set names a modal consumer that took a key, rather than an ordinary script overrun
    internal bool AwaitingSample { get { lock (_lock) return _awaitingSample; } }

    //a pending sample answers false and releases the next key, otherwise this only peeks at the remaining script
    public bool KeyAvailable
    {
        get
        {
            lock (_lock)
            {
                if (!_awaitingSample) return _keys.Count > 0;
                _awaitingSample = false;
                Monitor.PulseAll(_lock);
                OnChange?.Invoke();
                return false;
            }
        }
    }

    //set by Close, after which every read throws, so the pump stops and the composer read it feeds ends with it
    private bool _closed;

    public ConsoleKeyInfo ReadKey()
    {
        lock (_lock)
        {
            //the 5-ms wait is only a re-check interval, since the predicate is the gate. blocking on an empty script is deliberate, since a sentinel spins the composer.
            while (!_closed && (_awaitingSample || _keys.Count == 0)) Monitor.Wait(_lock, 5);
            if (_closed) throw new InvalidOperationException("the key script is closed");
            _awaitingSample = true;
            var key = _keys.Dequeue();
            OnChange?.Invoke();
            return key;
        }
    }

    //ends the script once the run is over. a pump left blocked here holds its thread, and the composer read behind it a pool thread, for the rest of the test run
    public void Close()
    {
        lock (_lock) { _closed = true; Monitor.PulseAll(_lock); }
    }

    //one keystroke with no Enter behind it, for a key that is not a line
    public ScriptedKeySource Key(ConsoleKeyInfo key)
    {
        lock (_lock) { _keys.Enqueue(key); Monitor.PulseAll(_lock); }
        return this;
    }

    //keys not read yet, so a test can wait until the script has reached the editor
    public int Pending { get { lock (_lock) return _keys.Count; } }

    //every key has left and the editor sampled after the last, which it does once a key is handled and a panel never does
    public bool Handled { get { lock (_lock) return _keys.Count == 0 && !_awaitingSample; } }

    //called when a key leaves and when the editor samples, so a waiting test wakes on the key
    public Action? OnChange { get; set; }

    //the typed text plus an Enter keystroke, so the result is one submitted line
    public ScriptedKeySource Line(string text)
    {
        lock (_lock)
        {
            foreach (var c in text) _keys.Enqueue(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
            _keys.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
            Monitor.PulseAll(_lock);
        }
        return this;
    }
}

//a thread-safe tap that records every write in order. tests wait on it, since polling the viewport would race the painter's concurrent updates
internal sealed class TappedSurface(VtScreenSurface inner, Changes changes) : ITermSurface
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _writes = new();

    public int Width => inner.Width;
    public int Height => inner.Height;

    public void Write(string s)
    {
        _writes.Enqueue(s);
        inner.Write(s);
        changes.Mark();
    }

    public string Text() => TermText.StripAnsiForWidth(string.Concat(_writes));

    //every byte as written, escapes included, for a test that asks which modes the session set
    public string Raw() => string.Concat(_writes);
}

//end a run with /quit only when the session sends, since a refused message returns to the composer. use a wait-until run, which stops at a condition.
public sealed class RichReplHarness
{
    public VtScreenSurface Surface { get; }
    //expose the pump so a test can assert what the loop set up on it, instead of grepping the source.
    public InputPump Pump { get; }
    public ScriptedKeySource Keys { get; } = new();
    public FakeChatClient Client { get; } = new();
    public WarningSink Warn { get; }
    public Conversation Convo { get; }
    //the loop the session runs on. a test arms it as the interactive entry would, since the harness starts past that entry.
    public AgentLoop Loop { get; }

    //fully qualified, since Repl alone binds to the namespace
    private readonly Gatto.Repl.Repl _repl;
    private readonly List<string> _warnings = new();
    private readonly TappedSurface _tap;

    //fired by every write, request, key and attach question, the things a run-until condition reads
    private readonly Changes _changes = new();

    //the list stays empty while the loop runs, since the loop routes warnings to the renderer. assert live warnings from the screen text
    public IReadOnlyList<string> Warnings => _warnings;

    //null returns the fixed launch prompt. a warning raised inside this callback appears during a rotation, and the rotation's display reset must not erase it.
    public Func<string>? Recompose { get; set; }

    //every seam is injectable, so a test can pin an absence or a real failure. a null modeControl keeps the real win32 control, which a test must never reach.
    public RichReplHarness(string cwd, string systemPrompt = "sys", int width = 100, int height = 30,
        bool mouseEnabled = false, Gatto.Terminal.IConsoleModeControl? modeControl = null,
        ToolRegistry? tools = null, TurnAbortHandle? turnAbort = null, IKeySource? keySource = null,
        bool? visionAvailable = true, bool dumpOnExit = false, string? unmanagedNotice = null,
        string? unmanagedVisionNotice = null,
        //the parameter takes a recording ITerminalTitle, so a test can read what the loop titled and when through the same seam production uses
        Gatto.Terminal.ITerminalTitle? title = null,
        //the configured threshold the session was built with, which the harness never arms, since only the interactive entry arms the loop
        int? contextBudget = null, double? autoCompact = null,
        Gatto.Terminal.InputDeafnessWatchdog? deafWatch = null,
        //a resumed session, passed straight through as the interactive entry passes them
        string? resumedFrom = null, string? resumedPath = null, string? resumeLine = null,
        //the grants store, absent unless a test hands one in, as a session without one has no grants
        Gatto.Core.Loop.Permissions.PermissionStore? permissions = null,
        //what /context counts, absent unless a test hands it in, as a session without it reports the figures unavailable
        Func<ContextUsageState, ContextInputs>? contextInputs = null,
        //the hooks the loop runs and the handle a prompt draws through, absent unless a test wires a real prompter, as the interactive entry does
        HookBus? hooks = null, Gatto.Repl.Render.Chrome.ChromeHandle? chrome = null,
        //the commands extensions registered, absent unless a test hands a registry in, as a home with no extension has none
        Gatto.Core.Commands.CommandRegistry? commands = null,
        //the warnings the launch raised before the banner, absent unless a test hands them in, as a clean launch raises none
        IReadOnlyList<string>? launchWarnings = null,
        //the clipboard Alt+V reads, absent unless a test pastes, as the real one would read this machine's clipboard
        Gatto.Terminal.IClipboardImageNative? clipboardImages = null,
        //the wild state and its seams, passed straight through as the interactive entry passes them, absent as a session with no wild state
        Gatto.Core.Loop.Permissions.WildState? wild = null, Action<bool>? persistWild = null, Func<bool>? wildPersisted = null, bool wildFromHome = false,
        //the home's default role and the /role seam, absent as a session that names its role and has no roles to switch to
        string? defaultRole = null, Func<string, RoleSwitchResult>? switchRole = null,
        //the launched role and its file's colours and rows, the generalist with none as a bare launch from a fresh home
        string roleName = "generalist", (RgbColor Dark, RgbColor? Light)? roleTint = null, IReadOnlyList<string>? roleCat = null,
        string? firstMessage = null,
        //the process's sandbox, absent as off Windows
        Gatto.Core.Sandbox.SandboxState? sandbox = null,
        //the loop's trace, absent as an untraced session, so a test can read what a traced rich turn wrote
        Gatto.Core.Trace? trace = null,
        //the session store, absent as a session that saves nothing, so a test can open a resumed identity or read a name
        SessionStore? sessions = null,
        //the line the launch says when the server held something other than the default, absent as a launch on the default
        string? servedNotice = null,
        //the wild line the launch says, absent as a launch with wild off and nothing forbidding it
        string? wildNotice = null,
        //the sandbox line the launch says, absent as a launch with the mode off
        string? sandboxNotice = null)
    {
        Surface = new VtScreenSurface(width, height);
        _tap = new TappedSurface(Surface, _changes);
        Client.OnRequest = _changes.Mark;
        Keys.OnChange = _changes.Mark;
        Warn = new WarningSink(m => _warnings.Add(m));
        Convo = new Conversation(systemPrompt);

        var loop = Loop = new AgentLoop(Client, tools ?? new ToolRegistry(), hooks ?? new HookBus(),
            _context = new HarnessToolContext(cwd), "test-model", trace: trace);

        _repl = new Gatto.Repl.Repl(
            loop, Convo, roleName: roleName, modelName: "test-model",
            sessions: sessions, client: Client, configContext: 8192, contextBudget: contextBudget, cwd: cwd,
            permissions: permissions,
            //the closure is assigned after construction so it can see the harness. a test sets it to make the recompose warn or count calls.
            recomposeSystem: () => Composed.Text(Recompose?.Invoke() ?? systemPrompt),
            wild: wild, persistWild: persistWild, wildPersisted: wildPersisted, wildFromHome: wildFromHome, sandbox: sandbox,
            switchRole: switchRole ?? (_ => new RoleSwitchResult(false, "", null, null, "no roles in the harness")),
            defaultRole: defaultRole,
            pump: Pump = new InputPump(new KeyInputSource(keySource ?? Keys)),
            warn: Warn,
            mouseEnabled: mouseEnabled,
            modeControl: modeControl,
            termSurface: _tap,
            //the blind-model guard reads this. false means no mmproj, and gatto must refuse to send image parts
            probeServing: () => new Gatto.Repl.ServingProbe(null,
                Gatto.Core.Client.ThinkCapability.None, visionAvailable),
            dumpOnExit: dumpOnExit,
            turnAbort: turnAbort,
            //a non-null notice makes this a session gatto does not serve itself.
            unmanagedNotice: unmanagedNotice,
            unmanagedVisionNotice: unmanagedVisionNotice,
            terminalTitle: title,
            autoCompact: autoCompact,
            deafWatch: deafWatch,
            resumedFrom: resumedFrom,
            resumedPath: resumedPath,
            resumeLine: resumeLine,
            contextInputs: contextInputs,
            chrome: chrome,
            commands: commands,
            launchWarnings: launchWarnings, servedNotice: servedNotice, sandboxNotice: sandboxNotice, wildNotice: wildNotice,
            clipboardImageSource: clipboardImages,
            roleTint: roleTint, roleCat: roleCat, firstMessage: firstMessage,
            armTasks: tasks => _context.Tasks = tasks);
    }

    //the tool context the loop runs on, which the rich face arms with its background tasks as the app context is armed
    private sealed class HarnessToolContext(string cwd) : IToolContext
    {
        public string Cwd => cwd;
        public string HomePath => cwd;
        public IUserPrompter? Prompter => null;
        public BackgroundTasks? Tasks { get; set; }
    }

    private readonly HarnessToolContext _context;

    //the session's background tasks while the rich loop runs, null before it arms them and after its teardown
    public BackgroundTasks? Tasks => _context.Tasks;

    //how the session ended, set when the rich loop returns
    public SessionEnd? Ended => _repl.Ended;

    //answers the attach-count question without a terminal. set it to a function of the SelectSpec, and null keeps the real widget.
    public Func<SelectSpec, SelectOutcome>? OnConfirmAttach
    {
        get => _repl.ConfirmAttachForTest;
        set => _repl.ConfirmAttachForTest = value is null ? null : spec => { var outcome = value(spec); _changes.Mark(); return outcome; };
    }

    //throws when the script does not drive the loop to its own exit. the rich loop returns 0 on cancellation, so a returned 0 says nothing about the run.
    public async Task<int> RunAsync(int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        int exit;
        try { exit = await _repl.RunRichAsync(new TermCaps(Rich: true, TrueColor: true), cts.Token); }
        finally { Keys.Close(); }
        if (cts.IsCancellationRequested)
            throw new TimeoutException(
                $"the rich loop did not exit within {timeoutMs}ms — the script never reached a quitting "
                + "command. Do NOT read the return value as a pass: RunRichAsync returns 0 on cancellation. "
                + (Keys.AwaitingSample
                    ? "NOTE: the key source is still awaiting an availability sample — a consumer took a key "
                      + "without consulting KeyAvailable (a MODAL prompt does this), so the pump is parked. "
                      + "See ScriptedKeySource's remarks; this needs a modal-aware release, not a delay. "
                    : "")
                + $"Final frame:\n{ScreenText()}");
        return exit;
    }

    //asks whether the session has written this text to the terminal. stripping ansi lets a phrase spanning a colour change still match.
    public bool Saw(string text) => _tap.Text().Contains(text, StringComparison.OrdinalIgnoreCase);

    //runs until start holds, then a measured window more, for a test of what does not happen. the window is the one wait here a clock owns
    public Task RunForAsync(Func<bool> start, TimeSpan window)
    {
        var opened = 0;
        var over = 0;
        return RunUntilAsync(() =>
        {
            if (start() && Interlocked.Exchange(ref opened, 1) == 0)
                _ = Task.Delay(window).ContinueWith(_ => { Volatile.Write(ref over, 1); _changes.Mark(); }, TaskScheduler.Default);
            return Volatile.Read(ref over) == 1;
        }, unsignalled: true);
    }

    //waits inside a running session for a condition on what it wrote or asked, woken by each change
    public Task<bool> UntilAsync(Func<bool> holds, TimeSpan bound) => _changes.UntilAsync(holds, bound);

    //everything the session wrote, in order and without ansi, for a test that asks what came after what
    public string Written() => _tap.Text();

    //the model the session believes it runs, which the banner row would hide since it keeps the launch name
    public string ModelName => _repl.ModelNameForTest;

    //the armed watch, null when nothing is watched, for a test of what a letter's count did
    internal Gatto.Repl.Watch? Watch => _repl.WatchForTest;

    //everything the session wrote with its escapes, for a test of the terminal modes it set
    public string RawWritten() => _tap.Raw();

    //ending by cancellation runs the teardown, so TranscriptDump holds the committed transcript. a scripted /quit behind a refusal would race the composer restore.
    public async Task RunUntilAsync(Func<bool> until, int timeoutMs = 15000, bool unsignalled = false)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var run = _repl.RunRichAsync(new TermCaps(Rich: true, TrueColor: true), cts.Token);
        await _changes.UntilAsync(until, TimeSpan.FromMilliseconds(timeoutMs), run, unsignalled);
        var timedOut = !until();
        cts.Cancel();
        try { await run; }
        finally { Keys.Close(); }
        if (timedOut)
            throw new TimeoutException(
                $"the condition never held within {timeoutMs}ms. Final frame:\n{ScreenText()}");
    }

    //use the last alt-screen frame, since leaving alt discards the buffer. a session that never entered alt falls back to the live viewport.
    public IReadOnlyList<string> FinalFrame() =>
        (Surface.AltFrameAtExit ?? Surface.Viewport).Select(TermText.StripAnsiForWidth).ToList();

    //the final frame as one string, so an assertion can match a substring
    public string ScreenText() => string.Join("\n", FinalFrame());

    //shows only committed rows, and requires dumpOnExit. the screen keeps typed composer text too, so only this dump tells committed from typed
    public string TranscriptDump() =>
        string.Join("\n", Surface.Viewport.Select(TermText.StripAnsiForWidth));
}
