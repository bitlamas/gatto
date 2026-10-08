using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Roles.Audition;
using Gatto.Tests.Fakes;
using Probes = Gatto.Tests.Fakes.WizardProbes;

namespace Gatto.Tests.Setup;

//write every branch into one rendered document, since green single-screen tests never show how the flow reads as a whole
public class RenderedWalkDumpTests : IDisposable
{
    //this class owns its own temp root instead of inheriting RenderedWalkTests, since inheriting would re-run every test of that class under this name
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-dump-").FullName;
    private int _homes;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    private string NewHomeForDump()
    {
        var home = Path.Combine(_root, $"home{++_homes}");
        Gatto.Core.Home.GattoHome.EnsureInitialized(home);
        return home;
    }

    private static string Out()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        var dir = Path.Combine(d!.FullName, "tmp", "walk", "branches");
        Directory.CreateDirectory(dir);
        return dir;
    }

    //one leg of the document: the branch label, why that branch belongs, and the run that reaches it
    private sealed record Leg(string Name, string Why, Func<WizardRig> Run);

    [Fact]
    public void DUMP_EVERY_BRANCH_FOR_A_READER()
    {
        var legs = new List<Leg>
        {
            new("happy-path", "the walk end to end, install accepted",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h), [.. WalkOpening.Keys, WizardRig.Digit('1'),
                    WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'),
                    WizardRig.Digit('1'), WizardRig.Digit('1')])),

            new("check-failed-taken-anyway", "the failed check, which warns, offers another model and lets the user go on",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h, audition: new AuditionCheck(
                        AuditionOutcome.Failed,
                        new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 2, 5))),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Digit('2'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1')])),

            new("check-could-not-run", "one of the four could-not-run arms",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h,
                        audition: new AuditionCheck(AuditionOutcome.CouldNotRun)),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Esc,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 6)])),

            new("install-dev-build", "the first of the three install renders",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h, install: Gatto.Cli.InstallState.DevBuild),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')])),

            new("install-half-done", "the second install render, the repair offer",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h,
                        install: Gatto.Cli.InstallState.InstalledButNotOnPath),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1')])),

            new("leave-at-the-door", "the leave epilogue BEFORE the writes",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h), [.. WalkOpening.Keys, WizardRig.Esc, WizardRig.Esc])),

            new("leave-at-the-shelf", "a second ending, further in and still before the writes",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h), [.. WalkOpening.Keys, WizardRig.Digit('1'),
                    WizardRig.Esc, WizardRig.Esc])),

            new("leave-at-the-check", "the leave epilogue AFTER the writes",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h), [.. WalkOpening.Keys, WizardRig.Digit('1'),
                    WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Esc, WizardRig.Esc])),

            //the in-session legs enter through StartAtModelSegment, so a run that drives Start alone never renders them

            new("in-session-shelf", "the shelf as /model add opens it; Esc leaves and says nothing was added",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h),
                    [WizardRig.Esc])),

            new("in-session-pick", "a row taken and checked, ending on the completion screen",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h),
                    [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1')])),

            new("in-session-check-declined", "the check skipped, ending on the saved-and-pointed line",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h),
                    [WizardRig.Enter, WizardRig.Digit('2'), WizardRig.Digit('2'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1')])),

            new("in-session-check-failed", "the check failed and taken anyway, ending on the saved line",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h, audition: new AuditionCheck(
                        AuditionOutcome.Failed,
                        new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 1, 5))),
                    [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('2'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1')])),

            new("in-session-leave", "Esc once a row is taken, ending on the saved-and-pointed line",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h),
                    [WizardRig.Enter, WizardRig.Esc, WizardRig.Esc])),

            //the two done shapes depend on _heldBy, so each needs a model already serving, or InSessionEnding falls through to the summary

            new("in-session-done-checked", "the check ran, so the swap already happened: stay or go back",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h, heldBy: "qwen3.6-35b-a3b"),
                    [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1')])),

            new("in-session-done-unchecked", "added unchecked, and the switch is still a question",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h, heldBy: "qwen3.6-35b-a3b"),
                    [WizardRig.Enter, WizardRig.Digit('2'), WizardRig.Digit('2'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1')])),

            new("in-session-download-keys",
                "the live model download and its three keys, drawn but not pressed",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h,
                        heldBy: "qwen3.6-35b-a3b",
                        hubOffer: RenderedWalkTests.FetchOffer,
                        modelTicks: RenderedWalkTests.HalfWay),
                    [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')])),

            //the snapshot must be one the classifier calls unified, since a machine with no gap between installed and visible memory reads as discrete
            new("in-session-shelf-unified",
                "a unified machine's shelf: no runs column, and the pane says fits rather than GPU",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h,
                        snapshot: RenderedWalkTests.Unified96),
                    [WizardRig.Esc])),

            //the leg above proves the snapshot alone changes nothing, so this one shows the fit tiers disagreeing
            new("in-session-shelf-does-not-fit",
                "the shelf when the rows do not all fit the card",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h,
                        rows: RenderedWalkTests.TooBigForACard),
                    [WizardRig.Esc])),

            new("setup-download",
                "the setup road reaching the model fetch, engine already on the machine",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h,
                        hubOffer: RenderedWalkTests.FetchOffer,
                        modelTicks: RenderedWalkTests.HalfWay),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
                     WizardRig.Digit('1'), WizardRig.Digit('1')])),

            //explain each unreachable screen on its own, since a drop and a mismatch need no keypress while the paused screen does.

            new("check-could-not-run-incomplete",
                "the file is shorter than its own header says, so no server was ever spawned",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h,
                        audition: new AuditionCheck(AuditionOutcome.CouldNotRun,
                            Stumble: new CheckStumble.Incomplete(
                                "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", @"C:\weights\gemma",
                                7_900_000_000L, 16_900_000_000L))),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Esc,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 6),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),

            new("check-could-not-run-exited",
                "the server came up and died before the model was loaded",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h,
                        audition: new AuditionCheck(AuditionOutcome.CouldNotRun,
                            Stumble: new CheckStumble.Exited(
                                ["llama_model_load: error loading model architecture: unknown model architecture: 'gemma4'",
                                 "llama_load_model_from_file: failed to load model"],
                                @"C:\home\logs\serve.log"))),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Esc,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 6),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),

            new("check-could-not-run-stopped",
                "the server was alive, the battery was running, and the recorded pid went away",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h,
                        audition: new AuditionCheck(AuditionOutcome.CouldNotRun,
                            Stumble: new CheckStumble.Stopped(
                                ["ggml_vulkan: device lost", "terminate called without an active exception"]))),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     WizardRig.Digit('1'), WizardRig.Esc,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 6),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),

            //the dropped outcome reaches two different screens, and a tick decides which, so each branch needs its own leg
            new("model-fetch-dropped",
                "the connection dropped mid-fetch, with bytes already on disk",
                () => Steered(h => Drop(RenderedWalkTests.ProbesForDump(h,
                        hubOffer: RenderedWalkTests.FetchOffer,
                        modelTicks: RenderedWalkTests.HalfWay)),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 8),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),

            new("watch-after-unreachable-fetch",
                "the fetch never received a byte, so the road is the browser watch on its unreachable arm",
                () => Steered(h => Drop(RenderedWalkTests.ProbesForDump(h,
                        hubOffer: RenderedWalkTests.FetchOffer)),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 8),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),

            new("model-fetch-mismatch",
                "what arrived is not what Hugging Face publishes, so gatto deleted it",
                () => Steered(h => Mismatch(RenderedWalkTests.ProbesForDump(h,
                        hubOffer: RenderedWalkTests.FetchOffer,
                        modelTicks: RenderedWalkTests.HalfWay)),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 8),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),

            //an empty shelf has two different screens, one from the machine's arithmetic and one from the network, so each needs its own leg
            new("shelf-none-fit-this-machine",
                "repos were listed and priced and none of their quants fit this machine",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h,
                        answer: _ => WizardProbes.Outcome([], HubSearchCause.NothingFits)),
                    [WizardRig.Esc])),

            new("shelf-cannot-reach-the-list",
                "every request gatto made came back a failure",
                () => InSession(h => RenderedWalkTests.ProbesForDump(h,
                        answer: _ => WizardProbes.Outcome([], HubSearchCause.HubFailed)),
                    [WizardRig.Esc])),

            //only the setup walk reaches HeldScreen, since the in-session walk goes to the check ask instead, so the entry decides the screen
            new("audition-held",
                "a model is already being served, so the check asks before taking the server",
                () => Steered(h => RenderedWalkTests.ProbesForDump(h, heldBy: "qwen3.6-35b-a3b"),
                    [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
                     .. Enumerable.Repeat(WizardRig.Digit('1'), 6),
                     WizardRig.Esc, WizardRig.Esc, WizardRig.Esc, WizardRig.Esc])),


        };

        var written = new List<string>();
        foreach (var leg in legs)
        {
            string body;
            try
            {
                var rig = leg.Run();
                //print both, since neither is the whole walk: the last frame stops before its key and Plain holds the closing screen
                var walk = rig.Frames.Count == 0 ? "(nothing was painted)" : rig.Frames[^1];
                var left = string.IsNullOrWhiteSpace(rig.Plain)
                    ? "(the terminal was left empty)" : rig.Plain;
                body = $"frames snapshotted: {rig.Frames.Count}\n\n==== the walk, up to the last keypress ====\n\n{walk}\n\n==== what the terminal was left holding ====\n\n{left}";
            }
            catch (Exception ex)
            {
                //write an artifact for a leg that throws, so the document names the failing branch instead of leaving only a stack trace.
                body = $"THIS LEG DID NOT COMPLETE: {ex.GetType().Name}: {ex.Message}";
            }
            File.WriteAllText(Path.Combine(Out(), leg.Name + ".txt"),
                $"# {leg.Name}\n# {leg.Why}\n\n{body}\n");
            written.Add(leg.Name);
        }

        Assert.Equal(legs.Count, written.Count);
        //this only checks that every artifact exists and is named, since no assertion here can judge how the rendering reads
        Assert.All(written, n => Assert.True(File.Exists(Path.Combine(Out(), n + ".txt"))));
    }

    //the strip proves the TUI face rendered, since the plain fallback paints none, so the test runs both faces
    [Fact]
    public void THE_WALK_IS_RENDERED_ON_THE_FACE_THAT_SHIPS()
    {
        var home = NewHomeForDump();
        var tui = new WizardRig(80);
        SetupRunner.Run(new SetupFlow(RenderedWalkTests.ProbesForDump(home)),
            tui.TuiFace([.. WalkOpening.Keys, WizardRig.Esc, WizardRig.Esc]), home);

        Assert.Contains(Strip, tui.Plain, StringComparison.Ordinal);

        //the plain face is the case where no strip may appear, so the two faces can be told apart
        var plainHome = NewHomeForDump();
        var plain = new WizardRig(80);
        SetupRunner.Run(new SetupFlow(RenderedWalkTests.ProbesForDump(plainHome)),
            plain.Face([.. WalkOpening.Keys, WizardRig.Esc, WizardRig.Esc]), plainHome);

        Assert.DoesNotContain(Strip, plain.Plain, StringComparison.Ordinal);
    }

    //each of the three producers must draw the row ScreenPainter.Header composes, since naming the command in prose is not that row
    [Fact]
    public void EVERY_ADD_ROAD_FRAME_DRAWS_THE_BANNER()
    {
        var banner = Gatto.Cli.Setup.Tui.ScreenPainter.Header(80, "0.5.0", "1a2b3c4", Gatto.Terminal.GlyphSet.Unicode);

        //the script sends two Esc keys, since the first arms the leave chord and the walk then asks for another key
        var shelf = InSession(h => RenderedWalkTests.ProbesForDump(h), [WizardRig.Esc, WizardRig.Esc]);
        var pick = InSession(h => RenderedWalkTests.ProbesForDump(h),
            [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
             WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')]);
        var done = InSession(h => RenderedWalkTests.ProbesForDump(h, heldBy: "qwen3.6-35b-a3b"),
            [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
             WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')]);

        //a search that answers only after the first paint, so the start-up screen is captured on every run rather than only on a slow one
        WizardRig? held = null;
        var waited = InSession(h =>
        {
            Probes? made = null;
            made = RenderedWalkTests.ProbesForDump(h, answer: _ =>
            {
                SpinWait.SpinUntil(() => held!.Frames.Count > 0, TimeSpan.FromSeconds(10));
                return Probes.Outcome(made!.Rows, null);
            });
            return made;
        }, [WizardRig.Esc, WizardRig.Esc], made: rig => held = rig);
        Assert.Contains(waited.Frames, IsStartUp);

        foreach (var rig in new[] { shelf, pick, done, waited })
        {
            Assert.NotEmpty(rig.Frames);
            foreach (var frame in rig.Frames)
                Assert.Contains(IsStartUp(frame) ? StartUpHeader : banner, frame, StringComparison.Ordinal);
        }

        //the setup flow must draw the same banner, so a change that keys the row to one entry only still fails here.
        var setup = Steered(h => RenderedWalkTests.ProbesForDump(h),
            [.. WalkOpening.Keys, WizardRig.Esc, WizardRig.Esc]);
        Assert.Contains(banner, string.Join("\n", setup.Frames), StringComparison.Ordinal);
    }

    //the start-up screen is the one in-session frame that draws the cat, so a frame is told apart by the drawing and never by the header under test
    private static readonly string CatLine = Gatto.Repl.Cats.For("", Gatto.Terminal.GlyphSet.Unicode).Split('\n')
        .Select(l => l.TrimEnd('\r').Trim()).OrderByDescending(l => l.Length).First();

    private static bool IsStartUp(string frame) => frame.Contains(CatLine, StringComparison.Ordinal);

    //the start-up screen names the program, since no command has been chosen there yet
    private static readonly string StartUpHeader =
        Gatto.Cli.Setup.Tui.ScreenPainter.Header(80, "0.5.0", "1a2b3c4", Gatto.Terminal.GlyphSet.Unicode, command: "gatto");

    //the in-session close shows the done rows and nothing from the retired prose or the install row
    [Fact]
    public void THE_IN_SESSION_CLOSE_IS_THE_DONE_SCREEN_AND_NOT_THE_RETIRED_PROSE()
    {
        //the script spends one key fewer, since the in-session close asks one question less than the other legs
        var pick = InSession(h => RenderedWalkTests.ProbesForDump(h),
            [WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
             WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')]);

        var drawn = string.Join("\n", pick.Frames) + pick.Plain;
        foreach (var retired in new[]
                 {
                     "seems to be answering properly",
                     "not installed, runs from",
                     "Congratulations, your model is ready",
                     "The configuration has been saved in",
                 })
            Assert.DoesNotContain(retired, drawn, StringComparison.Ordinal);

        //the positive must be a line the retired prose cannot produce, since the old wording matched the sentence forbidden above
        Assert.Contains("check", drawn, StringComparison.Ordinal);
        Assert.Contains("to use it", drawn, StringComparison.Ordinal);

        //the setup flow is the negative control: its own closing line must still appear, so an empty walk cannot pass.
        var setup = Steered(h => RenderedWalkTests.ProbesForDump(h),
            [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter, WizardRig.Digit('1'),
             WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')]);
        Assert.Contains("gatto is set up.", string.Join("\n", setup.Frames), StringComparison.Ordinal);
    }

    //the step names as the strip paints them. only the TUI face draws this row, so it tells the two faces apart.
    private const string Strip = "machine \u00b7 engine \u00b7 model \u00b7 check \u00b7 done";

    //a mutating helper keeps the steering beside the leg, since another optional argument would pass through two signatures
    private static Probes Drop(Probes p)
    {
        p.ModelResult = new HubFetchResult(HubFetchOutcome.Dropped,
            "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf");
        return p;
    }

    //set both fingerprints, since the screen renders both. a fixture with one would draw an empty row and still pass.
    private static Probes Mismatch(Probes p)
    {
        p.ModelResult = new HubFetchResult(HubFetchOutcome.Mismatch,
            "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            "9f2c4e1a7b03d85f6e2a9c14b7d0f3e85a1c6b92d4f70e3a8c5b1d69f2e4a70c",
            "3a1f0d2e9c8b7a6f5e4d3c2b1a09f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7");
        return p;
    }

    //run with modelSegmentOnly, so the flow enters at StartAtModelSegment the way GattoApp does, which is why the strip shows three steps
    private WizardRig InSession(Func<string, Probes> build, ConsoleKeyInfo[] keys,
        int watchKeys = 0, Action<WizardRig>? made = null)
    {
        var home = NewHomeForDump();
        var rig = new WizardRig(80) { WatchKeyBudget = watchKeys };
        made?.Invoke(rig);
        SetupRunner.Run(new SetupFlow(build(home)), rig.TuiFace(keys), home, modelSegmentOnly: true);
        return rig;
    }

    //the parameter builds the probes it wants, since the probe record's properties are init-only, and a leg never changes one it was given
    private WizardRig Steered(Func<string, Probes> build, ConsoleKeyInfo[] keys,
        int watchKeys = 0)
    {
        var home = NewHomeForDump();
        var probes = build(home);
        var rig = new WizardRig(80) { WatchKeyBudget = watchKeys };
        SetupRunner.Run(new SetupFlow(probes), rig.TuiFace(keys), home);
        return rig;
    }
}
