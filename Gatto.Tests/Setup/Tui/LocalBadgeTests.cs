using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the local shelf shows the user's record only beside the bytes it measured: the record's sha256 against the profile entry's live key
public class LocalBadgeTests
{
    private const string File = "ministral-Q6_K.gguf";
    private static readonly string FilePath = Path.Combine(@"C:\weights", "ministral", File);
    private static readonly string Sha = new('b', 64);

    private static FoundModel OnDisk() =>
        new(FilePath, 3_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "llama", "ministral", 32768, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static Badge Record(bool passed, string? sha256, IReadOnlyList<string>? fellShort = null) =>
        new(File, sha256, passed, fellShort ?? [], new DateOnly(2026, 9, 6), "abc1234", "defaults",
            RepoId: "unsloth/Ministral-3-3B-GGUF");

    //a hub row, so the run opens on the hub shelf and the source key moves it to this machine's
    private static ModelRow HubRow() =>
        ShelfRows.Of("org/m", "org", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null), FitRegime.FitsGpu, 32768, false);

    //the profile names the file and its entry holds the live key, the way the flow reads both
    private static FakeSetupProbes Probes(Badge? record, string? liveKey = null)
    {
        var p = new FakeSetupProbes { Llama = @"C:\llama\llama-server.exe", Found = [OnDisk()], Rows = [HubRow()] };
        p.Existing[FilePath] = "ministral";
        if (liveKey is not null) p.LiveKeys[FilePath] = liveKey;
        if (record is not null) p.Badges[File] = record;
        return p;
    }

    private static SetupScreen.Choice LocalShelfOf(FakeSetupProbes probes)
    {
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        var local = Assert.IsType<SetupScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        return local;
    }

    private static string Screen(FakeSetupProbes probes) =>
        string.Join("\n", WalkRender.Choice(LocalShelfOf(probes), 100,
            script: [new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]).Rows);

    //the pane as the face draws it at a width with room for each mark on one line
    private static string Wide(FakeSetupProbes probes)
    {
        var local = LocalShelfOf(probes);
        var row = Assert.Single(local.Shelf!.Rows);
        return string.Join("\n", Pane.Rows(row, local.Shelf.Facts![0], Gatto.Core.Hardware.MachineShape.Discrete, 120,
            glyphs: GlyphSet.Unicode).Select(r => r.Text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_RECORD_WHOSE_HASH_IS_THE_LIVE_KEY_SHOWS_ITS_MARK(bool passed)
    {
        var screen = Wide(Probes(Record(passed, Sha), liveKey: Sha));

        var g = GlyphSet.Unicode;
        Assert.Contains(passed ? $"{g.PassedHere} {SearchRow.PassedHereWords}" : $"{g.FailedHere} {SearchRow.FailedHereWords}",
            screen, StringComparison.Ordinal);
    }

    //a file replaced under the same name keeps the old record, and the differing hash keeps the mark off
    [Fact]
    public void A_RECORD_OF_OTHER_BYTES_SHOWS_NO_MARK()
    {
        var row = Assert.Single(LocalShelfOf(Probes(Record(true, new string('9', 64)), liveKey: Sha)).Shelf!.Rows);
        Assert.Null(row.Badge);
        Assert.Null(Assert.Single(LocalShelfOf(Probes(Record(true, Sha), liveKey: null)).Shelf!.Rows).Badge);
        Assert.Null(Assert.Single(LocalShelfOf(Probes(Record(true, null), liveKey: Sha)).Shelf!.Rows).Badge);
    }

    //a file gatto tested and that passed here shows both, the shipped mark first, each with the task it fell short on
    [Fact]
    public void A_TESTED_FILE_THAT_PASSED_HERE_SHOWS_BOTH_MARKS_TESTED_FIRST()
    {
        var shipped = ShippedRows.Any(fellShort: true);
        var screen = Wide(Probes(Record(true, shipped.Sha256, ["B1"]), liveKey: shipped.Sha256));

        var g = GlyphSet.Unicode;
        var tested = screen.IndexOf($"{g.Tested} {SearchRow.TestedWords}, fell short: {ShippedRows.Labels(shipped.FellShort, g)}", StringComparison.Ordinal);
        var here = screen.IndexOf($"{g.PassedHere} {SearchRow.PassedHereWords}, fell short: {ShippedRows.Labels(["B1"], g)}", StringComparison.Ordinal);
        Assert.True(tested >= 0 && here > tested, screen);
    }

    [Fact]
    public void AN_UNTESTED_FILE_HAS_NO_TOOLS_LINE_AND_NO_MARK()
    {
        var probes = Probes(null);
        var row = Assert.Single(LocalShelfOf(probes).Shelf!.Rows);

        Assert.Null(row.Badge);
        Assert.Null(row.Tested);
        Assert.DoesNotContain("tools ", Screen(probes), StringComparison.Ordinal);
    }

    //a mark is a stat of the profile entry, never a read of the file, so rendering the shelf hashes nothing
    [Fact]
    public void RENDERING_THE_SHELF_READS_NO_FILE()
    {
        var reads = 0;
        var probes = new FakeSetupProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [OnDisk()],
            Hash = _ => { Interlocked.Increment(ref reads); return Sha; },
            Rows = [ShelfRows.Of("org/m", "org", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, Sha), FitRegime.FitsGpu, 32768, false)],
        };
        probes.Existing[FilePath] = "ministral";
        probes.LiveKeys[FilePath] = Sha;

        var hub = Assert.IsType<SetupScreen.Choice>(new SetupFlow(probes).StartPastEngine());
        WalkRender.Choice(hub, 100, script: [new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]);
        Screen(probes);

        Assert.Equal(0, reads);
    }
}
