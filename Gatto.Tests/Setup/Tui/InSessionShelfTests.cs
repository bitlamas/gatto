using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the shelf's rows must be byte-identical on both flows, compared through the real face (two mocks test only the mocks)
public class InSessionShelfTests
{
    //the in-session strip is three sections and no aside, read from the production composer (a fixture that supplies it cannot fail about the flow)
    private static readonly IReadOnlyList<StripSection> InSessionStrip =
        WalkSection.For(SetupFlow.SearchKey, SetupPath.Llama, inSession: true);

    //the key here is test-only, so this strip is the all-pending one production never paints (three fixtures use it, the flow emits it nowhere)

    //the key that answers a numbered screen, with its NUL written as an escape (a raw control byte is invisible to a reviewer)
    private static readonly ConsoleKeyInfo Enter = new('\0', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo Down = new('\0', ConsoleKey.DownArrow, false, false, false);

    private static WizardScreen.Choice InSessionScreen(ShelfView shelf) =>
        new(SetupFlow.SearchKey, SetupFlow.ModelTitleFor(inSession: true),
            [.. shelf.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId))],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode))
        { Strip = InSessionStrip };

    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    //a watching screen needs a clock, or the run hangs instead of failing
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    private static (TuiWizardSurface F, Func<IReadOnlyList<string>> Painted) Face(
        bool watching, params ConsoleKeyInfo[] keys)
    {
        var s = new Gatto.Tests.Fakes.RecordingSurface { Width = 100 };
        //the clock answers ready for every screen, since an armed chord's bounded wait needs it and the frozen nowMs would spin forever
        var f = new TuiWizardSurface(s, new Keys(keys), new Gatto.Terminal.Theme(
                new Gatto.Terminal.TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        return (f, () => f.LastPainted);
    }

    //the shelf list's Esc arms the chord, two presses with the first painting the sentence
    [Fact]
    public void ON_THE_SETUP_ROAD_THE_CHORD_STILL_ARMS()
    {
        var (f, painted) = Face(watching: false, Esc, Esc);

        Assert.Null(f.Choose(ShelfTests.Screen(ShelfTests.Unified96())));
        Assert.Contains(painted(), r => r.Contains("Esc again to leave", StringComparison.Ordinal));
    }

    //a watching screen that is not keys-only keeps its chord, a press costs bytes and minutes (a keys-only watcher never reaches this branch)
    [Fact]
    public void AN_IN_SESSION_WATCHING_SCREEN_STILL_ARMS()
    {
        var (f, painted) = Face(watching: true, Esc, Esc);
        var screen = new WizardScreen.Choice("model.fetching", "Downloading…",
            [new ChoiceOption("stop", "stop the download"), new ChoiceOption("wait", "keep waiting")],
            Watching: true, Door: "or paste a different model…")
            { Strip = InSessionStrip };

        f.Choose(screen, watch: () => false);

        Assert.Contains(painted(), r => r.Contains("Esc again", StringComparison.Ordinal));
    }

    //slice by the chips and door rows rather than by index, the two frames differ in how many rows sit above the table
    private static string[] TableRegion(IReadOnlyList<string> frame)
    {
        var chips = Array.FindIndex([.. frame], r => r.Contains("gemma · qwen", StringComparison.Ordinal));
        var door = Array.FindIndex([.. frame], r => r.StartsWith("❯ ", StringComparison.Ordinal)
                                                    && r.Contains("search models", StringComparison.Ordinal));
        Assert.True(chips >= 0, "the frame has no chips row");
        Assert.True(door > chips, "the frame has no door row under its chips row");
        return [.. frame.Skip(chips).Take(door - chips + 1)];
    }

    //a mock a test renders follows the product copy, and a mock nothing renders stays as the record


    //the setup flow's empty shelf, matching the shape of s5-unified-96-local-empty-100
    private static WizardScreen.Choice EmptyLocalSetupScreen() =>
        new(SetupFlow.SearchKey, "Which model should gatto start with?", [],
            Shelf: new ShelfView([], CuratedPublisher: null, MachineShape.UnifiedWithShare,
                Source: ShelfSource.Local,
                Empty: ["", Gatto.Repl.Cats.EmptyOf(glyphs: GlyphSet.Unicode), "", "no models on this machine yet, gatto looked in:"]),
            Door: "type a folder path…")
        { Strip = ShelfTests.Strip };

    //an empty shelf with a door offers only Esc, the ring is the door alone so Tab and the arrows go nowhere
    [Fact]
    public void AN_EMPTY_SHELF_COLLAPSES_TO_THE_KEY_THAT_WORKS()
    {
        var frame = WalkRender.SettledFrame(EmptyLocalSetupScreen(), 100).Rows;

        Assert.Equal("  Esc leave", frame.Last(r => r.Trim().Length > 0));
    }

    //the ring holds one region on an empty shelf, so a press cannot move the keys off it
    [Fact]
    public void AND_TAB_MOVES_THE_KEYS_NOWHERE_ON_ONE()
    {
        var ring = new FocusRing(Shelf.Regions(EmptyLocalSetupScreen().Shelf!, 100, 0, hasDoor: true));
        var before = ring.Current;

        ring.Next();

        Assert.Equal(before, ring.Current);
        Assert.False(ring.HasSecondArea);
    }

    //a shelf with rows still has a ring, or the empty-shelf collapse above would pass as no ring anywhere
    [Fact]
    public void A_SHELF_WITH_ROWS_HAS_NO_STRIP_IN_ITS_RING()
    {
        var ring = Shelf.Regions(ShelfTests.Unified96(), 100, 0, hasDoor: true);

        //the chips lead this ring, and the strip is in no ring (the chips are the first zone a Tab reaches)
        Assert.DoesNotContain(Region.Strip, ring);
        Assert.Equal(Region.Families, ring[0]);
        Assert.Contains(Region.List, ring);
    }

    //find the keys row by Esc, the one key that never sheds (Tab next area is dropped at 100 columns)
    private static int KeysRowIn(IReadOnlyList<string> frame) =>
        Array.FindLastIndex([.. frame], r => r.Contains("Esc ", StringComparison.Ordinal));

    //the panel slice lives in Golden.Body alone, the s10 mocks draw a live conversation around the wizard

    //diff the whole panel rather than named rows, and drive the cursor to the frame's own row so the pane matches too
    [Fact]
    public void THE_COMPOSED_PANEL_MATCHES_ITS_DRAWN_FRAME()
    {
        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s10-shelf-100.txt"));

        var rendered = WalkRender.Choice(InSessionScreen(ShelfTests.Unified96()), 100,
            script: [Down, Enter]).Rows;

        Assert.Equal(Golden.Body(drawn), Golden.Body(rendered));
    }

    //the discrete twin differs from the frame above only in the machine, a discrete shelf has one more column
    [Fact]
    public void THE_DISCRETE_PANEL_MATCHES_ITS_OWN_DRAWN_FRAME()
    {
        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s10-shelf-discrete-100.txt"));

        var view = ShelfTests.Unified96() with { Shape = MachineShape.Discrete };
        var rendered = WalkRender.Choice(InSessionScreen(view), 100, script: [Down, Enter]).Rows;

        Assert.Equal(Golden.Body(drawn), Golden.Body(rendered));
    }

    //the discrete table has the runs column and the unified one has none, so the twin is not a copy of the same frame
    [Fact]
    public void THE_TWO_MACHINE_SHAPES_DRAW_DIFFERENT_IN_SESSION_TABLES()
    {
        var unified = File.ReadAllLines(Path.Combine(Golden.Dir, "s10-shelf-100.txt"));
        var discrete = File.ReadAllLines(Path.Combine(Golden.Dir, "s10-shelf-discrete-100.txt"));

        Assert.Contains(discrete, r => r.Contains("runs", StringComparison.Ordinal));
        Assert.DoesNotContain(unified, r => r.Contains("runs", StringComparison.Ordinal));
    }

    //the head comes from the painter, so pin it on a screen with no shelf (a head keying off the shelf would pass elsewhere)
    [Fact]
    public void AND_A_SCREEN_THAT_IS_NOT_A_SHELF_DRAWS_THE_SAME_HEAD()
    {
        var screen = new WizardScreen.Choice("model.fetching", "Downloading…",
            [new ChoiceOption("stop", "stop the download"), new ChoiceOption("wait", "keep waiting")],
            Door: "or paste a different model…")
            { Strip = InSessionStrip };

        var frame = WalkRender.Choice(screen, 100, script: [Enter]).Rows;

        Assert.Contains("gatto setup", frame[0], StringComparison.Ordinal);
        Assert.Equal("  ❯ model · check · done", frame[1]);
        Assert.StartsWith("─", frame[2], StringComparison.Ordinal);
    }

    //the same head on the setup flow, whose five-section strip would part the two if a head read the strip
    [Fact]
    public void THE_SETUP_ROAD_STILL_DRAWS_ITS_BANNER_AND_RULE()
    {
        var frame = WalkRender.Choice(ShelfTests.Screen(ShelfTests.Unified96()), 100, script: [Enter]).Rows;

        Assert.Contains("gatto setup", frame[0], StringComparison.Ordinal);
        Assert.StartsWith("─", frame[2], StringComparison.Ordinal);
    }

    //the same fixture on both flows must give a byte-identical table, compared from the product's renders rather than two goldens
    [Fact]
    public void THE_SHELF_TABLE_IS_THE_SAME_ROWS_ON_BOTH_ROADS()
    {
        var shelf = ShelfTests.Unified96();

        var setup = WalkRender.Choice(ShelfTests.Screen(shelf), 100).Rows;
        var session = WalkRender.Choice(InSessionScreen(shelf), 100).Rows;

        Assert.Equal(TableRegion(setup), TableRegion(session));
    }

    //the two flows differ outside the table, with "start with" on setup and "add" in a session
    [Fact]
    public void AND_THE_FRAMES_AROUND_THAT_TABLE_DIFFER()
    {
        var shelf = ShelfTests.Unified96();

        var setup = WalkRender.Choice(ShelfTests.Screen(shelf), 100).Rows;
        var session = WalkRender.Choice(InSessionScreen(shelf), 100).Rows;

        Assert.NotEqual<IEnumerable<string>>(setup, session);
        Assert.Contains(setup, r => r.Contains("Which model should gatto start with?", StringComparison.Ordinal));
        Assert.Contains(session, r => r.Contains("Which model should gatto add?", StringComparison.Ordinal));
    }

    //the in-session strip is three sections with no aside, the drawn strip row being the oracle
    [Fact]
    public void THE_IN_SESSION_STRIP_HAS_NO_ASIDE()
    {
        var frame = WalkRender.Choice(InSessionScreen(ShelfTests.Unified96()), 100).Rows;

        var strip = frame.First(r => r.Contains("model", StringComparison.Ordinal)
                                     && r.Contains("check", StringComparison.Ordinal)
                                     && r.Contains("done", StringComparison.Ordinal));
        Assert.Equal("  ❯ model · check · done", strip);
    }

    //the word is leave on every flow, and the add flow's keys row is where that is asserted

    //at 80 the pane goes under the table, the fixture and script unchanged so the diff is about the fold
    [Fact]
    public void THE_COMPOSED_PANEL_FOLDS_UNDER_THE_TABLE_AT_80()
    {
        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s10-shelf-80-80.txt"));

        var rendered = WalkRender.Choice(InSessionScreen(ShelfTests.Unified96()), 80,
            script: [Down, Enter]).Rows;

        Assert.Equal(Golden.Body(drawn), Golden.Body(rendered));
    }

    //compare the two widths rather than one, a guard that read only 80 would pass on a face that always folds
    [Fact]
    public void AND_AT_100_THE_SAME_SHELF_PUTS_THAT_PANE_BESIDE()
    {
        var shelf = ShelfTests.Unified96();

        var wide = WalkRender.Choice(InSessionScreen(shelf), 100, script: [Down, Enter]).Rows;
        var narrow = WalkRender.Choice(InSessionScreen(shelf), 80, script: [Down, Enter]).Rows;

        Assert.Contains(wide, r => r.Contains("│", StringComparison.Ordinal));
        Assert.DoesNotContain(narrow, r => r.Contains("│", StringComparison.Ordinal));
    }

    //a resize repaint goes through PaintCore, so that claim lives with the row diff in FrameFitsTests and CursorTests

    //the out-of-reach pair: one shelf, two frames, differing only in the notice

    //five local models, and no params column because LocalShelf.Row leaves Params null on every local row
    private static readonly (string Name, double Gb, string Kind, bool Vision, long Ctx,
        string Files, string Tail, HaveMark Have)[] LocalFive =
    [
        ("minimax-m2.7-REAP-139B-A10B-Q4_K_S", 82.3, "MoE A10B", false, 196608,
            "3 of 3 files here", @"minimaxai\minimax-m2.7-REAP", HaveMark.None),
        ("Llama-3.3-70B-Instruct-IQ2_XS", 21.1, "dense", false, 131072,
            "1 file", @"lmstudio-community\Llama-3.3-70B", HaveMark.None),
        ("Qwen3-30B-A3B-Instruct-2507-UD-Q4_K_M", 18.6, "MoE A3B", false, 262144,
            "1 file", @"unsloth\Qwen3-30B-A3B-2507", HaveMark.None),
        ("gemma-4-26B-A4B-it-UD-Q4_K_M", 16.9, "MoE A4B", true, 262144,
            "1 file + mmproj", @"unsloth\gemma-4-26B-A4B-it", HaveMark.Loaded),
        ("gemma-3-12b-it-Q4_K_M", 7.3, "dense", true, 131072,
            "1 file + mmproj", @"google\gemma-3-12b-it", HaveMark.Added),
    ];

    private const string LocalDir = @"C:\Users\you\.lmstudio\models\";

    private static long Gib(double gb) => (long)Math.Round(gb * 1024 * 1024 * 1024);

    private static ShelfView LocalShelfView()
    {
        var rows = LocalFive.Select(m => new ShelfRow(
            m.Name, "", new HubQuant(m.Name + ".gguf", Gib(m.Gb), null),
            FitRegime.FitsGpu, m.Ctx, m.Vision, Badge: null, Downloads: 0,
            Gated: false, Params: null,
            Structure: m.Kind.StartsWith("MoE", StringComparison.Ordinal) ? m.Kind : "dense")).ToArray();

        //the marks arrive through ModelFacts.Have, so this test only proves they reach the frame (the glyph and its column cost belong to HaveMarkRenderTests)
        var facts = LocalFive.Select((m, i) => new ModelFacts(
            Structure: rows[i].Structure, LocalPath: LocalDir + m.Tail + @"\",
            FilesHere: m.Files, Have: m.Have,
            Files: [new PaneFile(QuantToken.Of(m.Name + ".gguf") ?? "Q4_K_M",
                Gib(m.Gb), FitRegime.FitsGpu)])).ToArray();

        return new ShelfView(rows, CuratedPublisher: null, MachineShape.UnifiedWithShare,
            Families: ["gemma", "qwen", "deepseek", "glm", "mistral", "all"], Family: "all",
            Total: LocalFive.Length, Facts: facts,
            Source: ShelfSource.Local, Folder: LocalDir);
    }

    private static WizardScreen.Choice LocalScreen(bool notice)
    {
        var shelf = LocalShelfView();
        return new WizardScreen.Choice(SetupFlow.SearchKey, SetupFlow.ModelTitleFor(inSession: true),
            [.. shelf.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId))],
            //the notice goes in the body above the chips, the slot the discovery line uses on the Hub shelf
            BodyRows: notice ? [new WizardRow(SetupFlow.OutOfReach, Tone: RowTone.Aside)] : [],
            Shelf: shelf,
            Door: SetupFlow.LocalShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode))
        { Strip = InSessionStrip };
    }

    //one fixture family for both local frames, the out-of-reach notice set on the first alone. the params column stays empty, no local shelf can produce one
    [Theory]
    [InlineData(true, "s10-local-landing-100.txt")]
    [InlineData(false, "s10-local-have-100.txt")]
    public void THE_LOCAL_SHELF_MATCHES_ITS_DRAWN_FRAME(bool notice, string golden)
    {
        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, golden));

        var rendered = WalkRender.Choice(LocalScreen(notice), 100,
            script: [Down, Down, Down, Enter]).Rows;

        Assert.Equal(Golden.Body(drawn), Golden.Body(rendered));
    }

    //the two frames must differ only by the notice, or a drift in another row passes both tests
    [Fact]
    public void AND_THE_TWO_LOCAL_FRAMES_DIFFER_ONLY_BY_THAT_NOTICE()
    {
        var withIt = Golden.Body(WalkRender.Choice(LocalScreen(true), 100, script: [Enter]).Rows);
        var without = Golden.Body(WalkRender.Choice(LocalScreen(false), 100, script: [Enter]).Rows);

        Assert.Equal(withIt.Where(r => r != SetupFlow.OutOfReach.PadLeft(
            SetupFlow.OutOfReach.Length + 2)).Where(r => r.Trim().Length > 0),
            without.Where(r => r.Trim().Length > 0));
        Assert.Contains(withIt, r => r.Contains(SetupFlow.OutOfReach, StringComparison.Ordinal));
        Assert.DoesNotContain(without, r => r.Contains(SetupFlow.OutOfReach, StringComparison.Ordinal));
    }
    //panelRows has no production callers, so the panel geometry is deleted rather than re-derived (the terminal's own height arithmetic is covered elsewhere)

    //a paused fetch is the shelf's first row

    private static ISetupProbes.PausedFetch Part(string repoId, long done, long total) =>
        new(repoId, "qwen3-5-4b", done, total, @"C:\weights\qwen3-5-4b");

    //a shelf opened in a session, with or without a paused fetch behind it
    private static (SetupFlow Flow, WizardScreen.Choice Screen) OpenShelf(
        params ISetupProbes.PausedFetch[] paused)
    {
        //probes are built here because WizardProbes is a class with no with-expression, and this is the shelf's own fixture
        var probes = ModelFetchTests.ShelfProbes(paused: paused);
        var flow = new SetupFlow(probes);
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()));
    }

    //the row names the model and how far it got, which is why the record holds the total
    [Fact]
    public void A_PAUSED_FETCH_IS_THE_SHELFS_FIRST_ROW()
    {
        var (_, shelf) = OpenShelf(Part("unsloth/Qwen3.5-4B-GGUF", 3_200_000_000, 4_700_000_000));

        var first = shelf.Options[0];
        Assert.Equal(SetupFlow.ResumePausedFetch, first.Key);
        Assert.StartsWith("resume Qwen3.5-4B", first.Label, StringComparison.Ordinal);
        Assert.Contains("of ", first.Label, StringComparison.Ordinal);
    }

    //the oracle is the screen the answer reaches, the consent that says what the rest of the download is
    [Fact]
    public void AND_ENTER_ON_IT_RESUMES_THAT_FETCH()
    {
        var (flow, _) = OpenShelf(Part("unsloth/Qwen3.5-4B-GGUF", 3_200_000_000, 4_700_000_000));

        var after = flow.Answer(SetupFlow.ResumePausedFetch);

        Assert.Equal(SetupFlow.ModelConsentKey,
            Assert.IsType<WizardScreen.Choice>(after).Key);
    }

    //the resume row takes digit 1 while it exists, and the models follow it. the pair is the oracle, the with-row case alone cannot tell a shift from a coincidence
    [Fact]
    public void THE_FIRST_MODEL_IS_DIGIT_TWO_WITH_THE_ROW_AND_DIGIT_ONE_WITHOUT()
    {
        var (_, with) = OpenShelf(Part("unsloth/Qwen3.5-4B-GGUF", 1, 2));
        var (_, without) = OpenShelf();

        Assert.Equal(SetupFlow.ResumePausedFetch, with.Options[0].Key);
        Assert.Equal("0", with.Options[1].Key);      //the first model, at digit 2 while the row exists
        Assert.Equal("0", without.Options[0].Key);   //the same model is digit 1 when nothing is paused
    }

    //the table lines align to the options after the lead row, so a dropped offset shifts both ends
    [Fact]
    public void THE_RESUME_ROW_IS_NOT_A_TABLE_ROW_AND_THE_LAST_MODEL_IS_NOT_PROSE()
    {
        var (_, shelf) = OpenShelf(Part("unsloth/Qwen3.5-4B-GGUF", 1, 2));
        var view = shelf.Shelf!;

        //go through WizardPaint so the labels are the drawn ones, the binding takes the widget's options and the flow's are ChoiceOptions
        var labels = ShelfBinding.For(view, WizardPaint.Options(shelf, GlyphSet.Unicode),
            new Theme(TermCaps.Plain), GlyphSet.Unicode).LabelsAt(100);

        //the lead row keeps its own words
        Assert.StartsWith("resume ", labels[0], StringComparison.Ordinal);
        //every model keeps a table line, the last one included
        for (var i = 0; i < view.Rows.Count; i++)
            Assert.DoesNotContain("resume ", labels[i + 1], StringComparison.Ordinal);
        Assert.Contains(view.Rows[^1].RepoId.Split('/')[^1].Replace("-GGUF", ""),
            labels[view.Rows.Count], StringComparison.Ordinal);
    }
}
