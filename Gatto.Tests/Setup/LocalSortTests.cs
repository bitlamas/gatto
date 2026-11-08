using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//the local shelf draws the size label in the params column and sorts by its magnitude, with the Hub's click and flip
public class LocalSortTests
{
    private static (SetupFlow Flow, SetupScreen.Choice Shelf) Local()
    {
        var probes = new FakeSetupProbes
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
        return (flow, Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlSource)));
    }

    private static string[] Names(SetupScreen.Choice c) => [.. c.Shelf!.Rows.Select(r => r.Model)];

    [Fact]
    public void A_PARAMS_PRESS_SORTS_BY_THE_LABELS_MAGNITUDE_BOTH_WAYS_WITH_A_BLANK_LAST()
    {
        var (flow, shelf) = Local();
        Assert.Equal(["big", "tiny", "none"], Names(shelf));
        var smallest = Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.Equal(["tiny", "big", "none"], Names(smallest));
        Assert.True(smallest.Shelf!.SmallestFirst);
        var largest = Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.Equal(["big", "tiny", "none"], Names(largest));
        Assert.False(largest.Shelf!.SmallestFirst);
    }

    //the options follow the rows, so the sorted shelf's first row answers its own model
    [Fact]
    public void A_SORTED_ROW_ANSWERS_ITS_OWN_FILE()
    {
        var (flow, _) = Local();
        var smallest = Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        flow.Answer(smallest.Options[0].Key);
        Assert.Equal(@"C:\m\tiny-Q8_0.gguf", flow.Selected!.Path);
    }

    //a name-ordered scan on an 8 GB card, so the regimes interleave until the shelf groups them
    private static SetupScreen.Choice Mixed()
    {
        var probes = new FakeSetupProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("discrete"),
            Found = ShelfFixtures.LocalScan(
                (@"C:\m\a-huge-Q4_K_M.gguf", "llama", "70B", 40),
                (@"C:\m\b-small-Q4_K_M.gguf", "llama", "1B", 1),
                (@"C:\m\c-mid-Q4_K_M.gguf", "llama", "14B", 12),
                (@"C:\m\d-huger-Q4_K_M.gguf", "llama", "120B", 70),
                (@"C:\m\e-larger-Q4_K_M.gguf", "llama", "7B", 4),
                (@"C:\m\f-wide-Q4_K_M.gguf", "llama", "24B", 16)),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        return Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
    }

    [Fact]
    public void EACH_REGIME_IS_ONE_GROUP_WITH_THE_LARGEST_FIRST_INSIDE_IT()
    {
        var rows = Mixed().Shelf!.Rows;
        Assert.True(rows.Select(r => r.Fit).Distinct().Count() >= 3);
        Assert.Equal(rows.Select(r => r.Fit).Order(), rows.Select(r => r.Fit));
        foreach (var group in rows.GroupBy(r => r.Fit))
        {
            var totals = group.Select(r => Gatto.Core.Acquire.ModelStructure.SizeMagnitude(r.ParamsLabel)).ToList();
            Assert.Equal(totals.OrderDescending(), totals);
        }
    }

    [Fact]
    public void EACH_FIT_HEADING_IS_DRAWN_ONCE()
    {
        var shelf = Mixed();
        var (painted, _) = HitMapTests.Painted(shelf, height: 60);
        foreach (var fit in shelf.Shelf!.Rows.Select(r => r.Fit).Distinct())
            Assert.Single(painted, p => p.TrimStart().StartsWith(Shelf.GroupHeading(fit, ShelfSource.Local), StringComparison.Ordinal));
    }

    //one regime that fits and the too big rows are two groups, so the jump back to the largest model sits under its own heading
    [Fact]
    public void ONE_FITTING_REGIME_AND_TOO_BIG_ARE_TWO_GROUPS()
    {
        var probes = new FakeSetupProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("discrete"),
            Found = ShelfFixtures.LocalScan((@"C:\m\a-huge-Q4_K_M.gguf", "llama", "120B", 70), (@"C:\m\b-small-Q4_K_M.gguf", "llama", "1B", 1)),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        var shelf = Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        Assert.Equal([Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.DoesNotFit], shelf.Shelf!.Rows.Select(r => r.Fit));
        Assert.True(Shelf.Grouped(shelf.Shelf));
        var (painted, _) = HitMapTests.Painted(shelf);
        Assert.Single(painted, p => p.TrimStart().StartsWith("too big for this machine", StringComparison.Ordinal));
    }

    //the local memory group is sorted by params, so its heading makes no claim about speed, which the Hub's engine order does
    [Fact]
    public void THE_LOCAL_MEMORY_HEADING_CLAIMS_NO_SPEED_ORDER()
    {
        Assert.Equal("in system memory", Shelf.GroupHeading(Gatto.Core.Models.FitRegime.FitsRamOnly, ShelfSource.Local));
        Assert.Equal("in system memory, fastest first", Shelf.GroupHeading(Gatto.Core.Models.FitRegime.FitsRamOnly));
        var (painted, _) = HitMapTests.Painted(Mixed(), height: 60);
        Assert.DoesNotContain(painted, p => p.Contains("fastest first"));
    }

    //the options follow the grouped rows, so the first row answers the first model drawn
    [Fact]
    public void A_GROUPED_ROW_ANSWERS_ITS_OWN_FILE()
    {
        var shelf = Mixed();
        Assert.Equal(shelf.Shelf!.Rows[0].Model, shelf.Options[0].Label);
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
        var back = Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        Assert.Equal(ShelfSource.Hub, back.Shelf!.Source);
        Assert.True(back.Shelf.SmallestFirst);
    }
}
