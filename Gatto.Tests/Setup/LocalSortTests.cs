using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//the local shelf draws the size label in the params column and sorts by its magnitude, with the Hub's click and flip
public class LocalSortTests
{
    private static (SetupFlow Flow, WizardScreen.Choice Shelf) Local()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            Found = ShelfFixtures.LocalScan(
                (@"C:\m\big-Q4_K_M.gguf", "llama", "8B", 5),
                (@"C:\m\tiny-Q8_0.gguf", "gemma3", "270M", 1),
                (@"C:\m\none-Q4_K_M.gguf", "qwen3", null, 3)),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)));
    }

    private static string[] Names(WizardScreen.Choice c) => [.. c.Shelf!.Rows.Select(r => r.Model)];

    [Fact]
    public void A_PARAMS_PRESS_SORTS_BY_THE_LABELS_MAGNITUDE_BOTH_WAYS_WITH_A_BLANK_LAST()
    {
        var (flow, shelf) = Local();
        Assert.Equal(["big", "tiny", "none"], Names(shelf));
        var smallest = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.Equal(["tiny", "big", "none"], Names(smallest));
        Assert.True(smallest.Shelf!.SmallestFirst);
        var largest = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.Equal(["big", "tiny", "none"], Names(largest));
        Assert.False(largest.Shelf!.SmallestFirst);
    }

    //the options follow the rows, so the sorted shelf's first row answers its own model
    [Fact]
    public void A_SORTED_ROW_ANSWERS_ITS_OWN_FILE()
    {
        var (flow, _) = Local();
        var smallest = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        flow.Answer(smallest.Options[0].Key);
        Assert.Equal(@"C:\m\tiny-Q8_0.gguf", flow.Selected!.Path);
    }

    [Fact]
    public void THE_LOCAL_TABLE_DRAWS_THE_PARAMS_COLUMN_FROM_THE_LABEL()
    {
        var (_, shelf) = Local();
        var (rows, map) = HitMapTests.Painted(shelf);
        Assert.Single(map.Targets, t => t.Tag.Kind == HitKind.ParamsHeader);
        Assert.Contains(rows, r => r.Contains("tiny") && r.Contains("270M"));
        Assert.Contains(rows, r => r.Contains("big") && r.Contains("8B"));
    }

    [Fact]
    public void A_HUB_SORT_HOLDS_AFTER_M()
    {
        var (flow, _) = ShelfFixtures.Flow("unified");
        flow.Answer(SetupFlow.CtlParams);
        flow.Answer(SetupFlow.CtlSource);
        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        Assert.Equal(ShelfSource.Hub, back.Shelf!.Source);
        Assert.True(back.Shelf.SmallestFirst);
    }
}
