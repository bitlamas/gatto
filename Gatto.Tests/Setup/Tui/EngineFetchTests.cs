using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//llama.cpp is fetched by gatto, or the screen says exactly how. both screens must name the same folder, so the offline instruction points where gatto looks
public class EngineFetchTests
{
    private static readonly string Pin = Gatto.Roles.LlamaAssetSteering.PinnedRelease;

    //the generator's own fixture to the byte: an AMD card steered to Vulkan, the pinned release, and the folder gatto fetches into
    private const string Zip = "llama-b11071-bin-win-vulkan-x64.zip";
    private const string Into = @"C:\Users\you\.gatto\llama\b11071\";

    //exactly 214 MiB, so the row reads 214 MB the way the golden does (the arithmetic rather than 224395264, a number nobody can check)
    private const long Bytes = 214L * 1024 * 1024;

    private static readonly Gatto.Roles.LlamaAsset Pick =
        new(Zip, null, "an AMD discrete card", "Vulkan");

    private static readonly EngineAsset Asset =
        new(Zip, "https://example.invalid/" + Zip, new string('a', 64), Bytes);

    //the flow reads the engine from Llama alone, so leaving Llama null is the whole no-engine fixture
    private static WizardProbes Probes(
        EngineAsset? asset = null,
        Func<EnginePair, EngineFetch>? fetch = null,
        Gatto.Roles.LlamaAsset? pick = null,
        bool offering = true) =>
        new()
        {
            Llama = null,
            Asset = pick ?? Pick,
            Offer = offering ? new EngineFetchOffer(Into, asset is null ? null : new EnginePair(asset, null)) : null,
            Fetch = fetch,
            Verify = _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: Pin),
        };

    //the no-engine screen as the flow emits it, reached from the opening. don't build one by hand, that only proves the renderer on a screen nobody reaches
    private static WizardScreen.Choice NoEngine(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        return Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
    }

    private static WizardScreen.Choice Consent() => NoEngine(Probes(Asset));

    private static WizardScreen.Choice Fallback() => NoEngine(Probes(asset: null));

    //fixtures

    //every figure of this one fetch moment is on the frame, so a change to any of the four computations moves the golden
    private static readonly FetchTick Tick = new(Zip, 1, 1, 93L * 1024 * 1024, Bytes, 11_000);

    //the CUDA arm's fixture: 268 and 380 MB are illustrative, but the shape of two files with the second named below is real
    private const string CudaZip = "llama-b11071-bin-win-cuda-12.4-x64.zip";
    private const string CudartZip = "cudart-llama-bin-win-cuda-12.4-x64.zip";
    private static readonly FetchTick PairTick =
        new(CudaZip, 1, 2, 93L * 1024 * 1024, 268L * 1024 * 1024, 11_000);

    //the fetching screen the flow emits after the consent button, reached the way a user reaches it
    private static WizardScreen.Choice Fetching(WizardProbes? probes = null)
    {
        var flow = new SetupFlow(probes
            ?? Probes(Asset, _ => new EngineFetch(@"C:\e\llama-server.exe", null)));
        flow.StartPastOpening();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ConsentFetch));
    }

    private static WizardProbes PairProbes() =>
        new()
        {
            Llama = null,
            Asset = new Gatto.Roles.LlamaAsset(CudaZip, CudartZip, "an NVIDIA card", "CUDA 12.4"),
            Offer = new EngineFetchOffer(Into, new EnginePair(
                new EngineAsset(CudaZip, "https://example.invalid/" + CudaZip, new string('a', 64),
                    268L * 1024 * 1024),
                new EngineAsset(CudartZip, "https://example.invalid/" + CudartZip, new string('b', 64),
                    380L * 1024 * 1024))),
            Fetch = _ => new EngineFetch(@"C:\e\llama-server.exe", null),
            Verify = _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: Pin),
        };

    //the corpus

    [Fact]
    public void THE_FETCH_CONSENT_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "consent", 100,
            WalkRender.Choice(Consent(), 100, script: [WizardRig.Enter]).Rows);

    //below 90 columns the from row's why-clause shortens and every fact stays put (a wrapped row would break the label column a table needs)
    [Fact]
    public void THE_FETCH_CONSENT_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s4", "consent-80", 80,
            WalkRender.Choice(Consent(), 80, script: [WizardRig.Enter]).Rows);

    [Fact]
    public void THE_OFFLINE_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "fallback", 100,
            WalkRender.Choice(Fallback(), 100, script: [WizardRig.Enter]).Rows);

    [Fact]
    public void THE_FETCHING_SCREEN_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "fetching", 100,
            WalkRender.Watching(Fetching(), 100, Tick).Rows);

    //the bar narrows and the figures stay, because they are read off the tick and the fold changes only the picture
    [Fact]
    public void THE_FETCHING_SCREEN_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s4", "fetching-80", 80,
            WalkRender.Watching(Fetching(), 80, Tick).Rows);

    //the armed row names the cost from the bar's own figure, driven through the real face with a stopped clock
    [Fact]
    public void THE_ARMED_ESC_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "fetching-armed", 100,
            WalkRender.Watching(Fetching(), 100, Tick,
                script: [WizardRig.Esc, WizardRig.Esc], nowMs: () => 0).Rows);

    //the companion is named while the second file is still arriving, so nobody closes the wizard believing the pair is done
    [Fact]
    public void THE_CUDA_PAIR_NAMES_THE_FILE_STILL_TO_COME() =>
        Golden.AssertEquals("s4", "fetching-pair", 100,
            WalkRender.Watching(Fetching(PairProbes()), 100, PairTick).Rows);

    //render after the watch and hand it that fetch, otherwise the purr reads 0s and the golden pins the harness
    [Fact]
    public void THE_FETCHED_SCREEN_MATCHES_ITS_GOLDEN()
    {
        var (watch, arrived) = WatchThenArrived();

        Golden.AssertEquals("s4", "done", 100,
            WalkRender.AfterWatch(watch, arrived, 100, watchedMs: 12_000).Rows);
    }

    //the watch screen and the arrival from one flow, because the purr has to be the watch the face actually ran
    private static (WizardScreen.Choice Watch, WizardScreen.Choice Arrived) WatchThenArrived()
    {
        var flow = new SetupFlow(Probes(Asset, _ => new EngineFetch(@"C:\e\llama-server.exe", null)));
        flow.StartPastOpening();
        var watch = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ConsentFetch));
        var arrived = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        return (watch, arrived);
    }

    //the longest row is a path or a URL, and one width can't see another's overflow, so the sweep crosses the fold at 90
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EVERY_ROW_FITS_AT_EVERY_WIDTH_THE_LADDER_SERVES(bool fetching)
    {
        var screen = fetching ? Consent() : Fallback();
        for (var w = 78; w <= 120; w++)
            foreach (var row in WalkRender.Choice(screen, w, script: [WizardRig.Enter]).Rows)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= w,
                    $"at width {w} a row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells: {row}");
    }

    //which arm, and why

    //the consent puts the engine into a folder and the offline arm says to extract to one, so both must name the same folder
    [Fact]
    public void BOTH_ARMS_NAME_THE_SAME_FOLDER()
    {
        var fetches = Assert.IsType<EngineView>(Consent().Engine);
        var byHand = Assert.IsType<EngineView>(Fallback().Engine);

        Assert.Equal(Into, fetches.Into);
        Assert.Equal(fetches.Into, byHand.Into);
        Assert.Equal(fetches.ZipName, byHand.ZipName);
    }

    //a null asset stands for a GitHub that never answered, an ordinary state on the screen that must work before anything runs
    [Fact]
    public void AN_UNREACHABLE_RELEASE_API_SELECTS_THE_OFFLINE_ARM()
    {
        Assert.Equal(SetupFlow.FallbackKey, Fallback().Key);
        Assert.Contains("couldn't reach GitHub", string.Join("\n", Fallback().BodyRows!));
    }

    //only something that read the size may print it, so the offline arm, reached by a failed read, has no size
    [Fact]
    public void THE_OFFLINE_ARM_QUOTES_NO_SIZE()
    {
        Assert.Null(Assert.IsType<EngineView>(Fallback().Engine).Size);
        Assert.DoesNotContain("MB", RowText(Fallback()));
        Assert.Equal("214 MB", Assert.IsType<EngineView>(Consent().Engine).Size);
    }

    //a CUDA pick goes straight to the consent, and its companion is a row there, since forgetting the second file is the whole failure
    [Fact]
    public void A_CUDA_PICK_REACHES_THE_CONSENT_AND_ITS_COMPANION_IS_ON_THE_SCREEN()
    {
        var pick = new Gatto.Roles.LlamaAsset(Zip, "cudart-12.4.zip", "an NVIDIA card", "CUDA");
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = null,
            Asset = pick,
            Offer = new EngineFetchOffer(Into, new EnginePair(Asset,
                new EngineAsset("cudart-12.4.zip", "https://example.invalid/c", new string('b', 64),
                    63L * 1024 * 1024))),
            Verify = _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: Pin),
        });

        var screen = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        Assert.Equal(SetupFlow.ConsentKey, screen.Key);

        var rows = string.Join("\n", EngineFetchView.Rows(screen.Engine!.Value, 100, glyphs: GlyphSet.Unicode).Select(r => r.Text));
        Assert.Contains("with", rows, StringComparison.Ordinal);
        Assert.Contains("cudart-12.4.zip", rows, StringComparison.Ordinal);
        Assert.Contains("63 MB", rows, StringComparison.Ordinal);

        //one unit is offered, since naming two files and fetching one is the failure moved to the reading side
        Assert.Contains(screen.Options, o => o.Key == SetupFlow.ConsentFetch);
    }

    //a probe that offers no fetch still reaches the steering screen, and that is the default for every fixture here
    [Fact]
    public void A_PROBE_THAT_OFFERS_NO_FETCH_STILL_REACHES_THE_STEERING_SCREEN()
    {
        var flow = new SetupFlow(Probes(offering: false));

        Assert.Equal(SetupFlow.SteerKey,
            Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening()).Key);
    }

    //no golden covers this frame, so the sentence and the connect door are asserted (the key alone proves nothing about what a user sees)
    [Fact]
    public void THE_STEERING_SCREEN_CARRIES_ITS_SENTENCE_AND_THE_CONNECT_DOOR()
    {
        var screen = Assert.IsType<WizardScreen.Choice>(
            new SetupFlow(Probes(offering: false)).StartPastOpening());

        Assert.Equal(SetupFlow.SteerKey, screen.Key);
        Assert.Contains("Couldn't read this machine well enough to pick a llama.cpp build.",
            string.Join("\n", screen.BodyRows!), StringComparison.Ordinal);
        Assert.Contains(screen.Options,
            o => o.Label == SetupFlow.ConnectDoorLabel && o.Key == SetupFlow.ForkConnect);
    }

    //the door must be answered, since an option that only drew would strand the one user who reaches this screen
    [Fact]
    public void THE_STEERING_SCREENS_DOOR_TAKES_THE_CONNECT_ROAD()
    {
        var flow = new SetupFlow(Probes(offering: false));
        Assert.Equal(SetupFlow.SteerKey,
            Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening()).Key);

        var next = flow.Answer(SetupFlow.ForkConnect);

        //the door only has to lead somewhere, so the assertion stops at a different key rather than the destination
        Assert.NotNull(next);
        Assert.NotEqual(SetupFlow.SteerKey,
            next is WizardScreen.Choice c ? c.Key
            : next is WizardScreen.Ask a ? a.Key
            : next is WizardScreen.Info i ? i.Key
            : ((WizardScreen.Terminal)next).Key);
    }

    //a swept exe that fails verification is not a found engine, and the note goes to the record. a wrong guess is not the user's problem to fix on a screen
    [Fact]
    public void A_SWEPT_EXE_THAT_IS_NOT_THE_SERVER_REACHES_THE_CONSENT_AND_IS_NOTED()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-cli.exe",
            Asset = Pick,
            Offer = new EngineFetchOffer(Into, new EnginePair(Asset, null)),
            Verify = _ => new ProbeResult(ProbeShape.KnownSibling, "d", SiblingName: "llama-cli.exe"),
        });

        var screen = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());

        //the consent with its door, the same first screen a bare machine gets
        Assert.Equal(SetupFlow.ConsentKey, screen.Key);
        Assert.Contains(screen.Options, o => o.Key == SetupFlow.ForkConnect);

        //the rejected exe is noted rather than shown on the screen, and the note names it
        var noted = string.Join("\n", flow.TakeNarration().SelectMany(i => i.Rows).Select(r => r.Text));
        Assert.Contains("llama-cli.exe", noted, StringComparison.Ordinal);
        Assert.DoesNotContain("llama-cli.exe",
            string.Join("\n", screen.BodyRows!.Select(r => r.Text)), StringComparison.Ordinal);
    }

    //the answers

    //the button fetches, since a consent whose Enter did anything else would lie about the deed behind it
    [Fact]
    public void FETCH_IT_NOW_DOWNLOADS_THE_ASSET_THE_SCREEN_NAMED()
    {
        var probes = Probes(Asset, fetch: _ => new EngineFetch(@"C:\e\llama-server.exe", null));
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();

        flow.Answer(SetupFlow.ConsentFetch);
        //press the button and it starts work on the pool, so answer the arrival before reading what it fetched, since that joins the task
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(Zip, Assert.Single(probes.Fetched).Server.ZipName);
    }

    //the motion

    //escape must wait for the fetch to unwind before the consent returns, since the partial's delete runs inside the download's failing path
    [Fact]
    public void ESC_STOPS_THE_FETCH_AND_GATTO_WAITS_FOR_IT_TO_UNWIND()
    {
        var probes = new WizardProbes
        {
            Llama = null,
            Asset = Pick,
            Offer = new EngineFetchOffer(Into, new EnginePair(Asset, null)),
            BlockUntilCancelled = true,
            Fetch = _ => new EngineFetch(@"C:\e\llama-server.exe", null),
            Verify = _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: Pin),
        };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ConsentFetch);

        var back = flow.Answer(SetupFlow.FetchStop);

        Assert.True(probes.Unwound, "the stop answer returned while the fetch was still running");
        Assert.Equal(SetupFlow.ConsentKey, Assert.IsType<WizardScreen.Choice>(back).Key);
        Assert.Null(flow.Writes.LlamaServer);
    }

    //a finished watch has no task left to resolve it, so back from a later screen must reach the arrival. otherwise the only key stops a fetch that already ended
    [Fact]
    public void BACK_FROM_PAST_A_FINISHED_FETCH_NEVER_LANDS_ON_THE_WATCH()
    {
        var (flow, arrived) = Fetch(_ => new EngineFetch(@"C:\e\llama-server.exe", null));
        Assert.Equal(SetupFlow.FetchedKey, Assert.IsType<WizardScreen.Choice>(arrived).Key);
        flow.Answer(SetupFlow.FetchedNext);

        var once = flow.Answer(SetupFlow.BackKey);
        Assert.Equal(SetupFlow.FetchedKey, Assert.IsType<WizardScreen.Choice>(once).Key);
        var twice = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));
        Assert.False(twice.Watching, $"back landed on the watched screen {twice.Key}");
        Assert.Equal(SetupFlow.ConsentKey, twice.Key);
    }

    //a throw must open a screen showing the thrower's own sentence, since a faulted task counts as completed and would leave the watch stuck
    [Fact]
    public void A_FETCH_THAT_THROWS_LANDS_ON_A_SCREEN_AND_SAYS_WHAT_HAPPENED()
    {
        var (flow, emitted) = Fetch(_ => throw new IOException("the disk went away"));

        var screen = Assert.IsType<WizardScreen.Choice>(emitted);
        Assert.Equal(SetupFlow.FallbackKey, screen.Key);
        Assert.Contains("the disk went away", string.Join("\n", screen.BodyRows!));
        Assert.Null(flow.Writes.LlamaServer);
    }

    //the frame is composed again on the watch's clock, once per expired interval, so the last frame carrying the third moment proves the repaint
    [Fact]
    public void THE_FRAME_IS_REDRAWN_ON_THE_WATCHS_CLOCK_AND_SHOWS_THE_LATEST_MOMENT()
    {
        var moments = new Queue<FetchTick>(
        [
            Tick with { Done = 10L * 1024 * 1024 },
            Tick with { Done = 50L * 1024 * 1024 },
            Tick with { Done = 200L * 1024 * 1024 },
        ]);
        var painted = WalkRender.WatchingOverIntervals(Fetching(), 100, () => moments.Dequeue(),
            expiries: 2);

        var bar = Assert.Single(painted.Rows, r => r.Contains('━'));
        Assert.Contains("200 of 214 MB", bar);
        Assert.DoesNotContain("10 of 214 MB", string.Join("\n", painted.Rows));
    }

    //the arrival answers with a null key and no keypress, since the predicate turning true is the whole event
    [Fact]
    public void THE_WATCH_RESOLVING_ITSELF_ANSWERS_WITHOUT_A_KEYPRESS() =>
        Assert.Equal(SetupFlow.Landed, WalkRender.WatchArrives(Fetching(), 100, Tick));

    //every moving figure comes off one tick, and a second tick has to move all of them. otherwise a reader wired to a different clock could creep in unnoticed
    [Fact]
    public void EVERY_MOVING_THING_ON_THE_SCREEN_IS_READ_OFF_ONE_TICK()
    {
        string[] first = ["━━━━━━━━━━━━━", "93 of 214 MB", "8.5 MB/s", "~14 s left", "purr", "11s"];
        var early = Frame(Tick);
        foreach (var s in first) Assert.Contains(s, early);
        Assert.Contains("deletes the 93 MB already here", Armed(Tick));

        //a different moment, and every one of the eight has to say so
        var later = Tick with { Done = 200L * 1024 * 1024, ElapsedMs = 40_000 };
        var late = Frame(later);
        Assert.Contains("━━━━━━━━━━━━━━━━━━━━━━━━━━━━", late);      //28 of 30 cells at the later moment (13 at the first)
        Assert.Contains("200 of 214 MB", late);
        Assert.Contains("5.0 MB/s", late);
        Assert.Contains("~3 s left", late);
        Assert.Contains("purr..", late);                            //a different frame of the purr, so this moment reaches it
        Assert.Contains("40s", late);
        Assert.Contains("deletes the 200 MB already here", Armed(later));
    }

    //the bar floors, so 99.2% of the way through reads 29 of 30, since rounding would show a full bar before the last byte
    [Fact]
    public void THE_BAR_FILLS_ITS_LAST_CELL_ONLY_WHEN_THE_LAST_BYTE_LANDS()
    {
        var nearly = Tick with { Done = (long)(Bytes * 0.992) };

        var bar = Assert.Single(WalkRender.Watching(Fetching(), 100, nearly).Rows,
            r => r.Contains('━'));

        Assert.Contains("─", bar);
        Assert.Equal(29, bar.Count(ch => ch == '━'));
    }

    //the Enter key must do nothing while gatto works, since the generic arm would otherwise stop the download silently
    [Fact]
    public void ENTER_DOES_NOTHING_WHILE_GATTO_IS_FETCHING()
    {
        //if the enter key answered, Choose returns the stop key and the arrival never happens
        Assert.Equal(SetupFlow.Landed,
            WalkRender.WatchAfterKeys(Fetching(), 100, Tick, [WizardRig.Enter]));
    }

    //a digit does nothing on the watch either, since the inert keys are computed from this screen's own options
    [Fact]
    public void A_DIGIT_DOES_NOTHING_WHILE_GATTO_IS_FETCHING() =>
        Assert.Equal(SetupFlow.Landed,
            WalkRender.WatchAfterKeys(Fetching(), 100, Tick, [WizardRig.Digit('1')]));

    private static string Frame(FetchTick t) =>
        string.Join("\n", WalkRender.Watching(Fetching(), 100, t).Rows);

    private static string Armed(FetchTick t) =>
        string.Join("\n", WalkRender.Watching(Fetching(), 100, t,
            script: [WizardRig.Esc, WizardRig.Esc], nowMs: () => 0).Rows);

    //the config takes the exe the probe answered with, since the folder answer is what actually ran
    [Fact]
    public void A_FETCHED_ENGINE_IS_WRITTEN_ONLY_AFTER_IT_ANSWERS()
    {
        var (flow, screen) = Fetch(_ => new EngineFetch(@"C:\e\llama-server.exe", null));

        Assert.Equal(SetupFlow.FetchedKey, Assert.IsType<WizardScreen.Choice>(screen).Key);
        Assert.Equal(@"C:\e\llama-server.exe", flow.Writes.LlamaServer);
    }

    //an arrival is checked before it is launched, so a sibling program is refused by name and the config stays unwritten
    [Fact]
    public void AN_ARRIVAL_THAT_IS_NOT_THE_SERVER_IS_REFUSED_AND_NOTHING_IS_WRITTEN()
    {
        var probes = new WizardProbes
        {
            Llama = null,
            Asset = Pick,
            Offer = new EngineFetchOffer(Into, new EnginePair(Asset, null)),
            Fetch = _ => new EngineFetch(@"C:\e\llama-cli.exe", null),
            Verify = _ => new ProbeResult(ProbeShape.KnownSibling, "d", SiblingName: "llama-cli.exe"),
        };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();

        flow.Answer(SetupFlow.ConsentFetch);
        var screen = flow.Answer(SetupFlow.Landed);

        Assert.Contains("llama-cli.exe", RowsOf(screen));
        Assert.Null(flow.Writes.LlamaServer);
    }

    //a failed fetch shows the sentence the fetch produced, since a refused archive must not send the user to check their network
    [Fact]
    public void A_FETCH_THAT_FAILS_LANDS_ON_THE_OFFLINE_ARM_AND_SAYS_WHY()
    {
        var (_, emitted) = Fetch(_ => new EngineFetch(null, "the download contains 0 copies of llama-server.exe"));

        var screen = Assert.IsType<WizardScreen.Choice>(emitted);
        Assert.Equal(SetupFlow.FallbackKey, screen.Key);
        Assert.Contains("0 copies", string.Join("\n", screen.BodyRows!));
        Assert.DoesNotContain("couldn't reach GitHub", string.Join("\n", screen.BodyRows!));
    }

    //gatto named the folder, so it searches that folder and the shipped rule finds the exe inside the extracted zip
    [Fact]
    public void DONE_CHECK_THAT_FOLDER_WALKS_THE_FOLDER_GATTO_NAMED()
    {
        var asked = new List<string>();
        var probes = new WizardProbes
        {
            Llama = null,
            Asset = Pick,
            Offer = new EngineFetchOffer(Into, null),
            Verify = p => { asked.Add(p); return new ProbeResult(ProbeShape.ClassicServer, "d", Build: Pin); },
        };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();

        flow.Answer(SetupFlow.FallbackDone);

        Assert.Equal(Into, Assert.Single(asked));
    }

    //the connect door is row 2 of the consent, under the same key the found screen's door carries, so one handler answers both
    [Fact]
    public void THE_CONNECT_ROAD_IS_ON_THE_CONSENT_AS_ROW_TWO()
    {
        var options = Consent().Options;

        Assert.Equal(SetupFlow.ConsentFetch, options[0].Key);
        Assert.Equal(SetupFlow.ForkConnect, options[1].Key);
        Assert.Equal(2, options.Count);
        Assert.Equal(SetupFlow.ConnectDoorLabel, options[1].Label);
    }

    //an answer that is not one of the screen's two options goes to the verify, and no fetch may start from it
    [Fact]
    public void AN_ANSWER_THAT_IS_NOT_AN_OPTION_DOES_NOT_START_THE_FETCH()
    {
        var fetched = new List<string>();
        var flow = new SetupFlow(Probes(Asset,
            fetch: pair => { fetched.Add(pair.Server.ZipName); return new EngineFetch(@"C:\e", null); }));
        flow.StartPastOpening();

        //a door answer says it is one, so a key-shaped string is refused rather than read as a path
        Assert.Throws<InvalidOperationException>(() => flow.Answer("consent.something"));

        Assert.Empty(fetched);
    }

    //both engine screens have the same typed door, and only the sentences above it differ
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BOTH_ARMS_CARRY_THE_STANDING_TYPED_DOOR(bool fetching) =>
        Assert.Equal(SetupFlow.LlamaDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode), (fetching ? Consent() : Fallback()).Door);

    //helpers

    //the whole flow to the fetch's answer, returning the flow and the screen it opened. a test about the fetch needs what the user sees and what was written

    //press the button, then answer the arrival, and assert the watch in between, or the test passes even if the button stops fetching
    private static (SetupFlow Flow, WizardScreen Screen) Fetch(Func<EnginePair, EngineFetch> fetch)
    {
        var flow = new SetupFlow(Probes(Asset, fetch));
        flow.StartPastOpening();
        Assert.Equal(SetupFlow.FetchingKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ConsentFetch)).Key);
        //no wait is needed, since the arrival joins the task and returns only once the fetch has finished
        return (flow, flow.Answer(SetupFlow.Landed));
    }

    private static WizardScreen.Choice Arrived() =>
        Assert.IsType<WizardScreen.Choice>(
            Fetch(_ => new EngineFetch(@"C:\e\llama-server.exe", null)).Screen);

    private static string RowText(WizardScreen.Choice c) =>
        string.Join("\n", EngineFetchView.Rows(c.Engine!.Value, 100, glyphs: GlyphSet.Unicode).Select(r => r.Text));

    private static string RowsOf(WizardScreen s) => s switch
    {
        WizardScreen.Choice c => string.Join("\n", c.BodyRows ?? []),
        WizardScreen.Ask a => string.Join("\n", a.BodyRows ?? []),
        _ => "",
    };
    //the arrival follows a watch, so it shows the elapsed, and the flag is all the change needs (the face measured it)
    [Fact]
    public void THE_FETCHED_SCREEN_CARRIES_ITS_ELAPSED()
    {
        Assert.True(Arrived().PurredSinceWatch,
            "the engine arrival is the screen that follows a watch, so it says how long it purred");
    }

}
