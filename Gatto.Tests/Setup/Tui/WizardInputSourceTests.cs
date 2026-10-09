using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the face reads one input source, keys and the mouse alike, and its waits end on anything it can act on
public class WizardInputSourceTests
{
    [Fact]
    public void A_SCRIPTED_KEY_THROUGH_THE_INPUT_SOURCE_ANSWERS_AS_BEFORE()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var face = new WizardRig(120).TuiFaceEvents(new KeyEvent(WizardRig.Down), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[1].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_MOUSE_EVENT_IN_A_KEY_LOOP_IS_PASSED_OVER()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var face = new WizardRig(120).TuiFaceEvents(
            new MouseEvent(5, 5, MouseKind.Move, MouseButton.None, 0, 0), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }

    //counts the face's mouse drops and how much of the frame was painted at each, over a scripted source
    private sealed class DropSpy(IInputSource inner) : IInputSource
    {
        public TuiWizardSurface? Face;
        public List<int> PaintedAtDrop { get; } = [];
        public InputEvent Read() => inner.Read();
        public bool KeyDownAvailable => inner.KeyDownAvailable;
        public void DropMouse() => PaintedAtDrop.Add(Face!.LastPainted.Count);
    }

    [Fact]
    public void EACH_SCREEN_DROPS_STALE_MOUSE_EVENTS_ONCE_AFTER_ITS_FIRST_PAINT()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var spy = new DropSpy(new ScriptedInputSource(null, new KeyEvent(WizardRig.Down), new KeyEvent(WizardRig.Enter),
            new KeyEvent(WizardRig.Enter), new KeyEvent(WizardRig.Enter)));
        var face = spy.Face = new WizardRig(120).TuiFaceOver(spy);
        face.Choose(shelf);
        Assert.Single(spy.PaintedAtDrop);
        face.Ask(new WizardScreen.Ask("ask", "Where?", _ => null, "a folder", new AskOffer("here", @"C:\m")));
        Assert.Equal(2, spy.PaintedAtDrop.Count);
        Assert.All(spy.PaintedAtDrop, n => Assert.True(n > 0, "the drop ran before the screen was painted"));
    }

    [Fact]
    public async Task THE_DEFAULT_CLOCK_ENDS_ITS_WAIT_ON_A_PRESS()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var y = ShelfFixtures.RowY(shelf, row: 1, width: 120, height: 30);
        //the empty batch is a pause, so the first paint's drop meets nothing and the presses wait for the clock. row 1 twice, since the cursor starts on row 0
        var reader = new BatchConsoleReader([],
            [BatchConsoleReader.Press(2, y), BatchConsoleReader.Release(2, y)],
            [BatchConsoleReader.Press(2, y), BatchConsoleReader.Release(2, y)]);
        var rig = new WizardRig(120, defaultClock: true);
        rig.Surface.Height = 30;
        rig.Surface.ReportsResize = true;
        var face = rig.TuiFaceOver(new ConsoleInputSource(reader));
        //a face that polls key downs only never ends its wait, so the run is bounded and a hang fails
        var run = Task.Run(() => face.Choose(shelf));
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(shelf.Options[1].Key, await run);
    }
}
