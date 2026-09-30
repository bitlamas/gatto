using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//every screen here comes out of the flow, a hand-built one would prove the renderer on something nobody reaches
public class ModelFetchTests
{
    //the generator's own fixture, to the byte

    private const string Repo = "unsloth/gemma-4-26B-A4B-it-GGUF";
    private const string Weights = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf";
    private const string Mmproj = "mmproj-F16.gguf";
    private const string Into = @"C:\Users\you\.gatto\weights\gemma-4-26b-a4b-it\";

    private const long Gib = 1024L * 1024 * 1024;

    //written as arithmetic, so a reader can check the figure against the sentence it produces
    private const long WeightsBytes = (long)(16.9 * Gib);
    private const long MmprojBytes = 851L * 1024 * 1024;

    private const string SetRepo = "Qwen/Qwen3-Next-80B-A3B-Instruct-GGUF";
    private const string Shard1 = "Qwen3-Next-80B-A3B-Instruct-UD-Q4_K_M-00001-of-00002.gguf";
    private const string Shard2 = "Qwen3-Next-80B-A3B-Instruct-UD-Q4_K_M-00002-of-00002.gguf";
    private const string SetInto = @"C:\Users\you\.gatto\weights\qwen3-next-80b-a3b-instruct\";

    private static readonly string Sha = new('a', 64);

    //the one moment every frame is drawn at, so a change to any of its four figures moves the golden
    private static readonly FetchTick Tick =
        new(Weights, 1, 1, (long)(7.9 * Gib), WeightsBytes, 188_000);

    //the set's own moment, 11.2 GiB of 25.1 GiB over 292 s
    private static readonly FetchTick SetTick =
        new(Shard1, 1, 2, (long)(11.2 * Gib), (long)(25.1 * Gib), 292_000);

    private static HubQuant Quant(string file, long bytes) => new(file, bytes, Sha);

    private static HubQuant SetQuant() =>
        new(Shard1, (long)(48.5 * Gib), null, 2,
            [new HubFile(Shard1, (long)(25.1 * Gib), Sha),
             new HubFile(Shard2, (long)(23.4 * Gib), Sha)]);

    private static ShelfRow Row(string repoId, HubQuant quant, IReadOnlyList<HubQuant>? projectors = null) =>
        new(repoId, repoId.Split('/')[0], quant, Gatto.Core.Models.FitRegime.FitsGpu,
            32768, projectors is { Count: > 0 }, null, 100, false, Projectors: projectors);

    private static ModelFetchOffer VisionOffer() =>
        new(Repo, "gemma-4-26b-a4b-it", Into,
            Quant(Weights, WeightsBytes), Quant(Mmproj, MmprojBytes));

    private static ModelFetchOffer SetOffer() =>
        new(SetRepo, "qwen3-next-80b-a3b-instruct", SetInto, SetQuant(), null);

    private static ModelFetchOffer PlainOffer() =>
        new(Repo, "gemma-4-26b-a4b-it", Into, Quant(Weights, WeightsBytes), null);

    //the probes answer with an offer, a fake that offers nothing gets the browser watch instead
    private static WizardProbes Probes(ModelFetchOffer? offer, HubQuant? quant = null,
        HubFetchResult? result = null, IReadOnlyList<HubQuant>? projectors = null) =>
        new()
        {
            HubOffer = offer,
            ModelResult = result ?? new(HubFetchOutcome.Arrived),
            Rows = [Row(offer?.RepoId ?? Repo, quant ?? Quant(Weights, WeightsBytes), projectors)],
        };

    private static WizardScreen.Choice Consent(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));
    }

    private static WizardScreen.Choice Fetching(WizardProbes probes) => Fetching(probes, false);

    //the add path enters at the model segment, everything from the shelf on is the same three answers
    private static WizardScreen.Choice Fetching(WizardProbes probes, bool inSession)
    {
        var flow = new SetupFlow(probes);
        if (inSession) flow.StartAtModelSegment(); else flow.StartPastEngine();
        flow.Answer("0");
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchNow));
    }

    //the live download screen for the tests that guard its keys, exposed so the fixture is not copied again
    internal static WizardScreen.Choice FetchingScreen(bool inSession = false) =>
        Fetching(Probes(VisionOffer()), inSession);

    //the tick every download frame is drawn at
    internal static FetchTick FetchingTick => Tick;

    //probes with a one-row shelf, a discrete machine by default, so a unified test must pass its own
    internal static WizardProbes ShelfProbes(
        Gatto.Core.Hardware.HardwareSnapshot? snapshot = null,
        IReadOnlyList<Gatto.Cli.Setup.ISetupProbes.PausedFetch>? paused = null)
    {
        var offer = VisionOffer();
        return snapshot is null
            ? new WizardProbes
            {
                HubOffer = offer,
                ModelResult = new(HubFetchOutcome.Arrived),
                Rows = [Row(offer.RepoId, Quant(Weights, WeightsBytes))],
                Paused = paused ?? [],
            }
            : new WizardProbes
            {
                HubOffer = offer,
                ModelResult = new(HubFetchOutcome.Arrived),
                Rows = [Row(offer.RepoId, Quant(Weights, WeightsBytes))],
                Snapshot = snapshot,
                Paused = paused ?? [],
            };
    }

    //the drop screen for a fetch that kept something, a drop with no tick gets the browser watch
    internal static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Screen) Dropped(
        bool inSession = false)
    {
        var offer = VisionOffer();
        var probes = new WizardProbes
        {
            HubOffer = offer,
            ModelResult = new(HubFetchOutcome.Dropped, Weights),
            Rows = [Row(offer.RepoId, Quant(Weights, WeightsBytes))],
            ModelTicks = { Tick },
        };
        var flow = new SetupFlow(probes);
        if (inSession) flow.StartAtModelSegment(); else flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.ModelDroppedKey, screen.Key);
        //the strip says which path landed, three sections for add and five for setup
        Assert.Equal(inSession ? 3 : 5, screen.Strip.Count);
        return (flow, probes, screen);
    }

    //the fetch must still be running and the probes come back with the flow, since the pause oracles are records on them
    internal static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Screen) LiveFetch(
        bool inSession = true, int blockingFetches = int.MaxValue, HubFetchResult? result = null)
    {
        var offer = VisionOffer();
        var probes = new WizardProbes
        {
            HubOffer = offer,
            ModelResult = result ?? new(HubFetchOutcome.Arrived),
            Rows = [Row(offer.RepoId, Quant(Weights, WeightsBytes))],
            ModelTicks = { Tick },
            ModelBlockUntilCancelled = true,
            ModelBlockingFetches = blockingFetches,
        };
        var flow = new SetupFlow(probes);
        if (inSession) flow.StartAtModelSegment(); else flow.StartPastEngine();
        flow.Answer("0");
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchNow));
        Assert.Equal(SetupFlow.ModelFetchingKey, screen.Key);
        return (flow, probes, screen);
    }

    private static WizardScreen.Choice Arrived(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
    }

    //the corpus

    [Fact]
    public void THE_CONSENT_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s6", "consent", 100, ConsentGoldenRows());

    //the consent as the corpus pins it, one home so the golden and the guard cannot render two screens
    internal static IReadOnlyList<string> ConsentGoldenRows() =>
        WalkRender.Choice(Consent(Probes(VisionOffer(), projectors: [Quant(Mmproj, MmprojBytes)])),
            100, script: [WizardRig.Enter]).Rows;

    [Fact]
    public void THE_FETCHING_SCREEN_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s6", "fetching", 100, FetchingGoldenRows("fetching", 100));

    //the three fetching frames the corpus pins, one home so a golden and the guard cannot render two screens
    internal static IReadOnlyList<string> FetchingGoldenRows(string screen, int width) =>
        screen == "fetching-set"
            ? WalkRender.Watching(Fetching(Probes(SetOffer(), SetQuant())), width, SetTick).Rows
            : WalkRender.Watching(Fetching(Probes(VisionOffer())), width, Tick).Rows;

    //the figures are read off the tick, so they stay the same at both widths (only the bar narrows)
    [Fact]
    public void THE_FETCHING_SCREEN_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s6", "fetching-80", 80, FetchingGoldenRows("fetching-80", 80));

    //the Esc key arms the leave chord and the warning names the cost in the bar's own figure
    [Fact]
    public void THE_ARMED_ESC_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s6", "fetching-armed", 100,
            WalkRender.Watching(Fetching(Probes(VisionOffer())), 100, Tick,
                script: [WizardRig.Esc, WizardRig.Esc], nowMs: () => 0).Rows);

    [Fact]
    public void A_SHARD_SET_COUNTS_ITS_FILES_AND_NAMES_THE_NEXT_ONE() =>
        Golden.AssertEquals("s6", "fetching-set", 100, FetchingGoldenRows("fetching-set", 100));

    [Fact]
    public void THE_ARRIVAL_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s6", "done", 100,
            WalkRender.Choice(Arrived(Probes(VisionOffer())) with { PurredMs = 461_000 }, 100,
                script: [WizardRig.Enter]).Rows);

    //the marker and the then row

    //the file marker counts the unit in flight and the then row announces the next, decided separately
    [Theory]
    //offer, tick, marker, then
    [InlineData("plain", false, false, false)]
    [InlineData("vision", false, false, true)]
    [InlineData("set", true, true, true)]
    public void THE_COUNT_MARKER_AND_THE_NEXT_ROW_ARE_DECIDED_SEPARATELY(
        string arm, bool setTick, bool marker, bool then)
    {
        var (offer, quant) = arm switch
        {
            "set" => (SetOffer(), SetQuant()),
            "vision" => (VisionOffer(), Quant(Weights, WeightsBytes)),
            _ => (PlainOffer(), Quant(Weights, WeightsBytes)),
        };
        var rows = WalkRender.Watching(
            Fetching(Probes(offer, quant)), 100, setTick ? SetTick : Tick).Rows;
        var body = string.Join("\n", rows);

        Assert.Equal(marker, body.Contains(" · file 1 of ", StringComparison.Ordinal));
        Assert.Equal(then, rows.Any(r => r.TrimStart().StartsWith("then ", StringComparison.Ordinal)));
    }

    //the offer is the condition, and it reads the fetch's own rule

    //the consent is gated on CanVerify and the fetch refuses with NoDigest, the same rule asserted at both ends
    [Fact]
    public async Task A_FILE_WITH_NO_PUBLISHED_FINGERPRINT_IS_NEITHER_OFFERED_NOR_FETCHED()
    {
        var unhashed = new HubQuant(Weights, WeightsBytes, null);
        Assert.False(HubFetch.CanVerify(unhashed));
        Assert.True(HubFetch.CanVerify(Quant(Weights, WeightsBytes)));

        //the other end of the same rule, the fetch stops rather than pulling a file it cannot check
        var dir = Path.Combine(Path.GetTempPath(), "gatto-nodigest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            //awaited, a blocking wait here would deadlock and hang the suite
            var got = await HubFetch.FetchAsync(http, Repo, unhashed, dir, null, CancellationToken.None);
            Assert.Equal(HubFetchOutcome.NoDigest, got.Outcome);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    //no offer lands on the browser watch, which stays the default so older tests keep their screen
    [Fact]
    public void WITHOUT_AN_OFFER_THE_WALK_IS_THE_BROWSER_WATCH() =>
        Assert.Equal(SetupFlow.DownloadKey, Consent(Probes(offer: null)).Key);

    [Fact]
    public void WITH_AN_OFFER_THE_WALK_IS_THE_CONSENT() =>
        Assert.Equal(SetupFlow.ModelConsentKey, Consent(Probes(VisionOffer())).Key);

    //the consent needs a numbered way back that lands on the shelf, otherwise one option plus Esc means fetch or abandon
    [Fact]
    public void THE_CONSENT_HAS_A_ROAD_BACK_TO_THE_SHELF()
    {
        var probes = Probes(VisionOffer());
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        var consent = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));
        Assert.Equal(2, consent.Options.Count);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.PickAnother));
        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.Empty(probes.ModelFetched);
    }

    //the arrival says every file was proved, and names the key so the copy audit can see it
    [Fact]
    public void THE_ARRIVAL_SCREEN_SAYS_WHAT_WAS_PROVED()
    {
        var arrived = Arrived(Probes(VisionOffer()));
        Assert.Equal(SetupFlow.ModelArrivedKey, arrived.Key);

        var body = string.Join("\n", arrived.BodyRows!.Select(r => r.Text));
        Assert.Contains("byte for byte the files Hugging Face publishes.", body, StringComparison.Ordinal);
        Assert.Contains(Mmproj + " · 851 MB · verified", body, StringComparison.Ordinal);
    }

    //the deed behind each screen

    //the consent fetches the very offer the screen described, and the record is read after the watch resolves, where the product joins
    [Fact]
    public void CONSENTING_FETCHES_THE_OFFER_THE_SCREEN_DESCRIBED()
    {
        var offer = VisionOffer();
        var probes = Probes(offer);
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchNow));

        Assert.Equal(SetupFlow.ModelFetchingKey, screen.Key);
        Assert.True(screen.Watching);

        //the watch resolving is what joins the task, nothing may be read about the fetch before it
        flow.Answer(SetupFlow.Landed);
        Assert.Same(offer, Assert.Single(probes.ModelFetched));
    }

    //an answer that is neither a typed folder nor a known key throws, so no download starts by omission
    [Fact]
    public void THE_CONSENT_STARTS_NO_FETCH_ON_AN_ANSWER_IT_DOES_NOT_KNOW()
    {
        var probes = Probes(VisionOffer());
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");

        Assert.Throws<InvalidOperationException>(() => flow.Answer("model.fetch.probably"));

        //nothing was fetched, a throw alone would not say that
        Assert.Empty(probes.ModelFetched);
    }

    //only an unprovable fetch reaches the browser watch, an ending with its own screen must not fall through to it
    [Theory]
    [InlineData("NoDigest")]
    public void ONLY_AN_UNPROVABLE_FETCH_FALLS_BACK_TO_THE_WATCH(string ending)
    {
        //the ending travels as its name because a public theory cannot take an internal enum
        var outcome = Enum.Parse<HubFetchOutcome>(ending);
        var probes = Probes(VisionOffer(), result: new(outcome, Weights));
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.DownloadKey, after.Key);
    }

    //the arrival rescans the folder it fetched into, only the disk knows whether the set is complete
    [Fact]
    public void THE_ARRIVAL_RESCANS_THE_FOLDER_IT_FETCHED_INTO()
    {
        var probes = Probes(VisionOffer());
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);
        var before = probes.ScanRoots.Count;

        flow.Answer(SetupFlow.ModelArrivedNext);

        Assert.Equal(Into, probes.ScanRoots.Skip(before).Single());
    }


    //slice B: the two endings that are not arrival

    //the drop fixture, a set whose first shard stopped at 11.2 of 25.1 GB
    private static WizardProbes Dropping() =>
        new()
        {
            HubOffer = SetOffer(),
            ModelResult = new(HubFetchOutcome.Dropped, Shard1),
            ModelTicks = [SetTick],
            Rows = [Row(SetRepo, SetQuant())],
        };

    private static WizardScreen.Choice Ending(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
    }

    [Fact]
    public void THE_DROP_MATCHES_ITS_GOLDEN()
    {
        var screen = Ending(Dropping());
        //the key first, a golden alone would pass against the wrong screen
        Assert.Equal(SetupFlow.ModelDroppedKey, screen.Key);
        Golden.AssertEquals("s6", "dropped", 100,
            WalkRender.Choice(screen, 100, script: [WizardRig.Enter]).Rows);
    }

    //the screen has no live tick here, so its own figure is what the armed row names
    [Fact]
    public void THE_DROPS_ARMED_ESC_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s6", "dropped-armed", 100,
            WalkRender.Choice(Ending(Dropping()), 100,
                script: [WizardRig.Esc, WizardRig.Esc]).Rows);

    //a screen with kept bytes must not print the nothing-has-been-written line, so the figure is asserted as well
    [Fact]
    public void A_SCREEN_CARRYING_KEPT_BYTES_NEVER_SAYS_NOTHING_WAS_WRITTEN()
    {
        var screen = Ending(Dropping());
        Assert.NotNull(screen.Kept);

        var armed = WalkRender.Choice(screen, 100, script: [WizardRig.Esc, WizardRig.Esc]).Rows;
        var body = string.Join("\n", armed);

        Assert.DoesNotContain("nothing has been written", body, StringComparison.Ordinal);
        //the kept row and the armed row, both off the one tick the screen carries
        Assert.Contains("11.2 of 25.1 GB", body, StringComparison.Ordinal);
        Assert.Contains("Esc again: deletes the 11.2 GB already here", body, StringComparison.Ordinal);
    }

    //the mismatch names both fingerprints, one alone leaves the user nothing to check
    [Fact]
    public void THE_MISMATCH_MATCHES_ITS_GOLDEN()
    {
        var screen = Ending(new WizardProbes
        {
            HubOffer = VisionOffer(),
            //full 64-character shas, so the abbreviation rule produces the row on screen
            ModelResult = new(HubFetchOutcome.Mismatch, Weights,
                "a1b29c" + new string('0', 54) + "44e0",
                "7c3df1" + new string('0', 54) + "9aa2"),
            Rows = [Row(Repo, Quant(Weights, WeightsBytes))],
        });

        Assert.Equal(SetupFlow.ModelMismatchKey, screen.Key);
        Golden.AssertEquals("s6", "mismatch", 100,
            WalkRender.Choice(screen, 100, script: [WizardRig.Enter]).Rows);
    }

    //a drop with no tick is the Hub out of reach, so the tick decides between the two screens
    [Fact]
    public void A_DROP_THAT_KEPT_NOTHING_IS_THE_HUB_OUT_OF_REACH()
    {
        var probes = new WizardProbes
        {
            HubOffer = VisionOffer(),
            ModelResult = new(HubFetchOutcome.Dropped, Weights),
            Rows = [Row(Repo, Quant(Weights, WeightsBytes))],
        };
        Assert.Equal(SetupFlow.DownloadKey, Ending(probes).Key);
    }

    //the deeds behind the endings

    //resume starts the same offer again, what resume means is HubFetch's call from the .part on disk
    [Fact]
    public void RESUMING_STARTS_THE_SAME_OFFER_AGAIN()
    {
        var probes = Dropping();
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);
        Assert.Equal(1, probes.ModelStarts);

        var again = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelResume));
        Assert.Equal(SetupFlow.ModelFetchingKey, again.Key);

        flow.Answer(SetupFlow.Landed);
        Assert.Equal(2, probes.ModelStarts);
        Assert.Equal(2, probes.ModelFetched.Count);
        Assert.Same(probes.ModelFetched[0], probes.ModelFetched[1]);
    }

    //both endings land back on the shelf, and the drop keeps the partial since the screen promises what arrived is safe
    [Theory]
    [InlineData("dropped")]
    [InlineData("mismatch")]
    public void BOTH_ENDINGS_HAVE_A_ROAD_BACK_TO_THE_SHELF(string ending)
    {
        var probes = ending == "dropped" ? Dropping() : new WizardProbes
        {
            HubOffer = VisionOffer(),
            ModelResult = new(HubFetchOutcome.Mismatch, Weights, Sha, Sha),
            Rows = [Row(Repo, Quant(Weights, WeightsBytes))],
        };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(2, screen.Options.Count);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.PickAnother));
        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.Equal(1, probes.ModelStarts);
    }

    //the stop waits for the fetch to unwind, the deletion happens inside the cancelled fetch so the screen would otherwise lie
    [Fact]
    public void STOPPING_WAITS_FOR_THE_FETCH_TO_UNWIND()
    {
        var probes = new WizardProbes
        {
            HubOffer = VisionOffer(),
            ModelBlockUntilCancelled = true,
            Rows = [Row(Repo, Quant(Weights, WeightsBytes))],
        };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchStop));

        Assert.True(probes.ModelUnwound, "the flow spoke before the fetch had unwound");
        //the stop goes back to the offer, declining this download is not leaving setup
        Assert.Equal(SetupFlow.ModelConsentKey, after.Key);
    }

    //a resumed fetch is one wait, so the second leg adds to the first rather than replacing it
    [Fact]
    public void THE_WAIT_SURVIVES_A_RESUME()
    {
        var probes = Dropping();
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);

        probes.Arrive();
        flow.Answer(SetupFlow.ModelResume);
        var arrived = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        Assert.Equal(SetupFlow.ModelArrivedKey, arrived.Key);
        //two legs were run, so the figure the arrival carries covers both rather than restarting
        Assert.NotNull(arrived.PurredMs);
        Assert.Equal(2, probes.ModelStarts);
    }



    //slice C: the watch, reached when gatto cannot fetch

    //the roots the watch lists, taken from the scan's own answer so the screen cannot drift from where gatto looks
    private static readonly string[] WatchRoots = [Into, @"C:\Users\you\Downloads\"];

    //a dropped fetch with no tick, the state the fallback frames are drawn for
    private static WizardProbes Unreachable() =>
        new()
        {
            HubOffer = VisionOffer(),
            ModelResult = new(HubFetchOutcome.Dropped, Weights),
            Rows = [Row(Repo, Quant(Weights, WeightsBytes),
                        [Quant(Mmproj, MmprojBytes)])],
            Roots = WatchRoots,
        };

    private static WizardScreen.Choice Watching(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
    }

    //a clock that answers 0 once and then the given moment, so the purr comes out as a real wait
    private static Func<long> Stepping(long thenMs)
    {
        var first = true;
        return () => { if (first) { first = false; return 0; } return thenMs; };
    }

    //the watch has no tick, its purr comes from the face's own clock so the fixture passes none
    [Fact]
    public void THE_UNREACHABLE_WATCH_MATCHES_ITS_GOLDEN()
    {
        var screen = Watching(Unreachable());
        Assert.Equal(SetupFlow.DownloadKey, screen.Key);
        Assert.True(screen.Watching);

        //1h 4m 12s, the moment the frame is drawn at
        Golden.AssertEquals("s6", "fallback-watch", 100,
            WalkRender.Watching(screen, 100, null, nowMs: Stepping(3_852_000)).Rows);
    }


    //the watch verifies what lands

    //a file with the right name in Downloads at the published size
    private static Gatto.Core.Acquire.FoundModel Landed(long? bytes = null) =>
        new(@"C:\Users\you\Downloads\" + Weights, bytes ?? WeightsBytes, null);

    //unreadable and still-arriving keep the watch running, a file still being written is not the wrong file
    [Theory]
    //arriving, hash returned, expected verdict
    [InlineData(true, "aaa", "StillArriving")]
    [InlineData(false, null, "Unreadable")]
    [InlineData(false, "bbb", "NotThePublishedFile")]
    [InlineData(false, "aaa", "Verified")]
    public void THE_WATCH_CHECKS_WHAT_LANDS_IN_ORDER(bool arriving, string? hash, string expected)
    {
        var quant = new HubQuant(Weights, WeightsBytes, "aaa");
        var probes = new WizardProbes
        {
            HubOffer = null,
            Rows = [Row(Repo, quant)],
            Roots = WatchRoots,
            Arriving = _ => arriving,
            Hash = _ => hash,
        };

        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");

        probes.Found = [Landed()];
        //poll before answering, AnswerDownload reads the list the scan left and does not rescan, otherwise every case falls through to still watching
        flow.PollWatch();
        var next = flow.Answer(SetupFlow.Landed);

        var stillWatching = ScreenKey.Of(next) == SetupFlow.DownloadKey;
        var body = stillWatching
            ? string.Join(" ", ((WizardScreen.Choice)next).BodyRows!.Select(r => r.Text))
            : "";
        var accused = body.Contains("isn't the published one", StringComparison.Ordinal);

        var got = !stillWatching ? "Verified"
            : accused ? "NotThePublishedFile"
            : arriving ? "StillArriving"
            : "Unreadable";

        Assert.Equal(expected, got);
    }

    //with no published fingerprint the watch checks the name and the size, and no sentence claims a check it cannot make
    [Fact]
    public void WITHOUT_A_PUBLISHED_HASH_THE_SIZE_IS_THE_CHECK()
    {
        var quant = new HubQuant(Weights, WeightsBytes, null);
        var probes = new WizardProbes
        {
            HubOffer = null,
            Rows = [Row(Repo, quant)],
            Roots = WatchRoots,
            //the fake offers a hash from disk and the code must not use it, nothing published one to compare against
            Hash = _ => "whatever",
        };

        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        var watch = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));
        var body = string.Join("\n", watch.BodyRows!.Select(r => r.Text));
        Assert.Contains("It will check the name and the size when a file lands, but without a "
            + "published fingerprint it can't check the bytes.", body, StringComparison.Ordinal);

        //the right name at the wrong size is the only wrongness this state can see
        probes.Found = [Landed(bytes: WeightsBytes - 1)];
        flow.PollWatch();
        var wrong = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.DownloadKey, wrong.Key);
        Assert.Contains("isn't the published one",
            string.Join(" ", wrong.BodyRows!.Select(r => r.Text)), StringComparison.Ordinal);
    }


    //the two fingerprints at full length, so the abbreviation rule produces the drawn row
    private const string PublishedSha = "a1b29c" + "000000000000000000000000000000000000000000000000000000" + "44e0";
    private const string ArrivedSha = "7c3df1" + "000000000000000000000000000000000000000000000000000000" + "9aa2";

    //the file with the right name and the wrong bytes, reached by letting one land so the frame proves the whole pipeline
    [Fact]
    public void THE_WRONG_FILE_MATCHES_ITS_GOLDEN()
    {
        var probes = new WizardProbes
        {
            HubOffer = null,
            Rows = [Row(Repo, new HubQuant(Weights, WeightsBytes, PublishedSha))],
            Roots = WatchRoots,
            Hash = _ => ArrivedSha,
        };

        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        probes.Found = [Landed()];
        flow.PollWatch();
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        Assert.Equal(SetupFlow.DownloadKey, screen.Key);
        Assert.True(screen.Watching, "the watch stops after a wrong file, and it must not");

        //18m 2s, the moment the frame is drawn at
        Golden.AssertEquals("s6", "fallback-wrongfile", 100,
            WalkRender.Watching(screen, 100, null, nowMs: Stepping(1_082_000)).Rows);
    }


    //a vision pair with the weights in and no encoder, both paths reach the same rows under different cause sentences
    private static WizardProbes HalfArrived(bool unreachable = true) =>
        new()
        {
            HubOffer = unreachable ? VisionOffer() : null,
            ModelResult = new(HubFetchOutcome.Dropped, Weights),
            Rows = [Row(Repo, Quant(Weights, WeightsBytes), [Quant(Mmproj, MmprojBytes)])],
            Roots = WatchRoots,
            Hash = _ => Sha,
        };

    private static WizardScreen.Choice PartialWatch(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        //an offer in hand means the walk consents first and reaches the watch with nothing fetched
        if (probes.HubOffer is not null)
        {
            flow.Answer(SetupFlow.ModelFetchNow);
            flow.Answer(SetupFlow.Landed);
        }
        probes.Found = [Landed()];
        flow.PollWatch();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
    }

    //the watch waits for the whole unit, so weights alone keep it running since the model cannot see yet
    [Fact]
    public void THE_PARTIAL_ARM_MATCHES_ITS_GOLDEN()
    {
        var screen = PartialWatch(HalfArrived());
        Assert.Equal(SetupFlow.DownloadKey, screen.Key);

        //12m 40s, the moment the frame is drawn at
        Golden.AssertEquals("s6", "fallback-partial", 100,
            WalkRender.Watching(screen, 100, null, nowMs: Stepping(760_000)).Rows);
    }

    //text-only is offered only after a file lands, a watch waiting on an event is a trap no press resolves
    [Fact]
    public void TEXT_ONLY_IS_OFFERED_ONLY_WHERE_SOMETHING_HAS_ARRIVED()
    {
        Assert.Equal(
            [SetupFlow.UseTextOnly, SetupFlow.PickAnother],
            PartialWatch(HalfArrived()).Options.Select(o => o.Key));

        //nothing has arrived so there is nothing to decline, and the no-fingerprint path reaches the watch first
        var empty = new SetupFlow(HalfArrived(unreachable: false));
        empty.StartPastEngine();
        var watching = Assert.IsType<WizardScreen.Choice>(empty.Answer("0"));
        Assert.Equal([SetupFlow.PickAnother], watching.Options.Select(o => o.Key));
    }

    //answering it adopts the smaller unit and is remembered, the projector question must not be asked again
    [Fact]
    public void USING_IT_TEXT_ONLY_ADOPTS_AND_IS_NOT_ASKED_AGAIN()
    {
        var probes = HalfArrived(unreachable: false);
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        probes.Found = [Landed()];
        flow.PollWatch();
        var partial = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Contains(partial.Options, o => o.Key == SetupFlow.UseTextOnly);

        var after = flow.Answer(SetupFlow.UseTextOnly);

        //the answer moves on, anywhere other than the watch it just left
        Assert.NotEqual(SetupFlow.DownloadKey, ScreenKey.Of(after));
        //the projector question is not asked again on this path
        Assert.NotEqual("projector", ScreenKey.Of(after));
    }


    //both files arrive, so the watch proves the whole unit and the settled purr has its caller
    [Fact]
    public void THE_WATCHS_ARRIVAL_MATCHES_ITS_GOLDEN()
    {
        var probes = HalfArrived();
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        var watch = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        //both files arrive here, so this is the arrival rather than the partial arm
        probes.Found =
        [
            Landed(),
            new Gatto.Core.Acquire.FoundModel(
                @"C:\Users\you\Downloads\" + Mmproj, MmprojBytes, null),
        ];
        //the move offer is set after the files land, the arrival needs the files to be there
        probes.Move = _ => new MoveOffer(Into, false, WeightsBytes, null);
        flow.PollWatch();
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        Assert.Equal(SetupFlow.MoveKey, screen.Key);
        Assert.True(screen.PurredSinceWatch, "the arrival does not ask for the wait it just watched");

        //painted on the face that ran the watch, because the figure is the face's (1h 12m 4s)
        Golden.AssertEquals("s6", "fallback-arrived", 100,
            WalkRender.AfterWatch(watch, screen, 100, 4_324_000).Rows);
    }


    //both states must reach the wrong-file screen, and with no published hash the screen names the size check only
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void THE_WRONG_FILE_ARM_IS_REACHABLE_AND_HONEST_IN_BOTH_STATES(bool published)
    {
        var quant = published
            ? new HubQuant(Weights, WeightsBytes, PublishedSha)
            : new HubQuant(Weights, WeightsBytes, null);

        var probes = new WizardProbes
        {
            Rows = [Row(Repo, quant)],
            Roots = WatchRoots,
            Hash = _ => ArrivedSha,
        };

        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        //published, the hash disagrees. unpublished, the size disagrees, that state's own strongest check
        probes.Found = [Landed(bytes: published ? WeightsBytes : WeightsBytes - 1)];
        flow.PollWatch();
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        var body = string.Join("\n", screen.BodyRows!.Select(r => r.Text));
        Assert.Contains("isn't the published one", body, StringComparison.Ordinal);

        if (published)
        {
            Assert.Contains("a1b29c…44e0", body, StringComparison.Ordinal);
            Assert.Contains("7c3df1…9aa2", body, StringComparison.Ordinal);
            return;
        }

        //with no published hash the screen names the size it compared, since there is no fingerprint to show
        Assert.DoesNotContain("the file's fingerprint on Hugging Face", body, StringComparison.Ordinal);
        Assert.Contains("the size Hugging Face lists", body, StringComparison.Ordinal);
    }

    //the move offer belongs to the browser path alone

    //the move question only appears for a file gatto did not fetch, so both paths are asserted
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void THE_MOVE_OFFER_BELONGS_TO_THE_BROWSER_PATH_ALONE(bool gattoFetched)
    {
        var landed = gattoFetched
            ? Into + Weights
            : @"C:\Users\you\Downloads\" + Weights;

        var probes = new WizardProbes
        {
            HubOffer = VisionOffer(),
            Rows = [Row(Repo, Quant(Weights, WeightsBytes))],
            //the offer is made only for a file in Downloads, which is the rule's own shape
            Move = p => p.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)
                ? new MoveOffer(Into, false, WeightsBytes, null)
                : null,
        };

        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);

        //the file lands after the fetch, seeding it at launch sends the walk to discovery instead
        probes.Found = [new Gatto.Core.Acquire.FoundModel(landed, WeightsBytes, null)];
        var next = flow.Answer(SetupFlow.ModelArrivedNext);

        Assert.Equal(gattoFetched, ScreenKey.Of(next) != SetupFlow.MoveKey);
    }

    //width

    //the longest rows here are paths and repo labels, so sweep every width across the fold at 90
    [Fact]
    public void EVERY_ROW_FITS_AT_EVERY_WIDTH_THE_LADDER_SERVES()
    {
        var consent = Consent(Probes(VisionOffer(), projectors: [Quant(Mmproj, MmprojBytes)]));
        for (var w = 78; w <= 120; w++)
            foreach (var row in WalkRender.Choice(consent, w, script: [WizardRig.Enter]).Rows)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= w,
                    $"at width {w} a row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells: {row}");
    }
    //the Enter key does nothing on the fetching screen, and the oracle is the key Choose answers with
    [Fact]
    public void ENTER_DOES_NOTHING_WHILE_THE_MODEL_IS_FETCHING()
    {
        var probes = Probes(VisionOffer());
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchNow));
        Assert.Equal(SetupFlow.ModelFetchingKey, screen.Key);

        Assert.Equal(SetupFlow.Landed,
            WalkRender.WatchAfterKeys(screen, 100, tick: null,
                [new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]));

        flow.Answer(SetupFlow.Landed);   //join the fetch rather than leaving it running
    }
}
