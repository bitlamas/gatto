using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Repl.Input;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//one Ctrl+C leaves, except where a press has a cost. the press must arrive as a key through TreatControlCAsInput, so it shares one leave rule with Esc
public class CtrlCTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    private static ConsoleKeyInfo CtrlC =>
        new('', ConsoleKey.C, false, false, control: true);

    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    //a watching screen needs a clock, or the test hangs on a console that is not there and looks like a slow machine. this one always reports a key waiting
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static TuiWizardSurface Face(long now, params ConsoleKeyInfo[] keys) =>
        new(new RecordingSurface { Width = 100 }, new Keys(keys),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => now,
            clock: _ => new Ready());

    private static WizardScreen.Choice Offer() => new(
        "model.audition.offer", "Run the check?",
        [new ChoiceOption("yes", "Yes"), new ChoiceOption("no", "Not now")]);

    //the fixture is a fetch in progress, so a press on it has a cost
    private static WizardScreen.Choice Fetching() => new(
        "model.fetching", "Downloading…",
        [new ChoiceOption("stop", "stop the download")],
        KeysOnly: true, Watching: true);

    //null is what the runner reads as leaving, the value Esc Esc also gives, so both keys reach one leave rule
    [Fact]
    public void CTRL_C_ONCE_LEAVES_AN_ORDINARY_SCREEN()
    {
        Assert.Null(Face(0, CtrlC).Choose(Offer()));
    }

    //mid-fetch the press arms the same chord Esc uses, since a download is running and a press has a cost. the test reads a second key
    [Fact]
    public void CTRL_C_MID_FETCH_ARMS_RATHER_THAN_LEAVING()
    {
        var f = Face(0, CtrlC, CtrlC);

        //stopping a download and leaving the wizard are different deeds, and only the flow deletes a partial download
        Assert.Equal("stop", f.Choose(Fetching(), watch: () => false));
    }

    //a press after the chord's window lapses re-arms, so a stale arm cannot turn a later key into a stop. the test moves nowMs forward rather than sleeping
    [Fact]
    public void A_SECOND_CTRL_C_AFTER_THE_WINDOW_RE_ARMS()
    {
        var now = 0L;
        var f = new TuiWizardSurface(new RecordingSurface { Width = 100 },
            new Keys([CtrlC, CtrlC]), new Theme(new TermCaps(true, true)),
            "0.5.0", "1a2b3c4", () => now += Gatto.Repl.Repl.CtrlCDoubleTapMs + 1,
            clock: _ => new Ready());

        //both presses arm and neither answers, so the scripted keys run dry and the throw is the passing shape
        Assert.Throws<InvalidOperationException>(() => f.Choose(Fetching(), watch: () => false));
    }

    //a typed screen has no watch, so one press leaves on the same rule as Esc
    [Fact]
    public void CTRL_C_LEAVES_A_TYPED_SCREEN_TOO()
    {
        var ask = new WizardScreen.Ask("model.typedid", "Which repo?", _ => null,
            Placeholder: "type an id…");

        Assert.Null(Face(0, CtrlC).Ask(ask));
    }

    //any other key disarms, since the chord is shared with Esc and a stale warning would describe a state the user has left
    [Fact]
    public void AN_UNRELATED_KEY_DISARMS_THE_CTRL_C_CHORD()
    {
        var f = Face(0, CtrlC, K(ConsoleKey.DownArrow), CtrlC, CtrlC);

        //the arrow disarms, so the chord completes only on the last key, and a skipped disarm would stop the download a key early
        Assert.Equal("stop", f.Choose(Fetching(), watch: () => false));
    }

    //a rule reading only ConsoleKey.C would end the wizard on a plain c, a typed letter or a row label's first character
    [Fact]
    public void A_PLAIN_C_IS_A_LETTER_AND_DOES_NOT_LEAVE()
    {
        var plain = new ConsoleKeyInfo('c', ConsoleKey.C, false, false, false);
        var f = Face(0, plain, K(ConsoleKey.Enter));

        Assert.Equal("yes", f.Choose(Offer()));
    }

    //the Ctrl+C setting is taken and given back on every exit, so the seam stands in for a getter that throws on a redirected stdin
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CTRL_C_IS_GIVEN_BACK_ON_EVERY_EXIT(bool walkThrows)
    {
        var order = new List<string>();
        bool? handed = null;

        var run = () => WizardSession.Run(
            new AltScreen(new RecordingSurface { Width = 80 }),
            () => { order.Add("walk"); return walkThrows ? throw new InvalidOperationException() : 0; },
            () => [],
            TextWriter.Null,
            registerHooks: false,
            take: () => { order.Add("take"); return false; },
            giveBack: was => { order.Add("give back"); handed = was; });

        if (walkThrows) Assert.Throws<InvalidOperationException>(() => run());
        else Assert.Equal(0, run());

        Assert.Equal(["take", "walk", "give back"], order);
        //give back the value that was found, since a constant false would silently change the console for a process that had it on
        Assert.False(handed);
    }

    //an armed hint must vanish when its window lapses with no key pressed, so the waiting branch repaints rather than blocking on ReadKey
    [Fact]
    public void AN_ARMED_ESC_HINT_REPAINTS_ITSELF_AWAY_WHEN_ITS_WINDOW_LAPSES()
    {
        var now = 0L;
        long NowMs() => now++;

        IReadOnlyList<string>? withHint = null;
        TimeSpan? budget = null;
        TuiWizardSurface? f = null;

        f = new TuiWizardSurface(new RecordingSurface { Width = 100 }, new Keys([K(ConsoleKey.Escape)]),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", NowMs,
            clock: _ =>
            {
                //the clock is built only once the chord is armed and the branch waits, so the last paint is what the user saw
                withHint = f!.LastPainted;
                return new ExpiringClock(b => { budget = b; now += Gatto.Repl.Repl.CtrlCDoubleTapMs + 1; });
            });

        //the script holds one key, so the loop reads again only after the wait expires and it repaints. running dry is the sign a repaint happened
        Assert.Throws<InvalidOperationException>(() => f.Choose(Offer()));

        Assert.NotNull(withHint);
        Assert.Contains("Esc again to leave", string.Join("\n", withHint!));
        Assert.DoesNotContain("Esc again to leave", string.Join("\n", f.LastPainted));

        //assert on the remainder rather than Watch.Interval, since on a fresh chord the two are equal and the check would pass by accident
        Assert.NotNull(budget);
        Assert.True(budget!.Value > TimeSpan.Zero);
        Assert.True(budget!.Value < TimeSpan.FromMilliseconds(Gatto.Repl.Repl.CtrlCDoubleTapMs));
    }

    private sealed class ExpiringClock(Action<TimeSpan> onWait) : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) { onWait(budget); return false; }
        public long ElapsedMs => 0;
    }

    //at the boundary the chord has zero left, and a zero wait spins the repaint loop, so the floor is one millisecond
    [Fact]
    public void AT_THE_WINDOWS_LAST_TICK_THE_ARM_STILL_WAITS_A_REAL_MILLISECOND()
    {
        var now = 0L;
        var budgets = new List<TimeSpan>();

        var f = new TuiWizardSurface(new RecordingSurface { Width = 100 }, new Keys([K(ConsoleKey.Escape)]),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => now,
            clock: _ => new ExpiringClock(b =>
            {
                budgets.Add(b);
                now += Math.Max(1, (long)b.TotalMilliseconds);
            }));

        //the press arms at zero and the first wait spends the whole window, so the second wait is the one under test
        Assert.Throws<InvalidOperationException>(() => f.Choose(Offer()));

        Assert.Equal(2, budgets.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(Gatto.Repl.Repl.CtrlCDoubleTapMs), budgets[0]);
        Assert.True(budgets[1] >= TimeSpan.FromMilliseconds(1),
            $"the boundary wait was {budgets[1].TotalMilliseconds}ms, which spins the repaint loop");
    }

    //the watch poll must not own the wait budget, or an armed hint outlives its expiry and a press re-arms. the third budget is the oracle
    [Fact]
    public void A_CHORD_ARMED_MID_POLL_SHORTENS_THE_WATCHS_OWN_WAIT()
    {
        var now = 0L;
        var budgets = new List<TimeSpan>();
        var arrivals = new Queue<bool>([true, false, true]);

        var f = new TuiWizardSurface(new RecordingSurface { Width = 100 }, new Keys([CtrlC, CtrlC]),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => now,
            clock: _ => new StepClock(b => { budgets.Add(b); now += 600; }, arrivals.Dequeue));

        //a completed chord answers the stop option on a keys-only watch screen.
        Assert.Equal("stop", f.Choose(Fetching(), watch: () => false));

        Assert.Equal(3, budgets.Count);
        Assert.Equal(Watch.Interval, budgets[0]);   //nothing is armed yet, so the first wait keeps the poll's interval
        Assert.Equal(Watch.Interval, budgets[1]);   //armed, but the remainder is still the whole interval
        //one advance of the window is spent, so the third budget is the shorter deadline. it is pinned as a literal, since a computed value would agree by construction
        Assert.Equal(TimeSpan.FromMilliseconds(1400), budgets[2]);
        Assert.True(budgets[2] < Watch.Interval);
    }

    private sealed class StepClock(Action<TimeSpan> onWait, Func<bool> arrives) : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) { onWait(budget); return arrives(); }
        public long ElapsedMs => 0;
    }
}
