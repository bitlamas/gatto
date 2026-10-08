using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the chips are toggles over a set of lit families, the landing pair at open, and the chip row is never empty
public class ShelfChipsTests
{
    private static readonly IReadOnlyList<string> Landing = Families.Load().Landing;

    private static ModelRow Row(string id, long billions) => ShelfRows.Of(
        RepoId: id, Publisher: "unsloth", PickedQuant: new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Params: billions * 1_000_000_000);

    private static (SetupFlow Flow, WizardProbes Probes) Open(IReadOnlyList<ModelRow>? rows = null)
    {
        var probes = new WizardProbes { Rows = rows ?? [Row("unsloth/a", 4), Row("unsloth/b", 27), Row("unsloth/c", 9)] };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        return (flow, probes);
    }

    private static IReadOnlyList<string> Lit(WizardProbes probes) => [.. probes.LastRequest!.Lit.Order()];

    private static IReadOnlyList<string> Every() =>
        [.. Families.Load().Ladder.Where(f => f != "all").Order()];

    [Fact]
    public void THE_LANDING_LIGHTS_GEMMA_AND_QWEN()
    {
        var (_, probes) = Open();
        Assert.Equal(Landing.Order(), Lit(probes));
    }

    [Fact]
    public void A_CHIP_CLICK_TOGGLES()
    {
        var (flow, probes) = Open();
        flow.Answer(ShelfControls.FamilyAnswer("glm"));
        Assert.Equal(["gemma", "glm", "qwen"], Lit(probes));
        flow.Answer(ShelfControls.FamilyAnswer("gemma"));
        Assert.Equal(["glm", "qwen"], Lit(probes));
    }

    [Fact]
    public void ALL_LIGHTS_EVERY_FAMILY()
    {
        var (flow, probes) = Open();
        flow.Answer(ShelfControls.FamilyAnswer("all"));
        Assert.Equal(Every(), Lit(probes));
    }

    [Fact]
    public void A_SECOND_CLICK_ON_ALL_RETURNS_THE_LANDING_PAIR()
    {
        var (flow, probes) = Open();
        flow.Answer(ShelfControls.FamilyAnswer("all"));
        flow.Answer(ShelfControls.FamilyAnswer("all"));
        Assert.Equal(Landing.Order(), Lit(probes));
    }

    //turning off the last lit chip brings the landing pair back, so the chip row is never empty
    [Fact]
    public void THE_LAST_LIT_CHIP_BRINGS_THE_LANDING_BACK()
    {
        var (flow, probes) = Open();
        flow.Answer(ShelfControls.FamilyAnswer("gemma"));
        Assert.Equal(["qwen"], Lit(probes));
        flow.Answer(ShelfControls.FamilyAnswer("qwen"));
        Assert.Equal(Landing.Order(), Lit(probes));
    }

    //the params header re-arranges the rows in hand by total parameters and asks the engine nothing
    [Fact]
    public void THE_PARAMS_CLICK_SORTS_WITHOUT_A_REQUEST()
    {
        var (flow, probes) = Open();
        var asked = probes.Requests.Count;

        var smallest = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.Equal(["a", "c", "b"], smallest.Shelf!.Rows.Select(r => r.Model));
        Assert.True(smallest.Shelf.SmallestFirst);

        var largest = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.Equal(["b", "c", "a"], largest.Shelf!.Rows.Select(r => r.Model));
        Assert.False(largest.Shelf.SmallestFirst);
        Assert.Equal(asked, probes.Requests.Count);
    }

    //going back restores the lit families, the lift and the sort as the user left them
    [Fact]
    public void BACK_KEEPS_THE_SHELF_AS_LEFT()
    {
        var (flow, probes) = Open();
        flow.Answer(ShelfControls.FamilyAnswer("glm"));
        flow.Answer(SetupFlow.CtlLift);
        flow.Answer(SetupFlow.CtlParams);
        var left = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));

        flow.Answer("0");
        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.Equal(left.Shelf!.Lit!.Order(), back.Shelf!.Lit!.Order());
        Assert.True(back.Shelf.Lift);
        Assert.False(back.Shelf.SmallestFirst);
        Assert.Equal(left.Shelf.Rows.Select(r => r.Model), back.Shelf.Rows.Select(r => r.Model));
    }
}
