using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Tests;

public class InputPumpMouseTests
{
    private sealed class ScriptSource : IInputSource
    {
        private readonly BlockingCollection<InputEvent> _q = new();
        public void Push(InputEvent e) => _q.Add(e);
        public InputEvent Read() => _q.Take();   //the fake source blocks like the real one.
        public bool KeyDownAvailable => _q.Count > 0;
    }

    private static MouseEvent Wheel(int d) => new(0, 0, MouseKind.Wheel, MouseButton.None, d, 0);
    private static MouseEvent Press(int x, int y) => new(x, y, MouseKind.Press, MouseButton.Left, 0, 0);
    private static MouseEvent Move(int x, int y) => new(x, y, MouseKind.Move, MouseButton.None, 0, 0);
    private static KeyEvent Key(char c) => new(new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false));

    private static T Take<T>(BlockingCollection<T> q, int ms = 2000)
    {
        Assert.True(q.TryTake(out var v, ms), "expected an item within the deadline");
        return v!;
    }

    [Fact]
    public void Press_and_Wheel_reach_the_mouse_sink_but_Move_does_not()
    {
        var src = new ScriptSource();
        var pump = new InputPump(src);
        var seen = new BlockingCollection<MouseEvent>();
        pump.SetMouseSink(seen.Add);
        pump.Start();

        src.Push(Move(3, 3));        //the pump drops move events with no button held.
        src.Push(Wheel(120));
        src.Push(Press(5, 7));

        Assert.Equal(MouseKind.Wheel, Take(seen).Kind);
        Assert.Equal(MouseKind.Press, Take(seen).Kind);
        Assert.False(seen.TryTake(out _, 100));
    }

    [Fact]
    public void Keys_still_route_to_the_composer_unchanged()
    {
        var src = new ScriptSource();
        var pump = new InputPump(src);
        pump.Start();
        src.Push(Key('x'));
        Assert.Equal('x', Assert.IsType<ComposerInput.Key>(pump.Composer.Read()).K.KeyChar);
    }

    [Fact]   //mouse events must work while a modal holds focus, and they never enter the focus stack.
    public void Wheel_and_Press_reach_the_sink_while_a_focus_scope_holds_modal_focus()
    {
        var src = new ScriptSource();
        var pump = new InputPump(src);
        var seen = new BlockingCollection<MouseEvent>();
        pump.SetMouseSink(seen.Add);
        pump.Start();
        using var scope = pump.PushFocus();
        src.Push(Wheel(120));
        src.Push(Press(2, 2));
        Assert.Equal(MouseKind.Wheel, Take(seen).Kind);
        Assert.Equal(MouseKind.Press, Take(seen).Kind);
        Assert.False(scope.Keys.KeyAvailable);   //mouse events must not leak into the modal's keyboard.
    }
}
