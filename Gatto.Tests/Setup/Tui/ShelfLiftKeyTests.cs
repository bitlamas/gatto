using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup;

namespace Gatto.Tests.Setup.Tui;

//the shipping face must answer a, which the keys row draws, since the plain face's rig hides a dead key here
public class ShelfLiftKeyTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
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

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.A, false, false, false);
    private static readonly ConsoleKeyInfo Enter = new('\0', ConsoleKey.Enter, false, false, false);

    private static ShelfRow Row(int i) => new(
        $"unsloth/model-{i:00}", "unsloth", new HubQuant($"m{i:00}-Q4_K_M.gguf", 1_000_000_000, null),
        FitRegime.FitsGpu, 32768, false, null, 10, false, Params: 4_000_000_000);

    private static ShelfView View(MachineShape shape = MachineShape.Discrete,
        ShelfSource source = ShelfSource.Hub) => new(
        [.. Enumerable.Range(0, 6).Select(Row)], "unsloth", shape,
        Families: ["gemma", "all"], Family: "gemma", Total: 6, Source: source);

    private static WizardScreen.Choice Screen(bool inSession = false, ShelfView? shelf = null,
        bool everyKey = false)
    {
        var view = shelf ?? View();
        var options = view.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId)).ToList();
        //the everyKey flag adds the folder option and the back, so the row shows all eight keys, which the width pins expect
        if (everyKey) options.Add(new ChoiceOption(SetupFlow.Elsewhere, "Look in another folder…"));

        return new(SetupFlow.SearchKey, SetupFlow.ModelTitleFor(inSession), options,
            Shelf: view, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode))
        {
            Strip = WalkSection.For(SetupFlow.SearchKey, SetupPath.Llama, inSession),
            AllowBack = everyKey,
        };
    }

    //the rig here is the shipping face, whose dispatch is what the keys row advertises
    private static (string? Answer, IReadOnlyList<string> Frame) Press(
        WizardScreen.Choice screen, int width, params ConsoleKeyInfo[] keys)
    {
        var face = new TuiWizardSurface(
            new RecordingSurface { Width = width, Height = 60 },
            new Keys(keys), new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        string? answer = null;
        try { answer = face.Choose(screen); }
        catch (InvalidOperationException) { } //the script ran out before any answer.
        return (answer, face.LastPainted);
    }

    //a answers with the lift on any shelf, in a session or not
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_ANSWERS_WITH_THE_LIFT_ON_A_SHELF(bool inSession)
    {
        var (answer, _) = Press(Screen(inSession), 100, Ch('a'));

        Assert.Equal(SetupFlow.CtlLift, answer);
    }

    //a is an ordinary letter on a screen with no shelf, otherwise every screen answers stray keys with a control key
    [Fact]
    public void A_IS_AN_ORDINARY_LETTER_ON_A_SCREEN_WITH_NO_SHELF()
    {
        var plain = new WizardScreen.Choice("plain", "a question?",
            [new ChoiceOption("1", "one")]) { Strip = [] };

        var (answer, _) = Press(plain, 100, Ch('a'), Enter);

        Assert.NotEqual(SetupFlow.CtlLift, answer);
    }

    //a is text in a focused field, since the printable block runs before the key switch
    [Fact]
    public void A_IS_TEXT_IN_A_FOCUSED_SHELF_DOOR()
    {
        var (answer, _) = Press(Screen(), 100, Ch('/'), Ch('a'), Enter);

        Assert.Equal(ShelfControls.TypedAnswer("a"), answer);
    }

    //the lift is a toggle, so check the search re-ran with the flag flipped, read from the request the flow made
    [Fact]
    public void THE_HUB_SHELF_LIFTS_THE_FIT_FILTER()
    {
        var probes = new WizardProbes { Rows = [Row(0)] };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.FoundUse);
        Assert.False(probes.LastRequest!.IncludeUnfittable, "the shelf opened with the filter already lifted");

        flow.Answer(SetupFlow.CtlLift);

        Assert.True(probes.LastRequest!.IncludeUnfittable, "the search was not re-run with the filter lifted");
    }

    //one footer serves both shelves, so the local shelf must answer a rather than fall to the row-index reader
    [Fact]
    public void THE_LOCAL_SHELF_ANSWERS_THE_LIFT_RATHER_THAN_THROWING()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Roots = [@"C:\models"],
            Found = [new FoundModel(@"C:\m\a.gguf", 1_000_000_000, null)],
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.FoundUse);

        var back = flow.Answer(SetupFlow.CtlLift);

        Assert.Equal(SetupFlow.DiscoveredKey, ScreenKey.Of(back));
    }

    //the keys row must stay one line and shed hints before the legend, and each pin here was measured from this build
    [Theory]
    //every expected row below was printed from Footer.Compose, so don't hand-adjust them since the shed is what they protect
    [InlineData("unified", 120, "  Tab area   ↑↓ move   Enter next   m local   d search   a all sizes   b back   Esc leave      | fewer params = faster")]
    [InlineData("unified", 100, "  Enter next   m local   d search   a all sizes   b back   Esc leave       | fewer params = faster")]
    [InlineData("unified", 80, "  Enter next   m local   d search   a all sizes   b back   Esc leave")]
    [InlineData("discrete", 120, "  Tab area   ↑↓ move   Enter next   m local   d search   a all sizes   b back   Esc leave  | ✓ GPU · ⚠ RAM · ✗ too big")]
    [InlineData("discrete", 100, "  Enter next   m local   d search   a all sizes   b back   Esc leave   | ✓ GPU · ⚠ RAM · ✗ too big")]
    [InlineData("discrete", 80, "  Enter next   m local   d search   a all sizes   b back   Esc leave")]
    //a second byte pin for the session rows would fail about the wrong thing, so their Esc leave is pinned at its own site
    public void THE_KEYS_ROW_IS_ONE_LINE_AT_EVERY_WIDTH(string shape, int width, string expected)
    {
        var screen = Screen(
            shelf: View(shape == "unified" ? MachineShape.UnifiedWithShare : MachineShape.Discrete),
            everyKey: true);

        var (_, frame) = Press(screen, width);

        Assert.Equal(expected, frame[^1].TrimEnd());
        Assert.True(UnicodeWidth.Of(frame[^1]) <= width,
            $"the keys row is {UnicodeWidth.Of(frame[^1])} cells on a {width}-column terminal");
    }

    //the arrows shed before Tab, so state the surviving key at a width that keeps one hint
    [Fact]
    public void THE_ARROWS_SHED_BEFORE_TAB()
    {
        //97 sits inside the band that keeps one hint, since 95 sheds both and 100 keeps one
        var (_, frame) = Press(Screen(shelf: View(MachineShape.UnifiedWithShare)), 97);
        var row = frame[^1];

        Assert.Contains("Tab", row, StringComparison.Ordinal);
        Assert.DoesNotContain("move", row, StringComparison.Ordinal);
    }

    //at a wide width both hints must be present, otherwise a rule that drops both would satisfy the test above
    [Fact]
    public void AND_AT_A_WIDE_WIDTH_BOTH_HINTS_ARE_THERE()
    {
        var (_, frame) = Press(Screen(shelf: View(MachineShape.UnifiedWithShare)), 120);
        var row = frame[^1];

        Assert.Contains("Tab", row, StringComparison.Ordinal);
        Assert.Contains("move", row, StringComparison.Ordinal);
    }
}
