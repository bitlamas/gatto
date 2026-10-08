using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the chips row and the local folder slot as the keys meet them, driven through the face
public class ChipsRowKeysTests
{
    private static ModelRow Row(string id) => ShelfRows.Of(
        RepoId: id, Publisher: "unsloth",
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Params: 3_000_000_000, Arch: "qwen3");

    //two rows, so a cursor in the list answers differently
    private static WizardScreen.Choice Shelf()
    {
        var view = new ShelfView(
            [Row("unsloth/one"), Row("unsloth/two")], MachineShape.UnifiedWithShare,
            Families: Families.Load().Ladder, Family: "all");
        return new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [new ChoiceOption("0", "unsloth/one"), new ChoiceOption("1", "unsloth/two")],
            Shelf: view, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode));
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static int StepsBetween(WizardScreen.Choice c, Region from, Region to)
    {
        var ring = Regions(c).ToList();
        return (ring.IndexOf(to) - ring.IndexOf(from) + ring.Count) % ring.Count;
    }

    private static IReadOnlyList<Region> Regions(WizardScreen.Choice c) =>
        Gatto.Cli.Setup.Tui.Shelf.Regions(c.Shelf!, 100, 0, c.Door is not null);

    //the chips are one row, so an arrow there has nothing to move
    [Fact]
    public void THE_ARROWS_ARE_INERT_IN_THE_CHIPS_ROW()
    {
        var c = Shelf();
        var opening = Gatto.Cli.Setup.Tui.Shelf.Opening(c.Shelf!);

        var (answer, _) = WalkRender.Answered(c, 100,
        [
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), StepsBetween(c, opening, Region.Families)),
            Key(ConsoleKey.DownArrow), Key(ConsoleKey.UpArrow),
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), StepsBetween(c, Region.Families, Region.List)),
            Key(ConsoleKey.Enter),
        ]);

        Assert.Equal(c.Options[0].Key, answer);
    }

    //the local shelf's folder slot stays out of the focus ring, since it names a folder and opens nothing
    [Fact]
    public void THE_LOCAL_SHELFS_FOLDER_SLOT_IS_NOT_A_STOP()
    {
        var local = new ShelfView([Row("a/b")], MachineShape.Discrete,
            Families: ["gemma", "all"], Family: "all", Source: ShelfSource.Local, Folder: @"D:\m");

    }
}
