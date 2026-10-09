using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//an address typed at a Hub door: answered in place on gatto model, the connect road on gatto setup, and never a Hub search
public class DoorAddressTests
{
    private const string Notice = "gatto setup connects gatto to a server. This screen adds models.";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static ModelRow Row(string repo) =>
        ShelfRows.Of(repo, "qwen", new HubQuant("qwen3-8b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 32768, false, Badge: null, Downloads: 5, Gated: false, Params: 8_000_000_000);

    //a probe whose every Hub search is counted, so an address that reached the Hub shows as a count
    private sealed class Counted
    {
        public int Searches;
        public WizardProbes Probes = null!;
    }

    private static Counted Probes()
    {
        var c = new Counted();
        c.Probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Answer = _ => { Interlocked.Increment(ref c.Searches); return WizardProbes.Outcome([Row("qwen/a"), Row("qwen/b")]); },
        };
        return c;
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("localhost:1234")]
    public void ON_GATTO_MODEL_THE_SHELF_DOOR_SHOWS_THE_NOTICE_AND_ASKS_NO_HUB(string address)
    {
        var c = Probes();
        var flow = new SetupFlow(c.Probes) { CanSwitchSource = true };
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
        Assert.Equal(SetupFlow.SearchKey, shelf.Key);
        var before = c.Searches;
        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(address)));
        Assert.Equal(before, c.Searches);
        Assert.Equal(SetupFlow.SearchKey, after.Key);
        Assert.Equal(Notice, after.Notice);
        Assert.Equal(shelf.Shelf!.Rows, after.Shelf!.Rows);
        //no step was pushed, so Esc there is the leave chord rather than a step back to an identical shelf
        Assert.Equal(shelf.AllowBack, after.AllowBack);
    }

    [Fact]
    public void ON_GATTO_MODEL_THE_TYPED_ID_ASK_SAYS_THE_NOTICE_FOR_AN_ADDRESS()
    {
        var flow = new SetupFlow(Probes().Probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.TypeAnId));
        Assert.Equal(Notice, ask.Validate("http://127.0.0.1:8080"));
        Assert.Equal(Notice, ask.Validate("localhost:1234"));
        Assert.Null(ask.Validate("qwen/qwen3-8b"));
    }

    [Fact]
    public void ON_SETUP_THE_TYPED_ID_ASK_PASSES_AN_ADDRESS_ON()
    {
        var flow = new SetupFlow(Probes().Probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.TypeAnId));
        Assert.Null(ask.Validate("http://127.0.0.1:8080"));
    }

    [Fact]
    public void ON_GATTO_MODEL_THE_LOADING_SHELFS_DOOR_KEEPS_THE_LOAD_AND_SHOWS_THE_NOTICE()
    {
        using var release = new ManualResetEventSlim(false);
        var searches = 0;
        var probes = new WizardProbes
        {
            Slow = (_, _, _) => { Interlocked.Increment(ref searches); release.Wait(Bound, CancellationToken.None); return WizardProbes.Outcome([Row("qwen/a")]); },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            var loading = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
            Assert.Equal(SetupFlow.ShelfLoadingKey, loading.Key);
            //the load runs on the pool, so the one search is seen to start before the address is typed
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref searches) == 1, Bound));
            var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("http://127.0.0.1:8080")));
            Assert.Equal(SetupFlow.ShelfLoadingKey, after.Key);
            Assert.Equal(Notice, after.Notice);
            Assert.Equal(1, searches);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void ON_SETUP_AN_ADDRESS_TAKES_THE_CONNECT_ROAD_AND_ESC_RETURNS_TO_THE_SHELF()
    {
        var c = Probes();
        var flow = new SetupFlow(c.Probes) { CanSwitchSource = true };
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(SetupFlow.SearchKey, shelf.Key);
        var writes = flow.Writes;

        var silent = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("  http://127.0.0.1:8080  ")));
        Assert.Equal("http://127.0.0.1:8080", c.Probes.ProbedAt[^1]);
        Assert.Equal(SetupPath.Connect, flow.Path);
        Assert.Contains(silent.BodyRows!, r => r.Text == "No server answered at http://127.0.0.1:8080.");

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));
        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.Equal(SetupPath.Llama, flow.Path);
        Assert.Equal(writes, flow.Writes);
    }

    [Fact]
    public void ON_SETUP_LOOK_AGAIN_PROBES_THE_TYPED_ADDRESS()
    {
        var c = Probes();
        var flow = new SetupFlow(c.Probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        flow.Answer(ShelfControls.TypedAnswer("localhost:1234"));
        flow.Answer(SetupFlow.Retry);
        Assert.Equal(["localhost:1234", "localhost:1234"], c.Probes.ProbedAt);
        Assert.Empty(c.Probes.ProbeSkips);
    }

    [Fact]
    public void ON_SETUPS_LOADING_SHELF_AN_ADDRESS_CANCELS_THE_LOAD()
    {
        using var release = new ManualResetEventSlim(false);
        var cancelled = false;
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Slow = (_, _, ct) =>
            {
                cancelled = WaitHandle.WaitAny([release.WaitHandle, ct.WaitHandle], Bound) == 1;
                return WizardProbes.Outcome([Row("qwen/a")]);
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            Assert.Equal(SetupFlow.ShelfLoadingKey, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()).Key);
            flow.Answer(ShelfControls.TypedAnswer("http://127.0.0.1:8080"));
            Assert.Equal(SetupPath.Connect, flow.Path);
            Assert.True(SpinWait.SpinUntil(() => cancelled, Bound), "the load was not cancelled");
        }
        finally { release.Set(); }
    }

    public static TheoryData<string> Outcomes() => new() { "malformed", "noquant", "noweights", "private", "gated" };

    //every typed-id ask after a failed lookup holds the same rule, so an address there never reaches the flow on gatto model
    [Theory]
    [MemberData(nameof(Outcomes))]
    public void EVERY_TYPED_ID_ASK_SAYS_THE_NOTICE_FOR_AN_ADDRESS_ON_GATTO_MODEL(string outcome)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Answer = _ => WizardProbes.Outcome([Row("qwen/a")]),
            Typed = _ => outcome switch
            {
                "malformed" => new TypedIdOutcome.Malformed(),
                "noquant" => new TypedIdOutcome.NoUsableQuant(),
                "noweights" => new TypedIdOutcome.NoWeights(),
                "gated" => new TypedIdOutcome.Unreachable(true),
                _ => new TypedIdOutcome.Unreachable(false),
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.TypeAnId);
        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer("qwen/qwen3-8b"));
        Assert.Equal(Notice, again.Validate("localhost:1234"));
    }

    //a caller that skips the ask's own check still gets the ask back, never a throw
    [Fact]
    public void AN_ADDRESS_ANSWERED_PAST_THE_ASKS_CHECK_RETURNS_THE_ASK()
    {
        var flow = new SetupFlow(Probes().Probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.TypeAnId);
        Assert.IsType<WizardScreen.Ask>(flow.Answer("localhost:1234"));
    }

    [Fact]
    public void A_NUMBER_IS_SEARCHED_AS_TEXT_AT_THE_TYPED_ID_ASK()
    {
        var c = Probes();
        var flow = new SetupFlow(c.Probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        flow.Answer(SetupFlow.TypeAnId);
        var before = c.Searches;
        flow.Answer("128000");
        Assert.Equal(before + 1, c.Searches);
    }

    [Fact]
    public void A_NUMBER_IS_SEARCHED_AS_TEXT_AT_THE_LOADING_SHELFS_DOOR()
    {
        using var release = new ManualResetEventSlim(false);
        var searches = 0;
        var probes = new WizardProbes
        {
            Slow = (_, _, _) => { Interlocked.Increment(ref searches); release.Wait(Bound, CancellationToken.None); return WizardProbes.Outcome([Row("qwen/a")]); },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        try
        {
            flow.StartAtModelSegment();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref searches) == 1, Bound));
            flow.Answer(ShelfControls.TypedAnswer("128000"));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref searches) == 2, Bound), "the number did not restart the search");
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_NUMBER_IS_SEARCHED_AS_TEXT_AT_THE_SHELF_DOOR(bool addRoad)
    {
        var c = Probes();
        var flow = new SetupFlow(c.Probes) { CanSwitchSource = true };
        if (addRoad) flow.StartAtModelSegment(); else flow.StartPastEngine();
        var before = c.Searches;
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("128000")));
        Assert.Equal(before + 1, c.Searches);
        Assert.Null(screen.Notice);
    }

    //the face draws the notice and drops it at the first key, since the face's own loop never returns an arrow to the flow
    [Fact]
    public void THE_FACE_CLEARS_THE_NOTICE_AT_THE_FIRST_KEY()
    {
        var shelf = ShelfFixtures.Shelf("unified") with { Notice = Notice, AllowBack = true };
        var rig = new WizardRig(120);
        var face = rig.TuiFace(WizardRig.Down, WizardRig.Esc);
        face.Choose(shelf);
        Assert.Contains(rig.PaintedFrames[0], r => r.Contains(Notice));
        Assert.DoesNotContain(face.LastPainted, r => r.Contains(Notice));
    }

    //a wheel notch is a key the face makes for itself, not the user's key or press, so the notice stays
    [Fact]
    public void A_WHEEL_NOTCH_KEEPS_THE_NOTICE()
    {
        var shelf = ShelfFixtures.Shelf("unified") with { Notice = Notice, AllowBack = true };
        var row = ShelfFixtures.Target(shelf, HitKind.Row, index: 0);
        var rig = new WizardRig(120);
        var face = rig.TuiFaceEvents(RigMouse.Wheel(row, -120), (Action)(() => { }), new KeyEvent(WizardRig.Esc));
        face.Choose(shelf);
        Assert.Contains(rig.PaintedFrames[^1], r => r.Contains(Notice));
    }

    private static WizardScreen.Choice Loading(string? notice)
    {
        var shelf = ShelfFixtures.Shelf("unified");
        return shelf with { Watching = true, AllowBack = true, Notice = notice, Shelf = shelf.Shelf! with { Loading = true, Rows = [], Facts = [] } };
    }

    //a re-emit of one load keeps its clock for the notice, and any other loading screen starts at zero
    [Fact]
    public void THE_LOADING_SHELFS_CLOCK_SURVIVES_THE_NOTICE_AND_RESTARTS_OTHERWISE()
    {
        long now = 0;
        var face = new TuiWizardSurface(new RecordingSurface { Width = 120 },
            new ScriptedInputSource(null, new KeyEvent(WizardRig.Ch('m')), new KeyEvent(WizardRig.Ch('m')), new KeyEvent(WizardRig.Ch('m'))),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", nowMs: () => now);
        face.Choose(Loading(null));
        now = 7000;
        face.Choose(Loading(Notice));
        Assert.Contains(face.LastPainted, r => r.Contains(ChromeTicker.FormatElapsed(7000)));
        face.Choose(Loading(null));
        Assert.Contains(face.LastPainted, r => r.Contains(ChromeTicker.FormatElapsed(0)));
    }
}
