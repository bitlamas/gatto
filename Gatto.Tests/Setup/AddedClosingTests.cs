using Gatto.Cli.Setup;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//gatto model's closing record names every model the walk added, in order, and only the last says how to use it
public class AddedClosingTests
{
    private const string Holder = "qwen3.6-35b-a3b";

    private static (SetupFlow Flow, WizardProbes Probes) Started()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            HeldBy = Holder,
            Found = ShelfFixtures.LocalScan(
                (@"C:\m\alpha-Q4_K_M.gguf", "llama", "8B", 5),
                (@"C:\m\beta-Q4_K_M.gguf", "qwen3", "270M", 1)),
            Audition = new AuditionCheck(AuditionOutcome.Passed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 5, 5)),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.CtlSource);
        return (flow, probes);
    }

    private static void AddAnother(SetupFlow flow, int row, string id)
    {
        flow.Answer(row.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var screen = flow.ResumeAfterWrites(id);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.InSessionCheckKey, Assert.IsType<WizardScreen.Choice>(screen).Key);
        flow.Answer(SetupFlow.AddAnother);
        while (flow.NeedsWritesApplied) flow.ResumeAfterWrites(null);
    }

    [Fact]
    public void TWO_MODELS_ADDED_ARE_TWO_LINES_IN_ORDER_AND_ONLY_THE_LAST_SAYS_HOW_TO_USE_IT()
    {
        var (flow, _) = Started();
        AddAnother(flow, 0, "alpha");
        AddAnother(flow, 1, "beta");
        flow.MarkLeaving();

        var rows = flow.AddedClosings.Select(r => r.Text).ToList();
        Assert.Equal(2, rows.Count);
        Assert.StartsWith("alpha added", rows[0]);
        Assert.StartsWith("beta added", rows[1]);
        Assert.Contains($"not checked while {Holder} holds the server", rows[0]);
        Assert.DoesNotContain("/model", rows[0]);
        Assert.Contains("/model beta", rows[1]);
    }

    //a leave at the check ask says the user left, the holder clause belongs to a model added for another
    [Fact]
    public void A_LEAVE_AT_THE_CHECK_ASK_SAYS_THE_USER_LEFT_BEFORE_CHECKING()
    {
        var (flow, _) = Started();
        flow.Answer("0");
        var screen = flow.ResumeAfterWrites("alpha");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.InSessionCheckKey, Assert.IsType<WizardScreen.Choice>(screen).Key);
        flow.MarkLeaving();
        var row = Assert.Single(flow.AddedClosings);
        Assert.Contains("you left before checking it", row.Text);
        Assert.DoesNotContain("holds the server", row.Text);
    }

    [Fact]
    public void ONE_MODEL_ADDED_THEN_A_LEAVE_ON_THE_RETURNED_SHELF_KEEPS_ITS_LINE()
    {
        var (flow, _) = Started();
        AddAnother(flow, 0, "alpha");
        flow.MarkLeaving();

        var row = Assert.Single(flow.AddedClosings);
        Assert.StartsWith("alpha added", row.Text);
        Assert.Contains("/model alpha", row.Text);
        Assert.DoesNotContain("nothing added", row.Text);
    }
}
