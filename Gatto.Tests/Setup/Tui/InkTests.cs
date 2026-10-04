using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a golden proves the words and this proves the ink, a flat screen still matches its golden byte for byte
public class InkTests
{
    private sealed class NoKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => new('\0', ConsoleKey.Escape, false, false, false);
    }

    //every read here is an Escape, so the armed chord's wait is reached and the clock must answer at once
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static readonly Theme T = new(new TermCaps(true, true));

    private static (RecordingSurface S, TuiWizardSurface F) Face(int width = 100)
    {
        var s = new RecordingSurface { Width = width };
        return (s, new TuiWizardSurface(s, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready()));
    }

    //built from the theme's own Paint, so a palette change moves the expectation with it
    private static bool Wears(string screen, string text, RgbColor fg) =>
        screen.Contains(T.Paint(text, fg), StringComparison.Ordinal);

    [Fact]
    public void THE_WELCOMES_ROWS_CARRY_THE_INK_THE_MOCK_PROMISES()
    {
        var flow = new SetupFlow(new WizardProbes { Llama = null });
        var (s, f) = Face();
        f.Choose((WizardScreen.Choice)flow.Start());
        var screen = s.Text;

        //the footer's keys are bright and their verbs dim, which is what a user scans for
        Assert.True(Wears(screen, "Enter", Theme.Bright), "the footer's Enter is not bright");
        Assert.True(Wears(screen, " get started", Theme.Dim), "the footer's verb is not dim");
        Assert.True(Wears(screen, "Esc", Theme.Bright), "the footer's Esc is not bright");

        //the title is bright, the closing asides dim
        Assert.True(Wears(screen, "  Ready to set gatto up?", Theme.Bright), "the question is not bright");
        //match the whole run, a painted span is bracketed by its reset so a partial match never fires. the accented mark splits this row into three runs
        Assert.True(
            Wears(screen, "  Inside, Tab moves between the parts of a screen, ", Theme.Dim),
            "the closing aside is not dim");

        //the cat is painted accent, like the REPL's banner
        Assert.True(Wears(screen, "    /l、", Theme.Accent), "the cat is not accented");

        //the ❯ is accent inside a dim row, the highlight a single ink per row could not express
        Assert.True(Wears(screen, "❯", Theme.Accent), "the highlighted glyph is not accented");
    }


    //the model fetch's four screens

    private const string FetchRepo = "unsloth/gemma-4-26B-A4B-it-GGUF";
    private const string FetchFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf";
    private const long FetchGib = 1024L * 1024 * 1024;

    private static Gatto.Core.Acquire.HubQuant FetchQuant(string name, long bytes) =>
        new(name, bytes, new string('a', 64));

    private static ModelFetchOffer FetchOffer() =>
        new(FetchRepo, "gemma-4-26b-a4b-it", @"C:\Users\you\.gatto\weights\gemma-4-26b-a4b-it\",
            FetchQuant(FetchFile, (long)(16.9 * FetchGib)),
            FetchQuant("mmproj-F16.gguf", 851L * 1024 * 1024));

    private static WizardProbes FetchProbes(
        Gatto.Core.Acquire.HubFetchResult? result = null) =>
        new()
        {
            HubOffer = FetchOffer(),
            ModelResult = result ?? new(Gatto.Core.Acquire.HubFetchOutcome.Arrived),
            ModelTicks = result is null ? [] : [FetchTickValue],
            Rows =
            [
                new Gatto.Core.Acquire.ShelfRow(FetchRepo, "unsloth",
                    FetchQuant(FetchFile, (long)(16.9 * FetchGib)),
                    Gatto.Core.Models.FitRegime.FitsGpu, 32768, true, null, 100, false,
                    Projectors: [FetchQuant("mmproj-F16.gguf", 851L * 1024 * 1024)]),
            ],
        };

    private static readonly FetchTick FetchTickValue =
        new(FetchFile, 1, 1, (long)(7.9 * FetchGib), (long)(16.9 * FetchGib), 188_000);

    //drive the flow to the screen and paint it, returning what the terminal got
    private static string PaintFetch(WizardProbes probes, params string[] answers)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        WizardScreen screen = flow.Answer("0");
        foreach (var a in answers) screen = flow.Answer(a);

        var (s, f) = Face();
        f.Choose((WizardScreen.Choice)screen);
        return s.Text;
    }

    //the label is dim, the file name steps out of the grey and the size after it goes back to dim
    [Fact]
    public void THE_MODEL_CONSENTS_ROWS_WEAR_THEIR_INK()
    {
        var screen = PaintFetch(FetchProbes());

        //the dim run must stop at the file name, a lifted span has no escape of its own
        Assert.True(Wears(screen, "  fetch      ", Theme.Dim),
            "the fact label is not its own dim run, so the file name never stepped out of the aside");
        Assert.True(Wears(screen, "  Which model should gatto start with?", Theme.Bright),
            "the question is not bright");
        Assert.True(Wears(screen, "Enter", Theme.Bright), "the footer's Enter is not bright");
        Assert.True(Wears(screen, " choose", Theme.Dim), "the footer's verb is not dim");

        //without this negative a screen that painted everything bright would satisfy every positive above
        Assert.False(Wears(screen, "  fetch      ", Theme.Bright), "the fact label is bright, so the row is flat");
    }

    //the filled run is accent and the empty one is not, a difference no golden can see
    [Fact]
    public void THE_RUNNING_FETCHS_BAR_WEARS_ITS_INK()
    {
        var probes = FetchProbes(new(Gatto.Core.Acquire.HubFetchOutcome.Arrived));
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        var fetching = (WizardScreen.Choice)flow.Answer(SetupFlow.ModelFetchNow);

        var (s, f) = Face();
        //the watch must resolve, or the face polls for ever against a key source that never yields
        var polls = 0;
        f.Choose(fetching, watch: () => ++polls > 1, tick: () => FetchTickValue);
        var screen = s.Text;

        //14 of 30 cells at 7.9 of 16.9 GB, which is the filled run
        Assert.True(Wears(screen, new string('━', 14), Theme.Accent), "the bar's filled run is not accented");

        //the empty run must not be accent, or the bar reads as complete
        Assert.False(Wears(screen, new string('─', 16), Theme.Accent),
            "the bar's empty run is accented, so the bar cannot show progress");
    }

    //the tick is green and the sentence beside it is not, the glyph states the verdict and the words the fact
    [Fact]
    public void THE_ARRIVALS_TICK_IS_GREEN_AND_ITS_SENTENCE_IS_NOT()
    {
        var screen = PaintFetch(FetchProbes(), SetupFlow.ModelFetchNow, SetupFlow.Landed);

        Assert.True(Wears(screen, Gatto.Terminal.GlyphSet.Unicode.Ok, Theme.Ok), "the arrival's tick is not green");
        Assert.True(Wears(screen, "  fetched    ", Theme.Dim), "the fact label is not dim");
        Assert.False(Wears(screen, FetchFile, Theme.Ok), "the file name is green, so the row celebrates");
    }

    //the drop's mark is amber and never red, nothing was lost and one key continues
    [Fact]
    public void THE_FETCHS_FAILURE_MARKS_ARE_AMBER()
    {
        var dropped = PaintFetch(
            FetchProbes(new(Gatto.Core.Acquire.HubFetchOutcome.Dropped, FetchFile)),
            SetupFlow.ModelFetchNow, SetupFlow.Landed);

        Assert.True(Wears(dropped, Gatto.Terminal.GlyphSet.Unicode.Bad, Theme.Warn), "the drop's mark is not amber");
        Assert.False(Wears(dropped, Gatto.Terminal.GlyphSet.Unicode.Bad, Theme.Err), "the drop's mark is red, which over-states it");
        Assert.True(Wears(dropped, "  kept       ", Theme.Dim), "the kept label is not dim");
    }


    //the browser watch's four screens

    private const string WatchRepo = "unsloth/gemma-4-26B-A4B-it-GGUF";
    private const string WatchFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf";
    private const string WatchInto = @"C:\Users\you\.gatto\weights\gemma-4-26b-a4b-it\";
    private const string WatchDownloads = @"C:\Users\you\Downloads\";

    private static Gatto.Core.Acquire.HubQuant WatchQuant(string sha) =>
        new(WatchFile, 16L * 1024 * 1024 * 1024, sha);

    private static WizardProbes WatchProbes(string? sha = null, Func<string, string?>? hash = null) =>
        new()
        {
            Rows =
            [
                new Gatto.Core.Acquire.ShelfRow(WatchRepo, "unsloth", WatchQuant(sha!),
                    Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, null, 100, false),
            ],
            Roots = [WatchInto, WatchDownloads],
            Hash = hash,
        };

    //drive to the browser watch without a fingerprint, where it is the first screen, and paint it
    private static string PaintWatch(WizardProbes probes, bool land = false)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        WizardScreen screen = flow.Answer("0");
        if (land)
        {
            probes.Found = [new Gatto.Core.Acquire.FoundModel(
                WatchDownloads + WatchFile, 16L * 1024 * 1024 * 1024, null)];
            flow.PollWatch();
            screen = flow.Answer(SetupFlow.Landed);
        }

        var (s, f) = Face();
        f.Choose((WizardScreen.Choice)screen, watch: () => true, tick: () => null);
        return s.Text;
    }

    //the row's tail is dim (checking every few seconds, nothing yet), the first right-aligned note on a body row
    [Fact]
    public void THE_WATCHS_ROWS_WEAR_THEIR_INK()
    {
        var screen = PaintWatch(WatchProbes());

        Assert.True(Wears(screen, "  download   ", Theme.Dim),
            "the fact label is not its own dim run, so the file name never stepped out of the aside");
        Assert.True(Wears(screen, "checking every few seconds, nothing yet", Theme.Dim),
            "the row's tail is not dim");
        Assert.True(Wears(screen, "  Which model should gatto start with?", Theme.Bright),
            "the question is not bright");

        //a flat screen satisfies every positive above, so this negative is the one that can fail
        Assert.False(Wears(screen, "  download   ", Theme.Bright),
            "the fact label is bright, so the row is flat");
    }

    //the wrong file's mark is amber, the file is the user's and nothing was deleted
    [Fact]
    public void THE_WRONG_FILES_MARK_IS_AMBER()
    {
        var screen = PaintWatch(
            WatchProbes(sha: new string('a', 64), hash: _ => new string('b', 64)), land: true);

        Assert.True(Wears(screen, Gatto.Terminal.GlyphSet.Unicode.Bad, Theme.Warn), "the wrong file's mark is not amber");
        Assert.False(Wears(screen, Gatto.Terminal.GlyphSet.Unicode.Bad, Theme.Err),
            "the wrong file's mark is red, which over-states a file gatto did not write");
        //match the whole row here, nothing steps out so the dim run is the entire line and a prefix never fires
        Assert.True(
            Wears(screen, "  expected   aaaaaa…aaaa · the file's fingerprint on Hugging Face", Theme.Dim),
            "the fingerprint row is not dim");
    }


    //the marks take the row's dim ink, the row model has no verdict colour so the shape must distinguish them
    [Fact]
    public void THE_PARTIAL_ARMS_MARKS_TAKE_THE_ROWS_INK()
    {
        var probes = new WizardProbes
        {
            Rows =
            [
                new Gatto.Core.Acquire.ShelfRow(WatchRepo, "unsloth", WatchQuant(null!),
                    Gatto.Core.Models.FitRegime.FitsGpu, 32768, true, null, 100, false,
                    Projectors: [new Gatto.Core.Acquire.HubQuant("mmproj-F16.gguf", 851L << 20, null)]),
            ],
            Roots = [WatchInto, WatchDownloads],
        };
        var screen = PaintWatch(probes, land: true);

        //the row is dim up to the name, which lifts, so each mark sits in the dim segment before it
        Assert.True(Wears(screen, $"  download   {Gatto.Terminal.GlyphSet.Unicode.Ok} ", Theme.Dim),
            "the arrived file's mark is not in the row's dim run");
        Assert.True(Wears(screen, ", here", Theme.Dim), "the row's tail after the name is not dim");
        Assert.False(Wears(screen, Gatto.Terminal.GlyphSet.Unicode.Ok, Theme.Ok),
            "the in-value tick is green, which the row model cannot express — if this is now wanted, "
            + "it needs a mechanism rather than a coincidence");
    }

    //the tick is green and the file name beside it is not, a green line would be gatto celebrating
    [Fact]
    public void THE_WATCHS_ARRIVAL_TICK_IS_GREEN()
    {
        var probes = new WizardProbes
        {
            Rows =
            [
                new Gatto.Core.Acquire.ShelfRow(WatchRepo, "unsloth", WatchQuant(new string('a', 64)),
                    Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, null, 100, false),
            ],
            Roots = [WatchInto, WatchDownloads],
            Hash = _ => new string('a', 64),
            Move = _ => new MoveOffer(WatchInto, false, 16L << 30, null),
        };
        var screen = PaintWatch(probes, land: true);

        Assert.True(Wears(screen, Gatto.Terminal.GlyphSet.Unicode.Ok, Theme.Ok), "the arrival's tick is not green");
        Assert.False(Wears(screen, WatchFile, Theme.Ok),
            "the file name is green, so the row celebrates rather than reporting");
    }

    //the count steps out of the grey and the done row stays one dim run, finished tasks being named rather than marked
    [Fact]
    public void THE_LIVE_CHECKS_COUNT_STEPS_OUT_AND_ITS_FINISHED_TASKS_DO_NOT()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        });
        flow.StartAtModelSegment();
        WizardScreen offer = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) offer = flow.ResumeAfterWrites(null);
        var live = (WizardScreen.Choice)flow.Answer(SetupFlow.Yes);

        var (s, f) = Face();
        var polls = 0;
        f.Choose(live, watch: () => ++polls > 1,
            check: () => new CheckTick(3, 5, "edit a file", ["1) run a command", "2) use two tools in order"]));
        var screen = s.Text;

        Assert.True(Wears(screen, "  running       ", Theme.Dim),
            "the running label is not its own dim run, so the count never stepped out of the aside");
        Assert.False(Wears(screen, "  running       ", Theme.Bright),
            "the running label is bright, so the row is flat");

        //the whole row is one dim run, nothing in the finished list steps forward
        Assert.True(Wears(screen, "  done          1) run a command · 2) use two tools in order", Theme.Dim),
            "the done row is not one dim run, so a finished task is being emphasised");
    }

    //the labels are dim, the quant steps out and the speed figure alone takes the accent
    [Fact]
    public void THE_PASSES_STAMP_WEARS_THREE_INKS()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            Audition = new Gatto.Cli.Setup.AuditionCheck(
                Gatto.Cli.Setup.AuditionOutcome.Passed,
                new Gatto.Cli.Setup.AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        });
        flow.StartAtModelSegment();
        WizardScreen at = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) at = flow.ResumeAfterWrites(null);
        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        var passed = (WizardScreen.Choice)flow.Answer(SetupFlow.Landed);

        var (s, f) = Face();
        f.Choose(passed);
        var screen = s.Text;

        Assert.True(Wears(screen, "  quant         ", Theme.Dim),
            "the quant label is not its own dim run, so the value never stepped out of the aside");
        Assert.True(Wears(screen, "~31.4 tok/s", Theme.Accent), "the speed figure is not accented");
        Assert.False(Wears(screen, "~31.4 tok/s, usable", Theme.Accent),
            "the whole speed row is accented, so the band is shouting rather than the figure pointing");
        Assert.True(Wears(screen, Gatto.Terminal.GlyphSet.Unicode.Ok, Theme.Ok), "the pass tick is not green");
    }

    //a done section and its tick are green, the current one is accent and the sections ahead are dim
    [Fact]
    public void THE_STRIP_INKS_DONE_GREEN_CURRENT_ACCENT_AND_AHEAD_DIM()
    {
        var (s, f) = Face();
        f.Show(new WizardScreen.Info("k", [new WizardRow("body")])
        {
            Strip = WalkSection.For("model.search", SetupPath.Llama),
        });

        Assert.True(Wears(s.Text, "machine", Theme.Ok), "a done section is not green");
        Assert.True(Wears(s.Text, " " + GlyphSet.Unicode.Ok, Theme.Ok), "a done section's tick is not green");
        Assert.True(Wears(s.Text, "model", Theme.Accent), "the current section is not accent");
        Assert.False(Wears(s.Text, "model", Theme.Bright), "the current section is still bright");
        Assert.True(Wears(s.Text, "check", Theme.Dim), "a section ahead is not dim");
        Assert.True(Wears(s.Text, "❯ ", Theme.Accent), "the strip's cursor is not accented");
    }

    //tab once then Escape for every later read, so one run paints the door unfocused, focused and unfocused again
    private sealed class TabThenEscape : IKeySource
    {
        private bool _tabbed;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            if (_tabbed) return new('\0', ConsoleKey.Escape, false, false, false);
            _tabbed = true;
            return new('\t', ConsoleKey.Tab, false, false, false);
        }
    }

    //the search door is accent only while it has the keys, dim otherwise like any region without them
    [Fact]
    public void THE_DOOR_IS_ACCENT_ONLY_WHILE_IT_HAS_THE_KEYS()
    {
        var s = new RecordingSurface { Width = 100 };
        var f = new TuiWizardSurface(s, new TabThenEscape(), T, "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        f.Choose(new WizardScreen.Choice("ink.door", "Pick one",
            [new ChoiceOption("0", "one"), new ChoiceOption("1", "two")], Door: "search models…"));

        var g = GlyphSet.Unicode;
        var unfocused = g.Prompt + " search models…";
        var focused = g.Prompt + " " + g.Bar + " search models…";
        Assert.True(Wears(s.Text, unfocused, Theme.Dim), "the door without the keys is not dim");
        Assert.False(Wears(s.Text, unfocused, Theme.Accent), "the door without the keys is accent");
        Assert.True(Wears(s.Text, focused, Theme.Accent), "the door with the keys is not accent");
    }

    //keys from a script, then Escape for every later read
    private sealed class ScriptThenEscape(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() =>
            _q.Count > 0 ? _q.Dequeue() : new('\0', ConsoleKey.Escape, false, false, false);
    }

    //a door that holds a draft is dim too once the keys leave it
    [Fact]
    public void A_DRAFT_IS_DIM_ONCE_THE_KEYS_LEAVE_THE_DOOR()
    {
        var s = new RecordingSurface { Width = 100 };
        var tab = new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);
        var f = new TuiWizardSurface(s,
            new ScriptThenEscape(tab, new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false), tab),
            T, "0.5.0", "1a2b3c4", () => 0, clock: _ => new Ready());
        f.Choose(new WizardScreen.Choice("ink.draft", "Pick one",
            [new ChoiceOption("0", "one"), new ChoiceOption("1", "two")], Door: "search models…"));

        var drafted = GlyphSet.Unicode.Prompt + " q";
        Assert.True(Wears(s.Text, drafted, Theme.Dim), "the draft without the keys is not dim");
        Assert.False(Wears(s.Text, drafted, Theme.Accent), "the draft without the keys is accent");
    }

    //the label and the why-clause are dim and the GB figures accent inside them, which no golden can see
    [Fact]
    public void THE_MACHINE_FACTS_WEAR_THEIR_INK()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = null,
            Snapshot = new Gatto.Core.Hardware.HardwareSnapshot(128UL << 30, 32UL << 30,
                Gatto.Core.Hardware.GpuKind.Integrated, 96UL << 30),
            Names = new HardwareNames("AMD Ryzen AI Max+ 395", "AMD Radeon 8060S"),
        });
        var (s, f) = Face();
        flow.Start();
        f.Choose((WizardScreen.Choice)flow.Answer(SetupFlow.WelcomeGo));
        var screen = s.Text;

        Assert.True(Wears(screen, "  models        ", Theme.Dim), "the fact label is not dim");
        Assert.True(Wears(screen, "88 GB", Theme.Accent), "the GB figure is not accented");
        Assert.True(Wears(screen, "  What can this machine run?", Theme.Bright), "the question is not bright");

        //the figure is accent inside an otherwise dim row, which is why a row is a list of runs
        Assert.True(Wears(screen, ", gatto keeps ", Theme.Dim), "the why-clause around the figure is not dim");
    }

    //the tick is green and the sentence beside it plain, the mark being the news no golden can see
    [Fact]
    public void THE_FOUND_SCREENS_ROWS_CARRY_THE_INK_THE_MOCK_PROMISES()
    {
        const string exe = @"C:\llama\llama-b11071-bin-win-vulkan-x64\llama-server.exe";
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = exe,
            Verify = _ => new Gatto.Core.Tools.ProbeResult(
                Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease),
        });
        var (s, f) = Face();
        f.Choose((WizardScreen.Choice)flow.StartPastOpening());
        var screen = s.Text;

        //the mark is green as its own run, which no golden can tell from grey
        Assert.True(Wears(screen, "✓", Theme.Ok), "the found screen's tick is not green");
        //the sentence beside the tick must not be green, otherwise the tick assertion above would pass on a row painted all green
        Assert.False(Wears(screen, " llama-server.exe is already on this machine.", Theme.Ok),
            "the sentence beside the tick is painted like the tick");

        Assert.True(Wears(screen, "  Which engine build fits this machine?", Theme.Bright),
            "the question is not bright");
        //the three facts are secondary to the question, so they read as one block
        Assert.True(Wears(screen, "  found        " + exe, Theme.Dim), "the found row is not dim");
        Assert.True(Wears(screen, "1. Use it", Theme.Bright), "the chosen option is not bright");
        //the hint under the door stays dim, and it comes from DoorHints so a copy change doesn't fail a colour test
        Assert.True(Wears(screen, DoorHints.AnywhereOf(GlyphSet.Unicode), Theme.Dim),
            "the door's hint is not dim");
    }

    //the older arm's line explaining the flip is the screen's only deliberately secondary prose, so it must not read as body text
    [Fact]
    public void THE_OLDER_ARMS_ONE_LINE_IS_DIM_AND_THE_OPTIONS_ABOVE_IT_ARE_NOT()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-b9848-bin-win-vulkan-x64\llama-server.exe",
            Verify = _ => new Gatto.Core.Tools.ProbeResult(
                Gatto.Core.Tools.ProbeShape.ClassicServer, "d", Build: "b9848"),
        });
        var (s, f) = Face();
        f.Choose((WizardScreen.Choice)flow.StartPastOpening());
        var screen = s.Text;

        Assert.True(
            Wears(screen,
                "  Newer models sometimes need a newer engine, the shelf's \"runs on this machine\" answers assume",
                Theme.Dim),
            "the older arm's explanation is not dim");
        //the default option must be bright, or the aside above it stops reading as secondary
        Assert.True(Wears(screen, "1. Fetch b11071, the tested release", Theme.Bright),
            "the older arm's default option is not bright");
    }

    //a body row must not be found in accent or bright, or a broken matcher that says no to everything passes every check above
    [Fact]
    public void THE_MATCHER_DISCRIMINATES_OR_EVERY_ASSERTION_ABOVE_IS_VACUOUS()
    {
        var (s, f) = Face();
        f.Show(new WizardScreen.Info("k", [new WizardRow("an ordinary body row")]));

        Assert.Contains("an ordinary body row", s.Text, StringComparison.Ordinal);
        Assert.False(Wears(s.Text, "an ordinary body row", Theme.Accent),
            "the matcher claims a plain row is accented, so it cannot tell ink apart at all");
        Assert.False(Wears(s.Text, "an ordinary body row", Theme.Bright),
            "the matcher claims a plain row is bright");
    }

    //the closing line must render even with no rows above it, the only case where dropping it is visible
    [Fact]
    public void A_TERMINAL_WITH_NO_ROWS_STILL_SAYS_ITS_CLOSING_LINE()
    {
        var (_, f) = Face();
        f.End(new WizardScreen.Terminal("left", [], new WizardRow("Run gatto setup again whenever you like."), Success: true));

        Assert.Contains(f.LastPainted, r => r.Contains("Run gatto setup again", StringComparison.Ordinal));
    }

    //assert on the epilogue, a guard on the frame model would pass while the session prints something else
    [Fact]
    public void THE_CLOSING_LINE_REACHES_SCROLLBACK_ON_THE_WAY_OUT()
    {
        var (_, f) = Face();
        var alt = new AltScreen(new RecordingSurface { Width = 100 }, title: null);
        var epilogue = new StringWriter();

        WizardSession.Run(
            alt,
            () => { f.End(new WizardScreen.Terminal("left", [], new WizardRow("Run gatto setup again whenever you like."), Success: true)); return 0; },
            () => f.LastPainted, epilogue, registerHooks: false);

        Assert.Contains("Run gatto setup again", epilogue.ToString(), StringComparison.Ordinal);
    }

    //the file name steps out to plain ink, the accent marks a value to reproduce and this one is only named
    [Fact]
    public void THE_FETCH_ROWS_FILE_NAME_STEPS_OUT_OF_THE_ASIDE()
    {
        const string Zip = "llama-b11071-bin-win-vulkan-x64.zip";
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = null,
            Asset = new(Zip, null, "an AMD discrete card", "Vulkan"),
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\",
                new EnginePair(
                    new EngineAsset(Zip, "https://example.invalid/z", new string('a', 64),
                        214L * 1024 * 1024),
                    null)),
        });
        var (s, f) = Face();
        f.Choose((WizardScreen.Choice)flow.StartPastOpening());
        var screen = s.Text;

        Assert.Contains(
            T.Paint("  fetch" + new string(' ', 8), Theme.Dim) + Zip + T.Paint(" \u00b7 214 MB", Theme.Dim),
            screen, StringComparison.Ordinal);

        //the name must not be dim or accent, otherwise the split above proves nothing
        Assert.False(Wears(screen, Zip, Theme.Dim), "the file name was painted dim");
        Assert.False(Wears(screen, Zip, Theme.Accent), "the file name was painted accent");

        //the steering sentence's two variables keep the accent, so both inks are live on one screen
        Assert.True(Wears(screen, "an AMD discrete card", Theme.Accent), "the hardware fact is not accented");
        Assert.True(Wears(screen, "Vulkan", Theme.Accent), "the build name is not accented");

        //llama.cpp lifts out of the intro paragraph, it's the subject of that sentence rather than a value to copy
        Assert.False(Wears(screen, "llama.cpp", Theme.Dim), "llama.cpp was painted dim");
        Assert.False(Wears(screen, "llama.cpp", Theme.Accent), "llama.cpp was painted accent");
    }

    //the goldens diff LastPainted, so escape bytes there fail every corpus test and a row clamped by bytes loses cells at the edge
    [Fact]
    public void THE_PLAIN_RECORD_CARRIES_NO_ESCAPE_BYTES()
    {
        var flow = new SetupFlow(new WizardProbes { Llama = null });
        var (_, f) = Face();
        f.Choose((WizardScreen.Choice)flow.Start());

        Assert.All(f.LastPainted, r => Assert.DoesNotContain('\x1b', r));
    }
}
