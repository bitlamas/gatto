using Gatto.Terminal;
using static Gatto.Tests.Fakes.BatchConsoleReader;

namespace Gatto.Tests;

public class ConsoleInputSourceTests
{
    private sealed class FakeReader : IConsoleInputReader
    {
        private readonly Queue<INPUT_RECORD> _q;
        public FakeReader(params INPUT_RECORD[] recs) => _q = new(recs);
        public int Read(INPUT_RECORD[] buf) { buf[0] = _q.Dequeue(); return 1; }   //the fake hands one record per Read, shaped like a blocking console
        public int Peek(INPUT_RECORD[] buf)
        {
            var arr = _q.ToArray();
            var n = System.Math.Min(buf.Length, arr.Length);
            for (var i = 0; i < n; i++) buf[i] = arr[i];
            return n;                                                              //the Peek copy does not dequeue
        }
    }

    [Fact]
    public void RepeatCount_expands_to_that_many_KeyEvents()
    {
        var src = new ConsoleInputSource(new FakeReader(KeyRec('a', 3)));
        Assert.Equal('a', ((KeyEvent)src.Read()).Key.KeyChar);
        Assert.Equal('a', ((KeyEvent)src.Read()).Key.KeyChar);
        Assert.Equal('a', ((KeyEvent)src.Read()).Key.KeyChar);   //one record from the reader became three events here.
    }

    [Fact]
    public void Dropped_records_are_skipped_until_a_real_event()
    {
        var src = new ConsoleInputSource(new FakeReader(KeyRec('x', 1, down: false), KeyRec('y', 1)));
        Assert.Equal('y', ((KeyEvent)src.Read()).Key.KeyChar);
    }

    [Fact]
    public void Mouse_records_translate_to_MouseEvents()
    {
        var src = new ConsoleInputSource(new FakeReader(MouseRec(0x0001)));   //state 0x0001 after no earlier state is the left-press edge
        var m = Assert.IsType<MouseEvent>(src.Read());
        Assert.Equal(MouseKind.Press, m.Kind);
    }

    [Fact]
    public void KeyDownAvailable_peeks_without_consuming()
    {
        var src = new ConsoleInputSource(new FakeReader(KeyRec('a', 1)));
        Assert.True(src.KeyDownAvailable);
        Assert.Equal('a', ((KeyEvent)src.Read()).Key.KeyChar);
    }

    private sealed class DeadReader : IConsoleInputReader
    {
        public int Read(INPUT_RECORD[] buf) => 0;   //the fake models a dead console where Read always returns 0
        public int Peek(INPUT_RECORD[] buf) => 0;
    }

    [Fact]
    public void Read_throws_on_a_dead_console_instead_of_spinning()
    {
        var src = new ConsoleInputSource(new DeadReader());
        Assert.Throws<InvalidOperationException>(() => src.Read());
    }
}
