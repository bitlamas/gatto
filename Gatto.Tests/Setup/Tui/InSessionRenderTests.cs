using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//each frame is diffed against its golden, sliced between the first rule and the last. the slice is the panel, the transcript above and the status line below
public class InSessionRenderTests
{
    private const string Incoming = "gemma-4-26B-A4B-it";
    private const string Serving = "qwen-qwen3.6-35b-a3b";

    private static WizardProbes Probes() => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        HeldBy = Serving,
    };

    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    //press Enter, a numbered screen's first Esc may arm a chord and repaint so LastPainted holds that frame
    private static IReadOnlyList<string> Painted(WizardScreen.Choice screen, int width)
    {
        var f = new TuiWizardSurface(new RecordingSurface { Width = width }, new Keys([Enter]),
            new Gatto.Terminal.Theme(new Gatto.Terminal.TermCaps(true, true)),
            "0.5.0", "1a2b3c4", () => 0);
        f.Choose(screen);
        return f.LastPainted;
    }

    private static WizardScreen.Choice CheckAsk()
    {
        var flow = new SetupFlow(Probes());
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return Assert.IsType<WizardScreen.Choice>(screen);
    }

    //the 80-column frame is its own file in the corpus (the fold is what it is drawn to show)
    [Theory]
    [InlineData("check-ask", 100)]
    [InlineData("check-ask-80", 80)]
    public void THE_IN_SESSION_CHECK_ASK_MATCHES_ITS_DRAWN_FRAME(string screen, int width)
    {
        var diff = Golden.Diff(Golden.Body(Golden.Panel("s10", screen, width)),
            Golden.Body(Painted(CheckAsk(), width)));
        Assert.True(diff is null, $"s10-{screen} at {width}:\n{diff}");
    }

    //the first look narrows the surface and answers no. the key comes only after the face writes more output, which is the repaint
    private sealed class NarrowThenKeyAfterRepaint(RecordingSurface surface, int width) : Gatto.Terminal.IKeySource
    {
        //the key is offered anyway after 500 unanswered looks, so a missing repaint fails instead of hanging
        private const int GiveUpAfter = 500;
        private int _writtenAtNarrow = -1;
        private int _looks;
        public bool KeyAvailable
        {
            get
            {
                if (_writtenAtNarrow >= 0) return surface.Output.Length > _writtenAtNarrow || ++_looks > GiveUpAfter;
                surface.Width = width;
                _writtenAtNarrow = surface.Output.Length;
                return false;
            }
        }
        public ConsoleKeyInfo ReadKey()
        {
            surface.Width = width;
            throw new InvalidOperationException("the walk ends here");
        }
    }

    //a window narrowed while the check ask waits for a key is repainted at the new width with no key pressed
    [Fact]
    public void A_NARROWED_WINDOW_IS_REPAINTED_AT_ITS_NEW_WIDTH_WITHOUT_A_KEY()
    {
        var surface = new RecordingSurface { Width = 100, Height = 40, ReportsResize = true };
        var face = new TuiWizardSurface(surface, new NarrowThenKeyAfterRepaint(surface, 80),
            new Gatto.Terminal.Theme(new Gatto.Terminal.TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0);
        try { face.Choose(CheckAsk()); }
        catch (InvalidOperationException) { }

        var frame = face.LastPainted;
        Assert.True(frame.Any(r => r.Contains("stops and", StringComparison.Ordinal)),
            "the check ask's long row is not in the frame:\n" + string.Join("\n", frame));
        Assert.True(frame.All(r => Gatto.Terminal.UnicodeWidth.Of(r) <= 80),
            "a row is wider than the narrowed window:\n" + string.Join("\n", frame));
    }

    //a typed ask waits on a key too, so it needs its own test
    [Fact]
    public void A_TYPED_ASK_IS_REPAINTED_AT_A_NARROWED_WIDTH_WITHOUT_A_KEY()
    {
        var surface = new RecordingSurface { Width = 100, Height = 40, ReportsResize = true };
        var face = new TuiWizardSurface(surface, new NarrowThenKeyAfterRepaint(surface, 80),
            new Gatto.Terminal.Theme(new Gatto.Terminal.TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0);
        try { face.Ask(new WizardScreen.Ask("t4.ask", "Which folder should gatto look in?", Validate: _ => null, Placeholder: "type a folder path")); }
        catch (InvalidOperationException) { }

        var frame = face.LastPainted;
        Assert.True(frame.Any(r => r.Contains("Which folder", StringComparison.Ordinal)),
            "the ask is not in the frame:\n" + string.Join("\n", frame));
        Assert.True(frame.All(r => Gatto.Terminal.UnicodeWidth.Of(r) <= 80),
            "a row is wider than the narrowed window:\n" + string.Join("\n", frame));
    }

    //no row may be wider than the width, checked at each of the five widths in the sweep
    [Fact]
    public void THE_CHECK_ASK_FITS_EVERY_WIDTH()
    {
        foreach (var width in new[] { 60, 80, 91, 100, 120 })
        {
            var frame = Painted(CheckAsk(), width);
            Assert.True(frame.All(r => Gatto.Terminal.UnicodeWidth.Of(r) <= width),
                $"at {width} a row is wider than the width:\n" + string.Join("\n", frame));
        }
    }

    //the done frames are not diffed here, the model row claims "verified" only for a walk that fetched it

    //the live check in a session says the model is on the server and the legend says added, using the measured purr clock
    [Fact]
    public void THE_LIVE_CHECK_MATCHES_ITS_DRAWN_FRAME()
    {
        var battery = Gatto.Roles.Audition.Battery.Tasks;
        var probes = Probes();
        probes.AuditionMoments.AddRange([
            Gatto.Roles.Audition.AuditionProgress.Started(battery[0], 1, battery.Count),
            Gatto.Roles.Audition.AuditionProgress.Done(battery[0], 1, battery.Count, true),
            Gatto.Roles.Audition.AuditionProgress.Started(battery[1], 2, battery.Count),
            Gatto.Roles.Audition.AuditionProgress.Done(battery[1], 2, battery.Count, true),
            Gatto.Roles.Audition.AuditionProgress.Started(battery[2], 3, battery.Count),
        ]);

        var flow = new SetupFlow(probes);
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.InSessionCheckKey,
            Assert.IsType<WizardScreen.Choice>(screen).Key);

        var running = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));
        Assert.Equal(SetupFlow.AuditionRunningKey, running.Key);

        var rows = WalkRender.Watching(running, 100, tick: null, nowMs: Purring(),
            check: new CheckTick(3, 5, "edit a file",
                ["1) run a command", "2) use two tools in order"])).Rows;

        var diff = Golden.Diff(Golden.Body(Golden.Panel("s10", "check", 100)), Golden.Body(rows));
        Assert.True(diff is null, "s10-check at 100:\n" + diff);
    }

    //0 on the first read and 72,800 after, a constant clock renders zero elapsed
    private static Func<long> Purring()
    {
        var opened = false;
        return () => { if (!opened) { opened = true; return 0; } return 72_800; };
    }
}
