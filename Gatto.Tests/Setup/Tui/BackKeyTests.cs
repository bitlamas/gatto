using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the strip only shows information, and b is back, since Esc keeps one meaning, so each negative claim below needs a mutation test
public class BackKeyTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.B, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    //two sections are answered and the run stands on the third.
    private static readonly IReadOnlyList<StripSection> Road =
    [
        new("machine", StripState.Done), new("engine", StripState.Done),
        new("model", StripState.Current), new("check", StripState.Pending),
        new("done", StripState.Pending),
    ];

    //a shelf screen has a real second area, so Tab belongs to that area rather than the strip
    private static WizardScreen.Choice Shelf(bool allowBack) =>
        new("model.search", "Which model should gatto start with?",
            [new ChoiceOption("0", "unsloth/gemma-4-26B-A4B-it")],
            Shelf: new ShelfView(
                [new ShelfRow("unsloth/gemma-4-26B-A4B-it", "unsloth",
                    new HubQuant("gemma-Q4_K_M.gguf", 16_900_000_000, null),
                    FitRegime.FitsGpu, 131072, false, Badge: null, Downloads: 0, Gated: false)],
                "unsloth", MachineShape.UnifiedWithShare,
                Families: ["gemma", "all"], Family: "gemma"),
            Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode))
        { Strip = Road, AllowBack = allowBack };

    //a screen with one area: options and a strip, with no typed field and no pane
    private static WizardScreen.Choice OneArea(bool allowBack) =>
        new("backkey.one-area", "Which engine build fits this machine?",
            [new ChoiceOption("use", "Use it"), new ChoiceOption("fetch", "Fetch the tested release")])
        { Strip = Road, AllowBack = allowBack };

    private static (string? Answer, IReadOnlyList<string> Frame) Press(
        WizardScreen.Choice screen, params ConsoleKeyInfo[] keys) => Press(screen, 100, keys);

    //at 100 a full shelf no longer draws Tab, so a guard must ask at a width where it can appear
    private static (string? Answer, IReadOnlyList<string> Frame) Press(
        WizardScreen.Choice screen, int width, params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface { Width = width, Height = 40 };
        var face = new TuiWizardSurface(surface, new Keys(keys),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        string? answer = null;
        try { answer = face.Choose(screen); }
        catch (InvalidOperationException) { } //the script ran out before an answer arrived.
        return (answer, face.LastPainted);
    }

    private static string Footer(IReadOnlyList<string> frame) => frame[^1];

    //before any write b answers with back, on both the shelf and the one-area screen
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void B_GOES_BACK_ON_A_PRE_WRITE_SCREEN(bool shelf)
    {
        var (answer, frame) = Press(shelf ? Shelf(allowBack: true) : OneArea(allowBack: true), Ch('b'));

        Assert.Equal(SetupFlow.BackKey, answer);
        Assert.Contains("b back", Footer(frame), StringComparison.Ordinal);
    }

    //after the write pause b is neither advertised nor answered, so the footer must not promise a way back
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFTER_THE_WRITE_PAUSE_B_IS_NEITHER_ADVERTISED_NOR_ANSWERED(bool shelf)
    {
        var screen = shelf ? Shelf(allowBack: false) : OneArea(allowBack: false);

        var (answer, frame) = Press(screen, Ch('b'), K(ConsoleKey.Enter));

        Assert.DoesNotContain("b back", Footer(frame), StringComparison.Ordinal);
        Assert.NotEqual(SetupFlow.BackKey, answer);
    }

    //a screen where Esc answers the second option, the shape of the in-session check ask
    private static WizardScreen.Choice EscAnswers(bool allowBack) =>
        new("backkey.esc-answers", "Check gemma-4-26B-A4B-it now?",
            [new ChoiceOption("check", "Check it now"),
             new ChoiceOption("unchecked", "Add it unchecked", EscVerb: "add it unchecked")])
        { Strip = Road, AllowBack = allowBack };

    //with no way back, b answers the option that Esc answers, and the footer names both keys on one label
    [Fact]
    public void WITH_NO_BACK_ROAD_B_ANSWERS_THE_OPTION_ESC_ANSWERS()
    {
        var (answer, frame) = Press(EscAnswers(allowBack: false), Ch('b'));

        Assert.Equal("unchecked", answer);
        Assert.Contains("b / Esc add it unchecked", Footer(frame), StringComparison.Ordinal);
        Assert.DoesNotContain("b back", Footer(frame), StringComparison.Ordinal);
    }

    //the twin case: with a way back the footer keeps b back and gives Esc its own label, so no key names two actions
    [Fact]
    public void WITH_A_BACK_ROAD_B_STAYS_BACK_BESIDE_AN_ESC_ANSWER()
    {
        var (answer, frame) = Press(EscAnswers(allowBack: true), Ch('b'));

        Assert.Equal(SetupFlow.BackKey, answer);
        Assert.Contains("b back", Footer(frame), StringComparison.Ordinal);
        Assert.DoesNotContain("b / Esc", Footer(frame), StringComparison.Ordinal);
    }

    //the Tab key is neither advertised nor answered where there is nowhere to go, so the oracle is that the cursor did not move
    [Fact]
    public void TAB_IS_NEITHER_ADVERTISED_NOR_ANSWERED_ON_A_ONE_AREA_SCREEN()
    {
        var (_, frame) = Press(OneArea(allowBack: true), K(ConsoleKey.Tab));

        Assert.DoesNotContain("Tab", Footer(frame), StringComparison.Ordinal);
        //the cursor is still on the list, shown by the mark on its first option.
        Assert.Contains(frame, r => r.TrimStart().StartsWith("❯ 1.", StringComparison.Ordinal));
    }

    //a screen with a real second area still advertises Tab, so the check above cannot pass on a face that draws none
    [Fact]
    public void AND_A_SCREEN_WITH_A_REAL_SECOND_AREA_STILL_ADVERTISES_IT()
    {
        var (_, frame) = Press(Shelf(allowBack: true), 120);

        Assert.Contains("Tab", Footer(frame), StringComparison.Ordinal);
    }

    //the strip is never a stop for Tab, on any route through the setup flow
    [Fact]
    public void THE_STRIP_IS_A_STOP_ON_NO_ROAD()
    {
        var screen = OneArea(allowBack: true);

        //the script presses Tab then Enter, since a strip that was a stop would answer Enter with a section key
        var (answer, _) = Press(screen, K(ConsoleKey.Tab), K(ConsoleKey.Enter));

        Assert.False(answer?.StartsWith("section.", StringComparison.Ordinal) ?? false,
            $"the strip answered with '{answer}', so it is still a stop");
    }
}
