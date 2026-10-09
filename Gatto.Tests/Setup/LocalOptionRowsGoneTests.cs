using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//the local shelf has no option rows: the folder door types a folder, m reaches the Hub, and Esc does on the empty shelf
public class LocalOptionRowsGoneTests
{
    private static (SetupFlow Flow, WizardScreen.Choice Shelf) Local(params (string, string?, string?, int)[] files)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            Found = ShelfFixtures.LocalScan(files),
            //a sweep that looked somewhere, so an empty machine gets the empty shelf rather than the Hub
            Roots = [@"C:\m"],
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)));
    }

    [Fact]
    public void NO_POPULATED_LOCAL_FRAME_DRAWS_THE_OPTION_ROWS()
    {
        var (_, shelf) = Local((@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5));
        var (rows, _) = HitMapTests.Painted(shelf);
        Assert.DoesNotContain(rows, r => r.Contains("Look in another folder"));
        Assert.DoesNotContain(rows, r => r.Contains("Find one to download instead"));
        Assert.Equal(shelf.Shelf!.Rows.Count, shelf.Options.Count);
    }

    [Fact]
    public void ESC_ON_THE_EMPTY_LOCAL_SHELF_SHOWS_THE_HUB_SHELF()
    {
        var (flow, empty) = Local();
        Assert.Equal(SetupFlow.DiscoveredKey, empty.Key);
        Assert.Empty(empty.Shelf!.Rows);
        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));
        Assert.Equal(SetupFlow.SearchKey, hub.Key);
    }

    [Fact]
    public void M_ON_THE_POPULATED_LOCAL_SHELF_SHOWS_THE_HUB_SHELF()
    {
        var (flow, _) = Local((@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5));
        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        Assert.Equal(SetupFlow.SearchKey, hub.Key);
    }
}
