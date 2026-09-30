using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the badge is asked by the flow and passed in, since LocalShelf maps only what a FoundModel can see
public class LocalBadgeTests
{
    private const string File = "ministral-Q6_K.gguf";

    private static FoundModel OnDisk() =>
        new(Path.Combine(@"C:\weights", "ministral", File), 3_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "llama", "ministral", 32768, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static Badge Measured() => new(
        ModelKey: File, Measured: new DateOnly(2026, 9, 6), GattoBuild: "abc1234",
        SamplingNote: "defaults", Passed: 5, Ran: 5, RepoId: "unsloth/Ministral-3-3B-GGUF");

    private static WizardProbes Probes(bool measured)
    {
        var p = new WizardProbes { Llama = @"C:\llama\llama-server.exe", Found = [OnDisk()] };
        if (measured) p.Badges[File] = Measured();
        return p;
    }

    //the local shelf reached the way the flow reaches it, and the helper asserts the source it got
    private static WizardScreen.Choice LocalShelfOf(WizardProbes probes)
    {
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        return local;
    }

    private static string Screen(WizardProbes probes) =>
        string.Join("\n", WalkRender.Choice(LocalShelfOf(probes), 100,
            script: [new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]).Rows);

    //a measured model on the local shelf shows its badge
    [Fact]
    public void A_MEASURED_MODEL_ON_THE_LOCAL_SHELF_CARRIES_ITS_BADGE()
    {
        var row = Assert.Single(LocalShelfOf(Probes(measured: true)).Shelf!.Rows);

        Assert.Equal(File, row.Badge?.ModelKey);
    }

    //an unpriced row needs its own driver, since a machine with unreadable hardware builds every row through LocalShelf.Unpriced
    [Fact]
    public void AN_UNPRICED_ROW_CARRIES_ITS_BADGE_TOO()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [OnDisk()],
            Snapshot = null,
        };
        probes.Badges[File] = Measured();

        var row = Assert.Single(LocalShelfOf(probes).Shelf!.Rows);

        Assert.Equal(File, row.Badge?.ModelKey);
        Assert.Equal(FitRegime.Unknown, row.Fit);
    }

    //a model with no record still reads as unmeasured, so the fix is not "always measured"
    [Fact]
    public void A_MODEL_WITH_NO_RECORD_IS_STILL_UNMEASURED()
    {
        Assert.Null(Assert.Single(LocalShelfOf(Probes(measured: false)).Shelf!.Rows).Badge);
    }

    //the pane must stop saying not measured, since a badge nobody renders leaves the user the same sentence
    [Fact]
    public void THE_PANE_STOPS_SAYING_NOT_MEASURED_FOR_A_MEASURED_MODEL()
    {
        Assert.DoesNotContain(Pane.NotMeasured, Screen(Probes(measured: true)),
            StringComparison.Ordinal);
    }

    //the same search must find the sentence on an unmeasured shelf, or its absence says nothing about the screen
    [Fact]
    public void THE_PANE_STILL_SAYS_NOT_MEASURED_WHEN_NOTHING_MEASURED_IT()
    {
        Assert.Contains(Pane.NotMeasured, Screen(Probes(measured: false)),
            StringComparison.Ordinal);
    }
}
