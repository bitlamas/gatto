using System.Collections.Concurrent;

namespace Gatto.Terminal;

//scroll actions for the viewport, routed to the global sink before the focus stack, so they work while a modal holds focus
public enum ScrollKey { PageUp, PageDown, Top, Bottom }

//a consumed verdict stops routing here, a pass-through falls to the normal path. esc still reaches the composer and a ctrl+c with no selection dies harmlessly
public enum KeyDisposition { Consumed, PassThrough }

//one thread owns input and routes each key by the focus held at arrival, so nothing replays or gets eaten later. ctrl+c doesn't pass here, CancelKeyPress does
public sealed class InputPump
{
    private const int MaxConsecutiveReadFailures = 100;

    private readonly IInputSource _source;
    private readonly object _lock = new();
    //keys and mouse gestures share this one channel, arrival order intact, the editor thread is the only selection writer
    private readonly BlockingCollection<ComposerInput> _composer = new();
    private readonly List<BlockingCollection<ConsoleKeyInfo>> _stack = new();   //the last channel is the focused one
    private Thread? _thread;
    private bool _dead;
    private Action<ScrollKey>? _scrollSink;   //global scroll sink, consulted before the focus stack
    private Action<FocusKey>? _focusSink;     //global focus sink, also consulted before the stack
    private Action? _wildSink;                //wild-mode toggle on shift+tab, same tier as the others
    private Action<MouseEvent>? _mouseSink;   //the mouse sink, it bypasses the focus stack entirely
    private Func<ConsoleKeyInfo, KeyDisposition>? _cancelSink;   //cancel and copy for esc and ctrl+c, runs before the stack only when no modal is up
    private bool _dragging;                   //a left drag without shift is in flight, loop-thread-only, so no lock
    private Action<string>? _inputFault;        //notice that a key or mouse dispatch threw, fired once
    private bool _faultWarned;                  //latch, a throwing key must not produce a row per press
    private Action<ConsoleKeyInfo>? _keySink;   //notification that a key arrived, it doesn't consume the key
    private Action? _modalPushSink;             //notification that a modal scope opened, nothing consumed
    private Action<bool>? _modalWaitSink;       //modal focus opened (true) or closed (false), this pauses the turn clock

    public InputPump(IInputSource source)
    {
        _source = source;
        Composer = new ChannelComposerSource(_composer, () => _source.KeyDownAvailable);
    }

    //the composer's source, Read blocks until the pump routes a key or gesture. once the pump is dead it throws, LineEditor turns that into its backstop
    public IComposerSource Composer { get; }

    //test-only: keys routed into the channel but not yet read. the source peek can fire while a key is still in the source, this count rises only after routing
    internal int ComposerPending => _composer.Count;

    //idempotent, the thread is a background one so it never blocks exit
    public void Start()
    {
        lock (_lock)
        {
            if (_thread is not null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "gatto-input-pump" };
            _thread.Start();
        }
    }

    //keys arriving while the scope is open go only to it. dispose discards unread keys, replaying a modal answer into the composer is the bug
    public FocusScope PushFocus()
    {
        var ch = new BlockingCollection<ConsoleKeyInfo>();
        Action? modalPushSink;
        Action<bool>? modalWaitSink;
        lock (_lock)
        {
            //pushed before Start it can receive no key, so the scope is born completed and the first read throws. a hang is the worst shape a defect takes
            if (_dead || _thread is null) ch.CompleteAdding();
            else _stack.Add(ch);
            modalPushSink = _modalPushSink;   //read under the lock, fired outside it
            modalWaitSink = _modalWaitSink;
        }
        modalPushSink?.Invoke();   //fired outside the lock, a caller sink under it can invert lock order
        //fires on every push, the born-completed scope included, so the pop pairs stay balanced
        modalWaitSink?.Invoke(true);
        return new FocusScope(this, ch);
    }

    //hands a key to the composer as if typed. kept because InputPumpTests pins that contract
    public void InjectToComposer(ConsoleKeyInfo k)
    {
        try { _composer.Add(new ComposerInput.Key(k)); } catch (InvalidOperationException) { } //pump dead, drop it
    }

    //gestures go onto the same channel as keys, arrival order kept, one writer of selection. the coordinates must already be resolved, this only enqueues
    public void EnqueueComposerGesture(ComposerInput.Select gesture)
    {
        try { _composer.Add(gesture); } catch (InvalidOperationException) { } //pump dead, drop it
    }

    private void Pop(BlockingCollection<ConsoleKeyInfo> ch)
    {
        Action<bool>? modalWaitSink;
        lock (_lock)
        {
            _stack.Remove(ch);
            modalWaitSink = _modalWaitSink;
        }
        ch.CompleteAdding();
        modalWaitSink?.Invoke(false);   //outside the lock, exactly like the push half
    }

    //scroll keys go to this sink before the focus stack, so paging works during a modal. plain home and end aren't scroll keys and reach the composer
    public void SetScrollSink(Action<ScrollKey>? sink) { lock (_lock) _scrollSink = sink; }

    //the notice that dispatch threw fires once per session. a key that always fails would otherwise make a row per press
    public void SetInputFaultSink(Action<string>? sink) { lock (_lock) _inputFault = sink; }

    //ctrl-arrows are consumed before the focus stack, so they work under a modal. a prompt reads y/n/a and a picker plain arrows, no collision
    public void SetFocusSink(Action<FocusKey>? sink) { lock (_lock) _focusSink = sink; }

    //the wild toggle fires at any moment, even under a modal. routed after scroll and focus so it can't shadow them, before the stack so prompts never see it
    public void SetWildSink(Action? sink) { lock (_lock) _wildSink = sink; }

    //mouse events skip the focus stack and come only here. only Press and Wheel dispatch, Move and Release drop so pointer travel can't flood the pump
    public void SetMouseSink(Action<MouseEvent>? sink) { lock (_lock) _mouseSink = sink; }

    //ctrl+c is consulted even under a modal, copying text doesn't answer a prompt. esc is consulted only when no modal holds focus, prompts keep their own cancel
    public void SetCancelSink(Func<ConsoleKeyInfo, KeyDisposition>? sink) { lock (_lock) _cancelSink = sink; }

    //fired for every key, first, and the key goes on routing as before. it lets a consumer learn the user moved on
    public void SetKeySink(Action<ConsoleKeyInfo>? sink) { lock (_lock) _keySink = sink; }

    //fires every time a modal scope opens, pure notification. a modal changes the context of any in-flight gesture
    public void SetModalPushSink(Action? sink) { lock (_lock) _modalPushSink = sink; }

    //reports modal focus opening and closing so the turn clock can pause. every push fires true and every dispose false, nesting counts are the consumer's job
    public void SetModalWaitSink(Action<bool>? sink) { lock (_lock) _modalWaitSink = sink; }

    //test-only: whether anything listens for the modal edges. it lets a test run the loop to pin the wiring line
    internal bool HasModalWaitSink { get { lock (_lock) return _modalWaitSink is not null; } }

    //alt must not be down: on Windows AltGr is Ctrl+Alt, so a pasted ç typed as AltGr+C must not count as copy
    private static bool IsCopyChord(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control) && !k.Modifiers.HasFlag(ConsoleModifiers.Alt);

    private static ScrollKey? ScrollKeyOf(ConsoleKeyInfo k) => k.Key switch
    {
        ConsoleKey.PageUp => ScrollKey.PageUp,
        ConsoleKey.PageDown => ScrollKey.PageDown,
        //home and End scroll the whole transcript, ctrl accepted but not required. the composer keeps the arrow keys for line editing
        ConsoleKey.Home => ScrollKey.Top,
        ConsoleKey.End => ScrollKey.Bottom,
        _ => null,
    };

    private void Loop()
    {
        var failures = 0;
        while (true)
        {
            InputEvent ev;
            //don't reset failures on read, success is read plus dispatch. resetting early makes the ceiling unreachable for a sink that always faults
            try { ev = _source.Read(); }
            catch (Exception)
            {
                if (++failures >= MaxConsecutiveReadFailures) { Kill(); return; }
                Thread.Sleep(2);   //a fast-failing source must not spin a core
                continue;
            }

            //an escaping exception here kills the process, no backstop on this thread. same policy and same counter as the read, twins drift otherwise
            try
            {
                switch (ev)
                {
                    case KeyEvent k:
                        RouteKey(k.Key);
                        break;
                    case MouseEvent m:
                        HandleMouse(m);
                        break;
                }
                failures = 0;
            }
            catch (Exception)
            {
                Action<string>? fault = null;
                lock (_lock)
                {
                    if (!_faultWarned) { _faultWarned = true; fault = _inputFault; }
                }
                //fires outside the lock, the sink is Repl-owned and takes the render gate
                if (fault is not null)
                    try { fault("an input event could not be handled, continuing"); } catch (Exception) { }
                if (++failures >= MaxConsecutiveReadFailures) { Kill(); return; }
            }
        }
    }

    //a left Press without shift arms a drag, Release disarms. loop-thread-only, so _dragging needs no lock, the sink fires outside it
    private void HandleMouse(MouseEvent m)
    {
        var wasDragging = _dragging;
        if (m.Kind == MouseKind.Press && m.Button == MouseButton.Left && !m.Modifiers.HasFlag(ConsoleModifiers.Shift))
            _dragging = true;
        else if (m.Kind == MouseKind.Release)
            _dragging = false;

        var forward = m.Kind switch
        {
            MouseKind.Press or MouseKind.Wheel => true,
            MouseKind.Move => wasDragging,
            MouseKind.Release => wasDragging,
            _ => false,
        };
        if (!forward) return;
        Action<MouseEvent>? sink;
        lock (_lock) sink = _mouseSink;
        sink?.Invoke(m);
    }

    //notice first, then cancel, then global sinks before the focus stack, else the top scope or composer. sinks fire outside _lock to keep lock order
    private void RouteKey(ConsoleKeyInfo k)
    {
        //this fires for every key before anything can short-circuit, and doesn't change where the key goes
        Action<ConsoleKeyInfo>? keySink;
        lock (_lock) keySink = _keySink;
        keySink?.Invoke(k);

        //ctrl+c always consults, esc only with no modal up. the accepted race mis-routes one Esc and self-corrects on the next press
        Func<ConsoleKeyInfo, KeyDisposition>? cancelSink = null;
        lock (_lock)
            if (_cancelSink is { } cs && (IsCopyChord(k) || (k.Key == ConsoleKey.Escape && _stack.Count == 0)))
                cancelSink = cs;
        if (cancelSink is not null && cancelSink(k) == KeyDisposition.Consumed) return;

        Action<ScrollKey>? scrollSink = null;
        var scrollKey = default(ScrollKey);
        Action<FocusKey>? focusSink = null;
        var focusKey = default(FocusKey);
        Action? wildSink = null;
        lock (_lock)
        {
            if (_scrollSink is { } sink && ScrollKeyOf(k) is { } sk)
            {
                scrollSink = sink; scrollKey = sk;
            }
            else if (_focusSink is { } fsink && FocusKeys.Of(k) is { } fk)
            {
                focusSink = fsink; focusKey = fk;
            }
            //after scroll and focus so it can't shadow them, before the stack so modals never see it
            else if (_wildSink is { } wsink && WildKeys.Of(k))
            {
                wildSink = wsink;
            }
            else if (_stack.Count > 0)
            {
                try { _stack[^1].Add(k); }
                catch (InvalidOperationException) { } //defensive, Add runs under _lock, and a stacked channel is removed under _lock before or together with its completion
            }
            else
            {
                //keys wrap as ComposerInput.Key here, gestures enter the channel only via EnqueueComposerGesture
                try { _composer.Add(new ComposerInput.Key(k)); }
                catch (InvalidOperationException) { } //defensive, same rationale as the stack Add above
            }
        }
        scrollSink?.Invoke(scrollKey);
        focusSink?.Invoke(focusKey);
        wildSink?.Invoke();
    }

    private void Kill()
    {
        lock (_lock)
        {
            _dead = true;
            try { _composer.CompleteAdding(); } catch (ObjectDisposedException) { }
            foreach (var ch in _stack)
                try { ch.CompleteAdding(); } catch (ObjectDisposedException) { }
            _stack.Clear();
        }
    }

    public sealed class FocusScope : IDisposable
    {
        private readonly InputPump _pump;
        private readonly BlockingCollection<ConsoleKeyInfo> _ch;

        internal FocusScope(InputPump pump, BlockingCollection<ConsoleKeyInfo> ch)
        {
            _pump = pump; _ch = ch;
            Keys = new ChannelKeySource(ch, () => pump._source.KeyDownAvailable);   //the scope's peek still reads the source
        }

        public IKeySource Keys { get; }

        //born completed on a dead or unstarted pump, no key can ever arrive. assert this state, don't wait for a key that never comes
        internal bool BornCompleted => _ch.IsCompleted;

        //test-only: keys routed into this channel but unread. wait here rather than on the peek, which can fire before routing
        internal int PendingCount => _ch.Count;

        public void Dispose() => _pump.Pop(_ch);
    }

    private sealed class ChannelKeySource(BlockingCollection<ConsoleKeyInfo> ch, Func<bool> sourcePeek) : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => ch.Take();

        //the read's only bound is the turn token. that token cancels only on the user's ctrl+break, the one bound a tool-path wait may take
        public ConsoleKeyInfo ReadKey(CancellationToken ct) => ch.Take(ct);

        //channel backlog or a non-consuming source peek, the backlog alone misses a paste still in the buffer. the catch covers redirected stdin, where the peek throws
        public bool KeyAvailable
        {
            get
            {
                if (ch.Count > 0) return true;
                try { return sourcePeek(); }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    //same backlog-or-peek shape as the key source. a pending gesture counts as input too, the backlog is read regardless of kind
    private sealed class ChannelComposerSource(BlockingCollection<ComposerInput> ch, Func<bool> sourcePeek) : IComposerSource
    {
        public ComposerInput Read() => ch.Take();

        public bool KeyAvailable
        {
            get
            {
                if (ch.Count > 0) return true;
                try { return sourcePeek(); }
                catch (InvalidOperationException) { return false; }
            }
        }
    }
}
