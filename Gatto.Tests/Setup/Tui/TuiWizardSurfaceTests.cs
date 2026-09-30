using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Repl.Input;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the app-shaped face drives the same WizardScreens the plain face gets
public class TuiWizardSurfaceTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    private static ConsoleKeyInfo Sp(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Dg(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    private static WizardScreen.Choice Two(bool noDefault = false, string? door = null) => new(
        "k", "Replace the installed binary?",
        [new ChoiceOption("yes", "Yes, replace it"), new ChoiceOption("no", "Not now")],
        NoDefault: noDefault, Door: door);

    //banner fixtures that match the golden header, so a version bump doesn't move every expected row
    private const string Version = "0.5.0";
    private const string Build = "1a2b3c4";

    //an armed chord waits out its remainder, so the clock has to be eager or a chord armed by Esc waits forever
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static (RecordingSurface S, TuiWizardSurface F) Face(long now = 0, params ConsoleKeyInfo[] keys)
    {
        var s = new RecordingSurface { Width = 100 };
        return (s, new TuiWizardSurface(s, new Keys(keys), new Theme(new TermCaps(true, true)),
            Version, Build, () => now, clock: _ => new Ready()));
    }

    private static (RecordingSurface S, TuiWizardSurface F) FaceFor(string command,
        params ConsoleKeyInfo[] keys)
    {
        var s = new RecordingSurface { Width = 100 };
        return (s, new TuiWizardSurface(s, new Keys(keys), new Theme(new TermCaps(true, true)),
            Version, Build, () => 0, clock: _ => new Ready(), command: command));
    }

    //the surface is the seam a user reaches. a surface that ignored its command would still pass ScreenPainterTests
    [Theory]
    [InlineData("gatto setup")]
    [InlineData("gatto model")]
    public void THE_SURFACE_PAINTS_THE_COMMAND_IT_WAS_HANDED(string command)
    {
        var (s, f) = FaceFor(command, Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        f.Choose(Two());

        var rows = TerminalReplay.Plain(s.Text).Split('\n');
        Assert.StartsWith("  " + command, rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Enter_answers_the_row_under_the_cursor()
    {
        var (_, f) = Face(0, Sp(ConsoleKey.DownArrow), Sp(ConsoleKey.Enter));
        Assert.Equal("no", f.Choose(Two()));
    }

    //the same rule through the face, the two must not diverge
    [Fact]
    public void On_a_no_default_screen_the_first_digit_only_LIGHTS_its_row()
    {
        var (_, f) = Face(0, Dg('1'), Dg('1'));
        Assert.Equal("yes", f.Choose(Two(noDefault: true)));
    }

    [Fact]
    public void On_a_no_default_screen_ENTER_alone_answers_nothing()
    {
        //the keys are Enter then the chord, if Enter had answered the two Escs would never be read
        var (_, f) = Face(0, Sp(ConsoleKey.Enter), Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        Assert.Null(f.Choose(Two(noDefault: true)));
    }

    //one Esc arms, a second inside the window leaves
    [Fact]
    public void Esc_Esc_leaves_and_the_first_Esc_only_ARMS()
    {
        var (s, f) = Face(0, Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        Assert.Null(f.Choose(Two()));
        Assert.Contains("Esc again to leave", TerminalReplay.Plain(s.Text), StringComparison.Ordinal);
    }

    //any other key disarms, a warning that survived an unrelated keystroke would speak about a state the user has already left
    [Fact]
    public void An_unrelated_key_DISARMS_the_chord()
    {
        var (_, f) = Face(0, Sp(ConsoleKey.Escape), Sp(ConsoleKey.DownArrow), Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        Assert.Null(f.Choose(Two()));   //needs the last two Escs, the middle one was disarmed
    }

    //ink goes on after the clamp, so a row filling the width exactly keeps all its visible cells. ink first would cut by escape bytes at the right edge
    [Fact]
    public void An_INKED_row_at_exactly_the_width_keeps_all_its_visible_cells()
    {
        var (s, f) = Face(0, Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        f.Choose(Two());

        var rows = TerminalReplay.Plain(s.Text).Split('\n');
        var rule = rows.First(r => r.StartsWith('─'));
        Assert.Equal(100, Gatto.Terminal.UnicodeWidth.Of(rule));
        Assert.Contains("\x1b[", s.Text, StringComparison.Ordinal);   //the escape proves ink went in
        Assert.All(rows, r => Assert.True(Gatto.Terminal.UnicodeWidth.Of(r) <= 100));
    }

    //the rule row is painted, so the width assertion above can't pass because nothing was inked
    [Fact]
    public void The_rule_row_really_carries_ink()
    {
        var (s, f) = Face(0, Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        f.Choose(Two());
        var inked = s.Text.Split('\n').First(r => r.Contains('─'));
        Assert.Contains("\x1b[", inked, StringComparison.Ordinal);
    }

    //a screen with a door gains a region, one without has only the list. a stop the screen does not have would Tab the user into nothing
    [Fact]
    public void A_screen_with_a_door_renders_it_and_Tab_can_reach_it()
    {
        var (s, f) = Face(0, Sp(ConsoleKey.Tab), Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        Assert.Null(f.Choose(Two(door: "search models…")));
        Assert.Contains("search models…", TerminalReplay.Plain(s.Text), StringComparison.Ordinal);
    }

    //the Esc verb follows the keys, back while they are elsewhere and leave in the list
    [Fact]
    public void The_footer_says_back_while_the_keys_are_off_the_list()
    {
        var (s, f) = Face(0, Sp(ConsoleKey.Tab), Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape));
        f.Choose(Two(door: "search models…"));
        Assert.Contains("Esc back", TerminalReplay.Plain(s.Text), StringComparison.Ordinal);
    }

    //the armed warning dissolves when the window passes. the clock advances as the arm's own wait elapses, the next read comes after it
    [Fact]
    public void The_armed_warning_DISSOLVES_when_the_window_expires()
    {
        //each wait pushes the clock past its budget, so the second Esc finds nothing armed and re-arms, and the scripted keys run dry
        var clock = new AdvancingClock();
        var f = new TuiWizardSurface(new RecordingSurface { Width = 100 },
            new Keys([Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape)]),
            new Theme(new TermCaps(true, true)), Version, Build, clock.Now, clock: clock.Clock);

        Assert.Throws<InvalidOperationException>(() => f.Choose(Two(), null));

        //the run is deterministic, so the value is exact, and a bound would pass a window scaled a thousand times too long
        Assert.Equal(2 * (Gatto.Repl.Repl.CtrlCDoubleTapMs + 1), clock.Now());
    }

    //a clock the test drives by its own wait instead of sleeping
    private sealed class AdvancingClock
    {
        private long _t;
        public long Now() => _t;
        public Gatto.Repl.IPollClock Clock(bool isWatch) => new Advancing(this);

        private sealed class Advancing(AdvancingClock c) : Gatto.Repl.IPollClock
        {
            public bool WaitForKey(TimeSpan budget)
            {
                c._t += (long)budget.TotalMilliseconds + 1;
                return false;
            }
            public long ElapsedMs => 0;
        }
    }

    //the second Esc leaves inside the window, the half the test above cannot show. the face is built directly here, so it needs its own clock
    [Fact]
    public void INSIDE_the_window_the_second_Esc_leaves()
    {
        var now = 0L;
        var s = new RecordingSurface { Width = 100 };
        var f = new TuiWizardSurface(s, new Keys([Sp(ConsoleKey.Escape), Sp(ConsoleKey.Escape)]),
            new Theme(new TermCaps(true, true)), Version, Build, () => now, clock: _ => new Ready());
        Assert.Null(f.Choose(Two(), null));
    }

    //the chord is written nowhere, the armed warning is its only teacher. the census has to cover both spellings, the literal separator and the escape
    [Fact]
    public void The_face_never_writes_the_Esc_chord_anywhere()
    {
        //the terminal library is the toolkit a reader copies first, so a spelling there teaches the chord more widely than one in the wizard
        var offenders = Gatto.Tests.Census.SourceTree.ProductionFilesUnder("Cli", "Setup", "Tui")
            .Concat(Gatto.Tests.Census.SourceTree.ProductionFilesIn("Gatto.Terminal"))
            .Where(f => SpellsTheChord(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.True(offenders.Length == 0, "the chord is spelled out in: " + string.Join(", ", offenders));
    }

    //every way source can spell the chord, kept beside the test that uses it so the two cannot drift onto different patterns
    private static bool SpellsTheChord(string source) =>
        source.Contains("Esc" + Middot + "Esc", StringComparison.Ordinal)
        || source.Contains("Esc" + Backslash + "u00b7Esc", StringComparison.OrdinalIgnoreCase)
        || source.Contains("Esc.Esc", StringComparison.Ordinal);

    private const string Middot = "\u00b7";
    private const string Backslash = "\\";

    //a positive in every spelling the census claims to cover, one form alone would prove nothing about the others
    [Theory]
    [InlineData("a hint that says Esc" + "\u00b7" + "Esc out loud")]
    [InlineData("a hint that says Esc.Esc out loud")]
    public void The_chord_census_finds_every_spelling(string planted)
    {
        Assert.True(SpellsTheChord(planted), "the census cannot see this spelling: " + planted);
    }

    [Fact]
    public void The_chord_census_finds_the_ESCAPE_spelling_too()
    {
        //built at runtime so this file does not itself contain the string the census hunts
        var planted = "a hint that says Esc" + Backslash + "u00b7Esc out loud";
        Assert.True(SpellsTheChord(planted));
    }

    [Fact]
    public void The_chord_census_does_not_fire_on_ordinary_text()
    {
        Assert.False(SpellsTheChord("Esc leave"));
        Assert.False(SpellsTheChord("press Esc twice"));
    }
}
