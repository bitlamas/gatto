using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a wait with no probe line still draws motion, and keys typed during it are drained before the next question
public class PurrPulseTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private sealed class QueuedKeys(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public int Remaining => _q.Count;
        public bool KeyAvailable => _q.Count > 0;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    private static ConsoleKeyInfo Digit(char c) =>
        new(c, (ConsoleKey)((int)ConsoleKey.D0 + (c - '0')), false, false, false);

    private static WizardScreen.Choice Two() =>
        new("planted.ordinary", "Which one?",
            [new ChoiceOption("a", "the first"), new ChoiceOption("b", "the second")]);

    private static Screen Painted(WizardScreen.Choice c) =>
        new([.. c.Strip], Region.List, c.Question,
            [.. c.Options.Select(o => PaintedRow.Of("  " + o.Label))], null,
            [new FooterKey("Enter", "choose")]);

    //read the cat from Cats.Face, the one place that composes it, so the assertion can't test a face the product never draws
    private static string Cat => Cats.Face(GlyphSet.Unicode);

    //a wait with nothing to say must still show motion, since the flow leaves to work and writes no line
    [Fact]
    public void A_SILENT_TRANSITION_LONGER_THAN_A_SECOND_DRAWS_A_PURR()
    {
        var now = 0L;
        var pulse = new FakePulse();
        var surface = new RecordingSurface { Width = 100 };
        var face = new TuiWizardSurface(surface, new QueuedKeys(Digit('1')), T, "0.5.0", "1a2b3c4",
            () => now, pulse: () => pulse);

        Assert.Equal("a", face.Choose(Two()));

        var beforeTheTick = surface.Text.Length;
        now += 1200;
        pulse.Fire();

        var drawn = surface.Text[beforeTheTick..];
        Assert.Contains(Cat, drawn, StringComparison.Ordinal);
        //the pulse draws the purr into the title row, and 1200ms of the face clock reads as 1s
        Assert.Contains("1s", drawn, StringComparison.Ordinal);
    }

    //the oracle is the delay the pulse was armed with, since a pulse that never started also draws nothing
    [Fact]
    public void AN_INSTANT_TRANSITION_DRAWS_NOTHING_BECAUSE_THE_PULSE_IS_ARMED_LATE()
    {
        var pulse = new FakePulse();
        var face = new TuiWizardSurface(new RecordingSurface { Width = 100 },
            new QueuedKeys(Digit('1')), T, "0.5.0", "1a2b3c4", () => 0, pulse: () => pulse);

        face.Choose(Two());

        Assert.NotNull(pulse.After);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), pulse.After);
        Assert.True(pulse.Every > TimeSpan.Zero, "a stamp is not a motion");
    }


    //a timer can't promise its callback has returned, so the face disarms rather than trusting the stop. a tick after the next paint would write into a fresh frame
    [Fact]
    public void A_TICK_ARRIVING_AFTER_THE_NEXT_PAINT_WRITES_NOTHING()
    {
        var now = 0L;
        var pulse = new FakePulse();
        var surface = new RecordingSurface { Width = 100 };
        var face = new TuiWizardSurface(surface, new QueuedKeys(Digit('1')), T, "0.5.0", "1a2b3c4",
            () => now, pulse: () => pulse);

        face.Choose(Two());
        face.Paint((_, _) => Painted(Two()));

        var afterThePaint = surface.Text.Length;
        now += 5000;
        pulse.Fire();

        Assert.Equal(afterThePaint, surface.Text.Length);
        Assert.True(pulse.Stops > 0, "the paint never stopped the pulse");
    }

    //a missing pulse must never fall back to a real timer. the oracle is the armed pulse, since an output check passes with the defect present
    [Fact]
    public void A_FACE_GIVEN_NO_PULSE_NEVER_ARMS_ONE()
    {
        var face = new TuiWizardSurface(new RecordingSurface { Width = 100 },
            new QueuedKeys(Digit('1')), T, "0.5.0", "1a2b3c4", () => 0);

        Assert.False(face.PulseArmed);
        face.Choose(Two());
        Assert.False(face.PulseArmed);
    }


    private static WizardScreen.Choice Many(int options) =>
        new("planted.many", "Pick a model",
            [.. Enumerable.Range(0, options).Select(i => new ChoiceOption($"k{i:00}", $"option {i:00}"))]);

    //the drain uses the purr's own threshold, since both ask how long the wait was
    [Fact]
    public void KEYS_TYPED_DURING_A_LONG_SILENT_WAIT_DO_NOT_ANSWER_THE_NEXT_SCREEN()
    {
        var now = 0L;
        var keys = new QueuedKeys(Digit('1'), Digit('2'), Digit('2'));
        var face = new TuiWizardSurface(new RecordingSurface { Width = 100 }, keys, T,
            "0.5.0", "1a2b3c4", () => now);

        Assert.Equal("a", face.Choose(Two()));
        Assert.Equal(2, keys.Remaining);

        now += 5000;

        //the next screen finds nothing buffered, so it waits for a real keystroke (the empty script makes that observable)
        Assert.Throws<InvalidOperationException>(() => face.Choose(Two()));
        Assert.Equal(0, keys.Remaining);
    }

    //the drain must fire only after a long wait, since an unconditional drain eats keys a fast user typed on purpose
    [Fact]
    public void A_FAST_TRANSITION_KEEPS_THE_KEYS_A_USER_TYPED_DELIBERATELY()
    {
        var now = 0L;
        var keys = new QueuedKeys(Digit('1'), Digit('2'));
        var face = new TuiWizardSurface(new RecordingSurface { Width = 100 }, keys, T,
            "0.5.0", "1a2b3c4", () => now);

        Assert.Equal("a", face.Choose(Two()));

        now += 40;

        //the second key must answer, since a key count alone could be met by a key read and thrown away
        Assert.Equal("b", face.Choose(Two()));
    }

    //no wait precedes the first screen, so a drain keyed off the clock alone would eat type-ahead typed before it
    [Fact]
    public void NOTHING_IS_DRAINED_BEFORE_THE_FIRST_QUESTION()
    {
        var keys = new QueuedKeys(Digit('2'));
        var face = new TuiWizardSurface(new RecordingSurface { Width = 100 }, keys, T,
            "0.5.0", "1a2b3c4", () => 900_000);

        Assert.Equal("b", face.Choose(Two()));
    }
}
