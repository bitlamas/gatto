//drive the real Repl loop headlessly, with an injected terminal surface. it pins only what the live loop shows, so decisions stay in the seam tests.
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Terminal;

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
                return false;
            }
        }
    }

    public ConsoleKeyInfo ReadKey()
    {
        lock (_lock)
        {
            //the 5-ms wait is only a re-check interval, since the predicate is the gate. blocking on an empty script is deliberate, since a sentinel spins the composer.
            while (_awaitingSample || _keys.Count == 0) Monitor.Wait(_lock, 5);
            _awaitingSample = true;
            return _keys.Dequeue();
        }
    }

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
internal sealed class TappedSurface(VtScreenSurface inner) : ITermSurface
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _writes = new();

    public int Width => inner.Width;
    public int Height => inner.Height;

    public void Write(string s)
    {
        _writes.Enqueue(s);
        inner.Write(s);
    }

    public string Text() => TermText.StripAnsiForWidth(string.Concat(_writes));
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
        string? resumedFrom = null, string? resumedPath = null, string? resumeLine = null)
    {
        Surface = new VtScreenSurface(width, height);
        _tap = new TappedSurface(Surface);
        Warn = new WarningSink(m => _warnings.Add(m));
        Convo = new Conversation(systemPrompt);

        var loop = Loop = new AgentLoop(Client, tools ?? new ToolRegistry(), new HookBus(),
            new TestToolContext(cwd), "test-model");

        _repl = new Gatto.Repl.Repl(
            loop, Convo, roleName: "generalist", modelName: "test-model",
            sessions: null, client: Client, configContext: 8192, contextBudget: contextBudget, cwd: cwd,
            //the closure is assigned after construction so it can see the harness. a test sets it to make the recompose warn or count calls.
            recomposeSystem: () => Composed.Text(Recompose?.Invoke() ?? systemPrompt),
            toggleAuto: () => null,
            switchRole: _ => new RoleSwitchResult(false, "", null, null, "no roles in the harness"),
            pump: Pump = new InputPump(new KeyInputSource(keySource ?? Keys)),
            warn: Warn,
            mouseEnabled: mouseEnabled,
            modeControl: modeControl,
            termSurface: _tap,
            //the blind-model guard reads this. false means no mmproj, and gatto must refuse to send image parts
            probeServing: () => new Gatto.Repl.ServingProbe(null, null,
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
            resumeLine: resumeLine);
    }

    //answers the attach-count question without a terminal. set it to a function of the SelectSpec, and null keeps the real widget.
    public Func<SelectSpec, SelectOutcome>? OnConfirmAttach
    {
        get => _repl.ConfirmAttachForTest;
        set => _repl.ConfirmAttachForTest = value;
    }

    //throws when the script does not drive the loop to its own exit. the rich loop returns 0 on cancellation, so a returned 0 says nothing about the run.
    public async Task<int> RunAsync(int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var exit = await _repl.RunRichAsync(new TermCaps(Rich: true, TrueColor: true), cts.Token);
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

    //ending by cancellation runs the teardown, so TranscriptDump holds the committed transcript. a scripted /quit behind a refusal would race the composer restore.
    public async Task RunUntilAsync(Func<bool> until, int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var run = _repl.RunRichAsync(new TermCaps(Rich: true, TrueColor: true), cts.Token);
        while (!run.IsCompleted && !cts.IsCancellationRequested && !until())
            await Task.Delay(5);
        var timedOut = !until();
        cts.Cancel();
        await run;
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
