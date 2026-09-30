using System.Collections.Concurrent;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests;

//the drag latch arms on a press and drops on release, shift-press never arms. esc and ctrl+c consult the cancel sink before the focus stack
public class InputPumpDragTests
{
    private sealed class ScriptSource : IInputSource
    {
        private readonly BlockingCollection<InputEvent> _q = new();
        public void Push(InputEvent e) => _q.Add(e);
        public InputEvent Read() => _q.Take();
        public bool KeyDownAvailable => _q.Count > 0;
    }

    private static MouseEvent Press(int x, int y) => new(x, y, MouseKind.Press, MouseButton.Left, 0, 0);
    private static MouseEvent Move(int x, int y) => new(x, y, MouseKind.Move, MouseButton.None, 0, 0);
    private static MouseEvent Release(int x, int y) => new(x, y, MouseKind.Release, MouseButton.Left, 0, 0);
    private static readonly ConsoleKeyInfo Esc = new('\x1b', ConsoleKey.Escape, false, false, false);

    [Fact]
    public void Move_and_Release_reach_the_sink_only_while_dragging()
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var seen = new BlockingCollection<MouseEvent>();
        pump.SetMouseSink(seen.Add); pump.Start();
        src.Push(Move(1, 1));
        src.Push(Press(2, 2));                //pressing arms the drag latch.
        src.Push(Move(3, 3));
        src.Push(Release(3, 3));              //release is forwarded and disarms the latch.
        src.Push(Move(4, 4));
        Assert.Equal(MouseKind.Press, seen.Take().Kind);
        Assert.Equal(MouseKind.Move, seen.Take().Kind);
        Assert.Equal(MouseKind.Release, seen.Take().Kind);
        Assert.False(seen.TryTake(out _, 100));
    }

    [Fact]
    public void Shift_Press_does_not_arm_the_drag_latch()   //shift+drag belongs to the terminal's selection, so the latch must stay off.
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var seen = new BlockingCollection<MouseEvent>();
        pump.SetMouseSink(seen.Add); pump.Start();
        src.Push(new MouseEvent(0, 0, MouseKind.Press, MouseButton.Left, 0, ConsoleModifiers.Shift));
        Assert.Equal(MouseKind.Press, seen.Take().Kind);   //the press is forwarded but must not arm.
        src.Push(Move(3, 3));
        src.Push(Press(1, 1));                              //a later plain press shows the pump stayed live.
        Assert.Equal(MouseKind.Press, seen.Take().Kind);
        Assert.False(seen.TryTake(out _, 100));
    }

    [Fact]
    public void Esc_consults_the_cancel_sink_before_the_composer_when_no_modal()
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var consulted = false;
        pump.SetCancelSink(k => { consulted = true; return KeyDisposition.Consumed; });
        pump.Start();
        src.Push(new KeyEvent(Esc));
        Assert.True(System.Threading.SpinWait.SpinUntil(() => consulted, 5000));
        Assert.False(pump.Composer.KeyAvailable);   //a consumed key never reaches the composer.
    }

    [Fact]
    public void Esc_passes_through_to_the_composer_when_the_sink_declines()
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        pump.SetCancelSink(_ => KeyDisposition.PassThrough);
        pump.Start();
        src.Push(new KeyEvent(Esc));
        Assert.Equal(ConsoleKey.Escape, Assert.IsType<ComposerInput.Key>(pump.Composer.Read()).K.Key);
    }

    [Fact]
    public void Esc_under_a_modal_bypasses_the_cancel_sink()   //a focused modal consumes esc before the cancel sink sees it.
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var consulted = false;
        pump.SetCancelSink(_ => { consulted = true; return KeyDisposition.Consumed; });
        pump.Start();
        using var scope = pump.PushFocus();
        src.Push(new KeyEvent(Esc));
        Assert.Equal(ConsoleKey.Escape, scope.Keys.ReadKey().Key);
        Assert.False(consulted);
    }

    [Fact]
    public void AltGr_C_is_NOT_a_copy_chord()   //altgr arrives as ctrl+alt, so the copy chord requires control without alt.
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var consulted = false;
        pump.SetCancelSink(_ => { consulted = true; return KeyDisposition.Consumed; });
        pump.Start();
        var altGrC = new ConsoleKeyInfo('ç', ConsoleKey.C, shift: false, alt: true, control: true);
        src.Push(new KeyEvent(altGrC));
        Assert.Equal(ConsoleKey.C, Assert.IsType<ComposerInput.Key>(pump.Composer.Read()).K.Key);
        Assert.False(consulted);
    }

    [Fact]
    public void CtrlC_under_a_modal_STILL_consults_the_sink()   //copy must work while a prompt holds focus.
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var consulted = false;
        pump.SetCancelSink(k => { consulted = true; return KeyDisposition.Consumed; });
        pump.Start();
        using var scope = pump.PushFocus();
        var ctrlC = new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true);
        src.Push(new KeyEvent(ctrlC));
        Assert.True(System.Threading.SpinWait.SpinUntil(() => consulted, 5000));
    }

    [Fact]
    public void Plain_Ctrl_C_is_a_copy_chord()
    {
        var src = new ScriptSource(); var pump = new InputPump(src);
        var consulted = false;
        pump.SetCancelSink(k => { consulted = true; return KeyDisposition.Consumed; });
        pump.Start();
        var ctrlC = new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true);
        src.Push(new KeyEvent(ctrlC));
        Assert.True(System.Threading.SpinWait.SpinUntil(() => consulted, 5000));
        Assert.False(pump.Composer.KeyAvailable);
    }
}
