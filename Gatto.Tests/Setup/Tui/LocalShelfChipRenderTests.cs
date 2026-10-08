using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//this asserts what the terminal draws, since the numbered option list appears only when the shelf and its typed field are both null
public class LocalShelfChipRenderTests
{
    private const string B = @"D:\models\b\b-Q4_K_M.gguf";
    private const string C = @"D:\models\c\c-Q4_K_M.gguf";
    private const string D = @"D:\models\d\d-Q4_K_M.gguf";

    private static ModelRow HubRow() =>
        ShelfRows.Of("qwen/qwen3.5-7b", "qwen", new HubQuant("qwen3.5-7b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 7_000_000_000);

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

    [Fact]
    public void THE_FRAME_DRAWS_THE_THREE_MODELS_AFTER_A_HUB_CHIP()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.CtlFamily + AFamily);
        flow.Answer(SetupFlow.Elsewhere);
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(@"D:\models"));

        var frame = string.Join("\n", WalkRender.SettledFrame(screen, 100, "local-after-hub-chip").Rows);

        Assert.Contains("Found 3 models already on this machine", frame, StringComparison.Ordinal);
        //the rows carry no numbers, and the name prints without the .gguf extension
        Assert.Contains("b-Q4_K_M", frame, StringComparison.Ordinal);
        Assert.Contains("c-Q4_K_M", frame, StringComparison.Ordinal);
        Assert.Contains("d-Q4_K_M", frame, StringComparison.Ordinal);
        Assert.Contains("1–3 of 3", frame, StringComparison.Ordinal);

        //the face draws the numbered option list only when it has no shelf and no folder placeholder. seeing it beside the found models means the shelf is missing
        Assert.DoesNotContain("1. Look in another folder", frame, StringComparison.Ordinal);
    }

    //a local chip that hides every row still draws a shelf, and the count line names the all chip that lifts it
    [Fact]
    public void THE_FRAME_DRAWS_THE_CAT_AND_THE_WAY_BACK_WHEN_A_LOCAL_CHIP_EMPTIES_IT()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = false };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.Elsewhere);
        flow.Answer(@"D:\models");
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlFamily + AFamily));

        var frame = string.Join("\n", WalkRender.SettledFrame(screen, 100, "local-chip-empty").Rows);

        Assert.Contains("Found 3 models already on this machine", frame, StringComparison.Ordinal);
        Assert.Contains($"No {AFamily} model on this shelf", frame, StringComparison.Ordinal);
        Assert.Contains("all shows them", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("1. Look in another folder", frame, StringComparison.Ordinal);
    }
}
