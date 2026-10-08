using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//back steps one screen per push, a control re-asks in place and pushes nothing. the shelf's filter state travels back with its screen.
public class BackByScreenTests
{
    private static WizardProbes Probes() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Rows =
        [
            ShelfRows.Of("unsloth/gemma-4-26B-A4B-it", "unsloth",
                new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
                FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5,
                Gated: false, Params: 25_200_000_000, Arch: "gemma3"),
            ShelfRows.Of("unsloth/Qwen3.6-35B-A3B", "unsloth",
                new HubQuant("Qwen3.6-35B-A3B-Q4_K_M.gguf", 4_100_000_000, null),
                FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 4,
                Gated: false, Params: 34_700_000_000, Arch: "qwen3moe"),
        ],
    };

    private static (SetupFlow Flow, WizardScreen.Choice Shelf) OnTheShelf()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = true };
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.NotNull(shelf.Shelf);
        return (flow, shelf);
    }

    //a repeated control press adds no step of its own, so five of them then one back leaves the shelf.
    [Fact]
    public void FIVE_PRESSES_OF_A_THEN_ONE_B_LEAVES_THE_SHELF()
    {
        var (flow, _) = OnTheShelf();
        for (var i = 0; i < 5; i++) flow.Answer(ShelfControls.LiftAnswer());

        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.Answer(SetupFlow.BackKey)));
    }

    //a chip press leaves no back step, one back still arrives at the screen before the shelf.
    [Fact]
    public void A_CHIP_THEN_B_LANDS_ON_THE_SCREEN_BEFORE_THE_SHELF()
    {
        var (flow, _) = OnTheShelf();
        var filtered = Assert.IsType<WizardScreen.Choice>(ChipWalk.Narrow(flow, "gemma"));
        Assert.Equal("gemma", filtered.Shelf!.Family);

        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.Answer(SetupFlow.BackKey)));
    }

    //the filter survives the step back, so returning from the engine screen reopens the shelf as the user left it.
    [Fact]
    public void THE_FILTER_SURVIVES_THE_STEP_BACK_AND_THE_STEP_FORWARD()
    {
        var (flow, _) = OnTheShelf();
        ChipWalk.Narrow(flow, "gemma");
        flow.Answer(SetupFlow.BackKey);

        var again = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.FoundUse));

        Assert.Equal("gemma", again.Shelf!.Family);
    }

    //a screen answer still pushes a step, so a back from the screen it opens returns to the shelf.
    [Fact]
    public void A_SCREEN_ANSWER_STILL_PUSHES()
    {
        var (flow, shelf) = OnTheShelf();
        var next = flow.Answer(shelf.Options[0].Key);
        Assert.NotEqual(SetupFlow.SearchKey, ScreenKey.Of(next));

        Assert.Equal(SetupFlow.SearchKey, ScreenKey.Of(flow.Answer(SetupFlow.BackKey)));
    }

    //assert the no cases too, a predicate that says yes to everything would pass the first half alone.
    [Fact]
    public void EVERY_SHELF_CONTROL_IS_ONE_AND_THE_TWO_THAT_ADVANCE_ARE_NOT()
    {
        foreach (var c in ShelfControls.All)
            Assert.True(ShelfControls.IsControl(c.Answer), c.Answer);

        Assert.True(ShelfControls.IsControl(ShelfControls.FamilyAnswer("gemma")));
        Assert.True(ShelfControls.IsControl(ShelfControls.SourceAnswer()));

        //typed can re-search or open a row and pick advances, so neither may be a control (both leave the screen in some run).
        Assert.False(ShelfControls.IsControl(SetupFlow.CtlTyped + "unsloth/thing"));
        Assert.False(ShelfControls.IsControl(SetupFlow.CtlPick + "row:Q4_K_M"));
    }
}
