using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a wait ends on a key down, a press or a wheel, never on the moves and releases the wizard reads nothing from
public class ConsoleInputSourceEventAvailableTests
{
    private sealed class OneKeySource(char c) : IKeySource
    {
        private bool _read;
        public bool KeyAvailable => !_read;
        public ConsoleKeyInfo ReadKey() { _read = true; return new(c, ConsoleKey.A, false, false, false); }
    }

    [Fact]
    public void A_PRESS_ALONE_IS_AN_EVENT()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader([BatchConsoleReader.Press(10, 4)]));
        Assert.True(src.EventAvailable);
        Assert.Equal(MouseKind.Press, ((MouseEvent)src.Read()).Kind);
    }

    [Fact]
    public void A_PRESS_BEHIND_TWENTY_MOVES_IS_AN_EVENT()
    {
        var batch = Enumerable.Range(0, 20).Select(i => BatchConsoleReader.Move(i, 4)).Append(BatchConsoleReader.Press(20, 4)).ToArray();
        var src = new ConsoleInputSource(new BatchConsoleReader(batch));
        Assert.True(src.EventAvailable);
        Assert.Equal(MouseKind.Press, ((MouseEvent)src.Read()).Kind);
    }

    [Fact]
    public void A_FLUSHED_MOVE_AND_A_RELEASE_ARE_NOT_AN_EVENT()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader(
            [BatchConsoleReader.Press(1, 1), BatchConsoleReader.Move(2, 1, buttons: 1)], [BatchConsoleReader.Release(2, 1)]));
        src.Read();   //the press, and the batch's flushed move waits in the queue
        Assert.False(src.EventAvailable);
    }

    [Fact]
    public void A_KEY_WAITING_IN_THE_QUEUE_IS_AN_EVENT()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader([BatchConsoleReader.Key('a'), BatchConsoleReader.Key('b')]));
        src.Read();
        Assert.True(src.EventAvailable);
    }

    [Fact]
    public void A_WHEEL_IS_AN_EVENT()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader([BatchConsoleReader.Wheel(3, 3, -120)]));
        Assert.True(src.EventAvailable);
        Assert.Equal(-120, ((MouseEvent)src.Read()).WheelDelta);
    }

    [Fact]
    public void DROPPING_THE_MOUSE_KEEPS_THE_KEYS()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader(
            [BatchConsoleReader.Press(1, 1), BatchConsoleReader.Key('x'), BatchConsoleReader.Wheel(1, 1, 120)]));
        src.DropMouse();
        Assert.Equal('x', ((KeyEvent)src.Read()).Key.KeyChar);
        Assert.False(src.EventAvailable);
    }

    //the clock behind every wizard wait, over the real source, so a clock that polls key downs alone fails here
    [Fact]
    public void THE_CLOCK_ENDS_ITS_WAIT_ON_A_PRESS()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader([BatchConsoleReader.Press(1, 1)]));
        Assert.True(new Gatto.Cli.Setup.ConsolePollClock(src).WaitForKey(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void THE_CLOCK_IGNORES_MOVES_AND_RELEASES()
    {
        var src = new ConsoleInputSource(new BatchConsoleReader(
            [BatchConsoleReader.Press(1, 1), BatchConsoleReader.Release(1, 1)], [BatchConsoleReader.Move(2, 2)]));
        src.Read();
        Assert.False(new Gatto.Cli.Setup.ConsolePollClock(src).WaitForKey(TimeSpan.FromMilliseconds(100)));
    }

    //a reader that always has 16 more moves, as a pointer that never stops
    private sealed class EndlessMoves : IConsoleInputReader
    {
        public int Reads;
        public int Read(INPUT_RECORD[] buf)
        {
            if (++Reads > 10000) throw new InvalidOperationException("unbounded drain");
            return Fill(buf);
        }
        public int Peek(INPUT_RECORD[] buf) => Fill(buf);
        private static int Fill(INPUT_RECORD[] buf)
        {
            for (var i = 0; i < buf.Length; i++) buf[i] = BatchConsoleReader.Move(i, 1);
            return buf.Length;
        }
    }

    [Fact]
    public void ENDLESS_MOVES_CANNOT_HOLD_THE_WAIT()
    {
        var reader = new EndlessMoves();
        Assert.False(new ConsoleInputSource(reader).EventAvailable);
        Assert.InRange(reader.Reads, 1, 8);
    }

    //a press behind 200 moves sits past the wait's bound, and the drop at a screen change still removes it
    [Fact]
    public void A_STALE_PRESS_BEHIND_MANY_MOVES_IS_DROPPED()
    {
        var batch = Enumerable.Range(0, 200).Select(i => BatchConsoleReader.Move(i % 80, 4)).Append(BatchConsoleReader.Press(3, 4)).ToArray();
        var src = new ConsoleInputSource(new BatchConsoleReader(batch));
        src.DropMouse();
        Assert.False(src.EventAvailable);
    }

    [Fact]
    public void A_FAKE_WITHOUT_THE_MEMBER_FALLS_BACK_TO_KEYS() =>
        Assert.True(((IInputSource)new KeyInputSource(new OneKeySource('x'))).EventAvailable);
}
