using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests;

//consecutive Move events collapse to the latest at the source, the pump cannot drain ahead through IInputSource. one drag delivers tens of moves in one batch
public class ConsoleInputSourceCoalesceTests
{
    //the fake hands a whole burst in one Read, like the real console during a fast drag
    private sealed class BatchReader : IConsoleInputReader
    {
        private readonly Queue<INPUT_RECORD[]> _batches;
        public BatchReader(params INPUT_RECORD[][] batches) => _batches = new(batches);
        public int Read(INPUT_RECORD[] buf)
        {
            var b = _batches.Dequeue();
            for (var i = 0; i < b.Length; i++) buf[i] = b[i];
            return b.Length;
        }
        public int Peek(INPUT_RECORD[] buf) => 0;
    }

    private const uint MOUSE_MOVED = 0x0001, BTN_LEFT = 0x0001;

    //the Move record reports the current button state, as a real event does. the detector uses it to tell a move from a press or a release
    private static INPUT_RECORD Move(short x, short y, uint buttons = 0) => new()
    { EventType = 0x0002, MouseEvent = new MOUSE_EVENT_RECORD { dwButtonState = buttons, dwEventFlags = MOUSE_MOVED, dwMousePosition = new COORD { X = x, Y = y } } };
    private static INPUT_RECORD Press() => new()
    { EventType = 0x0002, MouseEvent = new MOUSE_EVENT_RECORD { dwButtonState = BTN_LEFT } };
    private static INPUT_RECORD Release() => new()   //the source compares against the previous button state, so state 0 after a held button reads as a release.
    { EventType = 0x0002, MouseEvent = new MOUSE_EVENT_RECORD { dwButtonState = 0 } };

    [Fact]
    public void Consecutive_moves_in_a_batch_collapse_to_the_last_release_survives_and_orders_after()
    {
        var src = new ConsoleInputSource(new BatchReader(new[]
        { Press(), Move(1, 1, BTN_LEFT), Move(2, 2, BTN_LEFT), Move(9, 9, BTN_LEFT), Release() }));
        Assert.Equal(MouseKind.Press, ((MouseEvent)src.Read()).Kind);
        var mv = (MouseEvent)src.Read();
        Assert.Equal(MouseKind.Move, mv.Kind);
        Assert.Equal((9, 9), (mv.X, mv.Y));
        Assert.Equal(MouseKind.Release, ((MouseEvent)src.Read()).Kind);
    }

    [Fact]
    public void A_lone_move_dispatches_immediately()   //drag latency is felt directly, so a lone move must not wait.
    {
        var src = new ConsoleInputSource(new BatchReader(new[] { Move(3, 3) }));
        var mv = (MouseEvent)src.Read();
        Assert.Equal(MouseKind.Move, mv.Kind);
        Assert.Equal((3, 3), (mv.X, mv.Y));
    }

    [Fact]
    public void Two_move_runs_split_by_a_press_each_collapse()
    {
        var src = new ConsoleInputSource(new BatchReader(new[]
        { Move(1, 1), Move(2, 2), Press(), Move(4, 4, BTN_LEFT), Move(5, 5, BTN_LEFT) }));
        var m1 = (MouseEvent)src.Read();
        Assert.Equal(MouseKind.Move, m1.Kind);
        Assert.Equal((2, 2), (m1.X, m1.Y));
        Assert.Equal(MouseKind.Press, ((MouseEvent)src.Read()).Kind);
        var m2 = (MouseEvent)src.Read();
        Assert.Equal((5, 5), (m2.X, m2.Y));
    }
}
