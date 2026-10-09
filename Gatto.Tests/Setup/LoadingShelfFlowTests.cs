using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//gatto model draws its shelf before the Hub answers, runs the scan beside the search, and lands when both are in
public class LoadingShelfFlowTests
{
    private static ModelRow Row(string id) => ShelfRows.Of(
        id, id.Split('/')[0], new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, null, 900, false, Params: 3_000_000_000, Arch: "qwen3");

    //a Hub that holds every search until the test lets go, recording each request and its token
    private sealed class HeldHub
    {
        public readonly ManualResetEventSlim Release = new(false);
        public readonly List<(ModelSearchRequest Request, CancellationToken Token)> Asked = [];
        public volatile bool Returned;

        public ShelfOutcome Search(ModelSearchRequest request, IProgress<SearchProgress>? progress,
            CancellationToken ct)
        {
            lock (Asked) Asked.Add((request, ct));
            //bounded, so a test that forgets the release fails instead of hanging the run
            Release.Wait(Bound, CancellationToken.None);
            Returned = true;
            return WizardProbes.Outcome([Row("unsloth/a")], null);
        }
    }

    private static (string Request, CancellationToken Token)[] Asked(HeldHub hub)
    {
        lock (hub.Asked) return [.. hub.Asked.Select(a => (LitOf(a.Request), a.Token))];
    }

    //the families a request lit, sorted and joined, so a test names the set a chip press left
    private static string LitOf(ModelSearchRequest r) => string.Join(",", r.Lit.Order());

    private static string LandingLit => string.Join(",", Families.Load().Landing.Order());

    //a chip that is not in the landing pair, so its press adds a family rather than turning one off
    private static string Added => Families.Load().Ladder[2];

    [Fact]
    public void THE_FIRST_FRAME_IS_PAINTED_BEFORE_ANY_HUB_REQUEST_RETURNS()
    {
        var hub = new HeldHub();
        var probes = new WizardProbes { Slow = hub.Search };
        var flow = new SetupFlow(probes);
        //the two Esc presses are the chord that leaves, taken while the Hub still holds its answer
        var rig = new WizardRig(width: 120) { PollTime = true, WatchKeyBudget = 2 };
        var face = rig.TuiFace(WizardRig.Esc, WizardRig.Esc);
        try
        {
            var exit = SetupRunner.Run(flow, face, homePath: null, modelSegmentOnly: true);

            Assert.Equal(0, exit);
            Assert.False(hub.Returned);
            //the first frame is the start-up screen: the purr, the sentence and the step line, and Esc
            var first = rig.PaintedFrames[0];
            Assert.Contains(first, r => r.Contains(Gatto.Repl.Cats.Face(Gatto.Terminal.GlyphSet.Unicode)));
            Assert.Contains(first, r => r.Contains("please wait while gatto fetches the models from Hugging Face"));
            Assert.Contains(first, r => r.Contains("> asking Hugging Face for"));
            Assert.DoesNotContain(first, r => Families.Load().Ladder.All(f => r.Contains(f)));
            Assert.Contains("Esc", first[^1]);
            //leaving stops the Hub requests nobody will read. a busy pool can start the search after the leave, so wait for it to arrive
            Assert.True(SpinUntil(() => Asked(hub).Length == 1));
            Assert.True(Assert.Single(Asked(hub)).Token.IsCancellationRequested);
        }
        finally { hub.Release.Set(); }
    }

    //the machine is read beside the scan and the search, never before the first frame, so a slow probe cannot blank the screen
    [Fact]
    public void THE_FIRST_FRAME_IS_PAINTED_BEFORE_THE_HARDWARE_READ_RETURNS()
    {
        var hub = new HeldHub();
        using var hold = new ManualResetEventSlim(false);
        var read = false;
        var probes = new WizardProbes
        {
            Slow = hub.Search,
            OnHardware = () => { hold.Wait(Bound); read = true; },
        };
        var rig = new WizardRig(width: 120) { PollTime = true, WatchKeyBudget = 2 };
        var face = rig.TuiFace(WizardRig.Esc, WizardRig.Esc);
        try
        {
            SetupRunner.Run(new SetupFlow(probes), face, homePath: null, modelSegmentOnly: true);

            Assert.False(read);
            Assert.Contains(rig.PaintedFrames[0], r => r.Contains(Gatto.Repl.Cats.Face(Gatto.Terminal.GlyphSet.Unicode)));
        }
        finally
        {
            hold.Set();
            hub.Release.Set();
        }
    }

    //a chip on a loading shelf restarts the search for its family, and the search it replaced is cancelled
    [Fact]
    public void A_CHIP_DURING_THE_LOAD_STARTS_IT_AGAIN_FOR_THAT_FAMILY()
    {
        var ladder = Families.Load().Ladder;
        var hub = new HeldHub();
        var flow = new SetupFlow(new WizardProbes
        {
            Slow = (r, p, ct) => LitOf(r) == LandingLit ? WizardProbes.Outcome([Row("unsloth/a")], null) : hub.Search(r, p, ct),
        });
        var right = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);
        var rig = new WizardRig(width: 120) { PollTime = true };
        //the second family's chip on the landed shelf turns it off, then the third's lights it while that load is held, then the chord
        var face = new Recording(rig.TuiFace(WizardRig.Tab, WizardRig.Tab, WizardRig.Tab, right, WizardRig.Enter,
            WizardRig.Tab, WizardRig.Tab, right, right, WizardRig.Enter, WizardRig.Esc, WizardRig.Esc))
        {
            OnLaterLoad = () => rig.WatchKeyBudget = 7,
        };
        try
        {
            SetupRunner.Run(flow, face, homePath: null, modelSegmentOnly: true);

            //the two searches start on the pool, so they can arrive in either order and after the runner returned
            Assert.True(SpinUntil(() => Asked(hub).Length == 2));
            var asked = Asked(hub);
            var first = Assert.Single(asked, a => a.Request == ladder[0]);
            var second = Assert.Single(asked, a => a.Request == string.Join(",", new[] { ladder[0], ladder[2] }.Order()));
            Assert.True(first.Token.IsCancellationRequested);
            Assert.NotEqual(first.Token, second.Token);
        }
        finally { hub.Release.Set(); }
    }

    //the scan waits for the search to start and the search for the scan, so run one after the other they would both time out
    [Fact]
    public void THE_SCAN_AND_THE_SEARCH_RUN_AT_THE_SAME_TIME_AND_THE_SHELF_LANDS_WHEN_BOTH_ARE_IN()
    {
        using var searching = new ManualResetEventSlim(false);
        using var scanning = new ManualResetEventSlim(false);
        var met = (Scan: false, Search: false);
        var probes = new WizardProbes
        {
            OnScan = _ =>
            {
                scanning.Set();
                met.Scan = searching.Wait(Bound);
            },
            Slow = (request, progress, ct) =>
            {
                searching.Set();
                met.Search = scanning.Wait(Bound, CancellationToken.None);
                return WizardProbes.Outcome([Row("unsloth/a"), Row("unsloth/b")], null);
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };

        var loading = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
        Assert.Equal(SetupFlow.ShelfLoadingKey, loading.Key);
        Assert.True(SpinUntil(flow.PollWatch));
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        Assert.True(met.Scan && met.Search, $"scan saw the search: {met.Scan}, search saw the scan: {met.Search}");
        Assert.Equal(SetupFlow.SearchKey, shelf.Key);
        Assert.Equal(2, shelf.Shelf!.Rows.Count);
    }

    //the step line names the one publisher, then counts the models read, from the search's own moments
    [Fact]
    public void THE_STEP_LINE_NAMES_THE_PUBLISHER_THEN_COUNTS_THE_MODELS()
    {
        using var reported = new ManualResetEventSlim(false);
        using var next = new ManualResetEventSlim(false);
        var probes = new WizardProbes
        {
            Slow = (request, progress, ct) =>
            {
                progress!.Report(new SearchProgress(SearchStage.Listing, 0, 1, ["unsloth"]));
                reported.Set();
                next.Wait(Bound, CancellationToken.None);
                progress.Report(new SearchProgress(SearchStage.Reading, 6, 13, ["unsloth"]));
                reported.Set();
                next.Wait(Bound, CancellationToken.None);
                return WizardProbes.Outcome([], null);
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(reported.Wait(Bound));
            Assert.Equal("asking Hugging Face for unsloth's models", flow.PollShelfLoad()!.Value.Step);

            reported.Reset();
            next.Set();
            Assert.True(reported.Wait(Bound));
            Assert.Equal("reading the files of 13 models · 6 of 13", flow.PollShelfLoad()!.Value.Step);
        }
        finally { next.Set(); }
    }

    //several publishers are counted as they answer, so the copy names none of them and needs no change when the list grows
    [Fact]
    public void SEVERAL_PUBLISHERS_ARE_COUNTED_RATHER_THAN_NAMED()
    {
        using var reported = new ManualResetEventSlim(false);
        using var done = new ManualResetEventSlim(false);
        var probes = new WizardProbes
        {
            Slow = (request, progress, ct) =>
            {
                progress!.Report(new SearchProgress(SearchStage.Listing, 3, 11, ["a", "b"]));
                reported.Set();
                done.Wait(Bound, CancellationToken.None);
                return WizardProbes.Outcome([], null);
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(reported.Wait(Bound));
            Assert.Equal("asking Hugging Face for 11 publishers' models · 3 of 11", flow.PollShelfLoad()!.Value.Step);
        }
        finally { done.Set(); }
    }

    //the search a chip replaces is cancelled at the press, while the new one is still running
    [Fact]
    public void A_CHIP_CANCELS_THE_SEARCH_IT_REPLACES()
    {
        var hub = new HeldHub();
        var flow = new SetupFlow(new WizardProbes { Slow = hub.Search })
        {
            CanSwitchSource = true, LoadsShelfInBackground = true,
        };
        var family = Added;
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinUntil(() => Asked(hub).Length == 1));
            flow.Answer(ShelfControls.FamilyAnswer(family));
            Assert.True(SpinUntil(() => Asked(hub).Length == 2));

            var asked = Asked(hub);
            Assert.True(Assert.Single(asked, a => a.Request == LandingLit).Token.IsCancellationRequested);
            Assert.False(Assert.Single(asked, a => a.Request.Split(',').Contains(family)).Token.IsCancellationRequested);
        }
        finally { hub.Release.Set(); }
    }

    //a word typed in the door while the shelf loads restarts the load with that word, as a chip does with its family
    [Fact]
    public void A_TERM_TYPED_DURING_THE_LOAD_STARTS_IT_AGAIN_WITH_THAT_TERM()
    {
        var hub = new HeldHub();
        var flow = new SetupFlow(new WizardProbes { Slow = hub.Search })
        {
            CanSwitchSource = true, LoadsShelfInBackground = true,
        };
        try
        {
            flow.StartAtModelSegment();
            //the first search is in before the word is typed, so the two arrive in the order the test reads them
            Assert.True(SpinUntil(() => Asked(hub).Length == 1));
            var again = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("coder")));

            Assert.Equal(SetupFlow.ShelfLoadingKey, again.Key);
            Assert.True(SpinUntil(() => Asked(hub).Length == 2));
            lock (hub.Asked)
            {
                Assert.Equal("coder", hub.Asked[1].Request.Search);
                Assert.True(hub.Asked[0].Token.IsCancellationRequested);
            }
        }
        finally { hub.Release.Set(); }
    }

    //m drops the Hub search and lands on this machine's shelf once the scan answers
    [Fact]
    public void M_DURING_THE_LOAD_STOPS_THE_SEARCH_AND_LANDS_ON_THE_LOCAL_SHELF()
    {
        var hub = new HeldHub();
        var probes = new WizardProbes
        {
            Slow = hub.Search,
            Found = [new FoundModel(@"D:\w\a\a-Q4_K_M.gguf", 4_000_000_000, null)],
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinUntil(() => Asked(hub).Length == 1));
            var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));

            Assert.Equal(SetupFlow.ShelfLoadingKey, local.Key);
            Assert.Equal(Gatto.Cli.Setup.ShelfSource.Local, local.Shelf!.Source);
            Assert.True(Asked(hub)[0].Token.IsCancellationRequested);
            //the held search no longer decides the landing, so the watch resolves on the scan alone
            Assert.True(SpinUntil(flow.PollWatch));
            Assert.Equal(SetupFlow.DiscoveredKey,
                Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);
        }
        finally { hub.Release.Set(); }
    }

    //the record is composed from the flow's facts, so leaving while the shelf loads, or after a chip restarted it, writes what leaving the landed shelf writes
    [Fact]
    public void THE_RECORD_OF_A_LEAVE_DURING_THE_LOAD_IS_THE_RECORD_OF_A_LEAVE_FROM_THE_SHELF()
    {
        var stamp = new Gatto.Cli.VersionStamp("0.5.0", "1a2b3c4", Dev: false);
        IReadOnlyList<string> Record(SetupFlow flow) => WizardSession.Scrollback(flow, () => [], 100,
            Gatto.Terminal.GlyphSet.Unicode, stamp, new DateOnly(2026, 10, 6), "gatto model", theme: null);

        //left while the Hub still holds the search
        var held = new HeldHub();
        var during = new SetupFlow(new WizardProbes { Slow = held.Search });
        var rig = new WizardRig(width: 120) { PollTime = true, WatchKeyBudget = 2 };
        try { SetupRunner.Run(during, rig.TuiFace(WizardRig.Esc, WizardRig.Esc), homePath: null, modelSegmentOnly: true); }
        finally { held.Release.Set(); }

        //left from the landed shelf, the load answering at once
        var landed = new SetupFlow(new WizardProbes { Slow = (r, p, ct) => WizardProbes.Outcome([Row("unsloth/a")], null) });
        SetupRunner.Run(landed, new WizardRig(width: 120) { PollTime = true }.TuiFace(WizardRig.Esc, WizardRig.Esc),
            homePath: null, modelSegmentOnly: true);

        //a chip on the landed shelf starts a load the Hub holds, and the shelf is left during it
        using var hold = new ManualResetEventSlim(false);
        var restarted = new SetupFlow(new WizardProbes
        {
            Slow = (r, p, ct) =>
            {
                if (r.Family() is not null) hold.Wait(Bound, CancellationToken.None);
                return WizardProbes.Outcome([Row("unsloth/a")], null);
            },
        });
        var right = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);
        var chipRig = new WizardRig(width: 120) { PollTime = true };
        try
        {
            SetupRunner.Run(restarted, new Recording(chipRig.TuiFace(WizardRig.Tab, WizardRig.Tab, right, WizardRig.Enter,
                WizardRig.Esc, WizardRig.Esc)) { OnLaterLoad = () => chipRig.WatchKeyBudget = 2 },
                homePath: null, modelSegmentOnly: true);
        }
        finally { hold.Set(); }

        var expected = Record(landed);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Record(during));
        Assert.Equal(expected, Record(restarted));
        Assert.DoesNotContain(expected, r => r.Contains(SetupFlow.Landed) || r.Contains(SetupFlow.ShelfLoadingKey));
    }

    //gatto setup's first arrival at the model step opens on the start-up screen, where b does nothing and the chord leaves
    [Fact]
    public void B_ON_THE_START_UP_SCREEN_DOES_NOTHING_AND_THE_CHORD_LEAVES()
    {
        var hub = new HeldHub();
        var flow = new SetupFlow(new WizardProbes { Slow = hub.Search });
        var b = new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false);
        //the opening, Enter on the found screen, then b and the chord taken on the start-up screen
        var rig = new WizardRig(width: 120) { PollTime = true, WatchKeyBudget = 3 };
        var face = new Recording(rig.TuiFace([.. WalkOpening.Keys, WizardRig.Enter, b, WizardRig.Esc, WizardRig.Esc]));
        try
        {
            SetupRunner.Run(flow, face, homePath: null);

            Assert.Equal([SetupFlow.WelcomeKey, SetupFlow.MachineKey, SetupFlow.FoundKey,
                SetupFlow.ShelfLoadingKey], face.Keys);
            Assert.True(face.Starting[^1]);
            Assert.True(SpinUntil(() => Asked(hub).Length == 1));
            Assert.True(Assert.Single(Asked(hub)).Token.IsCancellationRequested);
        }
        finally { hub.Release.Set(); }
    }

    //a flow on gatto setup's road standing on the landed Hub shelf, its chip searches held while the test holds the hub
    private static SetupFlow OnTheSetupShelf(HeldHub? chipHub)
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Slow = (request, progress, ct) => request.Lit.Contains(Added) && chipHub is { } held
                ? held.Search(request, progress, ct)
                : WizardProbes.Outcome([Row("unsloth/a")], null),
        })
        {
            CanSwitchSource = true, LoadsShelfInBackground = true, OpensOnLoadingScreen = true,
        };
        var found = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        Assert.True(Assert.IsType<WizardScreen.Choice>(flow.Answer(found.Options[0].Key)).Starting);
        Assert.True(SpinUntil(flow.PollWatch));
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);
        return flow;
    }

    //b during a chip's load goes where b goes once the chip's shelf has landed, the step before the shelf, and the held search is cancelled
    [Fact]
    public void B_DURING_A_CHIPS_LOAD_AND_B_AFTER_IT_LANDS_REACH_THE_SAME_SCREEN()
    {
        var chip = ShelfControls.FamilyAnswer(Added);
        var held = new HeldHub();
        try
        {
            var during = OnTheSetupShelf(held);
            var loading = Assert.IsType<WizardScreen.Choice>(during.Answer(chip));
            Assert.True(loading.AllowBack);
            Assert.True(SpinUntil(() => Asked(held).Length == 1));
            var fromTheLoad = Assert.IsType<WizardScreen.Choice>(during.Answer(SetupFlow.BackKey));
            Assert.True(Asked(held)[0].Token.IsCancellationRequested);

            var after = OnTheSetupShelf(chipHub: null);
            Assert.Equal(SetupFlow.ShelfLoadingKey, Assert.IsType<WizardScreen.Choice>(after.Answer(chip)).Key);
            Assert.True(SpinUntil(after.PollWatch));
            Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(after.Answer(SetupFlow.Landed)).Key);
            var fromTheShelf = Assert.IsType<WizardScreen.Choice>(after.Answer(SetupFlow.BackKey));

            Assert.Equal(SetupFlow.FoundKey, fromTheShelf.Key);
            Assert.Equal(fromTheShelf.Key, fromTheLoad.Key);
        }
        finally { held.Release.Set(); }
    }

    //gatto model's first shelf has nothing behind it, so a chip's loading shelf offers no back, a restarted one included
    [Fact]
    public void A_CHIPS_LOADING_SHELF_OFFERS_NO_BACK_WITH_NOTHING_BEHIND_IT()
    {
        var ladder = Families.Load().Ladder;
        var held = new HeldHub();
        var flow = new SetupFlow(new WizardProbes
        {
            Slow = (r, p, ct) => r.Family() is null ? WizardProbes.Outcome([Row("unsloth/a")], null) : held.Search(r, p, ct),
        })
        {
            CanSwitchSource = true, LoadsShelfInBackground = true, OpensOnLoadingScreen = true,
        };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinUntil(flow.PollWatch));
            Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);

            Assert.False(Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer(ladder[1]))).AllowBack);
            Assert.False(Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer(ladder[2]))).AllowBack);
        }
        finally { held.Release.Set(); }
    }

    private const string ServingStep = "checking the model gatto is serving";

    //a server that answers after the search holds the landing, the line names that wait, and the shelf marks the loaded model from that one answer
    [Fact]
    public void THE_SERVING_PROBE_STARTS_WITH_THE_LOAD_AND_THE_SHELF_LANDS_ONCE_WITH_ITS_ANSWER()
    {
        using var hold = new ManualResetEventSlim(false);
        var searches = 0;
        var probes = new WizardProbes
        {
            Slow = (r, p, ct) => { Interlocked.Increment(ref searches); return WizardProbes.Outcome([Row("unsloth/a")], null); },
            Loaded = "a",
            OnLoaded = () => hold.Wait(Bound),
        };
        probes.FromRepo["unsloth/a"] = "a";
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinUntil(() => flow.PollShelfLoad()?.Step == ServingStep));
            Assert.False(flow.PollWatch());
            hold.Set();
            Assert.True(SpinUntil(flow.PollWatch));

            var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
            Assert.Equal(SetupFlow.SearchKey, shelf.Key);
            Assert.Equal(Gatto.Terminal.HaveMark.Loaded, shelf.Shelf!.Facts![0].Have);
            Assert.Equal(1, probes.LoadedAsked);
            Assert.Equal(1, searches);

            //a sort after the landing re-arranges with the landed answer and asks the server nothing
            var sorted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlParams));
            Assert.Equal(Gatto.Terminal.HaveMark.Loaded, sorted.Shelf!.Facts![0].Have);
            Assert.Equal(1, probes.LoadedAsked);
        }
        finally { hold.Set(); }
    }

    //a chip while the server is still answering restarts the search and keeps that request, so the chip's shelf lands on its answer and no second probe starts
    [Fact]
    public void A_CHIP_DURING_THE_SERVING_WAIT_KEEPS_THE_PROBE_IN_FLIGHT()
    {
        using var hold = new ManualResetEventSlim(false);
        var probes = new WizardProbes
        {
            Slow = (r, p, ct) => WizardProbes.Outcome([Row("unsloth/a")], null),
            Loaded = "a",
            OnLoaded = () => hold.Wait(Bound),
        };
        probes.FromRepo["unsloth/a"] = "a";
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinUntil(() => flow.PollShelfLoad()?.Step == ServingStep));

            Assert.Equal(SetupFlow.ShelfLoadingKey, Assert.IsType<WizardScreen.Choice>(
                flow.Answer(ShelfControls.FamilyAnswer(Families.Load().Ladder[1]))).Key);
            Assert.True(SpinUntil(() => flow.PollShelfLoad()?.Step == ServingStep));
            Assert.Equal(1, probes.LoadedAsked);
            hold.Set();
            Assert.True(SpinUntil(flow.PollWatch));

            var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
            Assert.Equal(Gatto.Terminal.HaveMark.Loaded, shelf.Shelf!.Facts![0].Have);
            Assert.Equal(1, probes.LoadedAsked);
        }
        finally { hold.Set(); }
    }

    //the pulse fires after every answer, as a landing that takes a second lets it, so each loading path is drawn with its tick between screens
    [Theory]
    [InlineData("start")]
    [InlineData("chip")]
    [InlineData("term")]
    [InlineData("m")]
    [InlineData("folder")]
    [InlineData("repo id")]
    public void EVERY_LOADING_PATH_DRAWS_ONE_PURR_IN_EVERY_FRAME(string path)
    {
        var probes = new WizardProbes
        {
            Slow = (request, progress, ct) => WizardProbes.Outcome([Row("unsloth/a")], null),
            Found = [new FoundModel(@"D:\w\a\a-Q4_K_M.gguf", 4_000_000_000, null)],
            Typed = id => new TypedIdOutcome.Unreachable(false),
        };
        var right = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);
        static ConsoleKeyInfo[] Typed(string text) =>
            [WizardRig.Ch('?'), .. text.Select(WizardRig.Ch), WizardRig.Enter];
        ConsoleKeyInfo[] keys = path switch
        {
            "start" => [],
            "chip" => [WizardRig.Tab, WizardRig.Tab, WizardRig.Tab, right, WizardRig.Enter],
            "term" => Typed("coder"),
            "m" => [WizardRig.Ch('m')],
            "folder" => Typed(@"D:\w"),
            _ => Typed("org/model"),
        };
        var rig = new WizardRig(width: 120) { PollTime = true, Pulse = new FakePulse() };
        //the repo id path takes one Esc back to the shelf from the lookup's question, then every path leaves on the Ctrl+C chord
        ConsoleKeyInfo[] back = path == "repo id" ? [WizardRig.Esc] : [];
        var face = new Pulsing(rig.TuiFace([.. keys, .. back, WizardRig.CtrlC, WizardRig.CtrlC]), rig.Pulse);

        SetupRunner.Run(new SetupFlow(probes), face, homePath: null, modelSegmentOnly: true);

        var loads = face.Between.Where(b => b.Key == SetupFlow.ShelfLoadingKey).ToList();
        //the start-up screen, then the path's own load (and on the repo id path the shelf restored by Esc from the lookup's question)
        Assert.True(path == "start" ? loads.Count == 1 : loads.Count >= 2, $"{loads.Count} loads");
        Assert.True(face.Starting[0]);
        Assert.DoesNotContain(true, face.Starting.Skip(1));
        foreach (var (_, frame) in loads)
        {
            Assert.Equal(1, Purrs(frame));
            Assert.Contains(frame, r => r.Contains("Hugging Face") || r.Contains("looking"));
        }
        Assert.All(rig.PaintedFrames, f => Assert.True(Purrs(f) <= 1, string.Join(Environment.NewLine, f)));
        Assert.All(face.Between, b => Assert.True(Purrs(b.Frame) <= 1, string.Join(Environment.NewLine, b.Frame)));
    }

    private static int Purrs(IReadOnlyList<string> frame) =>
        string.Join("\n", frame).Split(Gatto.Repl.Cats.Face(Gatto.Terminal.GlyphSet.Unicode)).Length - 1;

    //the TUI face with the pulse fired after each answer, keeping the frame each tick drew
    private sealed class Pulsing(Gatto.Cli.Setup.Tui.TuiWizardSurface inner, FakePulse pulse) : IWizardSurface
    {
        public readonly List<(string Key, IReadOnlyList<string> Frame)> Between = [];
        public readonly List<bool> Starting = [];

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null, Func<FetchTick?>? tick = null,
            Func<CheckTick?>? check = null) => Choose(c, watch, tick, check, null);

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch, Func<FetchTick?>? tick,
            Func<CheckTick?>? check, Func<ShelfLoadTick?>? load)
        {
            if (c.Key == SetupFlow.ShelfLoadingKey) Starting.Add(c.Starting);
            var answer = inner.Choose(c, watch, tick, check, load);
            pulse.Fire();
            Between.Add((c.Key, inner.LastPainted));
            return answer;
        }

        public int RowBudget => inner.RowBudget;
        public Gatto.Terminal.GlyphSet Glyphs => inner.Glyphs;
        public bool CanSwitchSource => inner.CanSwitchSource;
        public bool ShowsChoiceBodyRows => inner.ShowsChoiceBodyRows;
        public bool DrawsLoadingShelf => inner.DrawsLoadingShelf;
        public string? Ask(WizardScreen.Ask a) => inner.Ask(a);
        public void Show(WizardScreen.Info i) => inner.Show(i);
        public void End(WizardScreen.Terminal t) => inner.End(t);
    }

    //the TUI face with every asked screen's key recorded, in order
    private sealed class Recording(Gatto.Cli.Setup.Tui.TuiWizardSurface inner) : IWizardSurface
    {
        public readonly List<string> Keys = [];
        public readonly List<bool> Starting = [];

        //run once, when the first loading shelf after the start-up screen is asked
        public Action? OnLaterLoad { get; set; }

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null, Func<FetchTick?>? tick = null,
            Func<CheckTick?>? check = null) => Choose(c, watch, tick, check, null);

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch, Func<FetchTick?>? tick,
            Func<CheckTick?>? check, Func<ShelfLoadTick?>? load)
        {
            Keys.Add(c.Key);
            Starting.Add(c.Starting);
            //the start-up screen takes no key but Esc, so a script meant for a later loading shelf is let in there only
            if (c is { Key: SetupFlow.ShelfLoadingKey, Starting: false } && OnLaterLoad is { } arm)
            {
                OnLaterLoad = null;
                arm();
            }
            return inner.Choose(c, watch, tick, check, load);
        }

        public int RowBudget => inner.RowBudget;
        public Gatto.Terminal.GlyphSet Glyphs => inner.Glyphs;
        public bool CanSwitchSource => inner.CanSwitchSource;
        public bool ShowsChoiceBodyRows => inner.ShowsChoiceBodyRows;
        public bool DrawsLoadingShelf => inner.DrawsLoadingShelf;
        public string? Ask(WizardScreen.Ask a) { Keys.Add(a.Key); return inner.Ask(a); }
        public void Show(WizardScreen.Info i) => inner.Show(i);
        public void End(WizardScreen.Terminal t) => inner.End(t);
    }

    //a flow standing on the landed Hub shelf, with a scan the test can hold
    private static (SetupFlow Flow, WizardProbes Probes) OnTheLandedShelf(ManualResetEventSlim? scanHold = null)
    {
        var probes = new WizardProbes
        {
            Slow = (request, progress, ct) => WizardProbes.Outcome([Row("unsloth/a")], null),
            Found = [new FoundModel(@"D:\w\a\a-Q4_K_M.gguf", 4_000_000_000, null)],
            Typed = id => new TypedIdOutcome.Unreachable(false),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        flow.StartAtModelSegment();
        Assert.True(SpinUntil(flow.PollWatch));
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);
        if (scanHold is { } hold) probes.OnScan = _ => hold.Wait(Bound);
        return (flow, probes);
    }

    //m sweeps this machine behind the local loading shelf, since a disk scan takes seconds
    [Fact]
    public void M_ON_THE_HUB_SHELF_SWEEPS_BEHIND_THE_LOCAL_LOADING_SHELF()
    {
        using var hold = new ManualResetEventSlim(false);
        var (flow, _) = OnTheLandedShelf(hold);
        try
        {
            var loading = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));

            Assert.Equal(SetupFlow.ShelfLoadingKey, loading.Key);
            Assert.Equal(Gatto.Cli.Setup.ShelfSource.Local, loading.Shelf!.Source);
            Assert.Equal("looking for models on this machine…", flow.PollShelfLoad()!.Value.Step);
            hold.Set();
            Assert.True(SpinUntil(flow.PollWatch));
            //the line keeps its words while the landing composes the shelf, so a frame drawn then still says what gatto is doing
            Assert.Equal("looking for models on this machine…", flow.PollShelfLoad()!.Value.Step);
            Assert.Equal(SetupFlow.DiscoveredKey,
                Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);
        }
        finally { hold.Set(); }
    }

    //a typed folder is swept behind the loading shelf, which names the folder in its slot and its step line
    [Fact]
    public void A_TYPED_FOLDER_IS_SWEPT_BEHIND_THE_LOADING_SHELF()
    {
        using var hold = new ManualResetEventSlim(false);
        var (flow, probes) = OnTheLandedShelf(hold);
        try
        {
            var loading = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"D:\w")));

            Assert.Equal(SetupFlow.ShelfLoadingKey, loading.Key);
            Assert.Equal(@"D:\w", loading.Shelf!.Folder);
            Assert.Equal(@"looking for models in D:\w…", flow.PollShelfLoad()!.Value.Step);
            hold.Set();
            Assert.True(SpinUntil(flow.PollWatch));
            Assert.Equal(@"looking for models in D:\w…", flow.PollShelfLoad()!.Value.Step);
            Assert.Equal(SetupFlow.DiscoveredKey,
                Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed)).Key);
            Assert.Contains(@"D:\w", probes.ScanRoots);
        }
        finally { hold.Set(); }
    }

    //a typed repo id is looked up behind the loading shelf, with a line that names the id, and lands on the lookup's own answer
    [Fact]
    public void A_TYPED_REPO_ID_IS_LOOKED_UP_BEHIND_THE_LOADING_SHELF()
    {
        using var hold = new ManualResetEventSlim(false);
        var probes = new WizardProbes
        {
            Slow = (request, progress, ct) => WizardProbes.Outcome([Row("unsloth/a")], null),
            Typed = id => { hold.Wait(Bound); return new TypedIdOutcome.Unreachable(false); },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinUntil(flow.PollWatch));
            flow.Answer(SetupFlow.Landed);

            var loading = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("org/model")));

            Assert.Equal(SetupFlow.ShelfLoadingKey, loading.Key);
            Assert.Equal("looking up org/model on Hugging Face", flow.PollShelfLoad()!.Value.Step);
            Assert.False(flow.PollWatch());
            hold.Set();
            Assert.True(SpinUntil(flow.PollWatch));
            Assert.Equal(SetupFlow.TypedIdKey,
                Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Landed)).Key);
        }
        finally { hold.Set(); }
    }

    //bounded, so a load that never ends fails the test instead of hanging the run
    private static bool SpinUntil(Func<bool> done) => SpinWait.SpinUntil(done, Bound);

    //every wait here ends on its event, so the bound only matters when the suite's load starts a pool task late, and 5 s was once too short for that
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
}
