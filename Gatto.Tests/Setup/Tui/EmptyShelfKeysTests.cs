using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Repl.Term;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//two screens take m when there is no list: the empty local shelf from the Hub and the Hub outage from the local shelf
public class EmptyShelfKeysTests
{
    private static readonly ConsoleKeyInfo M = new('m', ConsoleKey.M, false, false, false);
    private static readonly ConsoleKeyInfo B = new('b', ConsoleKey.B, false, false, false);
    private static readonly ConsoleKeyInfo Esc = new('\0', ConsoleKey.Escape, false, false, false);

    private static ShelfRow HubRow() => new(
        "unsloth/gemma-4-26B-A4B-it", "unsloth",
        new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
        Params: 25_200_000_000);

    private static FoundModel OnDisk(string name) =>
        new(Path.Combine(@"C:\home\weights", name, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "gemma3", name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static SetupFlow Flow(bool hubUp, params FoundModel[] found) => new(new WizardProbes
    {
        Llama = @"C:\llama\llama-server.exe",
        Rows = hubUp ? [HubRow()] : [],
        Answer = hubUp ? null : _ => new HubSearchOutcome([], HubSearchCause.HubFailed),
        Roots = [@"C:\home\weights"],
        Found = [.. found],
    }) { CanSwitchSource = true };

    //the Hub answers and the machine has no model, so the flow opens on the Hub shelf
    private static (SetupFlow Flow, WizardScreen.Choice Empty) EmptyLocalFromTheHub()
    {
        var flow = Flow(hubUp: true);
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()).Key);
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(SetupFlow.DiscoveredKey, empty.Key);
        Assert.Empty(empty.Shelf!.Rows);
        return (flow, empty);
    }

    //the Hub is out of reach and the machine has one model, so the flow opens on the local shelf
    private static (SetupFlow Flow, WizardScreen.Choice Outage) OutageFromTheLocalShelf()
    {
        var flow = Flow(hubUp: false, OnDisk("one"));
        Assert.Equal(ShelfSource.Local, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()).Shelf!.Source);
        var outage = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(SetupFlow.SearchKey, outage.Key);
        Assert.Null(outage.Shelf);
        return (flow, outage);
    }

    private static string Footer(IReadOnlyList<string> rows) => rows.Last(r => r.Trim().Length > 0);

    //m records a step when it opens a screen with no list, so Esc on the empty local shelf returns to the Hub shelf
    [Fact]
    public void M_FROM_THE_HUB_TO_THE_EMPTY_LOCAL_SHELF_AND_ESC_RETURNS_TO_THE_HUB()
    {
        var (flow, empty) = EmptyLocalFromTheHub();
        Assert.True(empty.AllowBack);

        var (answer, _) = WalkRender.Answered(empty, 100, [Esc]);
        Assert.Equal(SetupFlow.BackKey, answer);

        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(answer!));
        Assert.Equal(SetupFlow.SearchKey, hub.Key);
        Assert.NotEmpty(hub.Shelf!.Rows);
    }

    //the outage screen is not a shelf, but m switches source, b goes back to the local shelf and Esc still arms the leave
    [Fact]
    public void M_FROM_THE_LOCAL_SHELF_TO_THE_OUTAGE_SCREEN_WHERE_M_WORKS_AND_B_RETURNS()
    {
        var (flow, outage) = OutageFromTheLocalShelf();
        Assert.True(outage.SwitchesSource);
        Assert.True(outage.AllowBack);

        Assert.Equal(SetupFlow.CtlSource, WalkRender.Answered(outage, 100, [M]).Answer);
        var (answer, _) = WalkRender.Answered(outage, 100, [B]);
        Assert.Equal(SetupFlow.BackKey, answer);

        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(answer!));
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        Assert.NotEmpty(local.Shelf.Rows);
    }

    //m back from the outage screen undoes the step m recorded on the way in, so the local shelf offers no back
    [Fact]
    public void M_AGAIN_FROM_THE_OUTAGE_SCREEN_LEAVES_NO_STEP_BEHIND()
    {
        var (flow, _) = OutageFromTheLocalShelf();

        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        Assert.False(local.AllowBack);
    }

    //m between two shelves that have rows records no step, so the second shelf offers no back
    [Fact]
    public void M_BETWEEN_TWO_SHELVES_WITH_ROWS_RECORDS_NO_STEP()
    {
        var flow = Flow(hubUp: true, OnDisk("one"));
        var first = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
        Assert.NotEmpty(first.Shelf!.Rows);

        var other = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.NotEqual(first.Shelf.Source, other.Shelf!.Source);
        Assert.NotEmpty(other.Shelf.Rows);
        Assert.False(other.AllowBack);
    }

    //going forward and back again keeps the step m recorded, so the local shelf still offers no back
    [Fact]
    public void BACK_TO_THE_OUTAGE_SCREEN_THEN_M_LEAVES_NO_STEP_BEHIND()
    {
        var (flow, _) = OutageFromTheLocalShelf();
        Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.TypeAnId));
        var outage = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));
        Assert.Equal(SetupFlow.SearchKey, outage.Key);
        Assert.Null(outage.Shelf);

        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        Assert.False(local.AllowBack);
    }

    [Fact]
    public void THE_OUTAGE_SCREEN_S_FOOTER_OFFERS_M_LOCAL_AND_B_BACK()
    {
        var (_, outage) = OutageFromTheLocalShelf();

        var footer = Footer(WalkRender.SettledFrame(outage, 100).Rows);

        Assert.Contains("m local", footer, StringComparison.Ordinal);
        Assert.Contains("b back", footer, StringComparison.Ordinal);
    }

    //a count of one must read as one, so the assertion is on the frame
    [Fact]
    public void THE_OUTAGE_SCREEN_SAYS_ONE_MODEL_IS_ONE_M_AWAY()
    {
        var (_, outage) = OutageFromTheLocalShelf();

        var frame = string.Join("\n", WalkRender.SettledFrame(outage, 100).Rows);

        Assert.Contains("The 1 model on this machine is one m away", frame, StringComparison.Ordinal);
    }

    //the empty local shelf's field takes every letter, so its footer offers no m
    [Fact]
    public void THE_EMPTY_LOCAL_SHELF_S_FOOTER_DROPS_M_AND_SAYS_ESC_BACK()
    {
        var (_, empty) = EmptyLocalFromTheHub();

        Assert.Equal("  Esc back", Footer(WalkRender.SettledFrame(empty, 100).Rows));
    }

    //the TUI face paints no option row that no key reaches, and the block names Esc the way the footer does
    [Fact]
    public void THE_EMPTY_LOCAL_SHELF_DRAWS_NO_ESCAPE_ROWS_AND_NAMES_ESC()
    {
        var (_, empty) = EmptyLocalFromTheHub();

        var frame = string.Join("\n", WalkRender.SettledFrame(empty, 100).Rows);

        Assert.DoesNotContain("Look in another folder", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Find one to download instead", frame, StringComparison.Ordinal);
        Assert.Contains("press Esc to go back", frame, StringComparison.Ordinal);
    }

    //the numbered face binds no m and has no typed field, so the flow keeps both options for it (nothing constructs that face today)
    [Fact]
    public void THE_EMPTY_LOCAL_SHELF_KEEPS_BOTH_OPTIONS_FOR_THE_NUMBERED_FACE()
    {
        var (_, empty) = EmptyLocalFromTheHub();

        Assert.Equal([SetupFlow.Elsewhere, SetupFlow.SearchInstead], empty.Options.Select(o => o.Key));
    }

    //the empty local shelf's golden is rendered from the flow's own screen, so it moves with the product
    [Fact]
    public void THE_FLOW_S_EMPTY_LOCAL_SHELF_IS_ITS_GOLDEN()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = [HubRow()],
            Roots = [@"C:\Users\you\.gatto\weights\", @"C:\Users\you\Downloads\"],
        }) { CanSwitchSource = true };
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()).Key);
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Golden.AssertEquals("s5", "unified-96-local-empty", 100, WalkRender.SettledFrame(empty, 100).Rows);
    }

    //the flow's own outage screen is the oracle, so don't hand-build a shelf-shaped copy of it for the golden
    [Fact]
    public void THE_FLOW_S_OUTAGE_SCREEN_IS_ITS_GOLDEN()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = [],
            Answer = _ => new HubSearchOutcome([], HubSearchCause.HubFailed),
            Roots = [@"C:\Users\you\.gatto\weights\"],
            Found = [OnDisk("one")],
        }) { CanSwitchSource = true };
        Assert.Equal(ShelfSource.Local, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()).Shelf!.Source);
        var outage = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Golden.AssertEquals("s5", "unified-96-hub-out-of-reach", 100, WalkRender.SettledFrame(outage, 100).Rows);
    }
}
