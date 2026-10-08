using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the blank row above the shelf's door is always kept, even when the frame runs over and the fit must take a blank
public class DoorBlankFitTests
{
    private static ModelRow Hub(int i, FitRegime fit) => ShelfRows.Of(
        $"unsloth/model-{i:00}-GGUF", "unsloth",
        new HubQuant($"model-{i:00}-Q4_K_M.gguf", 4_000_000_000, null),
        fit, 262144, false, Badge: null, Downloads: 5, Gated: false, Params: 8_000_000_000);

    private static FoundModel OnDisk(string name) =>
        new(Path.Combine(@"C:\weights", name + ".gguf"), 4_000_000_000, null);

    //the hub shelf with 18 models and the discovery line, with the flow's row budget set from this height. tooBig hides rows until a.
    private static WizardScreen.Choice HubShelf(int height, int tooBig)
    {
        var probes = new WizardProbes
        {
            Rows = [.. Enumerable.Range(1, 18).Select(i =>
                Hub(i, i > 18 - tooBig ? FitRegime.DoesNotFit : FitRegime.FitsGpu))],
            Found = [OnDisk("one"), OnDisk("two")],
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, RowBudget = Shelf.RowBudget(height) };
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(ShelfSource.Hub, shelf.Shelf!.Source);
        Assert.Contains(shelf.BodyRows ?? [], r => r.Text.Contains("m shows them", StringComparison.Ordinal));
        return shelf;
    }

    //the local shelf with 18 models, which draws two escape rows under the table
    private static WizardScreen.Choice LocalShelf(int height)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [.. Enumerable.Range(1, 18).Select(i =>
                new FoundModel($@"C:\weights\model-{i:00}-Q4_K_M.gguf", 4_000_000_000, null))],
        };
        var flow = new SetupFlow(probes) { RowBudget = Shelf.RowBudget(height) };
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
        Assert.Equal(ShelfSource.Local, shelf.Shelf!.Source);
        return shelf;
    }

    public static TheoryData<string> Shelves() => new() { "hub", "hub-too-big", "local" };

    private static IReadOnlyList<string> Frame(string which, int height) =>
        WalkRender.SettledFrame(which switch
        {
            "hub" => HubShelf(height, tooBig: 0),
            "hub-too-big" => HubShelf(height, tooBig: 3),
            _ => LocalShelf(height),
        }, 120, height: height).Rows;

    private static int ModelRows(IReadOnlyList<string> rows) =>
        rows.Count(r => r.Contains("model-", StringComparison.Ordinal) && r.Contains(" GB ", StringComparison.Ordinal));

    private static string Dump(int height, IReadOnlyList<string> rows) =>
        $"at {height}: {rows.Count} rows, {ModelRows(rows)} model rows:\n" + string.Join("\n", rows);

    //the door is the row above the closing rule and the keys row
    private static int DoorOf(IReadOnlyList<string> rows)
    {
        var door = rows.Count - 3;
        Assert.Contains("search", rows[door], StringComparison.Ordinal);
        return door;
    }

    //at these heights every row outside the models is one the window counts, so the frame fits and keeps both blanks
    [Theory]
    [MemberData(nameof(Shelves))]
    public void THE_WINDOW_PAYS_FOR_THE_ROWS_OUTSIDE_THE_SHELF(string which)
    {
        foreach (var height in new[] { 26, 30, 34 })
        {
            var rows = Frame(which, height);
            var dump = Dump(height, rows);
            var title = rows.ToList().FindIndex(r => r.Contains("Which model", StringComparison.Ordinal)
                                                     || r.Contains("Found 18 models", StringComparison.Ordinal));

            Assert.True(rows.Count <= height, $"the frame is over, {dump}");
            Assert.StartsWith("  gatto setup", rows[0], StringComparison.Ordinal);
            Assert.True(rows[DoorOf(rows) - 1].Trim().Length == 0, $"the row above the door is not blank, {dump}");
            Assert.True(title >= 0 && rows[title + 1].Trim().Length == 0, $"the row under the title is not blank, {dump}");
        }
    }

    //at 30 rows, 30 less the 15 rows Shelf.FrameRows counts and the discovery line and its blank, leaves 13 models
    [Fact]
    public void THE_REPORTED_30_ROW_FRAME_SHOWS_THIRTEEN_MODELS_AND_KEEPS_ITS_BLANKS()
    {
        var rows = Frame("hub-too-big", 30);

        Assert.True(ModelRows(rows) == 13, Dump(30, rows));
    }

    //at 17 and 22 rows the pane is taller than the table and pads it, so the frame is still over. the fit takes another blank and leaves the door's alone.
    [Theory]
    [MemberData(nameof(Shelves))]
    public void WHEN_THE_PANE_OUTGROWS_A_SHORT_TABLE_THE_FIT_STILL_KEEPS_THE_DOOR_BLANK(string which)
    {
        foreach (var height in new[] { 17, 22 })
        {
            var rows = Frame(which, height);

            Assert.True(rows[DoorOf(rows) - 1].Trim().Length == 0,
                $"the row above the door is not blank, {Dump(height, rows)}");
        }
    }

    //a height below the rows outside the shelf windows the table to one model, since an allowance of 0 reads as unknown
    [Fact]
    public void A_HEIGHT_BELOW_THE_ROWS_OUTSIDE_THE_SHELF_WINDOWS_TO_ONE_MODEL()
    {
        var rows = Frame("local", 2);

        Assert.True(ModelRows(rows) == 1, Dump(2, rows));
    }
}
