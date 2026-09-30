using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//fixtures state sizes in gibibytes and fit verdicts by hand, since the golden's string is the oracle
public class ShelfTests
{

    private static ShelfView Counted(int newer, int older, int tooBig) => new(
        [new ShelfRow("o/m", "o", new HubQuant("m.gguf", 1_000_000_000, null),
            FitRegime.FitsGpu, 32768, false, null, 10, false)],
        null, MachineShape.Discrete,
        HiddenByFit: tooBig, HiddenOlder: older, HiddenNewer: newer, Total: 1 + newer + older + tooBig);

    //rows above the reviewed generation are counted as newer, and the count is a parse result that nothing sorts or filters by
    [Fact]
    public void A_NEWER_GENERATION_IS_COUNTED_BY_ITS_OWN_WORD()
    {
        var line = Shelf.CountLine(Counted(newer: 3, older: 0, tooBig: 0), glyphs: GlyphSet.Unicode);

        Assert.Contains("3 newer", line, StringComparison.Ordinal);
        Assert.DoesNotContain("older", line, StringComparison.Ordinal);
        Assert.Contains("a shows all", line, StringComparison.Ordinal);
    }

    //the newer and older buckets are never summed, since one hidden count would read as six models failing one test
    [Fact]
    public void THE_NEWER_AND_OLDER_BUCKETS_ARE_NEVER_SUMMED()
    {
        var line = Shelf.CountLine(Counted(newer: 3, older: 3, tooBig: 0), glyphs: GlyphSet.Unicode);

        Assert.Contains("3 newer", line, StringComparison.Ordinal);
        Assert.Contains("3 older", line, StringComparison.Ordinal);
        Assert.DoesNotContain("6", line, StringComparison.Ordinal);
    }

    //count newest first, since the reader scans the generation ladder that way, and the fit bucket stays last
    [Fact]
    public void THE_BUCKETS_READ_NEWEST_FIRST_WITH_FIT_LAST()
    {
        var line = Shelf.CountLine(Counted(newer: 1, older: 2, tooBig: 4), glyphs: GlyphSet.Unicode);

        var newer = line.IndexOf("1 newer", StringComparison.Ordinal);
        var older = line.IndexOf("2 older", StringComparison.Ordinal);
        var big = line.IndexOf("4 too big", StringComparison.Ordinal);

        Assert.True(newer >= 0 && older > newer && big > older,
            $"expected newer, then older, then too big - got: {line}");
    }

    //an empty bucket is silent, so a shelf hiding no generation prints nothing about it
    [Fact]
    public void A_SHELF_HIDING_NOTHING_SAYS_NOTHING()
    {
        var line = Shelf.CountLine(Counted(newer: 0, older: 0, tooBig: 0), glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain("newer", line, StringComparison.Ordinal);
        Assert.DoesNotContain("older", line, StringComparison.Ordinal);
        Assert.DoesNotContain("a shows all", line, StringComparison.Ordinal);
    }

    private static long Gib(double gb) => (long)Math.Round(gb * 1024 * 1024 * 1024);

    private static readonly IReadOnlyList<string> Families =
        ["gemma", "qwen", "deepseek", "glm", "mistral", "all"];

    internal static readonly IReadOnlyList<StripSection> Strip =
    [
        new("machine", StripState.Done), new("engine", StripState.Done),
        new("model", StripState.Current), new("check", StripState.Pending),
        new("done", StripState.Pending),
    ];

    private static ShelfRow Row(string name, double paramsB, double gb, long? ctx, bool vision,
        FitRegime fit) =>
        new($"unsloth/{name}", "unsloth",
            new HubQuant($"{name}-Q4_K_M.gguf", Gib(gb), null),
            fit, ctx, vision, Badge: null, Downloads: 0, Gated: false,
            Params: (long)Math.Round(paramsB * 1_000_000_000),
            Arch: ArchOf(name));

    //map a name to its arch by the generator's own rule, so the corpus and the fixture cannot disagree
    private static string? ArchOf(string name) =>
        name.StartsWith("gemma-3n", StringComparison.OrdinalIgnoreCase) ? "gemma3n"
        : name.StartsWith("gemma-3", StringComparison.OrdinalIgnoreCase) ? "gemma3"
        : name.StartsWith("gemma-4", StringComparison.OrdinalIgnoreCase) ? "gemma4"
        : null;

    private static PaneFile File(string quant, double gb, FitRegime fit) =>
        new(quant, Gib(gb), fit);

    //this fixture mirrors the generator's unified-96 shelf, four current rows with six older ones counted but hidden, and the cursor opens on the 26B

    private const FitRegime Fits = FitRegime.FitsGpu;

    private static ShelfView Gemma(MachineShape shape, FitRegime[] fits, ModelFacts[] facts,
        ShelfRow[] rows, int hiddenOlder, int hiddenByFit) =>
        new(rows, "unsloth", shape, Families: Families, Family: "gemma",
            Total: 10, HiddenOlder: hiddenOlder, HiddenByFit: hiddenByFit, Facts: facts);

    internal static ShelfView Unified96()
    {
        ShelfRow[] rows =
        [
            Row("gemma-4-31B-it", 30.7, 18.3, 262144, true, Fits),
            Row("gemma-4-26B-A4B-it", 25.2, 16.9, 262144, true, Fits),
            Row("gemma-4-12b-it", 11.9, 7.1, 262144, true, Fits),
            Row("gemma-4-E4B-it", 7.5, 5.0, 131072, true, Fits),
        ];
        ModelFacts[] facts =
        [
            new(Structure: "dense",
                Files: [File("Q4_K_M", 18.3, Fits), File("Q5_K_M", 21.7, Fits), File("Q6_K", 25.2, Fits)]),
            new(Structure: "MoE A4B", FileCount: 22,
                Files:
                [
                    File("Q4_K_S", 16.5, Fits), File("Q4_K_M", 16.9, Fits), File("Q5_K_S", 18.9, Fits),
                    File("Q5_K_M", 21.2, Fits), File("Q6_K", 23.2, Fits), File("Q8_0", 26.9, Fits),
                    File("Q3_K_M", 12.7, Fits), File("IQ4_XS", 13.6, Fits), File("MXFP4_MOE", 16.6, Fits),
                ]),
            new(Structure: "dense",
                Files: [File("Q4_K_M", 7.1, Fits), File("Q5_K_M", 8.4, Fits), File("Q6_K", 9.8, Fits)]),
            new(Structure: "dense",
                Files: [File("Q4_K_M", 5.0, Fits), File("Q5_K_M", 5.5, Fits), File("Q6_K", 7.1, Fits)]),
        ];
        return Gemma(MachineShape.UnifiedWithShare, [], facts, rows, hiddenOlder: 6, hiddenByFit: 0);
    }

    //the fixture models an 8 GB card, where the cursor opens on the E4B, the biggest GPU-resident row
    private static ShelfView Discrete8()
    {
        ShelfRow[] rows =
        [
            Row("gemma-4-12b-it", 11.9, 7.1, 262144, true, FitRegime.FitsRamOnly),
            Row("gemma-4-E4B-it", 7.5, 5.0, 131072, true, FitRegime.FitsGpu),
        ];
        ModelFacts[] facts =
        [
            new(Structure: "dense",
                Files:
                [
                    File("Q4_K_M", 7.1, FitRegime.FitsRamOnly), File("Q5_K_M", 8.4, FitRegime.FitsRamOnly),
                    File("Q6_K", 9.8, FitRegime.FitsRamOnly),
                ]),
            new(Structure: "dense",
                Files:
                [
                    File("Q4_K_M", 5.0, FitRegime.FitsGpu), File("Q5_K_M", 5.5, FitRegime.FitsGpu),
                    File("Q6_K", 7.1, FitRegime.FitsRamOnly),
                ]),
        ];
        return Gemma(MachineShape.Discrete, [], facts, rows, hiddenOlder: 5, hiddenByFit: 3);
    }

    //the fixture models the carve-out machine, where no MoE fits so the cursor stays on the first row
    private static ShelfView Carveout1()
    {
        ShelfRow[] rows =
        [
            Row("gemma-4-12b-it", 11.9, 7.1, 262144, true, FitRegime.FitsRamOnly),
            Row("gemma-4-E4B-it", 7.5, 5.0, 131072, true, FitRegime.FitsRamOnly),
        ];
        ModelFacts[] facts =
        [
            new(Structure: "dense",
                Files:
                [
                    File("Q4_K_M", 7.1, FitRegime.FitsRamOnly), File("Q5_K_M", 8.4, FitRegime.FitsRamOnly),
                    File("Q6_K", 9.8, FitRegime.FitsRamOnly),
                ]),
            new(Structure: "dense",
                Files:
                [
                    File("Q4_K_M", 5.0, FitRegime.FitsRamOnly), File("Q5_K_M", 5.5, FitRegime.FitsRamOnly),
                    File("Q6_K", 7.1, FitRegime.FitsRamOnly),
                ]),
        ];
        return Gemma(MachineShape.UnifiedNoShare, [], facts, rows, hiddenOlder: 5, hiddenByFit: 3);
    }

    //the hub lists a model and its -MTP twin as two rows, and folded by origin they are one model with two builds
    private static ShelfView QwenFolded()
    {
        ShelfRow[] rows =
        [
            Row("Qwen3-Coder-Next", 79.7, 48.5, 262144, false, Fits),
            Row("Qwen3-Next-80B-A3B-Instruct", 79.7, 48.5, 262144, false, Fits),
            Row("Qwen3-Next-80B-A3B-Thinking", 79.7, 48.5, 262144, false, Fits),
            Row("Qwen3.5-35B-A3B", 35.5, 22.6, 262144, true, Fits),
            Row("Qwen3.6-35B-A3B", 34.7, 22.1, 262144, true, Fits),
            Row("Qwen-AgentWorld-35B-A3B", 34.7, 22.1, 262144, false, Fits),
        ];

        //only the cursor's row gets a full fact set, since the pane renders one model and the table needs the structure cell
        ModelFacts Bare(string kind) => new(Structure: kind);

        ModelFacts[] facts =
        [
            new(Structure: "MoE", Experts: (512, 10)),
            Bare("MoE A3B"), Bare("MoE A3B"), Bare("MoE A3B"),
            new(Structure: "MoE A3B",
                Files:
                [
                    File("Q4_K_M", 22.1, Fits), File("Q5_K_M", 26.5, Fits), File("Q6_K", 29.3, Fits),
                ],
                Builds:
                [
                    new PaneBuild("standard", "plain build", Picked: true),
                    new PaneBuild("MTP", "speculative head inside", Picked: false),
                ]),
            Bare("MoE A3B"),
        ];

        return new ShelfView(rows, "unsloth", MachineShape.UnifiedWithShare,
            Families: Families, Family: "qwen", Total: 20, HiddenOlder: 13, HiddenByFit: 9,
            MoreBelow: 14, Facts: facts);
    }

    internal static WizardScreen.Choice Screen(ShelfView shelf) =>
        new(SetupFlow.SearchKey, "Which model should gatto start with?",
            [.. shelf.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId))],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode)) { Strip = Strip, AllowBack = true };

    //press the keys that move the cursor to a row and answer, so the captured frame is what the user reaches
    private static IEnumerable<ConsoleKeyInfo> ToRow(int row) =>
        [.. Enumerable.Repeat(Key(ConsoleKey.DownArrow), row), Key(ConsoleKey.Enter)];

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    //the chips row and the quoted typed word must still show when a search finds nothing, so drive the golden through the flow
    [Fact]
    public void AN_EMPTY_SEARCH_MATCHES_ITS_GOLDEN()
    {
        IReadOnlyList<ShelfRow> browse = [Row("qwen3.5-7b", 7, 4, 262144, false, FitRegime.FitsGpu)];
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = browse,
            //build a machine the classifier reads as unified, since a wrong one fails this golden in the footer as a keys defect
            Snapshot = new HardwareSnapshot(137438953472, 34359738368, GpuKind.Integrated, 103079215104),
            //the fake answers rows until a search is typed, since a fake always answering empty leaves no shelf to empty
            Answer = req => req.Search is { Length: > 0 }
                ? new HubSearchOutcome([], null, "unsloth")
                : new HubSearchOutcome(browse, null, "unsloth"),
        };
        var flow = new SetupFlow(probes);
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        //lift the filter first, since the golden shows this shelf unfiltered and the flow keeps the typed word in Draft
        flow.Answer(SetupFlow.CtlLift);
        var screen = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(ShelfControls.TypedAnswer("qwerty")));

        Golden.AssertEquals("s5", "unified-96-empty", 100,
            WalkRender.SettledFrame(screen, 100).Rows);
    }


    //the frame must render and the run must survive Enter, since an escaping exception is invisible to content assertions
    [Fact]
    public void ENTER_ON_A_SHELF_WITH_NO_ROWS_DOES_NOT_THROW()
    {
        //the fixture sets Searched, the field the chips row's sentence reads, since the subject here is Enter
        var shelf = new ShelfView([], "unsloth", MachineShape.UnifiedWithShare,
            Families: Families, Family: "qwen", Searched: true);
        var screen = new WizardScreen.Choice(SetupFlow.SearchKey,
            "Which model should gatto start with?", [], Shelf: shelf, Door: "qwerty")
            { Strip = Strip, AllowBack = true };

        //the script must press Enter, since a run with no script dries up before the key is read
        var frame = WalkRender.SettledFrame(screen, 100, script: [Key(ConsoleKey.Enter)]);

        Assert.NotEmpty(frame.Rows);
        //the chips row must render even with no models, since it is how the empty-search screen explains itself
        Assert.Contains(frame.Rows, r => r.Contains("lifted, you searched", StringComparison.Ordinal));
    }

    [Fact]
    public void THE_GEMMA_SHELF_ON_A_UNIFIED_MACHINE_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s5", "unified-96-default", 100,
            WalkRender.Choice(Screen(Unified96()), 100, script: ToRow(1)).Rows);

    //one focus state drives the chips cursor, the dimmed table cursor and the footer verb, so a face must not decide focus for itself
    [Fact]
    public void THE_FAMILIES_ROW_WEARS_THE_CURSOR_WHEN_THE_KEYS_ARE_THERE()
    {
        //read the Tab count from Shelf.Regions, so it moves when the ring gains a stop
        var ring = Shelf.Regions(Unified96(), 100, 0, hasDoor: true);
        var toFamilies = (ring.ToList().IndexOf(Region.Families) - ring.ToList().IndexOf(Region.List)
                          + ring.Count) % ring.Count;

        List<ConsoleKeyInfo> script = [Key(ConsoleKey.DownArrow)];
        script.AddRange(Enumerable.Repeat(Key(ConsoleKey.Tab), toFamilies));
        script.Add(Key(ConsoleKey.RightArrow));
        //end the script with Enter, since Esc in the families zone is spent returning the keys and repaints
        script.Add(Key(ConsoleKey.Enter));

        Golden.AssertEquals("s5", "unified-96-chips", 100,
            WalkRender.Choice(Screen(Unified96()), 100, script: script).Rows);
    }

    //the two zones keep two cursors, since one shared index would move the model while the user reads its facts
    [Fact]
    public void THE_PANE_WEARS_THE_CURSOR_WHEN_THE_KEYS_ARE_IN_IT()
    {
        List<ConsoleKeyInfo> script = [Key(ConsoleKey.DownArrow), Key(ConsoleKey.Tab)];
        script.AddRange(Enumerable.Repeat(Key(ConsoleKey.DownArrow), 3));
        script.Add(Key(ConsoleKey.Enter));

        Golden.AssertEquals("s5", "unified-96-pane", 100,
            WalkRender.SettledFrame(Screen(Unified96()), 100, script: script).Rows);
    }

    //the count line names the three reasons a model is away, and only one of them has a key to lift it
    [Fact]
    public void THE_FOLDED_QWEN_SHELF_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s5", "unified-96-qwen-folded", 100,
            WalkRender.Choice(Screen(QwenFolded()), 100, script: ToRow(4)).Rows);

    //the heaviest pane, every block drawn at once with the keys in the files zone, so a fit here means a fit anywhere
    [Fact]
    public void THE_PANE_AT_FULL_LOAD_WITH_THE_KEYS_IN_THE_FILES() =>
        Golden.AssertEquals("s5", "unified-96-full-file", 100,
            WalkRender.SettledFrame(Screen(QwenFolded()), 100, script: [.. Downs(4), Tab, Enter]).Rows);

    //the builds zone is its own Tab stop, since one vertical run would push the user past thirty quants
    [Fact]
    public void ONE_TAB_FURTHER_IS_THE_BUILDS_ZONE() =>
        Golden.AssertEquals("s5", "unified-96-full-build", 100,
            WalkRender.SettledFrame(Screen(QwenFolded()), 100,
                script: [.. Downs(4), Tab, Tab, Key(ConsoleKey.DownArrow), Enter]).Rows);

    //one build gets no zone, since a heading over a single row discriminates nothing and almost every repo has one build
    [Fact]
    public void A_MODEL_WITH_ONE_BUILD_HAS_NO_BUILDS_ZONE()
    {
        var row = Row("solo", 30, 18, 262144, false, Fits);
        var one = Pane.Rows(row, new ModelFacts(Structure: "dense",
            Builds: [new PaneBuild("standard", "plain build", true)]),
            MachineShape.UnifiedWithShare, 40);
        var two = Pane.Rows(row, new ModelFacts(Structure: "dense",
            Builds: [new PaneBuild("standard", "plain build", true),
                     new PaneBuild("MTP", "speculative head inside", false)]),
            MachineShape.UnifiedWithShare, 40);

        Assert.DoesNotContain(one, r => r.Text.Contains("builds of this model", StringComparison.Ordinal));
        Assert.Contains(two, r => r.Text.Contains("builds of this model", StringComparison.Ordinal));
    }

    //the ring follows the cursor, since a fixed ring would offer stops that draw nothing
    [Fact]
    public void THE_RING_GAINS_A_BUILDS_STOP_ONLY_ON_A_ROW_THAT_HAS_BUILDS()
    {
        var shelf = QwenFolded();

        Assert.Contains(Region.Builds, Shelf.Regions(shelf, 100, 4, hasDoor: true));
        Assert.DoesNotContain(Region.Builds, Shelf.Regions(shelf, 100, 5, hasDoor: true));
        Assert.DoesNotContain(Region.Builds, Shelf.Regions(shelf, 100, 0, hasDoor: true));

        //row six has neither files nor builds, so one Tab from the list reaches the input and the focused caret is the tell
        var noBuilds = WalkRender.SettledFrame(Screen(shelf), 100,
            script: [.. Downs(5), Tab]).Rows;
        Assert.DoesNotContain(noBuilds, r => r.Contains("builds of this model", StringComparison.Ordinal));
        Assert.Contains(noBuilds, r => r.Contains("❯ ▏ search models…", StringComparison.Ordinal));
    }

    //the two rows are built to disagree, one with builds and files opening at the second entry, the other with neither
    private static ShelfView TwoRows()
    {
        ShelfRow[] rows = [Row("alpha", 30, 18, 4096, false, Fits), Row("beta", 30, 18, 4096, false, Fits)];
        ModelFacts[] facts =
        [
            new(Structure: "dense",
                Files: [File("Q4_K_M", 8, Fits), File("Q5_K_M", 9, Fits), File("Q6_K", 10, Fits)]),
            new(Structure: "dense",
                //the knee sits at index zero while the run leaves the cursor at index one, so the two positions differ
                Files: [File("Q4_K_M", 5, Fits), File("Q5_K_M", 8, Fits), File("Q6_K", 10, Fits)],
                Builds: [new PaneBuild("standard", "plain build", true),
                         new PaneBuild("MTP", "speculative head inside", false)]),
        ];
        return new ShelfView(rows, "unsloth", MachineShape.UnifiedWithShare,
            Families: Families, Family: "gemma", Total: 2, Facts: facts);
    }

    //the up arrow must rebuild the ring too, since a test running only down never sees a missing up rebuild
    [Fact]
    public void WALKING_BACK_UP_LOSES_THE_BUILDS_STOP()
    {
        //the script holds no trailing Enter, so the run ends on this frame
        var rows = WalkRender.SettledFrame(Screen(TwoRows()), 100,
            script: [Key(ConsoleKey.DownArrow), Key(ConsoleKey.UpArrow), Tab, Tab]).Rows;

        Assert.DoesNotContain(rows, r => r.Contains("builds of this model", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("❯ ▏ search models…", StringComparison.Ordinal));
    }

    //the pane's pick must return to the new row's own knee, since an index kept across rows picks an unrelated file
    [Fact]
    public void THE_PANES_PICK_RETURNS_TO_THE_KNEE_ON_A_NEW_ROW()
    {
        var rows = WalkRender.SettledFrame(Screen(TwoRows()), 100,
            script:
            [
                Tab,                              //the keys move into the first row's files
                Key(ConsoleKey.DownArrow),        //this press moves the pick off the knee, onto the second file
                Key(ConsoleKey.Escape),           //the Esc key hands control back to the list
                Key(ConsoleKey.DownArrow),        //this moves to row two, whose knee is its first entry.
                Tab, Enter,
            ]).Rows;

        Assert.Contains(rows, r => r.Contains("❯ Q4_K_M", StringComparison.Ordinal));
        //the new row's index one is Q5_K_M, exactly where a cursor that survived the row change would sit
        Assert.DoesNotContain(rows, r => r.Contains("❯ Q5_K_M", StringComparison.Ordinal));
    }

    //at 80 columns the pane's facts move under the table and the list becomes one line, with nothing lost
    [Fact]
    public void THE_FRAME_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s5", "unified-96-80", 80,
            WalkRender.Choice(Screen(QwenFolded()), 80, script: ToRow(4)).Rows);

    //assert the threshold from both sides, and compute the boundary from the fixture since a hard-coded width would drift
    [Fact]
    public void THE_PANE_APPEARS_EXACTLY_AT_ITS_THRESHOLD_AND_NOT_A_CELL_BEFORE()
    {
        var shelf = QwenFolded();
        var fits = Shelf.LeftWidth(shelf, g: GlyphSet.Unicode) + 2 + Pane.MinWidth;

        Assert.Equal(Pane.MinWidth, Shelf.PaneWidth(shelf, fits, g: GlyphSet.Unicode));
        Assert.Equal(0, Shelf.PaneWidth(shelf, fits - 1, g: GlyphSet.Unicode));

        var wide = Shelf.Body(shelf, 4, 0, -1, Region.List, fits).Select(r => r.Text).ToList();
        var narrow = Shelf.Body(shelf, 4, 0, -1, Region.List, fits - 1).Select(r => r.Text).ToList();

        Assert.Contains(wide, r => r.Contains('│'));
        Assert.DoesNotContain(narrow, r => r.Contains('│'));
        //the ruled border opens the block on the folded side only.
        Assert.Contains(narrow, r => r.StartsWith("───", StringComparison.Ordinal));
        Assert.DoesNotContain(wide, r => r.StartsWith("───", StringComparison.Ordinal));

        //no row may overflow on either side of the boundary, where a layout switch usually breaks by one cell
        foreach (var (rows, w) in new[] { (wide, fits), (narrow, fits - 1) })
            foreach (var text in rows)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(text) <= w,
                    $"at width {w} a row is {Gatto.Terminal.UnicodeWidth.Of(text)} cells: {text}");
    }

    //in a folded frame the arrows move the one-line files list sideways, and they must stay silent when the pane sits beside the table
    [Fact]
    public void A_FOLDED_FILES_LINE_IS_WALKED_SIDEWAYS()
    {
        var right = WalkRender.SettledFrame(Screen(QwenFolded()), 80,
            script: [.. Downs(4), Tab, Key(ConsoleKey.RightArrow), Enter]).Rows;
        Assert.Contains(right, r => r.Contains("❯ Q5_K_M 26.5 GB", StringComparison.Ordinal));
        Assert.Contains(right, r => r.Contains("1 heavier →", StringComparison.Ordinal));

        //the left arrow returns the pick to the knee and its own count
        var back = WalkRender.SettledFrame(Screen(QwenFolded()), 80,
            script: [.. Downs(4), Tab, Key(ConsoleKey.RightArrow), Key(ConsoleKey.LeftArrow), Enter]).Rows;
        Assert.Contains(back, r => r.Contains("❯ Q4_K_M 22.1 GB", StringComparison.Ordinal));
        Assert.Contains(back, r => r.Contains("2 heavier →", StringComparison.Ordinal));

        //at 100 columns the pane sits beside the table and the same press moves nothing
        var unfolded = WalkRender.SettledFrame(Screen(QwenFolded()), 100,
            script: [.. Downs(4), Tab, Key(ConsoleKey.RightArrow), Enter]).Rows;
        Assert.Contains(unfolded, r => r.Contains("❯ Q4_K_M", StringComparison.Ordinal));
        Assert.DoesNotContain(unfolded, r => r.Contains("❯ Q5_K_M", StringComparison.Ordinal));
    }

    private static readonly ConsoleKeyInfo Tab = new('\0', ConsoleKey.Tab, false, false, false);

    private static readonly ConsoleKeyInfo Enter = new('\0', ConsoleKey.Enter, false, false, false);

    private static IEnumerable<ConsoleKeyInfo> Downs(int n) =>
        Enumerable.Repeat(Key(ConsoleKey.DownArrow), n);

    [Fact]
    public void THE_DISCRETE_SHELF_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s5", "discrete-8", 100,
            WalkRender.Choice(Screen(Discrete8()), 100, script: ToRow(1)).Rows);

    //the discovery line sits under the title and above the chips, since the user's own machine outranks generic content
    [Fact]
    public void THE_DISCOVERY_LINE_SITS_UNDER_THE_TITLE()
    {
        var screen = Screen(Unified96()) with { BodyRows = [Found] };
        Golden.AssertEquals("s5", "unified-96-hub-found", 100,
            WalkRender.Choice(screen, 100, script: ToRow(1)).Rows);
    }

    //goldens hold plain text, so this row's inks need their own oracle: a dim sentence with one bright key letter and no accent
    [Fact]
    public void THE_DISCOVERY_LINES_KEY_WEARS_THE_KEYS_WHITE()
    {
        var theme = new Theme(new TermCaps(true, true));
        var surface = new RecordingSurface { Width = 100 };
        var face = new TuiWizardSurface(surface, new EscOnly(), theme, "0.5.0", "1a2b3c4", () => 0);
        face.Choose(Screen(Unified96()) with { BodyRows = [Found] },
            watch: null, tick: null);
        //assert on the single row, since the footer paints its own bright m and a screen-wide search reads the wrong row
        var row = surface.Text.Split('\n')
            .Single(l => l.Contains("gatto also found", StringComparison.Ordinal));

        //the expectation comes from the theme's own Paint, so a palette change moves it
        Assert.Contains(theme.Paint("m", Theme.Bright), row, StringComparison.Ordinal);
        //the dim tail proves the split's place, since the key run ends and dim resumes after it
        Assert.Contains(theme.Paint(" shows them", Theme.Dim), row, StringComparison.Ordinal);

        //an accent here would promise an action the line is not offering, so the row must show none
        Assert.DoesNotContain(theme.Paint("m", Theme.Accent), row, StringComparison.Ordinal);
    }

    //the key must match on both sides of its word, so the fixture puts a word just before it
    [Fact]
    public void A_KEY_INSIDE_PROSE_MATCHES_ON_BOTH_SIDES_OF_THE_WORD()
    {
        var theme = new Theme(new TermCaps(true, true));
        var surface = new RecordingSurface { Width = 100 };
        var face = new TuiWizardSurface(surface, new EscOnly(), theme, "0.5.0", "1a2b3c4", () => 0);

        face.Choose(new WizardScreen.Choice("k", "q",
            [new ChoiceOption("a", "a")],
            BodyRows: [new WizardRow("gatto found them, m shows them", RowTone.Aside, Keys: ["m"])]));

        var row = surface.Text.Split('\n')
            .Single(l => l.Contains("gatto found them", StringComparison.Ordinal));

        //the dim run includes the word before the key and the row's two-cell gutter
        Assert.Contains(theme.Paint("  gatto found them, ", Theme.Dim), row, StringComparison.Ordinal);
        Assert.Contains(theme.Paint("m", Theme.Bright), row, StringComparison.Ordinal);
        Assert.Contains(theme.Paint(" shows them", Theme.Dim), row, StringComparison.Ordinal);
    }

    private sealed class EscOnly : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => new('\0', ConsoleKey.Enter, false, false, false);
    }

    //the row names m as a key, so a screen need not know about themes
    private static readonly WizardRow Found = new(
        "gatto also found 5 models already on this machine, m shows them",
        RowTone.Aside, Keys: ["m"]);

    [Fact]
    public void THE_CARVEOUT_SHELF_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s5", "carveout-1", 100,
            WalkRender.Choice(Screen(Carveout1()), 100, script: ToRow(0)).Rows);

    //numbered rows stop at nine, since a tenth showing 10. promises a keystroke nobody can press
    [Fact]
    public void ROWS_ARE_NUMBERED_ONE_TO_NINE_AND_THE_COLUMN_STAYS_BLANK_PAST_IT()
    {
        var rows = Enumerable.Range(0, 12)
            .Select(i => Row($"m{i}", 1, 1, 4096, false, Fits)).ToArray();
        var view = new ShelfView(rows, "unsloth", MachineShape.UnifiedWithShare,
            Families: Families, Family: "gemma", Total: 12);

        //keep the cursor off every row, so the digits are the only difference between them
        var table = Shelf.Table(view, row: -1, focused: true, glyphs: GlyphSet.Unicode).Select(r => r.Text).ToList();

        //the first entry is the header, so the model rows start at index one.
        for (var i = 1; i <= 9; i++)
            Assert.Equal($"  {i}. ", table[i][..5]);

        //from the tenth row on, a two-cell mark is drawn and three blank cells replace the digits.
        foreach (var i in new[] { 10, 11, 12 })
        {
            Assert.Equal("     ", table[i][..5]);
            Assert.DoesNotContain($"{i}.", table[i][..8], StringComparison.Ordinal);
        }

        //every row's model name must start at the same column, which a digits-only check cannot see
        var at = table.Skip(1).Select(r => r.IndexOf('m', StringComparison.Ordinal)).Distinct().ToList();
        Assert.Single(at);
    }

    //the body must fit at the composer, since the painter's clamp hides overflow as trailing spaces
    [Fact]
    public void THE_COMPOSED_BODY_NEVER_EXCEEDS_ITS_WIDTH()
    {
        foreach (var (name, view) in new (string, ShelfView)[]
                 { ("unified-96", Unified96()), ("discrete-8", Discrete8()), ("carveout-1", Carveout1()) })
            for (var w = 78; w <= 120; w++)
                foreach (var row in Shelf.Body(view, row: 0, chip: 0, file: -1, focus: Region.List, width: w))
                    Assert.True(Gatto.Terminal.UnicodeWidth.Of(row.Text) <= w,
                        $"{name} at width {w}: a body row is "
                        + $"{Gatto.Terminal.UnicodeWidth.Of(row.Text)} cells: {row.Text}");
    }

    //each drawn digit must answer its own row, and the key must hold a real KeyChar since that is how a terminal sends one
    [Fact]
    public void A_RENDERED_DIGIT_ANSWERS_ITS_ROW()
    {
        var shelf = Unified96();
        var screen = Screen(shelf);

        //pressing 3 answers row index two, the third row on the shelf
        Assert.Equal("2", WalkRender.Answer(screen, new ConsoleKeyInfo('3', ConsoleKey.D3, false, false, false)));
        Assert.Equal("0", WalkRender.Answer(screen, new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false)));
    }

    //a repo name is never truncated, since the model column widens to the longest name
    [Fact]
    public void A_REPO_NAME_IS_NEVER_TRUNCATED_HOWEVER_LONG()
    {
        var name = "Qwen3-Next-80B-A3B-Instruct-2507-FP8-Dynamic-Extra-Long";
        var view = new ShelfView([Row(name, 80, 48.5, 262144, false, Fits)],
            "unsloth", MachineShape.UnifiedWithShare, Families: Families, Family: "qwen", Total: 1);

        var table = Shelf.Table(view, row: 0, focused: true, glyphs: GlyphSet.Unicode).Select(r => r.Text).ToList();

        Assert.Contains(name, table[1], StringComparison.Ordinal);
        Assert.DoesNotContain("…", table[1], StringComparison.Ordinal);
    }

    //the legend must explain exactly the marks the table draws, so a unified shelf explains the params column instead
    [Fact]
    public void THE_LEGEND_NAMES_EVERY_MARK_THE_TABLE_DRAWS()
    {
        var discrete = WalkRender.Choice(Screen(Discrete8()), 100, script: ToRow(1)).Rows;
        var legend = discrete[^1];
        foreach (var row in discrete.Where(r => r.Contains('│')))
            foreach (var glyph in new[] { "✓", "⚠", "✗" })
                if (row.Split('│')[0].Contains(glyph, StringComparison.Ordinal))
                    Assert.Contains(glyph, legend, StringComparison.Ordinal);

        //a machine with no runs column explains no glyph, since a marks legend there would be decoration
        var unified = WalkRender.Choice(Screen(Unified96()), 100, script: ToRow(1)).Rows;
        Assert.Contains("fewer params = faster", unified[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("RAM", unified[^1], StringComparison.Ordinal);
    }

    //the pane derives no facts, so an unfilled one stays absent and only the identity and honesty rows draw
    [Fact]
    public void A_ROW_WITH_NO_FACTS_RENDERS_ITS_IDENTITY_AND_NOTHING_INVENTED()
    {
        var view = new ShelfView([Row("gemma-4-12b-it", 11.9, 7.1, 262144, true, Fits)],
            "unsloth", MachineShape.UnifiedWithShare, Families: Families, Family: "gemma", Total: 1);

        var pane = Pane.Rows(view.Rows[0], null, view.Shape, 40).Select(r => r.Text.TrimEnd()).ToList();

        Assert.Equal("gemma-4-12b-it", pane[0]);
        Assert.Equal("by unsloth", pane[1]);
        Assert.Contains(pane, r => r == "✗ not measured by gatto yet");
        //skip an absent-files assertion here, since the heading never draws and the golden is its only oracle
        Assert.DoesNotContain(pane, r => r.StartsWith("files", StringComparison.Ordinal));
        Assert.DoesNotContain(pane, r => r.Contains("MoE", StringComparison.Ordinal));
    }

    //a typed search lifts the family default, so no chip may be lit and the lift is said in words too
    [Fact]
    public void A_SEARCHED_SHELF_LIGHTS_NO_CHIP_AND_SAYS_WHY()
    {
        var view = Unified96();
        var searched = view with { Searched = true };

        var off = Shelf.Chips(view, chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode);
        var on = Shelf.Chips(searched, chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode);

        Assert.Contains("(lifted, you searched)", on.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(lifted", off.Text, StringComparison.Ordinal);

        //ink is the subject, since both rows spell the same families and only one accents it
        Assert.Contains(off.Runs, r => r.Text == "gemma" && r.Ink == RunInk.Accent);
        Assert.DoesNotContain(on.Runs, r => r.Text == "gemma" && r.Ink == RunInk.Accent);
    }

    //the fit toggle moves the count line only and leaves the chips lit, so the two fields move their own rows
    [Fact]
    public void THE_FIT_TOGGLE_MOVES_THE_COUNT_LINE_AND_NOT_THE_CHIPS_ROW()
    {
        var lifted = Unified96() with { Lift = true };

        var chips = Shelf.Chips(lifted, chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain("you searched", chips.Text, StringComparison.Ordinal);
        Assert.Contains(chips.Runs, r => r.Text == "gemma" && r.Ink == RunInk.Accent);

        //the count line is the only place the fit toggle must be readable.
        Assert.Contains("too big", ShelfBinding.CountLine(lifted, GlyphSet.Unicode),
            StringComparison.OrdinalIgnoreCase);
    }

    //an option the arrows reach but nobody paints leaves the cursor on an invisible row, so the face must draw it
    [Fact]
    public void AN_OPTION_BEYOND_THE_SHELFS_ROWS_IS_STILL_RENDERED()
    {
        var shelf = Unified96();
        var screen = new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [.. shelf.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId)),
             new ChoiceOption("something-new", "Something the face was never told about")],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode)) { Strip = Strip, AllowBack = true };

        var rows = WalkRender.Choice(screen, 100, script: ToRow(0)).Rows;
        Assert.Contains(rows, r => r.Contains("never told about", StringComparison.Ordinal));
    }

    //a view whose facts do not line up with its rows is refused at construction, so no row shows the wrong structure
    [Fact]
    public void FACTS_THAT_DO_NOT_LINE_UP_WITH_THE_ROWS_ARE_REFUSED()
    {
        var rows = new[] { Row("a", 1, 1, 4096, false, Fits), Row("b", 1, 1, 4096, false, Fits) };
        var ex = Assert.Throws<ArgumentException>(() =>
            new ShelfView(rows, "unsloth", MachineShape.UnifiedWithShare,
                Facts: [new ModelFacts(Structure: "dense")]));
        Assert.Contains("2 rows, 1 facts", ex.Message, StringComparison.Ordinal);
    }
    //a copy assigns backing fields directly, so the check lives in the init accessor to catch a trimmed view
    [Fact]
    public void FACTS_THAT_DO_NOT_LINE_UP_AFTER_A_COPY_ARE_REFUSED_TOO()
    {
        var v = new ShelfView(
            [Row("a", 1, 1, 4096, false, Fits), Row("b", 1, 1, 4096, false, Fits)],
            "unsloth", MachineShape.UnifiedWithShare,
            Facts: [new ModelFacts(Structure: "dense"), new ModelFacts(Structure: "dense")]);

        var ex = Assert.Throws<ArgumentException>(() =>
            v with { Rows = [v.Rows[0]], Facts = v.Facts });
        Assert.Contains("1 rows, 2 facts", ex.Message, StringComparison.Ordinal);
    }

    //the consumer must refuse too, since readers skip missing facts silently and the marks would stop partway down
    [Fact]
    public void BODY_REFUSES_A_VIEW_WHOSE_FACTS_DO_NOT_LINE_UP_WITH_ITS_ROWS()
    {
        var v = new ShelfView(
            [Row("a", 1, 1, 4096, false, Fits), Row("b", 1, 1, 4096, false, Fits)],
            "unsloth", MachineShape.UnifiedWithShare, Families: Families, Family: "gemma",
            Facts: [new ModelFacts(Structure: "dense"), new ModelFacts(Structure: "dense")]);

        //only Rows is copied here, so the init accessor never sees it
        var short_ = v with { Rows = [v.Rows[0]] };

        var ex = Assert.Throws<ArgumentException>(() =>
            Shelf.Body(short_, row: 0, chip: 0, file: -1, Region.List, width: 100));
        Assert.Contains("1 rows, 2 facts", ex.Message, StringComparison.Ordinal);
    }

    //the shelf as the flow emits it, built from the flow's own key so a test cannot answer with an unknown one
    private static WizardScreen.Choice WithFolderDoor(ShelfView shelf) =>
        new(SetupFlow.SearchKey, "Which model should gatto start with?",
            [.. shelf.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId)),
             new ChoiceOption(SetupFlow.Elsewhere, "I already have a model, let me point at the folder")],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode)) { Strip = Strip, AllowBack = true };

    //the folder escape is a footer key, since as a row it sat past every model and nothing advertised it
    [Fact]
    public void THE_SHELFS_FOOTER_OFFERS_THE_FOLDER_DOOR()
    {
        var shelf = Unified96();
        var keys = Shelf.Keys(new FocusRing(Shelf.Regions(shelf, 100, 0, hasDoor: true), Shelf.Opening(shelf)),
            ShelfSource.Hub, searchKey: true);

        Assert.Contains(keys, k => k.Key == "d");
    }

    //a screen without the option must not advertise the key, since the footer computes the offer from the screen's own options
    [Fact]
    public void A_SHELF_WITHOUT_THE_OPTION_DOES_NOT_OFFER_THE_KEY()
    {
        var shelf = Unified96();
        var keys = Shelf.Keys(new FocusRing(Shelf.Regions(shelf, 100, 0, hasDoor: true), Shelf.Opening(shelf)),
            ShelfSource.Hub, searchKey: false);

        Assert.DoesNotContain(keys, k => k.Key == "d");
    }

    //a drawn key proves nothing, so a shelf with no door must still answer d and / with the search
    [Theory]
    [InlineData('d')]
    [InlineData('/')]
    public void A_SHELF_WITH_NO_DOOR_STILL_ANSWERS_WITH_THE_SEARCH_ROAD(char key)
    {
        var doorless = WithFolderDoor(Unified96()) with { Door = null };

        var (answer, _) = WalkRender.Answered(doorless, 100,
            [new ConsoleKeyInfo(key, ConsoleKey.D, false, false, false)]);

        Assert.Equal(SetupFlow.CtlSearch, answer);
    }

    //this drives the real flag, pressing d on a screen without the option where nothing answers
    [Fact]
    public void A_SHELF_SCREEN_WITHOUT_THE_OPTION_DOES_NOT_ANSWER_d()
    {
        //a shelf with rows answers d, since it opens the search field, while a shelf that fetched nothing has no ladder to search
        Assert.Throws<WalkRender.WalkEnded>(() => WalkRender.Answered(
            Screen(Unified96() with { Source = ShelfSource.Local }), 100,
            [new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)]));
    }

    //d on a hub shelf with rows moves the keys into the search field, a second key onto one path
    [Fact]
    public void A_HUB_SHELF_ANSWERS_d_WITH_THE_SEARCH_DOOR()
    {
        var (answer, _) = WalkRender.Answered(Screen(Unified96()), 100,
            [new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false),
             new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false),
             new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false),
             new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]);

        Assert.Equal(ShelfControls.TypedAnswer("qw"), answer);
    }

    //the shelf with a door, then the options the flow adds, so the run must skip the door rather than cap the list
    private static WizardScreen.Choice WithFolderDoorAnd(ShelfView shelf, params string[] after) =>
        new(SetupFlow.SearchKey, "Which model should gatto start with?",
            [.. shelf.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.RepoId)),
             new ChoiceOption(SetupFlow.Elsewhere, "I already have a model, let me point at the folder"),
             .. after.Select(k => new ChoiceOption(k, "Type a model's name from Hugging Face"))],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode)) { Strip = Strip, AllowBack = true };

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    //the arrows step over the unpainted option, since leaving it reachable would fire a choice nobody saw
    [Fact]
    public void THE_ARROWS_STEP_OVER_THE_UNPAINTED_DOOR_ONTO_THE_ROW_BELOW_IT()
    {
        var shelf = Unified96();
        var screen = WithFolderDoorAnd(shelf, SetupFlow.TypeAnId);

        //the presses go past the last model, onto the unpainted option's index
        var (answer, _) = WalkRender.Answered(screen, 100,
            [.. Enumerable.Repeat(Key(ConsoleKey.DownArrow), shelf.Rows.Count), Key(ConsoleKey.Enter)]);

        Assert.Equal(SetupFlow.TypeAnId, answer);
    }

    //with nothing below it the run stops at the last model, which is the shape a populated shelf really has
    [Fact]
    public void AND_WITH_NOTHING_BELOW_IT_THE_WALK_STOPS_AT_THE_LAST_MODEL()
    {
        var shelf = Unified96();

        var (answer, _) = WalkRender.Answered(WithFolderDoor(shelf), 100,
            [.. Enumerable.Repeat(Key(ConsoleKey.DownArrow), shelf.Rows.Count + 3), Key(ConsoleKey.Enter)]);

        Assert.Equal((shelf.Rows.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            answer);
    }

    //a digit must not reach the unpainted option, whose number could pass a bounds check with no row drawn
    [Fact]
    public void NOR_DOES_THE_DOORS_DIGIT_ANSWER_ANYTHING()
    {
        var shelf = Unified96();
        //the option follows the models, so its digit is one past the last model's.
        var digit = (char)('1' + shelf.Rows.Count);

        Assert.Throws<WalkRender.WalkEnded>(() =>
            WalkRender.Answered(WithFolderDoor(shelf), 100, [Char(digit)]));
    }

    //the list can never answer the folder option, so d is the only way to it, and the sweep covers both paths
    [Fact]
    public void THE_LIST_CAN_NEVER_ANSWER_THE_FOLDER_DOOR()
    {
        var shelf = Unified96();
        var screen = WithFolderDoorAnd(shelf, SetupFlow.TypeAnId);

        for (var downs = 0; downs <= shelf.Rows.Count + 2; downs++)
        {
            var (answer, _) = WalkRender.Answered(screen, 100,
                [.. Enumerable.Repeat(Key(ConsoleKey.DownArrow), downs), Key(ConsoleKey.Enter)]);
            Assert.NotEqual(SetupFlow.Elsewhere, answer);
        }
    }

    //the flow still holds the option the key answers, but the face must not paint it as a row too
    [Fact]
    public void THE_FOLDER_SENTENCE_IS_NO_LONGER_A_ROW_PAST_THE_TABLE()
    {
        var rows = WalkRender.Choice(WithFolderDoor(Unified96()), 100, script: ToRow(0)).Rows;

        Assert.DoesNotContain(rows, r => r.Contains("I already have a model", StringComparison.Ordinal));
    }

}
