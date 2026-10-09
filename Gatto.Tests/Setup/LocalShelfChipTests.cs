using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//a family chip filters only the shelf it was pressed on, each test below holds one side of that
public class LocalShelfChipTests
{
    private const string B = @"D:\models\b\b-Q4_K_M.gguf";
    private const string C = @"D:\models\c\c-Q4_K_M.gguf";
    private const string D = @"D:\models\d\d-Q4_K_M.gguf";

    private static ModelRow HubRow() =>
        ShelfRows.Of("qwen/qwen3.5-7b", "qwen", new HubQuant("qwen3.5-7b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 7_000_000_000);

    //the first sweep finds one model, the rescan of a typed folder finds three. none of the three has a family header, so a chip filter drops all three
    private static WizardProbes Probes()
    {
        var p = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [HubRow()],
            Found = [new FoundModel(@"C:\weights\a\a-Q4_K_M.gguf", 4_000_000_000, null)],
        };
        p.OnScan = n =>
        {
            if (n >= 2)
                p.Found =
                [
                    new FoundModel(B, 4_000_000_000, null),
                    new FoundModel(C, 3_000_000_000, null),
                    new FoundModel(D, 2_000_000_000, null),
                ];
        };
        return p;
    }

    private static string AFamily => Families.Load().Ladder[0];

    //a chip pressed on the Hub shelf has no say on the local shelf. models found in a typed folder must all still be listed after it.
    [Fact]
    public void A_HUB_CHIP_DOES_NOT_FILTER_THE_LOCAL_SHELF()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.CtlFamily + AFamily);

        flow.Answer(SetupFlow.Elsewhere);
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(@"D:\models"));

        Assert.Equal("Found 3 models already on this machine", screen.Question);
        Assert.NotNull(screen.Shelf);
        Assert.Equal(3, screen.Shelf!.Rows.Count);
        Assert.NotNull(screen.Door);
        Assert.Equal("all", screen.Shelf.Family);
    }

    //a local filter the user chose must stand, the screen stays a shelf and names the key that clears it
    [Fact]
    public void A_LOCAL_CHIP_THAT_MATCHES_NOTHING_KEEPS_THE_SHELF_AND_SAYS_SO()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = false };
        flow.StartAtModelSegment();
        flow.Answer(ShelfControls.TypedAnswer(@"D:\models"));

        var screen = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.CtlFamily + AFamily));

        Assert.Equal("Found 3 models already on this machine", screen.Question);
        Assert.NotNull(screen.Door);
        Assert.NotNull(screen.Shelf);
        Assert.Empty(screen.Shelf!.Rows);
        Assert.Equal(AFamily, screen.Shelf.Family);
        Assert.Equal(3, screen.Shelf.HiddenByFamily);

        //the empty text names the key that clears the filter (all), and both shelves compose the clause the same way
        var empty = string.Join("\n", screen.Shelf.Empty ?? []);
        Assert.Contains(AFamily, empty, StringComparison.Ordinal);
        Assert.Contains("all shows them", empty, StringComparison.Ordinal);
    }

    //isolation must hold in both directions, a local chip that writes the Hub's field would pass the sibling test and only move the defect
    [Fact]
    public void A_LOCAL_CHIP_DOES_NOT_FILTER_THE_HUB_SHELF()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.Elsewhere);
        flow.Answer(@"D:\models");
        flow.Answer(SetupFlow.CtlFamily + AFamily);

        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));

        Assert.Equal(Families.Load().Landing, hub.Shelf!.Lit!.Order());
    }
}
