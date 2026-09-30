using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a rendered key is a promise, so every assertion here presses m and reads what came back
public class SourceSwitchTests
{
    //a found model with a readable architecture, since the family chips filter on it and a header-less fixture would leave those tests vacuous
    private static FoundModel OnDisk(string name, string arch) =>
        new(Path.Combine(@"C:\weights", name, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, arch, name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static ShelfRow HubRow() => new(
        "unsloth/gemma-4-26B-A4B-it", "unsloth",
        new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
        Params: 25_200_000_000);

    //a sweep that finds two models on disk, one gemma and one qwen, and their order matters
    private static (SetupFlow Flow, WizardProbes Probes) TwoOnDisk()
    {
        var probes = new WizardProbes
        {
            Rows = [HubRow()],
            Found = [OnDisk("gemma-4-26B-A4B-it", "gemma3"), OnDisk("Qwen3-30B-A3B", "qwen3")],
        };
        return (Flow(probes), probes);
    }

    //every flow here binds m, since the key exists only on the tui face and a default flow follows the numbered face
    private static SetupFlow Flow(WizardProbes probes) => new(probes) { CanSwitchSource = true };

    //press m to reach the local shelf, and assert the shelf arrived at, so a blind helper cannot keep passing
    private static WizardScreen.Choice LocalShelfOf(SetupFlow flow)
    {
        flow.StartPastEngine();
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        return local;
    }

    private static ConsoleKeyInfo M => new('m', ConsoleKey.M, false, false, false);

    //the key

    //with models on disk and a reachable Hub the walk opens on the Hub shelf, and the discovery line names what the sweep found
    [Fact]
    public void WITH_MODELS_ON_DISK_THE_WALK_OPENS_ON_THE_HUB_SHELF()
    {
        var (flow, _) = TwoOnDisk();
        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.SearchKey, opened.Key);
        Assert.Equal(ShelfSource.Hub, opened.Shelf!.Source);
    }

    //the face answers m with the source control
    [Fact]
    public void M_ON_A_SHELF_ANSWERS_THE_SOURCE_CONTROL()
    {
        var (flow, _) = TwoOnDisk();
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(ShelfControls.SourceAnswer(), WalkRender.AnswerAfter(shelf, 100, [M]));
    }

    //on a screen with no shelf m is a letter the user meant to type
    [Fact]
    public void M_IS_JUST_A_LETTER_WHERE_THERE_IS_NO_SHELF()
    {
        var plain = new WizardScreen.Choice("k", "Which?",
            [new ChoiceOption("0", "one"), new ChoiceOption("1", "two")]);

        Assert.NotEqual(ShelfControls.SourceAnswer(), WalkRender.AnswerAfter(plain, 100,
            [M, new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]));
    }

    //where m goes

    //from the Hub shelf m opens the local one, a shelf with the same table and arithmetic whose source says which it is
    [Fact]
    public void M_TAKES_YOU_FROM_THE_HUB_SHELF_TO_THE_LOCAL_ONE()
    {
        var (flow, _) = TwoOnDisk();
        flow.StartPastEngine();

        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Equal(SetupFlow.DiscoveredKey, local.Key);
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        Assert.Equal(2, local.Shelf!.Rows.Count);
    }

    //the key is a toggle, so a second press must return to the Hub shelf
    [Fact]
    public void AND_BACK_AGAIN()
    {
        var (flow, _) = TwoOnDisk();
        flow.StartPastEngine();
        flow.Answer(ShelfControls.SourceAnswer());

        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Equal(SetupFlow.SearchKey, hub.Key);
        Assert.Equal(ShelfSource.Hub, hub.Shelf!.Source);
    }

    //m sweeps the disk again, a user may have just finished a download in another window. the assertion needs a file that did not exist when the walk started
    [Fact]
    public void M_SWEEPS_AGAIN_RATHER_THAN_SHOWING_WHAT_THE_WALK_FOUND()
    {
        var probes = new WizardProbes { Rows = [HubRow()] };
        //the file arrives between the walk's first sweep and the one m asks for, as a download finishing in another window would
        probes.OnScan = n => { if (n >= 2) probes.Found = [OnDisk("arrived-later", "gemma3")]; };
        var flow = Flow(probes);
        //nothing found means the walk opens on the Hub shelf, so one m reaches the local one. the file must not exist when the walk starts
        flow.StartPastEngine();

        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Single(local.Shelf!.Rows);
        Assert.Contains("arrived-later", local.Shelf!.Rows[0].RepoId, StringComparison.Ordinal);
    }

    //the fork

    //a local row goes to the check step, there is nothing to download and no published fingerprint to state
    [Fact]
    public void PICKING_A_LOCAL_MODEL_SKIPS_THE_DOWNLOAD_STEP()
    {
        var (flow, _) = TwoOnDisk();
        var local = LocalShelfOf(flow);

        var next = flow.Answer(local.Options[0].Key);

        Assert.NotNull(flow.Selected);

        //read the key off whichever screen shape came back, that is the name the walk map records
        var key = next switch
        {
            WizardScreen.Choice c2 => c2.Key,
            WizardScreen.Ask a => a.Key,
            WizardScreen.Info i => i.Key,
            _ => "",
        };
        Assert.DoesNotContain("download", key, StringComparison.OrdinalIgnoreCase);
    }

    //an option's key indexes the unfiltered list, so a key counting shown rows would adopt the wrong model while the shelf still looks right
    [Fact]
    public void A_FILTERED_ROW_ADOPTS_THE_MODEL_IT_NAMES()
    {
        var (flow, _) = TwoOnDisk();
        LocalShelfOf(flow);

        var qwen = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer("qwen")));
        Assert.Single(qwen.Shelf!.Rows);
        Assert.Contains("Qwen3", qwen.Shelf!.Rows[0].RepoId, StringComparison.Ordinal);

        flow.Answer(qwen.Options[0].Key);

        Assert.Contains("Qwen3", flow.Selected!.Path, StringComparison.Ordinal);
    }

    //the rescan behind m passes no folder after a typed one. a typed folder that stays put is guarded in TypedFolderShelfTests
    [Fact]
    public void THE_RESCAN_BEHIND_M_HAS_NO_FOLDER_AFTER_A_TYPED_ONE()
    {
        var probes = new WizardProbes { Rows = [HubRow()] };
        var flow = Flow(probes);
        flow.StartPastEngine();

        flow.Answer(SetupFlow.Elsewhere);
        flow.Answer(@"D:\my models");
        //an empty typed folder stays on the local shelf, so the round trip goes to the Hub first
        Assert.Equal(SetupFlow.SearchKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer())).Key);
        var asked = probes.ScanRoots.Count;

        flow.Answer(ShelfControls.SourceAnswer());

        Assert.Null(probes.ScanRoots[^1]);
        Assert.True(probes.ScanRoots.Count > asked, "`m` did not sweep again at all");
    }

    //the hidden count comes from the same filter as the rows, so the shelf can't report a number it didn't hide
    [Fact]
    public void THE_HIDDEN_COUNT_IS_WHAT_THE_FILTER_ACTUALLY_HID()
    {
        var (flow, _) = TwoOnDisk();
        LocalShelfOf(flow);

        var qwen = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer("qwen")));

        Assert.Equal(1, qwen.Shelf!.HiddenByFamily);
        Assert.Equal(2, qwen.Shelf!.Total);
    }
}
