using System.Runtime.InteropServices;

namespace Gatto.Terminal;

//reads raw console input records, seamed so the drain, expand and peek logic is tested against a fake
public interface IConsoleInputReader
{
    int Read(INPUT_RECORD[] buf);    //blocks until at least one record, and returns how many it wrote
    int Peek(INPUT_RECORD[] buf);    //reads nothing, and returns how many records are available up to buf.Length
}

//the real input source: ReadConsoleInput drained, each record translated, a held key expanded into N events. the key-down peek is non-consuming
public sealed class ConsoleInputSource(IConsoleInputReader reader) : IInputSource
{
    private readonly Queue<InputEvent> _pending = new();
    private readonly INPUT_RECORD[] _batch = new INPUT_RECORD[64];   //read a whole burst, so the moves inside one drag coalesce
    private readonly INPUT_RECORD[] _peek = new INPUT_RECORD[16];
    private uint _prevButtons;
    private MouseEvent? _heldMove;   //holds the latest of a run of consecutive Move records, flushed when the run ends or the batch does
    private long _recordsRead;

    //every raw record the reader consumed, including the ones the translator discards
    public long RecordsRead => Interlocked.Read(ref _recordsRead);

    public InputEvent Read()
    {
        while (_pending.Count == 0)
        {
            var n = reader.Read(_batch);
            //a console that returns 0 is dead, and the throw is what stops the loop spinning a core forever
            if (n <= 0) throw new InvalidOperationException("console input closed");
            Interlocked.Add(ref _recordsRead, n);
            for (var i = 0; i < n; i++) Translate(_batch[i]);
            FlushHeldMove();   //deliver the last Move of the batch now, drag latency is something the user feels
        }
        return _pending.Dequeue();
    }

    private void Translate(in INPUT_RECORD rec)
    {
        if (rec.EventType == NativeInput.EventKey)
        {
            if (NativeInput.TranslateKey(rec.KeyEvent) is { } key)
            {
                FlushHeldMove();   //flush the held Move run before this key, so the order survives
                var reps = rec.KeyEvent.wRepeatCount < 1 ? 1 : rec.KeyEvent.wRepeatCount;
                for (var r = 0; r < reps; r++) _pending.Enqueue(new KeyEvent(key));   //one key event per repeat count
            }
        }
        else if (rec.EventType == NativeInput.EventMouse)
        {
            if (NativeInput.TranslateMouse(rec.MouseEvent, ref _prevButtons) is { } mouse)
            {
                //collapse a run of Moves to its latest, and flush the run before a press, release or wheel so the release is never dropped
                if (mouse.Kind == MouseKind.Move) _heldMove = mouse;
                else { FlushHeldMove(); _pending.Enqueue(mouse); }
            }
        }
        //window-buffer-size, focus and menu events are ignored
    }

    private void FlushHeldMove()
    {
        if (_heldMove is { } m) { _pending.Enqueue(m); _heldMove = null; }
    }

    //peeks the OS buffer and leaves the pending queue alone. true when a translator-kept record is a key-down, and Console.KeyAvailable would consume what it sees
    public bool KeyDownAvailable
    {
        get
        {
            var n = reader.Peek(_peek);
            for (var i = 0; i < n; i++)
                if (_peek[i].EventType == NativeInput.EventKey && NativeInput.TranslateKey(_peek[i].KeyEvent) is not null)
                    return true;
            return false;
        }
    }

    //a wait ends on what the wizard acts on. the OS peek holds 16 records and a press behind them is invisible to it, so the buffer is drained into the queue first
    public bool EventAvailable
    {
        get
        {
            DropMovesAndReleases();
            DrainWithoutBlocking(WaitRounds);
            DropMovesAndReleases();
            return _pending.Count > 0;
        }
    }

    //the pointer's newest position without blocking, so a hover can follow it while the wait itself still ignores moves. only the moves ahead of the first other event, so a key pressed before the pointer crossed acts first
    public MouseEvent? TakeLatestMove()
    {
        DrainWithoutBlocking(WaitRounds);
        MouseEvent? latest = null;
        while (_pending.TryPeek(out var e) && e is MouseEvent { Kind: MouseKind.Move } m)
        {
            _pending.Dequeue();
            latest = m;
        }
        return latest;
    }

    //keys survive a screen change as type-ahead, a press on the old screen must not reach the new one
    public void DropMouse()
    {
        DrainWithoutBlocking(DropRounds);
        Keep(e => e is not MouseEvent);
    }

    //a wait answers within a poll, so 8 peeked rounds bound a pointer that keeps moving. a drop runs longer, since a stale press it misses reaches the next screen as a live one
    private const int WaitRounds = 8, DropRounds = 64;

    private void DropMovesAndReleases() => Keep(e => e is not MouseEvent { Kind: MouseKind.Move or MouseKind.Release });

    private void Keep(Func<InputEvent, bool> keep)
    {
        for (var i = _pending.Count; i > 0; i--)
        {
            var e = _pending.Dequeue();
            if (keep(e)) _pending.Enqueue(e);
        }
    }

    //read exactly what the peek counted, so one round reads at most a peek's worth. the read cannot block, the peek saw records
    private void DrainWithoutBlocking(int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            var n = reader.Peek(_peek);
            if (n <= 0) return;
            var buf = new INPUT_RECORD[n];
            var got = reader.Read(buf);
            if (got <= 0) return;
            Interlocked.Add(ref _recordsRead, got);
            for (var i = 0; i < got; i++) Translate(buf[i]);
            FlushHeldMove();
        }
    }
}

//the real reader over STD_INPUT_HANDLE, using the Unicode entry point so accented, CJK and surrogate-pair characters marshal correctly
public sealed class Win32ConsoleInputReader : IConsoleInputReader
{
    private const int STD_INPUT_HANDLE = -10;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleInput(nint h, [Out] INPUT_RECORD[] buf, uint len, out uint read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PeekConsoleInput(nint h, [Out] INPUT_RECORD[] buf, uint len, out uint read);

    private readonly nint _h = GetStdHandle(STD_INPUT_HANDLE);

    public int Read(INPUT_RECORD[] buf)
    {
        //throw when the call fails, a dead console handle is the end of the session. a failed peek returns 0, and a false answer there is harmless
        if (!ReadConsoleInput(_h, buf, (uint)buf.Length, out var n))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return (int)n;
    }

    public int Peek(INPUT_RECORD[] buf) { PeekConsoleInput(_h, buf, (uint)buf.Length, out var n); return (int)n; }
}
