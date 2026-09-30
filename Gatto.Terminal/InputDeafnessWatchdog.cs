using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gatto.Terminal;

//writes records into the console input queue that the pump's reader drains. a seam, so a test can feed a fake reader's queue instead.
public interface IConsoleInputWriter
{
    bool Write(INPUT_RECORD record);
}

//the real writer over STD_INPUT_HANDLE (a redirected handle has no reader for a record)
public sealed class Win32ConsoleInputWriter : IConsoleInputWriter
{
    private const int STD_INPUT_HANDLE = -10;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleInput(nint h, INPUT_RECORD[] buf, uint len, out uint written);

    private readonly nint _h = GetStdHandle(STD_INPUT_HANDLE);

    public bool Write(INPUT_RECORD record) => WriteConsoleInput(_h, [record], 1, out var n) && n == 1;
}

//proves the console reader still drains input while gatto waits. it writes a record the translator drops and watches the reader's count move.
public sealed class InputDeafnessWatchdog
{
    //no number in the row (no fixed timing may reach a screen)
    public const string Row = "keys stopped reaching gatto";

    internal static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    private const ushort VkF14 = 0x7D;

    private readonly Func<long> _recordsRead;
    private readonly IConsoleInputWriter _writer;
    private readonly TimeSpan _every;
    private readonly TimeSpan _bound;
    private readonly object _lock = new();
    private Action<string>? _deafSink;
    private Timer? _timer;
    private int _armed;
    private int _checking;
    private bool _fired;

    internal InputDeafnessWatchdog(Func<long> recordsRead, IConsoleInputWriter writer, TimeSpan every, TimeSpan bound)
    {
        _recordsRead = recordsRead;
        _writer = writer;
        _every = every;
        _bound = bound;
    }

    //the only way to build one in production. other sources can't count the tracer, and a redirected stdin has no console to write into
    public static InputDeafnessWatchdog? For(IInputSource source, bool inputRedirected, Func<IConsoleInputWriter> writer) =>
        source is ConsoleInputSource console && !inputRedirected
            ? new InputDeafnessWatchdog(() => console.RecordsRead, writer(), CheckEvery, Bound)
            : null;

    //a key-up of a key no keyboard has. the translator drops every key-up but Alt, so the composer never sees it.
    public static INPUT_RECORD Tracer() => new()
    {
        EventType = NativeInput.EventKey,
        KeyEvent = new KEY_EVENT_RECORD { bKeyDown = 0, wRepeatCount = 1, wVirtualKeyCode = VkF14 },
    };

    public void SetDeafSink(Action<string>? sink) { lock (_lock) _deafSink = sink; }

    internal int ArmedDepth { get { lock (_lock) return _armed; } }

    //arming nests (a modal wait opens inside a turn, and both must close before the checks stop)
    public void Arm()
    {
        lock (_lock)
        {
            if (_armed++ == 0 && !_fired) _timer = new Timer(_ => Tick(), null, _every, _every);
        }
    }

    //never waits for a check in flight (the caller may hold the render gate that the deaf sink takes)
    public void Disarm()
    {
        lock (_lock)
        {
            if (_armed == 0) return;
            if (--_armed == 0) { _timer?.Dispose(); _timer = null; }
        }
    }

    //true when the reader consumed a record within the bound. a write that failed proves nothing, so it never reads as deaf.
    internal bool Check()
    {
        var before = _recordsRead();
        if (!_writer.Write(Tracer())) return true;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _bound)
        {
            if (_recordsRead() != before) return true;
            Thread.Sleep(5);
        }
        return _recordsRead() != before;
    }

    internal void Tick()
    {
        lock (_lock) { if (_armed == 0 || _fired) return; }
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            if (Check()) return;
            Action<string>? sink;
            lock (_lock)
            {
                if (_armed == 0 || _fired) return;
                _fired = true;
                _timer?.Dispose();
                _timer = null;
                sink = _deafSink;
            }
            sink?.Invoke(Row);
        }
        finally { Volatile.Write(ref _checking, 0); }
    }
}
