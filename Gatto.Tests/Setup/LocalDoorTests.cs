using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the local door is a folder door: it adds a .gguf and sweeps a folder, a word included, and answers an address as its shelf does
public class LocalDoorTests
{
    private const string Notice = "gatto setup connects gatto to a server. This screen adds models.";

    private static (SetupFlow Flow, WizardScreen.Choice Shelf, WizardProbes Probes) Local(bool addRoad)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            Found = ShelfFixtures.LocalScan((@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5)),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        if (addRoad) flow.StartAtModelSegment(); else flow.StartPastEngine();
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)), probes);
    }

    [Fact]
    public void THE_POPULATED_PLACEHOLDER_SAYS_WHAT_THE_DOOR_DOES()
    {
        var (_, shelf, _) = Local(addRoad: true);
        Assert.Equal(SetupFlow.DiscoveredKey, shelf.Key);
        Assert.Equal("type a .gguf to add, a folder to look in\u2026", shelf.Door);
    }

    [Theory]
    [InlineData(@"D:\models", @"D:\models")]
    [InlineData("gemma", "gemma")]
    public void A_FOLDER_OR_A_WORD_IS_SWEPT_AS_A_PATH(string typed, string root)
    {
        var (flow, _, probes) = Local(addRoad: true);
        flow.Answer(ShelfControls.TypedAnswer(typed));
        Assert.EndsWith(root, probes.ScanRoots[^1]);
    }

    [Fact]
    public void A_GGUF_IS_ADDED()
    {
        var (flow, _, _) = Local(addRoad: true);
        flow.Answer(ShelfControls.TypedAnswer(@"C:\m\x-Q4_K_M.gguf"));
        Assert.Equal(@"C:\m\x-Q4_K_M.gguf", flow.Selected?.Path);
    }

    [Fact]
    public void ON_GATTO_MODEL_AN_ADDRESS_SHOWS_THE_NOTICE()
    {
        var (flow, shelf, probes) = Local(addRoad: true);
        var roots = probes.ScanRoots.Count;
        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("http://127.0.0.1:8080")));
        Assert.Equal(SetupFlow.DiscoveredKey, after.Key);
        Assert.Equal(Notice, after.Notice);
        Assert.Equal(roots, probes.ScanRoots.Count);
        Assert.Equal(shelf.AllowBack, after.AllowBack);
    }

    [Fact]
    public void ON_SETUP_AN_ADDRESS_TAKES_THE_CONNECT_ROAD()
    {
        var (flow, _, probes) = Local(addRoad: false);
        flow.Answer(ShelfControls.TypedAnswer("localhost:1234"));
        Assert.Equal(SetupPath.Connect, flow.Path);
        Assert.Equal("localhost:1234", probes.ProbedAt[^1]);
    }
}
