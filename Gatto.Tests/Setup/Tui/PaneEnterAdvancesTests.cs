using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the pane's Enter chooses the quant and moves on, asserted through the answer the face returns
public class PaneEnterAdvancesTests
{
    private const string Knee = "Q4_K_M";
    private const string Heavier = "Q6_K";

    private static FileRef Ref(string token) => new("unsloth", "unsloth/gemma-4-26B", token + ".gguf");

    private static ModelRow Row() => ShelfRows.Of(
        RepoId: "unsloth/gemma-4-26B", Publisher: "unsloth",
        PickedQuant: new HubQuant(Knee + ".gguf", 16_900_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: 900, Gated: false, Params: 26_000_000_000, Arch: "gemma3");

    //two files, so the pane cursor's file and the row's own default are different answers. with one file, or without the Down, both would come out the same
    private static WizardScreen.Choice OnTheShelf()
    {
        var shelf = new ShelfView([Row()], MachineShape.UnifiedWithShare, Total: 1,
            Facts: [new ModelFacts(Files: [
                new PaneFile(Knee, 16_900_000_000, FitRegime.FitsGpu, Ref(Knee)),
                new PaneFile(Heavier, 21_100_000_000, FitRegime.FitsGpu, Ref(Heavier))])]);
        return new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [new ChoiceOption("0", "unsloth/gemma-4-26B")],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode));
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static IReadOnlyList<Region> Ring(WizardScreen.Choice c) =>
        Shelf.Regions(c.Shelf!, 100, 0, c.Door is not null);

    //count Tab from Opening, where the keys start on a populated shelf. from index 0 the helper ends one zone short and the failure reads as the defect under test
    private static IEnumerable<ConsoleKeyInfo> TabTo(WizardScreen.Choice c, Region want)
    {
        var ring = Ring(c).ToList();
        var steps = (ring.IndexOf(want) - ring.IndexOf(Shelf.Opening(c.Shelf!)) + ring.Count) % ring.Count;
        return Enumerable.Repeat(Key(ConsoleKey.Tab), steps);
    }

    //one Enter in the pane answers with the whole shelf pick of the cursor's file
    [Fact]
    public void ENTER_IN_THE_PANE_CHOOSES_THE_QUANT_AND_ADVANCES()
    {
        var c = OnTheShelf();

        var answer = WalkRender.AnswerAfter(c, 100,
        [
            .. TabTo(c, Region.Files),
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter),
        ]);

        Assert.Equal(ShelfControls.PickAnswer("0", Ref(Heavier)), answer);
    }

    //the Down moves the pane cursor off the row's picked quant, so a build that ignores the pane answers Q4_K_M and fails here
    [Fact]
    public void THE_PANE_ENTER_CARRIES_THE_FILE_UNDER_ITS_OWN_CURSOR()
    {
        var c = OnTheShelf();

        var answer = WalkRender.AnswerAfter(c, 100,
        [
            .. TabTo(c, Region.Files),
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter),
        ]);

        Assert.DoesNotContain(Knee, answer);
    }

    //leaving the cursor alone is still a choice, so Enter picks the resolved default and advances
    [Fact]
    public void AN_UNTOUCHED_PANE_CURSOR_STILL_CHOOSES_AND_ADVANCES()
    {
        var c = OnTheShelf();

        var answer = WalkRender.AnswerAfter(c, 100,
            [.. TabTo(c, Region.Files), Key(ConsoleKey.Enter)]);

        Assert.Equal(ShelfControls.PickAnswer("0", Ref(Knee)), answer);
    }

    //the list's Enter still answers on its own, the half a pane fix could break while the rows above pass
    [Fact]
    public void THE_LISTS_ENTER_STILL_ADVANCES()
    {
        var c = OnTheShelf();

        var answer = WalkRender.AnswerAfter(c, 100, [Key(ConsoleKey.Enter)]);

        Assert.Equal("0", answer);
    }

    //the Enter word follows the zone, choose in the pane, pick in the builds and the publisher, next in the list
    [Fact]
    public void THE_FOOTERS_ENTER_WORD_FOLLOWS_THE_ZONE()
    {
        var c = OnTheShelf();
        var ring = Ring(c);

        Assert.Equal("choose", EnterWord(ring, Region.Files));
        Assert.Equal("next", EnterWord(ring, Region.List));
    }

    private static string EnterWord(IReadOnlyList<Region> regions, Region at)
    {
        var ring = new FocusRing(regions);
        while (ring.Current != at) ring.Next();
        return Shelf.Keys(ring, ShelfSource.Hub, glyphs: Gatto.Terminal.GlyphSet.Unicode)
            .Single(k => k.Key == "Enter").Verb;
    }
}
