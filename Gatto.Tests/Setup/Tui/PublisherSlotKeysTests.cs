using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the key is driven through the face, since the flow would accept it even if the face never sent it
public class PublisherSlotKeysTests
{
    private static ShelfRow Row(string id) => new(
        RepoId: id, Publisher: "unsloth",
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: 900, Gated: false, Params: 3_000_000_000, Arch: "qwen3");

    //a hub shelf with a curated publisher, since that is what draws the slot. two rows, so a cursor in the list answers differently
    private static WizardScreen.Choice Shelf()
    {
        var view = new ShelfView(
            [Row("unsloth/one"), Row("unsloth/two")], "unsloth", MachineShape.UnifiedWithShare,
            Families: Families.Load().Ladder, Family: "all");
        return new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [new ChoiceOption("0", "unsloth/one"), new ChoiceOption("1", "unsloth/two")],
            Shelf: view, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode));
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    //compute the tabs from the ring, starting at the opening region, since a fixed count pins the test to a shape it doesn't own
    private static IEnumerable<ConsoleKeyInfo> TabTo(WizardScreen.Choice c, Region want) =>
        Enumerable.Repeat(Key(ConsoleKey.Tab), Steps(c, want));

    private static int StepsBetween(WizardScreen.Choice c, Region from, Region to)
    {
        var ring = Shelf_Regions(c).ToList();
        return (ring.IndexOf(to) - ring.IndexOf(from) + ring.Count) % ring.Count;
    }

    private static int Steps(WizardScreen.Choice c, Region want)
    {
        var ring = Shelf_Regions(c).ToList();
        var from = ring.IndexOf(Gatto.Cli.Setup.Tui.Shelf.Opening(c.Shelf!));
        var to = ring.IndexOf(want);
        Assert.True(from >= 0 && to >= 0,
            $"the ring has no {want}: {string.Join(", ", ring)}");
        return (to - from + ring.Count) % ring.Count;
    }

    private static IReadOnlyList<Region> Shelf_Regions(WizardScreen.Choice c) =>
        Gatto.Cli.Setup.Tui.Shelf.Regions(c.Shelf!, 100, 0, c.Door is not null);

    //assert the answer the face returns, since a frame check would pass while the wrong question was answered
    [Fact]
    public void ENTER_IN_THE_PUBLISHER_SLOT_OPENS_THE_PICKER()
    {
        var c = Shelf();

        var (answer, _) = WalkRender.Answered(c, 100,
            [.. TabTo(c, Region.Publisher), Key(ConsoleKey.Enter)]);

        Assert.Equal(SetupFlow.CtlPublisher, answer);
    }

    //the test above would pass if the two answers happened to be equal, so this one names answering a model as the failure
    [Fact]
    public void ENTER_IN_THE_SLOT_DOES_NOT_ANSWER_A_MODEL()
    {
        var c = Shelf();

        var (answer, _) = WalkRender.Answered(c, 100,
            [.. TabTo(c, Region.Publisher), Key(ConsoleKey.Enter)]);

        Assert.DoesNotContain(c.Options, o => o.Key == answer);
    }

    //the arrows do nothing in the slot, so a moved list shows up as the second model being answered
    [Fact]
    public void THE_ARROWS_ARE_INERT_IN_THE_PUBLISHER_SLOT()
    {
        var c = Shelf();

        var (answer, _) = WalkRender.Answered(c, 100,
        [
            .. TabTo(c, Region.Publisher),
            Key(ConsoleKey.DownArrow), Key(ConsoleKey.DownArrow),
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), StepsBetween(c, Region.Publisher, Region.List)),
            Key(ConsoleKey.Enter),
        ]);

        Assert.Equal(c.Options[0].Key, answer);
    }

    //the chips are one row, so an arrow there has nothing to move
    [Fact]
    public void THE_ARROWS_ARE_INERT_IN_THE_CHIPS_ROW_TOO()
    {
        var c = Shelf();

        var (answer, _) = WalkRender.Answered(c, 100,
        [
            .. TabTo(c, Region.Families),
            Key(ConsoleKey.DownArrow), Key(ConsoleKey.UpArrow),
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), StepsBetween(c, Region.Families, Region.List)),
            Key(ConsoleKey.Enter),
        ]);

        Assert.Equal(c.Options[0].Key, answer);
    }

    //the Tab key must still move on, so the fix can't become a zone that swallows every key (an inert slot would pass)
    [Fact]
    public void TAB_STILL_LEAVES_THE_PUBLISHER_SLOT()
    {
        var c = Shelf();

        var (answer, _) = WalkRender.Answered(c, 100,
        [
            .. TabTo(c, Region.Publisher),
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), StepsBetween(c, Region.Publisher, Region.List)),
            Key(ConsoleKey.Enter),
        ]);

        Assert.Equal(c.Options[0].Key, answer);
    }
}
