using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//an ask with an offer shows a list, so Esc arms the leave chord there. an ask with only a typed field has no list, so one press leaves
public class AskEscChordTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    //it always reports a key waiting, so the script alone drives the run, since a hang would name no screen
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool KeyAvailable => true;
        public bool WaitForKey(TimeSpan _) => true;
        public long ElapsedMs => 0;
    }

    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Esc => K(ConsoleKey.Escape);
    private static ConsoleKeyInfo Tab => K(ConsoleKey.Tab);

    //the clock stands still unless a test moves it, since a moving clock would make these tests about timing
    private static TuiWizardSurface Face(RecordingSurface surface, Func<long> now,
        params ConsoleKeyInfo[] keys) =>
        new(surface, new Keys(keys), new Theme(new TermCaps(true, true)),
            "0.5.0", "1a2b3c4", now, clock: _ => new Ready());

    private static TuiWizardSurface Face(params ConsoleKeyInfo[] keys) =>
        Face(new RecordingSurface { Width = 100 }, () => 0, keys);

    //an ask with an offer, so it has a list and the keys start in it.
    private static WizardScreen.Ask Offered() => new(
        "model.context", "How much context?", _ => null,
        Placeholder: "type a number…",
        Offer: new AskOffer("Use 8,192", "8192"));

    //an ask with only a typed field, so no list exists anywhere in it.
    private static WizardScreen.Ask DoorOnly() => new(
        "model.typedid", "Which repo?", _ => null, Placeholder: "type an id…");

    //one Esc arms and the screen stays, so the face asks for another key and the script runs dry
    [Fact]
    public void ONE_ESC_ARMS_AND_THE_ASK_STAYS()
    {
        var f = Face(Esc);

        Assert.Throws<InvalidOperationException>(() => f.Ask(Offered()));
    }

    //without this the test above passes on a screen that ignores Esc altogether
    [Fact]
    public void AND_A_SECOND_ESC_LEAVES()
    {
        Assert.Null(Face(Esc, Esc).Ask(Offered()));
    }

    //a first press that does nothing in silence is worse than one press, so the armed sentence must be on the painted frame
    [Fact]
    public void AND_THE_ARMED_ROW_SAYS_WHAT_A_SECOND_PRESS_COSTS()
    {
        var surface = new RecordingSurface { Width = 100 };
        var f = Face(surface, () => 0, Esc, Esc);

        f.Ask(Offered());

        Assert.Contains("Esc again to leave", surface.Text, StringComparison.Ordinal);
        //the default armed sentence names no cost, here as on the choice ladder.
        Assert.DoesNotContain("nothing has been written", surface.Text, StringComparison.Ordinal);
    }

    //a screen that always drew the armed sentence passes the test above, so this half stops the row being furniture
    [Fact]
    public void AND_NOTHING_SAYS_IT_BEFORE_THE_FIRST_PRESS()
    {
        var surface = new RecordingSurface { Width = 100 };
        var f = Face(surface, () => 0);

        Assert.Throws<InvalidOperationException>(() => f.Ask(Offered()));

        Assert.DoesNotContain("Esc again to leave", surface.Text, StringComparison.Ordinal);
    }

    //a second press outside the window arms again instead of leaving, so a stray Esc cannot leave the wizard much later
    [Fact]
    public void THE_ARM_EXPIRES()
    {
        long now = 0;
        var f = Face(new RecordingSurface { Width = 100 },
            () => now += Gatto.Repl.Repl.CtrlCDoubleTapMs + 1, Esc, Esc);

        Assert.Throws<InvalidOperationException>(() => f.Ask(Offered()));
    }

    //without this row the rule reads as every ask needing two presses
    [Fact]
    public void A_DOOR_ONLY_ASK_STILL_LEAVES_IN_ONE_PRESS()
    {
        Assert.Null(Face(Esc).Ask(DoorOnly()));
    }

    //from the typed field, the first Esc returns the keys to the list, so the dry script is what shows it armed
    [Fact]
    public void ESC_FROM_THE_DOOR_RETURNS_THE_KEYS_BEFORE_IT_ARMS()
    {
        var f = Face(Tab, Esc, Esc);

        Assert.Throws<InvalidOperationException>(() => f.Ask(Offered()));
    }

    //without this the test above passes on a tree where Esc from the typed field does nothing at all. the whole ladder is three presses and never more
    [Fact]
    public void AND_THE_PRESS_AFTER_THAT_LEAVES()
    {
        Assert.Null(Face(Tab, Esc, Esc, Esc).Ask(Offered()));
    }
}
