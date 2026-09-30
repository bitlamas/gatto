using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the Enter key answers from the region the keys are in, so Enter moves with the tab. every assertion here reads the answer a key gives
public class ChipAnswerTests
{
    private static long Gib(double gb) => (long)Math.Round(gb * 1024 * 1024 * 1024);

    private static readonly IReadOnlyList<string> Ladder =
        ["gemma", "qwen", "deepseek", "glm", "mistral", "all"];

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static ShelfRow Row(string name) =>
        new($"unsloth/{name}", "unsloth", new HubQuant($"{name}-Q4_K_M.gguf", Gib(8), null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 0, Gated: false,
            Params: 8_000_000_000);

    private static WizardScreen.Choice Screen()
    {
        ShelfRow[] rows = [Row("gemma-4-31B-it"), Row("gemma-4-26B-A4B-it")];
        var shelf = new ShelfView(rows, "unsloth",
            MachineShape.UnifiedWithShare,
            Families: Ladder, Family: "gemma", Total: 2);
        return new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [.. rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId))],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode));
    }

    //the number of Tabs that move the keys onto the chips, read from Shelf.Regions so a stop added to the ring cannot shift Enter
    private static IEnumerable<ConsoleKeyInfo> ToChips(int steps = 0)
    {
        var ring = Shelf.Regions(Screen().Shelf!, 100, 0, hasDoor: true).ToList();
        var tabs = (ring.IndexOf(Region.Families) - ring.IndexOf(Region.List) + ring.Count) % ring.Count;
        return
        [
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), tabs),
            .. Enumerable.Repeat(Key(ConsoleKey.RightArrow), steps),
            Key(ConsoleKey.Enter),
        ];
    }

    [Fact]
    public void ENTER_ON_THE_FAMILY_CHIPS_DOES_NOT_ANSWER_WITH_A_MODEL()
    {
        var c = Screen();
        var answer = WalkRender.AnswerAfter(c, 100, ToChips());

        Assert.NotNull(answer);
        Assert.DoesNotContain(answer, c.Options.Select(o => o.Key));
    }

    //any wrong answer satisfies not a model, so only the exact chip proves the behaviour
    [Theory]
    [InlineData(0, "gemma")]
    [InlineData(1, "qwen")]
    [InlineData(5, "all")]
    public void ENTER_ON_THE_CHIPS_ANSWERS_WITH_THE_CHIP_UNDER_THE_CURSOR(int steps, string family)
    {
        Assert.Equal(ShelfControls.FamilyAnswer(family),
            WalkRender.AnswerAfter(Screen(), 100, ToChips(steps)));
    }

    //the Enter key on the list must still answer with a model, so a fix that made Enter inert everywhere fails here
    [Fact]
    public void ENTER_ON_THE_LIST_STILL_ANSWERS_WITH_A_MODEL()
    {
        var c = Screen();
        Assert.Equal(c.Options[0].Key, WalkRender.AnswerAfter(c, 100, [Key(ConsoleKey.Enter)]));
    }

    //the files zone and the list both advance with Enter, so the oracle is the answer. picking and advancing render alike at the moment the key arrives
    [Fact]
    public void ENTER_IN_THE_FILES_ZONE_ADVANCES_AND_SO_DOES_THE_LISTS()
    {
        ShelfRow[] rows = [Row("gemma-4-31B-it")];
        var shelf = new ShelfView(rows, "unsloth",
            MachineShape.UnifiedWithShare,
            Families: Ladder, Family: "gemma", Total: 1,
            Facts: [new ModelFacts(Files: [new PaneFile("Q4_K_M", Gib(8), FitRegime.FitsGpu)])]);
        var c = new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [new ChoiceOption("0", rows[0].RepoId)], Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode));

        var ring = Shelf.Regions(shelf, 100, 0, hasDoor: true).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Region.List) + ring.Count) % ring.Count;

        //the pane's Enter must return an answer, running dry here is the defect. a flow that comes back for another key is what tells stayed from advanced
        Assert.Equal(ShelfControls.PickAnswer("0", "Q4_K_M"), WalkRender.AnswerAfter(c, 100,
            [.. Enumerable.Repeat(Key(ConsoleKey.Tab), tabs), Key(ConsoleKey.Enter)]));

        //without this half the guard passes against an Enter that advances from every zone, including the ones that must pick and stay
        Assert.Equal("0", WalkRender.AnswerAfter(c, 100, [Key(ConsoleKey.Enter)]));
    }

    //the footer's verb must come from what Enter does in each zone, a footer reading next where Enter does not advance breaks its own promise
    [Fact]
    public void AND_THE_FOOTER_SAYS_CHOOSE_WHERE_ENTER_CHOOSES()
    {
        //the fixture must supply Files, otherwise the zone is missing from the ring and the assertions test a screen that cannot exist
        var shelf = new ShelfView([Row("gemma-4-31B-it")], "unsloth", MachineShape.UnifiedWithShare,
            Families: Ladder, Family: "gemma", Total: 1,
            Facts: [new ModelFacts(Files: [new PaneFile("Q4_K_M", Gib(8), FitRegime.FitsGpu)])]);
        var regions = Shelf.Regions(shelf, 100, 0, hasDoor: true);

        Assert.Equal("next", EnterVerb(regions, Region.List));
        Assert.Equal("choose", EnterVerb(regions, Region.Files));
    }

    //the loop is bounded and asserts the wanted region was reached, so a ring without it fails instead of looping forever
    private static string EnterVerb(IReadOnlyList<Region> regions, Region want)
    {
        var ring = new FocusRing(regions);
        for (var i = 0; i < regions.Count && ring.Current != want; i++) ring.Next();
        Assert.Equal(want, ring.Current);
        return Shelf.Keys(ring, ShelfSource.Hub).Single(k => k.Key == "Enter").Verb;
    }

    private static ShelfRow LiveRow() => new(
        "unsloth/gemma-4-26B-A4B-it", "unsloth",
        new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", Gib(16.9), null),
        FitRegime.FitsGpu, 262144, true, Badge: null, Downloads: 10, Gated: false,
        Params: 25_200_000_000);

    //drive the real flow here, a hand-built fixture could hide a missing chips row. compare with Families.Load().Ladder, a copied list could drift
    [Fact]
    public void THE_LIVE_SHELF_CARRIES_THE_CHIPS_ROW()
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [LiveRow()] });
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(Families.Load().Ladder, shelf.Shelf!.Families);
        Assert.Equal("all", shelf.Shelf!.Family);
    }

    //the chip's answer must reach the search request, a face-only test passes even when the family never enters HubSearchRequest
    [Fact]
    public void ANSWERING_A_CHIP_SEARCHES_THAT_FAMILY()
    {
        var probes = new WizardProbes { Rows = [LiveRow()] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();

        flow.Answer(ShelfControls.FamilyAnswer("qwen"));

        Assert.Equal("qwen", probes.LastRequest!.Family);
    }

    //a null alone would also pass with the Family field never set, so this pairs with the sibling that proves a value arrives
    [Fact]
    public void THE_ALL_CHIP_SEARCHES_NO_FAMILY()
    {
        var probes = new WizardProbes { Rows = [LiveRow()] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();

        flow.Answer(ShelfControls.FamilyAnswer("qwen"));
        flow.Answer(ShelfControls.FamilyAnswer("all"));

        Assert.Null(probes.LastRequest!.Family);
    }

    //the lit chip follows the answer that drove the search, so the row the user reads and the search cannot disagree
    [Fact]
    public void THE_LIT_CHIP_IS_THE_ONE_THE_SEARCH_RAN()
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [LiveRow()] });
        flow.StartPastEngine();

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer("deepseek")));

        Assert.Equal("deepseek", after.Shelf!.Family);
    }
}
