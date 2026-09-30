using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the consent folder field is typeable and gatto reads that drive, so every test gives the screen input. a refusal lists download, margin and free space
public class ConsentDoorTests
{
    private const string Repo = "unsloth/gemma-4-26B-A4B-it-GGUF";
    private const string Weights = "gemma-4-26B-A4B-it-Q4_K_M.gguf";
    private const string Mmproj = "mmproj-F16.gguf";
    private const string Into = @"C:\Users\you\.gatto\weights\gemma-4-26b-a4b-it\";

    private static HubQuant Quant(string file, long bytes) => new(file, bytes, new string('a', 64));

    //the fake disk reports this value, injected rather than read so a test never depends on this machine's drives
    private static WizardProbes Probes(long weights, long? mmproj = null, long? free = null)
    {
        var offer = new ModelFetchOffer(Repo, "gemma-4-26b-a4b-it", Into,
            Quant(Weights, weights), mmproj is { } m ? Quant(Mmproj, m) : null);
        return new WizardProbes
        {
            HubOffer = offer,
            FreeSpace = free,
            ModelResult = new HubFetchResult(HubFetchOutcome.Arrived),
            Rows = [Row(Repo, Quant(Weights, weights))],
        };
    }

    //a shelf row that offers the quant under test.
    private static ShelfRow Row(string repoId, HubQuant quant) =>
        new(repoId, repoId.Split('/')[0], quant, Gatto.Core.Models.FitRegime.FitsGpu,
            32768, false, null, 100, false);

    private static (SetupFlow Flow, WizardScreen.Choice Consent) At(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.Answer("0")));
    }

    private static string Text(WizardScreen screen) =>
        string.Join("\n", Assert.IsType<WizardScreen.Choice>(screen).BodyRows?.Select(r => r.Text) ?? [])
        + "\n" + string.Join("\n",
            ModelFetchView.Rows(Assert.IsType<WizardScreen.Choice>(screen).Model!.Value, 100,
                GlyphSet.Unicode).Select(r => r.Text));

    //the consent screen must show a typeable folder field, in the shape the engine screen uses
    [Fact]
    public void THE_CONSENT_CARRIES_A_TYPED_DOOR()
    {
        var (_, consent) = At(Probes(18_000_000_000));

        Assert.NotNull(consent.Door);
        Assert.Contains("folder", consent.Door!, StringComparison.Ordinal);
    }

    //enter on a typed folder must start the fetch into that folder. the assertion checks the destination, the key alone would pass on the old root
    [Fact]
    public void TYPING_A_FOLDER_STARTS_THE_FETCH_INTO_IT()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: 500_000_000_000));

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"D:\weights")));

        Assert.Equal(SetupFlow.ModelFetchingKey, after.Key);
        Assert.Equal(@"D:\weights", flow.Writes.WeightsRoot);
        Assert.Contains(@"D:\weights", Text(after), StringComparison.Ordinal);
    }

    //a refused root keeps the consent screen and its reason, accepting would start a download the screen just said cannot fit
    [Fact]
    public void A_REFUSED_ROOT_KEEPS_THE_CONSENT_AND_SAYS_WHY()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: 15_000_000_000));

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"D:\weights")));

        Assert.Equal(SetupFlow.ModelConsentKey, after.Key);
        Assert.Null(flow.Writes.WeightsRoot);
        Assert.Contains("14.0 GB", Text(after), StringComparison.Ordinal);
    }

    //an empty typed answer sets no root and starts no download, the TUI face never sends one but another face might
    [Fact]
    public void AN_EMPTY_DOOR_ANSWER_SETS_NOTHING_AND_FETCHES_NOTHING()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: 500_000_000_000));

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("   ")));

        Assert.Equal(SetupFlow.ModelConsentKey, after.Key);
        Assert.Null(flow.Writes.WeightsRoot);
    }

    //typing the folder may move the root, but no config file is written until the model's scaffold step. a user who presses Esc is left with no changes
    [Fact]
    public void AND_NOTHING_IS_WRITTEN_UNTIL_THE_SCAFFOLD()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: 500_000_000_000));

        flow.Answer(ShelfControls.TypedAnswer(@"D:\weights"));

        Assert.Equal(@"D:\weights", flow.Writes.WeightsRoot);
        Assert.Null(flow.Writes.CreateModel);
    }

    //the refusal names the download, the margin and the free space, so the reader sees which number to change. the figures are pinned as the product words them
    [Fact]
    public void A_TARGET_WITH_TOO_LITTLE_ROOM_IS_REFUSED_AND_NAMES_BOTH_FIGURES()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: 15_000_000_000));

        var after = flow.Answer(ShelfControls.TypedAnswer(@"C:\weights"));

        Assert.Equal(SetupFlow.ModelConsentKey, Assert.IsType<WizardScreen.Choice>(after).Key);
        var text = Text(after);
        Assert.Contains("16.8 GB", text, StringComparison.Ordinal);
        Assert.Contains("14.0 GB", text, StringComparison.Ordinal);
        Assert.Contains("margin", text, StringComparison.Ordinal);
        Assert.NotNull(Assert.IsType<WizardScreen.Choice>(after).Door);
    }

    //the margin is enforced, 18.0 GB into 18.5 GB free fits the file but not the file plus the margin. without this case an ignored margin would pass
    [Fact]
    public void THE_MARGIN_IS_COUNTED_AND_A_TARGET_INSIDE_IT_IS_REFUSED()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: 18_500_000_000));

        var after = flow.Answer(ShelfControls.TypedAnswer(@"C:\weights"));

        Assert.Contains("not enough room", Text(after), StringComparison.Ordinal);
        Assert.NotNull(Assert.IsType<WizardScreen.Choice>(after).Door);
    }

    //the projector file counts toward the needed space, a check from the weights alone would accept a target that fills up mid-fetch
    [Fact]
    public void AND_THE_PROJECTOR_COUNTS_TOWARD_THE_DOWNLOAD()
    {
        var (flow, _) = At(Probes(14_000_000_000, mmproj: 5_000_000_000, free: 18_500_000_000));

        var after = flow.Answer(ShelfControls.TypedAnswer(@"C:\weights"));

        Assert.Contains("not enough room", Text(after), StringComparison.Ordinal);
    }

    //a target with room must be accepted, otherwise a refusal that fired on every path would satisfy the refusal tests
    [Fact]
    public void AND_A_TARGET_WITH_ROOM_IS_ACCEPTED()
    {
        var (flow, _) = At(Probes(14_000_000_000, mmproj: 5_000_000_000, free: 60_000_000_000));

        var after = flow.Answer(ShelfControls.TypedAnswer(@"D:\weights"));

        Assert.DoesNotContain("not enough room", Text(after), StringComparison.Ordinal);
        Assert.Equal(@"D:\weights", flow.Writes.WeightsRoot);
    }

    //a null free space means the probe could not answer, so no refusal. refusing an unknown value would mean inventing a number to say no with
    [Fact]
    public void AN_UNREADABLE_DRIVE_IS_NOT_A_REFUSAL()
    {
        var (flow, _) = At(Probes(18_000_000_000, free: null));

        var after = flow.Answer(ShelfControls.TypedAnswer(@"D:\weights"));

        Assert.DoesNotContain("not enough room", Text(after), StringComparison.Ordinal);
        Assert.Equal(@"D:\weights", flow.Writes.WeightsRoot);
    }
}
