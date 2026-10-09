using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//both check screens of gatto model can add the model and return to the shelf it was picked from, for another
public class AddAnotherModelTests
{
    private const string Holder = "qwen3.6-35b-a3b";

    //the add road over found files, with the session's model holding the server when a holder is named
    private static (SetupFlow Flow, WizardProbes Probes) Started(string? heldBy = Holder, string? defaultModel = null)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            HeldBy = heldBy,
            Found = ShelfFixtures.LocalScan(
                (@"C:\m\alpha-Q4_K_M.gguf", "llama", "8B", 5),
                (@"C:\m\beta-Q4_K_M.gguf", "qwen3", "270M", 1)),
            Audition = new AuditionCheck(AuditionOutcome.Passed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 5, 5)),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        Assert.Equal(SetupFlow.DiscoveredKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)).Key);
        return (flow, probes);
    }

    //a local row picked and its scaffold written, the id the writer would produce passed back in
    private static WizardScreen.Choice PickAndReachTheCheck(SetupFlow flow, int row, string id)
    {
        var screen = flow.Answer(row.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(flow.NeedsWritesApplied);
        screen = flow.ResumeAfterWrites(id);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return Assert.IsType<WizardScreen.Choice>(screen);
    }

    private static WizardScreen ThroughThePause(SetupFlow flow, string answer)
    {
        var screen = flow.Answer(answer);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return screen;
    }

    [Fact]
    public void THE_IN_SESSION_CHECK_OFFERS_ADD_ANOTHER_LAST_AND_ESC_KEEPS_ADD_IT_UNCHECKED()
    {
        var (flow, _) = Started();
        var check = PickAndReachTheCheck(flow, 0, "alpha");
        Assert.Equal(SetupFlow.InSessionCheckKey, check.Key);
        Assert.Equal("Add another model", check.Options[^1].Label);
        Assert.Equal(SetupFlow.AddAnother, check.Options[^1].Key);
        Assert.Equal(SetupFlow.AddUnchecked, Assert.Single(check.Options, o => o.EscVerb is not null).Key);
    }

    [Fact]
    public void ADD_ANOTHER_WRITES_THE_DEFAULT_ONLY_WHEN_ABSENT_AND_RETURNS_TO_THE_SHELF_WITH_NO_PROVE()
    {
        var (flow, probes) = Started();
        PickAndReachTheCheck(flow, 0, "alpha");
        var saving = flow.Answer(SetupFlow.AddAnother);
        Assert.True(flow.NeedsWritesApplied);
        Assert.Equal("alpha", flow.Writes.DefaultModelIfAbsent);
        Assert.Null(flow.Writes.DefaultModel);
        Assert.IsType<WizardScreen.Info>(saving);
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.ResumeAfterWrites(null));
        Assert.Equal(SetupFlow.DiscoveredKey, shelf.Key);
        Assert.Equal(0, probes.Proofs);
        Assert.Equal(0, probes.AuditionStarts);
    }

    [Fact]
    public void A_SECOND_PICK_REACHES_THE_CHECK_AGAIN()
    {
        var (flow, _) = Started();
        PickAndReachTheCheck(flow, 0, "alpha");
        ThroughThePause(flow, SetupFlow.AddAnother);
        Assert.Equal(SetupFlow.InSessionCheckKey, PickAndReachTheCheck(flow, 1, "beta").Key);
    }

    [Fact]
    public void THE_SHELF_RETURNS_AS_THE_USER_LEFT_IT()
    {
        var (flow, _) = Started();
        flow.Answer(SetupFlow.CtlParams);
        var sorted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
        Assert.False(sorted.Shelf!.SmallestFirst);
        PickAndReachTheCheck(flow, 0, "alpha");
        var back = Assert.IsType<WizardScreen.Choice>(ThroughThePause(flow, SetupFlow.AddAnother));
        Assert.False(back.Shelf!.SmallestFirst);
        Assert.Equal(sorted.Shelf.Rows.Select(r => r.Model), back.Shelf.Rows.Select(r => r.Model));
    }

    [Fact]
    public void THE_LOCAL_SHELF_RESCANS_ON_RETURN()
    {
        var (flow, probes) = Started();
        PickAndReachTheCheck(flow, 0, "alpha");
        var scans = probes.ScanRoots.Count;
        ThroughThePause(flow, SetupFlow.AddAnother);
        Assert.True(probes.ScanRoots.Count > scans);
    }

    //the returned shelf has nothing behind it, and the screens after it step back again
    [Fact]
    public void IN_THE_SECOND_ROUND_ESC_STEPS_BACK_AS_IN_THE_FIRST()
    {
        var (flow, _) = Started();
        PickAndReachTheCheck(flow, 0, "alpha");
        var shelf = Assert.IsType<WizardScreen.Choice>(ThroughThePause(flow, SetupFlow.AddAnother));
        Assert.False(shelf.AllowBack);
        var typed = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"C:\m")));
        Assert.True(typed.AllowBack);
        Assert.Equal(SetupFlow.DiscoveredKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey)).Key);
    }

    //a model added from the Hub returns to the Hub shelf with its sort, and nothing behind it
    [Fact]
    public void THE_HUB_SHELF_RETURNS_AFTER_A_HUB_PICK_WITH_ITS_SORT()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Answer = _ => WizardProbes.Outcome([
                ShelfRows.Of("qwen/a", "qwen", new HubQuant("a-Q4_K_M.gguf", 4_000_000_000, null), FitRegime.FitsGpu, 32768, false, Badge: null, Downloads: 5, Gated: false, Params: 8_000_000_000),
                ShelfRows.Of("qwen/b", "qwen", new HubQuant("b-Q4_K_M.gguf", 2_000_000_000, null), FitRegime.FitsGpu, 32768, false, Badge: null, Downloads: 5, Gated: false, Params: 4_000_000_000)]),
            HeldBy = "qwen3.5",
            Snapshot = ShelfFixtures.Snapshot("unified"),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()).Key);
        flow.Answer(SetupFlow.CtlParams);
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.InSessionCheckKey, Assert.IsType<WizardScreen.Choice>(screen).Key);
        var back = Assert.IsType<WizardScreen.Choice>(ThroughThePause(flow, SetupFlow.AddAnother));
        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.True(back.Shelf!.SmallestFirst);
        Assert.False(back.AllowBack);
    }

    //the second model's line names no holder when it holds the server itself, so the first model's holder does not leak
    [Fact]
    public void A_SECOND_MODEL_NO_ONE_HOLDS_THE_SERVER_FOR_SAYS_NO_HOLDER()
    {
        var (flow, _) = Started(heldBy: "beta");
        Assert.Equal(SetupFlow.InSessionCheckKey, PickAndReachTheCheck(flow, 0, "alpha").Key);
        ThroughThePause(flow, SetupFlow.AddAnother);
        Assert.Equal(SetupFlow.AuditionOfferKey, PickAndReachTheCheck(flow, 1, "beta").Key);
        ThroughThePause(flow, SetupFlow.AddAnother);
        flow.MarkLeaving();
        var rows = flow.AddedClosings.Select(r => r.Text).ToList();
        Assert.Contains("beta holds the server", rows[0]);
        Assert.DoesNotContain("holds the server", rows[1]);
    }

    [Fact]
    public void WITH_NO_SERVER_HELD_THE_AUDITION_OFFER_ENDS_WITH_ADD_ANOTHER()
    {
        var (flow, probes) = Started(heldBy: null);
        var offer = PickAndReachTheCheck(flow, 0, "alpha");
        Assert.Equal(SetupFlow.AuditionOfferKey, offer.Key);
        Assert.Equal([SetupFlow.Yes, SetupFlow.Skip, SetupFlow.AddAnother], offer.Options.Select(o => o.Key));
        Assert.Equal("Add another model", offer.Options[^1].Label);
        var shelf = Assert.IsType<WizardScreen.Choice>(ThroughThePause(flow, SetupFlow.AddAnother));
        Assert.Equal(SetupFlow.DiscoveredKey, shelf.Key);
        Assert.Equal(0, probes.Proofs);
    }
}
