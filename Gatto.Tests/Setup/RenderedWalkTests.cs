using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;
using Probes = Gatto.Tests.Fakes.WizardProbes;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//render the whole setup flow and assert on the frame, single-screen tests pass while the flow still has dead ends
public class RenderedWalkTests : IDisposable
{
    //the later stages need a real home on disk, a null homePath stops the run at the write step so nothing after it renders
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-walk-").FullName;
    private int _homes;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    //a fresh home per run, the model scaffold is not idempotent and a shared home fails on the second run
    private string NewHome()
    {
        var home = Path.Combine(_root, $"home{++_homes}");
        Gatto.Core.Home.GattoHome.EnsureInitialized(home);
        return home;
    }

    //the sweep runs from 52, the narrowest served width, to 120, an edge defect fires only where a row ends at the width
    public static TheoryData<int> Widths => [52, 72, 80, 120];

    //fill every optional cell, a fixture poorer than reality misses a defect that needs a populated one
    private static ShelfRow Row(
        string repoId, string file = "model-Q4_K_M.gguf", long? paramCount = 8_000_000_000,
        Badge? badge = null) =>
        new(repoId, repoId.Split('/')[0], new HubQuant(file, 4_000_000_000, null),
            Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, badge, 100, false,
            LastModified: new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero),
            Params: paramCount);

    private static Badge Measured(string modelKey) =>
        new(modelKey, new DateOnly(2026, 7, 30), "v0.3.4", "greedy", Passed: 5, Ran: 5);

    //drive the whole setup flow over a real home, so the run reaches the completion screen instead of stopping at the write pause.
    private WizardRig Walk(int width, params ConsoleKeyInfo[] keys)
    {
        var rig = new WizardRig(width);
        var home = NewHome();
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.Face(keys), home);
        return rig;
    }

    //a branch runs the same world with one fact changed at width 80, the width sweep already covers the rest

    //strip every space from the frame, a literal match would assert the text never wraps instead of that the screen says it
    private static string Squash(string frame) =>
        new([.. frame.Where(c => !char.IsWhiteSpace(c))]);

    private WizardRig Branch(Probes probes, params ConsoleKeyInfo[] keys)
    {
        var rig = new WizardRig(80);
        var home = NewHome();
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(probes), rig.Face(keys), home);
        return rig;
    }

    //the home the last run used, so a path assertion compares against what the run actually did
    private string _walkHome = "";

    //nothing is installed and one Hub model arrives late, a file present from the start is found by discovery and the shelf never renders

    //keep every branch dimension a parameter of one fixture, a copy per branch could describe a different product and still pass
    private static Probes SteeredProbes(
        string home,
        AuditionCheck? audition = null,
        Gatto.Cli.InstallState? install = null,
        ConnectProbe? server = null,
        Func<HubSearchRequest, HubSearchOutcome>? answer = null,
        Func<string, TypedIdOutcome>? typed = null,
        string? heldBy = null,
        ModelFetchOffer? hubOffer = null,
        IReadOnlyList<FetchTick>? modelTicks = null,
        Gatto.Core.Hardware.HardwareSnapshot? snapshot = null,
        bool holdTheFetch = false,
        IReadOnlyList<ShelfRow>? rows = null)
    {
        var downloads = Path.Combine(home, "Downloads");
        Directory.CreateDirectory(downloads);
        var arriving = Path.Combine(downloads, "model-Q4_K_M.gguf");

        var probes = new Probes
        {
            Rows = rows is null
                ?
                [
                    Row("bartowski/Qwen3-8B-GGUF", badge: Measured("model-Q4_K_M.gguf")),
                    Row("unsloth/Llama-3.3-8B-GGUF", "llama-Q4_K_M.gguf", 3_800_000_000),
                ]
                : [.. rows],
            Found = [],
            //list both roots the real scan sweeps, the home's models folder and Downloads, a single root hides a defect that only shows across roots
            Roots = [Path.Combine(home, "models"), downloads],
            Llama = @"C:\llama\llama-server.exe",
            Audition = audition ?? new AuditionCheck(
                AuditionOutcome.Passed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 5, 5)),
            Install = install ?? Gatto.Cli.InstallState.Installed,
            Server = server,
            Answer = answer,
            Typed = typed,
            //the in-session done screens need another model already holding the server, null leaves every other run unchanged
            HeldBy = heldBy,
            //the flow needs a HubOffer to reach a download screen, a run without one skips the longest step of the funnel
            HubOffer = hubOffer,
            ModelTicks = modelTicks is null ? [] : [.. modelTicks],
            //block the fetch until the run cancels it, otherwise the download screen shows for one frame and no key is pressed
            ModelBlockUntilCancelled = holdTheFetch,
            //default to the discrete machine, a unified one draws a different shelf and one tier alone covers half the shelves
            Snapshot = snapshot ?? Discrete8,
        };
        probes.OnScan = n =>
        {
            //copy a real gguf file here, the writes scaffold from this path and a fake path renders as write.failed
            if (n < 3) return;
            if (!File.Exists(arriving))
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), arriving);
            //state the size instead of reading the tiny fixture, the summary would read 0 MB beside a 3.7 GB shelf row
            const long size = 4_000_000_000;
            probes.Found = [new FoundModel(arriving, size, null)];
            //the live probe answers the active model from the profile the run just wrote, unset leaves the summary with no model row
            probes.ActiveModel = (arriving, size);
        };
        return probes;
    }

    //keep the full key script in one place, every assertion must press the same keys
    private static ConsoleKeyInfo[] FullWalk =>
    [
        .. WalkOpening.Keys,       //the welcome screen, then the machine section.
        WizardRig.Digit('1'),      //choose llama.cpp at the fork
        WizardRig.Enter,           //pick the first row on the shelf.
        //the keys below need a real home
        WizardRig.Digit('1'),      //answer yes to the audition offer.
        WizardRig.Digit('1'),
        WizardRig.Digit('1'),
        WizardRig.Digit('1'),
    ];

    //list every key instead of splicing by position, one extra screen silently changes every later answer
    private static ConsoleKeyInfo[] WalkAnsweringInstall(char install) =>
    [
        .. WalkOpening.Keys,           //the welcome screen, then the machine section.
        WizardRig.Digit('1'),          //choose llama.cpp at the fork
        WizardRig.Enter,               //pick the first row on the shelf.
        WizardRig.Digit('1'),          //answer yes to the audition offer.
        WizardRig.Digit('1'),          //advance past the pass screen.
        WizardRig.Digit(install),      //answer the install question, which the done step asks first.
        WizardRig.Digit('1'),          //answer the update consent.
    ];

    //the run must finish at every width, a funnel that throws, hangs or dead-ends at one width alone would pass screen-by-screen tests
    [Theory]
    [MemberData(nameof(Widths))]
    public void THE_WALK_REACHES_THE_DOWNLOAD_SCREEN_at_every_rung(int width)
    {
        var rig = Walk(width, FullWalk);

        Assert.NotEmpty(rig.Frames);
        //assert on the frames the user saw, the final surface erases every answered block
        var seen = string.Join("\n", rig.Frames);
        Assert.Contains("huggingface.co", seen, StringComparison.Ordinal);
    }

    //drive the real face under the ASCII set, padding comes from real glyph widths and a substituted golden frame reports a false overflow
    [Theory]
    [MemberData(nameof(Widths))]
    public void EVERY_FRAME_OF_THE_WALK_IS_ASCII_UNDER_THE_ASCII_SET(int width)
    {
        var rig = new WizardRig(width, Gatto.Terminal.GlyphSet.Ascii);
        var home = NewHome();
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.Face(FullWalk), home);

        AssertAsciiVocabulary(rig, width);
        //the plain face keeps its rows in the replay, so the width check reads them here, the full-screen face needs a second capture
        foreach (var (frame, f) in rig.Frames.Select((x, i) => (x, i)))
            AssertRowsFit($"frame {f + 1}", frame.Split('\n'), width);
    }

    //run the same sweep on the full-screen face, it composes its rows differently from the plain one
    [Theory]
    [MemberData(nameof(Widths))]
    public void EVERY_FRAME_OF_THE_TUI_WALK_IS_ASCII_UNDER_THE_ASCII_SET(int width)
    {
        var rig = new WizardRig(width, Gatto.Terminal.GlyphSet.Ascii);
        var home = NewHome();
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.TuiFace(FullWalk), home);

        //read the composed rows here, this face places rows by cursor addressing and a replay would flatten them into one line
        Assert.NotEmpty(rig.PaintedFrames);
        foreach (var (frame, f) in rig.PaintedFrames.Select((x, i) => (x, i)))
        {
            AssertRowsAscii($"frame {f + 1}", frame, width);
            AssertRowsFit($"frame {f + 1}", frame, width);
        }

        //check the flattened screen too, text written outside the painter reaches the terminal without a composed row
        AssertAsciiVocabulary(rig, width);
    }

    //every visible character has to draw on a legacy console, and one copy here keeps the two faces from asking different questions
    private static void AssertAsciiVocabulary(WizardRig rig, int width)
    {
        Assert.NotEmpty(rig.Frames);
        foreach (var (frame, f) in rig.Frames.Select((x, i) => (x, i)))
            foreach (var (row, r) in frame.Split('\n').Select((x, i) => (x, i)))
                foreach (var ch in row)
                    Assert.True(ch <= 0x7F || ch == EmDash,
                        $"frame {f + 1} row {r + 1} at width {width} drew U+{(int)ch:X4}, which a "
                        + $"legacy console has no glyph for:\n  {row}");
    }

    //check row by row so a failure names the row instead of the whole screen
    private static void AssertRowsAscii(string what, IReadOnlyList<string> rows, int width)
    {
        for (var r = 0; r < rows.Count; r++)
            foreach (var ch in rows[r])
                Assert.True(ch <= 0x7F || ch == EmDash,
                    $"{what} row {r + 1} at width {width} drew U+{(int)ch:X4}, which a legacy console "
                    + $"has no glyph for:\n  {rows[r]}");
    }

    //measure the width under the ASCII set, a stand-in is wider so a row that fills the terminal under Unicode runs past the edge
    private static void AssertRowsFit(string what, IReadOnlyList<string> rows, int width)
    {
        for (var r = 0; r < rows.Count; r++)
            Assert.True(Gatto.Terminal.UnicodeWidth.Of(rows[r]) <= width,
                $"{what} row {r + 1} is {Gatto.Terminal.UnicodeWidth.Of(rows[r])} cells under the "
                + $"ASCII set but the terminal is {width} wide:\n  {rows[r]}");
    }

    //the em dash is the sweep's one exception on purpose, an ASCII twin would preserve it so a lost dash would not show
    private const char EmDash = '—';

    //the rig snapshots during a poll, a watching screen resolves with no key and can vanish between two frames
    [Theory]
    [MemberData(nameof(Widths))]
    public void THE_WATCH_RENDERS_and_says_it_is_watching(int width)
    {
        var rig = Walk(width, FullWalk);

        var seen = string.Join("\n", rig.Frames);
        Assert.Contains("watching", seen, StringComparison.OrdinalIgnoreCase);

        //strip the spaces from both sides, a long root path wraps at narrow widths and a literal match would assert it never wraps
        var squashed = new string([.. seen.Where(c => !char.IsWhiteSpace(c))]);
        foreach (var root in new[] { Path.Combine(_walkHome, "models"), Path.Combine(_walkHome, "Downloads") })
            Assert.Contains(new string([.. root.Where(c => !char.IsWhiteSpace(c))]), squashed, StringComparison.Ordinal);
        //the replaced option must not reappear at any width, a narrower table could lay the options out again
        Assert.DoesNotContain("look for it on common folders", seen, StringComparison.OrdinalIgnoreCase);
    }

    //check the rendered frames, one consent affordance must hold everywhere and only the run shows whether a prompt still asks the old way
    [Theory]
    [MemberData(nameof(Widths))]
    public void NO_BRACKETED_YES_NO_SURVIVES_ANYWHERE_on_the_walk(int width)
    {
        var rig = Walk(width, FullWalk);

        var everything = string.Join("\n", rig.Frames) + "\n" + rig.Plain;
        Assert.DoesNotContain("[y/N]", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(y/n)", everything, StringComparison.OrdinalIgnoreCase);
    }

    //no rendered row may exceed the terminal width, a wider row wraps into a line the erase never clears, so measure the replayed screen
    [Theory]
    [MemberData(nameof(Widths))]
    public void NO_ROW_OVERFLOWS_THE_TERMINAL(int width)
    {
        var rig = Walk(width, FullWalk);

        var offenders = (string.Join("\n", rig.Frames) + "\n" + rig.Plain)
            .Split('\n')
            .Select(r => r.TrimEnd('\r'))
            .Where(r => Gatto.Terminal.UnicodeWidth.Of(r) > width)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} rendered row(s) are wider than the {width}-column terminal:\n  "
            + string.Join("\n  ", offenders.Select(r => $"[{Gatto.Terminal.UnicodeWidth.Of(r)}] {r}")));
    }

    //both row kinds need their own test, one has a Hang and the other does not
    [Theory]
    [MemberData(nameof(Widths))]
    public void AN_INDENTED_ROW_KEEPS_ITS_INDENT_WHEN_IT_WRAPS(int width)
    {
        var rig = Walk(width, FullWalk);
        var lines = (string.Join("\n", rig.Frames) + "\n" + rig.Plain)
            .Split('\n').Select(r => r.TrimEnd('\r')).ToList();

        //find the root rows by position under the heading, at 52 columns the path wraps and a text filter matches nothing
        var heads = lines.Select((r, i) => (Row: r, At: i))
            .Where(x => x.Row.Contains("gatto looked in:", StringComparison.Ordinal)
                     || x.Row.Contains("It is watching", StringComparison.Ordinal))
            .Select(x => x.At).ToList();
        Assert.True(heads.Count > 0, $"the walk rendered no roots block at {width} columns");

        var roots = heads.SelectMany(h => lines.Skip(h + 1).TakeWhile(r => r.Trim().Length > 0))
            .Distinct().ToList();
        Assert.NotEmpty(roots);
        foreach (var row in roots)
            Assert.True(row.StartsWith("    ", StringComparison.Ordinal) && row[4] != ' ',
                $"a root row lost its indent at {width} columns: [{row}]");

        //read the expected column off the rendered line, a copied constant drifts. find the block by position, its words match other rows too
        var mapHead = lines.FindIndex(r => r.Contains("Setting up takes", StringComparison.Ordinal));
        Assert.True(mapHead >= 0, $"the walk rendered no map at {width} columns");

        var block = lines.Skip(mapHead + 1).SkipWhile(r => r.Trim().Length == 0)
            .TakeWhile(r => r.StartsWith("    ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(block);

        //this test reads the map's model row and its gloss, a gloss that wraps at the narrow widths gives the test a continuation
        var stepRow = block.First(r => r.Contains("model", StringComparison.Ordinal));
        var column = stepRow.IndexOf("pick one from", StringComparison.Ordinal);
        Assert.True(column > 4, "the map's description column was not found on its own step row");

        //only the step rows sit at column 4, so the count comes from the section list and a renamed step breaks no literal
        var steps = Gatto.Cli.Setup.WalkSection.LlamaRoad.Length;
        var atStep = block.Count(r => r.Length - r.TrimStart().Length == 4);
        Assert.True(atStep == steps,
            $"{atStep} map lines sit at the step column at {width} columns, expected the {steps} steps, "
            + "a continuation fell back to the margin:\n  " + string.Join("\n  ", block));

        foreach (var row in block)
        {
            var at = row.Length - row.TrimStart().Length;
            Assert.True(at == 4 || at == column,
                $"a map line sits at column {at}, neither step (4) nor description ({column}), "
                + $"at {width}: [{row}]");
        }

        //52 is the narrowest width the product claims, so a map row must wrap there or the guard checks a shape it never saw
        if (width == 52)
            Assert.True(block.Count > steps,
                "no map row wrapped at 52 columns, the guard saw no continuation");
    }

    //write the artifact even when the run throws, the transcript matters most when a script runs out of keys
    [Fact]
    public void DUMP_THE_WALK()
    {
        var dir = System.IO.Path.Combine(SourceRoot(), "tmp", "walk");
        System.IO.Directory.CreateDirectory(dir);
        foreach (var width in new[] { 52, 72, 80, 120 })
        {
            var rig = new WizardRig(width);
            string? stopped = null;
            try
            {
                var home = NewHome();
                SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.Face(FullWalk), home);
            }
            catch (Exception ex) { stopped = $"{ex.GetType().Name}: {ex.Message}"; }

            var sb = new System.Text.StringBuilder();
            sb.Append("WIDTH ").Append(width).Append(" - ").Append(rig.Frames.Count).AppendLine(" frames");
            if (stopped is not null) sb.Append("STOPPED EARLY - ").AppendLine(stopped);
            for (var i = 0; i < rig.Frames.Count; i++)
            {
                sb.Append("=== frame ").Append(i).AppendLine(" ===");
                sb.AppendLine(rig.Frames[i]);
            }
            sb.AppendLine("=== final surface ===");
            sb.AppendLine(rig.Plain);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"walk-{width}.txt"), sb.ToString());
        }
    }

    //expose accessors so the dump runs the same probes the assertions drive, a copy would show a product no test covers
    internal string NewHomeForDump() => NewHome();

    internal static Probes ProbesForDump(
        string home,
        AuditionCheck? audition = null,
        Gatto.Cli.InstallState? install = null,
        ConnectProbe? server = null,
        Func<HubSearchRequest, HubSearchOutcome>? answer = null,
        Func<string, TypedIdOutcome>? typed = null,
        string? heldBy = null,
        ModelFetchOffer? hubOffer = null,
        IReadOnlyList<FetchTick>? modelTicks = null,
        Gatto.Core.Hardware.HardwareSnapshot? snapshot = null,
        bool holdTheFetch = false,
        IReadOnlyList<ShelfRow>? rows = null) =>
        SteeredProbes(home, audition, install, server, answer, typed, heldBy, hubOffer, modelTicks,
            snapshot, holdTheFetch, rows);

    //report one tick so the bar shows progress, a fetch that reports nothing draws an empty bar and the pause states never render
    internal static IReadOnlyList<FetchTick> HalfWay =>
        [new FetchTick("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 1, 2, 7_900_000_000L, 16_900_000_000L,
            183_000L)];

    //the offer the fetch tests pull, the pause's return line names this id
    internal static ModelFetchOffer FetchOffer =>
        new("unsloth/gemma-4-26B-A4B-it", "gemma-4-26b-a4b-it", @"C:\weights\gemma",
            new HubQuant("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 16_900_000_000L, null), null);

    //keep the gap between installed and visible RAM equal to the graphics share, or the classifier reads this machine as discrete
    internal static Gatto.Core.Hardware.HardwareSnapshot Unified96 =>
        new(128 * Gib, 32 * Gib, Gatto.Core.Hardware.GpuKind.Integrated, 96 * Gib);

    //the discrete counterpart of Unified96, so the unified fixture has a sibling that differs
    internal static Gatto.Core.Hardware.HardwareSnapshot Discrete8 =>
        new(34359738368, 34093496320, Gatto.Core.Hardware.GpuKind.Discrete, 8589934592);

    private const ulong Gib = 1024UL * 1024 * 1024;

    //use rows where the two tiers disagree, a rule about which wins proves nothing when every row fits every card
    internal static IReadOnlyList<ShelfRow> TooBigForACard =>
    [
        Row("bartowski/Qwen3-8B-GGUF", badge: Measured("model-Q4_K_M.gguf")),
        Big("unsloth/Qwen3.6-70B-GGUF", "qwen3.6-70b-Q4_K_M.gguf", 70_000_000_000,
            40_000_000_000, Gatto.Core.Models.FitRegime.FitsRamOnly),
        Big("unsloth/Kimi-K2-GGUF", "kimi-k2-Q4_K_M.gguf", 1_000_000_000_000,
            600_000_000_000, Gatto.Core.Models.FitRegime.DoesNotFit),
    ];

    //give the row its own size and fit regime, the shelf reads both and a row that sets one draws a contradiction
    private static ShelfRow Big(string repoId, string file, long paramCount, long bytes,
        Gatto.Core.Models.FitRegime fit) =>
        new(repoId, repoId.Split('/')[0], new HubQuant(file, bytes, null), fit, 32768, false, null,
            100, false, LastModified: new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero),
            Params: paramCount);

    private static string SourceRoot()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "Gatto.sln")))
            d = d.Parent;
        return d!.FullName;
    }

    //assert the heading, both options and the measurement stamp are there, a failing model can still be fast so the numbers matter
    [Fact]
    public void A_FAILING_AUDITION_RENDERS_ITS_VERDICT_ITS_EVIDENCE_AND_BOTH_WAYS_OUT()
    {
        var home = NewHome();
        var probes = SteeredProbes(home, audition: new AuditionCheck(
            AuditionOutcome.Failed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 2, 5)));
        var rig = new WizardRig(80);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(probes), rig.Face(
        [
            .. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
            WizardRig.Digit('1'),          //answer yes to the audition offer.
            WizardRig.Digit('2'),          //choose to use it anyway, the product allows it on insistence
            WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
        ]), home);

        //assert on the first frame that holds the verdict, the shelf draws the same Q4_K_M text and later frames collapse the body rows
        var verdict = rig.Frames.Select(Squash)
            .First(f => f.Contains("struggledtoruncommandsandeditfiles"));

        Assert.Contains("Pickadifferentmodel", verdict, StringComparison.Ordinal);
        Assert.Contains("Useitanyway", verdict, StringComparison.Ordinal);
        //these stamp rows appear only on this failure screen, so they are the ones to pin
        Assert.Contains("sampling", verdict, StringComparison.Ordinal);
        Assert.Contains("greedy", verdict, StringComparison.Ordinal);
        Assert.Contains("tok/s", verdict, StringComparison.Ordinal);
        Assert.Contains("Youcanstilluseitifyouwantto.", verdict, StringComparison.Ordinal);
    }

    //assert on the frames, a branch taken but never rendered cannot be checked by a user, and the run must continue after the refusal
    [Fact]
    public void THE_DEV_BUILD_REFUSAL_IS_NARRATED_and_the_walk_carries_on()
    {
        var home = NewHome();
        var probes = SteeredProbes(home, install: Gatto.Cli.InstallState.DevBuild);
        var rig = new WizardRig(80);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(probes), rig.Face(
        [
            .. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
            WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
        ]), home);

        var seen = string.Join("\n", rig.Frames);
        //strip the spaces before matching, the sentence wraps at 80 columns and a literal match would assert that it never wraps
        var squashed = new string([.. seen.Where(c => !char.IsWhiteSpace(c))]);
        Assert.Contains("developmentbuild", squashed, StringComparison.Ordinal);
        Assert.Contains("skippingtheinstallstep", squashed, StringComparison.Ordinal);
        //the message only informs, nothing is wrong and the user has nothing to decide, so the run must reach the fork
        Assert.Contains("huggingface.co", seen, StringComparison.Ordinal);
    }

    //list the home's files with sizes so acceptance judges the disk, and skip Downloads where the fixture copies a file to model a download
    private static IReadOnlyList<string> Tree(string home) =>
        [.. Directory.EnumerateFileSystemEntries(home, "*", SearchOption.AllDirectories)
            .Select(e => Path.GetRelativePath(home, e))
            .Where(r => !r.StartsWith("Downloads", StringComparison.Ordinal))
            .Select(r => r + (File.Exists(Path.Combine(home, r)) ? ":" + new FileInfo(Path.Combine(home, r)).Length : "/"))
            .OrderBy(x => x, StringComparer.Ordinal)];

    //assert on the disk tree, the screen is a claim and the tree is the fact, and the flow only accumulates intent
    [Fact]
    public void DECLINING_AT_THE_DOOR_WRITES_NOTHING()
    {
        var home = NewHome();
        //build the probes before the baseline, SteeredProbes creates the fixture's Downloads folder so an earlier snapshot measures the fixture
        var probes = SteeredProbes(home);
        var before = Tree(home);
        var rig = new WizardRig(80);
        _walkHome = home;

        SetupRunner.Run(new SetupFlow(probes), rig.Face([WizardRig.Digit('2')]), home);

        Assert.Equal(before, Tree(home));
        //prove the run reached the opening question and answered it, an unchanged tree alone shows only that nothing ran
        Assert.Contains("Ready to set gatto up?", string.Join("\n", rig.Frames), StringComparison.Ordinal);
    }

    //the shelf is the last screen before the writes, so leaving there must write nothing
    [Fact]
    public void LEAVING_AT_THE_SHELF_WRITES_NOTHING()
    {
        var home = NewHome();
        var probes = SteeredProbes(home);
        var before = Tree(home);
        var rig = new WizardRig(80);
        _walkHome = home;

        SetupRunner.Run(new SetupFlow(probes), rig.Face(
            [.. WalkOpening.Keys, WizardRig.Digit('1'), Esc]), home);

        Assert.Equal(before, Tree(home));
    }

    //once the writes have run, leaving must not report that nothing was written, only the flow knows what it applied
    [Fact]
    public void LEAVING_AT_THE_CHECK_REPORTS_A_SETUP_THAT_IS_COMPLETE()
    {
        var home = NewHome();
        var probes = SteeredProbes(home);
        var rig = new WizardRig(80);
        _walkHome = home;

        //keys for the opening question, the fork and the pick, then Esc at the audition offer, where the writes have already taken effect
        SetupRunner.Run(new SetupFlow(probes), rig.Face(
            [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter, Esc]), home);

        //check the disk, the run wrote a model folder
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(home, "models")));
        //the config must point at the model the run wrote, this is the state where the model exists with nothing aimed at it
        Assert.Equal(
            Assert.Single(Gatto.Roles.Model.ListIds(Path.Combine(home, "models"))),
            Gatto.Core.Home.GattoConfig.Load(home).DefaultModel);

        //assert the ruled sentence is present too, read it off the render where the writes applied
        var squashed = Squash(string.Join("\n", rig.Frames) + "\n" + rig.Plain);
        Assert.Contains("gattoissetupandpointedat", squashed, StringComparison.Ordinal);
        //both wrong claims must stay off this screen, each needs its own check
        Assert.DoesNotContain("nothingwaswritten", squashed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothinghasbeenwritten", squashed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("setupisunfinished", squashed, StringComparison.Ordinal);
        //assert this spelling away too, the same wrong claim in other words
        Assert.DoesNotContain("notpointedatamodelyet", squashed, StringComparison.Ordinal);
    }

    //test this leave at the runner's own site, press one Esc (the chord is the face's) and build the head through GattoApp's call
    [Fact]
    public void ESC_AT_THE_CHECK_LEAVES_AT_EXIT_0_AND_THE_RECORD_SAYS_WHERE()
    {
        var home = NewHome();
        var flow = new SetupFlow(SteeredProbes(home));
        var rig = new WizardRig(100);
        _walkHome = home;

        var exit = SetupRunner.Run(flow, rig.Face(
            [.. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter, Esc]), home);

        Assert.Equal(0, exit);

        var record = WizardSession.Scrollback(flow, () => [], 100, GlyphSet.Unicode, Stamp,
            new DateOnly(2026, 8, 25), "gatto setup", theme: null);

        Assert.Equal("(^· ·^)Ⳋ  gatto setup · left at the check · v0.5.0 · build 1a2b3c4 · 2026-08-25",
            record[0]);
    }

    private static readonly Gatto.Cli.VersionStamp Stamp = new("0.5.0", "1a2b3c4", Dev: false);

    //read the header from the banner writer, the record opens with it, then a blank row, and its body sits at column zero
    [Fact]
    public void THE_RECORD_OPENS_WITH_THE_CAT_AND_THE_STANDARD_HEADER()
    {
        var home = NewHome();
        var flow = new SetupFlow(SteeredProbes(home));
        var rig = new WizardRig(100);
        _walkHome = home;

        SetupRunner.Run(flow, rig.Face(FullWalk), home);

        var header = new StringWriter();
        Gatto.Cli.CommandBanner.WriteHeader(header, null, "setup", GlyphSet.Unicode, Stamp);
        var record = WizardSession.Scrollback(flow, () => ["the painted frame"], 100, GlyphSet.Unicode, Stamp,
            new DateOnly(2026, 8, 25), "gatto setup", theme: null);

        Assert.Equal(header.ToString().Split(Environment.NewLine)[1] + " · 2026-08-25", record[0]);
        Assert.StartsWith("(^· ·^)Ⳋ  gatto setup · ", record[0], StringComparison.Ordinal);
        Assert.Equal("", record[1]);
        Assert.StartsWith("machine   ", record[2], StringComparison.Ordinal);
        Assert.All(record, row =>
        {
            Assert.DoesNotContain('♯', row);
            Assert.DoesNotContain('─', row);
            Assert.False(row.Length > 2 && row[0] == ' ' && row[1] == ' ' && row[2] != ' ', "a row kept the frame's gutter: " + row);
        });
    }

    //a finished run has no stop segment, a head that always included one would pass the guard above
    [Fact]
    public void A_FINISHED_WALK_HAS_NO_STOP_SEGMENT_IN_ITS_HEAD()
    {
        var home = NewHome();
        var flow = new SetupFlow(SteeredProbes(home));
        var rig = new WizardRig(100);
        _walkHome = home;

        SetupRunner.Run(flow, rig.Face(FullWalk), home);

        Assert.Null(flow.LeftAt);
        Assert.Equal("(^· ·^)Ⳋ  gatto setup · v0.5.0 · build 1a2b3c4 · 2026-08-25",
            WizardSession.Scrollback(flow, () => [], 100, GlyphSet.Unicode, Stamp,
                new DateOnly(2026, 8, 25), "gatto setup", theme: null)[0]);
    }

    //write the line feed as a value, the escape can be turned into a real newline by an editing pipeline
    private const char Lf = (char)10;

    private static ConsoleKeyInfo Esc => new('', ConsoleKey.Escape, false, false, false);

    private WizardRig Connect(ConnectProbe? server, params ConsoleKeyInfo[] keys)
    {
        var home = NewHome();
        var rig = new WizardRig(80);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home, server: server)), rig.Face(keys), home);
        return rig;
    }

    //the user confirms the server gatto found, the address and model list come from the server's own answers
    [Fact]
    public void THE_CONNECT_PATH_CONFIRMS_THE_SERVER_IT_FOUND()
    {
        var rig = Connect(new ConnectProbe("http://127.0.0.1:1234", ["qwen3-8b"], 8192),
            [.. WalkOpening.Keys, WizardRig.Digit('2'), WizardRig.Digit('1'),
            WizardRig.Digit('1'), WizardRig.Digit('1')]);

        var seen = Squash(string.Join("\n", rig.Frames));
        //the screen asks one question, and the address appears as a fact below it.
        Assert.Contains("Whichservershouldgattotalkto?", seen, StringComparison.Ordinal);
        Assert.Contains("serverhttp://127.0.0.1:1234", seen, StringComparison.Ordinal);
        //the row renders a label and its value, so the label is lower case here
        Assert.Contains("servingqwen3-8b", seen, StringComparison.Ordinal);
    }

    //the probe states no context window, the context is never inferred from a model ceiling
    [Fact]
    public void THE_CONNECT_PATH_ASKS_FOR_A_CONTEXT_THE_SERVER_WOULD_NOT_REPORT()
    {
        var rig = Connect(new ConnectProbe("http://127.0.0.1:1234", ["qwen3-8b"], null),
            [.. WalkOpening.Keys, WizardRig.Digit('2'), WizardRig.Digit('1'),
            WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1')]);

        var seen = Squash(string.Join("\n", rig.Frames));
        Assert.Contains("didn'treportitscontextwindow", seen, StringComparison.Ordinal);
        Assert.Contains("overridesthisanswer", seen, StringComparison.Ordinal);
    }

    //the list reports what a server could load, so one entry reads as serving and several as a catalogue
    [Fact]
    public void A_SERVER_LISTING_MANY_MODELS_IS_A_CATALOGUE_not_seven_things_being_served()
    {
        //press Esc, it is valid on every choice screen so the run reaches the end and a failure names the property
        var rig = Connect(new ConnectProbe("http://127.0.0.1:1234", ["a-8b", "b-14b", "c-32b"], 8192),
            [.. WalkOpening.Keys, WizardRig.Digit('2'), WizardRig.Digit('1'),
            WizardRig.Digit('1'), WizardRig.Digit('1'), Esc]);

        var seen = Squash(string.Join("\n", rig.Frames));
        Assert.Contains("lists3modelsitcanload", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("Servinga-8b", seen, StringComparison.Ordinal);
    }

    //a screen that answers nothing must still offer a way forward and a way out, or the whole flow blocks
    [Fact]
    public void NO_SERVER_ANSWERING_OFFERS_A_RETRY_AND_A_WAY_OUT()
    {
        //press Esc instead of the leave option's digit, a script that answers by position fails bare when that option disappears
        var rig = Connect(null, [.. WalkOpening.Keys, WizardRig.Digit('2'), Esc]);

        var seen = Squash(string.Join("\n", rig.Frames));
        //the retry state is a normal screen in the flow, it asks the flow's own question and offers a look again option
        Assert.Contains("Whichservershouldgattotalkto", seen, StringComparison.Ordinal);
        Assert.Contains("Lookagain", seen, StringComparison.Ordinal);
        //the screen names each port with the program that owns it, which tells the user what to start next
        Assert.Contains("theportsllama.cppandLMStudiousuallyuse", seen, StringComparison.Ordinal);
        //the way out is Esc, so the screen offers no option to leave setup
        Assert.DoesNotContain("Leavesetup", seen, StringComparison.Ordinal);
        //the plain surface has no typed field, so ConnectTests guards it where it renders, and Esc is the way out here
    }

    //a shelf run, opening, fork, then the state's own keys, and width is a parameter since the shelf's layout depends on it
    private WizardRig Shelf(int width, Func<HubSearchRequest, HubSearchOutcome> answer,
        params ConsoleKeyInfo[] after)
    {
        var home = NewHome();
        var rig = new WizardRig(width);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home, answer: answer)), rig.Face(
            [.. WalkOpening.Keys, WizardRig.Digit('1'), .. after]), home);
        return rig;
    }

    //nothing fitting is a fact about the arithmetic, the screen must still offer a way forward, and each cause needs its own run
    [Theory]
    [MemberData(nameof(Widths))]
    public void A_SHELF_WHERE_NOTHING_FITS_SAYS_SO_AND_IS_NOT_A_DEAD_END(int width)
    {
        var rig = Shelf(width, _ => new HubSearchOutcome([], HubSearchCause.NothingFits), Esc);

        var seen = Squash(string.Join("\n", rig.Frames));
        Assert.Contains("Noneofthesemodelsfitthismachine", seen, StringComparison.Ordinal);
        Assert.Contains("TypeamodelsnamefromHuggingFace", seen.Replace("'", ""), StringComparison.Ordinal);
    }

    //a hub failure is the cause a user cannot fix, so the screen names the network
    [Theory]
    [MemberData(nameof(Widths))]
    public void A_SHELF_THE_HUB_COULD_NOT_ANSWER_BLAMES_THE_NETWORK_not_the_machine(int width)
    {
        var rig = Shelf(width, _ => new HubSearchOutcome([], HubSearchCause.HubFailed), Esc);

        var seen = Squash(string.Join("\n", rig.Frames));
        Assert.Contains("Couldntreachthemodellist", seen.Replace("'", ""), StringComparison.Ordinal);
        //assert the other cause's sentence is absent, a check for any empty screen would pass while both causes share one
        Assert.DoesNotContain("fitthismachine", seen, StringComparison.Ordinal);
    }

    //curation may narrow a default but must leave the user's options, and the curated shelf keeps the way to the wider list
    [Theory]
    [MemberData(nameof(Widths))]
    public void THE_CURATED_SHELF_NAMES_ITS_PUBLISHER_AND_OFFERS_THE_WIDER_ONE(int width)
    {
        var rig = Shelf(width,
            r => new HubSearchOutcome(
                [Row("bartowski/Qwen3-8B-GGUF", badge: Measured("model-Q4_K_M.gguf"))],
                null, r.View == HubSearchView.Curated ? "bartowski" : null),
            Esc);

        var seen = Squash(string.Join("\n", rig.Frames));
        Assert.Contains("bartowski", seen, StringComparison.Ordinal);
    }

    //a fresh machine has measured nothing, so no row shows a tick, and the glyph's words appear wherever the glyph does
    [Theory]
    [MemberData(nameof(Widths))]
    public void AN_EMPTY_REGISTER_PUTS_NO_TICK_ON_ANY_ROW(int width)
    {
        var rig = Shelf(width,
            _ => new HubSearchOutcome(
                [Row("bartowski/Qwen3-8B-GGUF", badge: null),
                 Row("unsloth/Llama-3.3-8B-GGUF", "llama-Q4_K_M.gguf", 3_800_000_000, badge: null)],
                null, "bartowski"),
            Esc);

        //scope the check to the lines naming a repo, a frame is cumulative and the welcome map above the table uses the same tick
        var modelRows = rig.Frames
            .First(f => f.Contains("Qwen3-8B", StringComparison.Ordinal))
            .Split(Lf)
            .Where(l => l.Contains("Qwen3-8B", StringComparison.Ordinal)
                     || l.Contains("Llama-3.3-8B", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(modelRows);
        Assert.All(modelRows, r =>
            Assert.DoesNotContain(Gatto.Terminal.GlyphSet.Unicode.Ok, r, StringComparison.Ordinal));

        //assert the shelf also says nothing has been verified, which makes the absent tick a fact about the register
        var seen = Squash(string.Join(" ", rig.Frames));
        Assert.Contains(Squash(Gatto.Cli.Setup.ShelfBinding.UnmeasuredShelf), seen, StringComparison.Ordinal);
    }

    //the count line states what the filter hid, since a line leading with failures lists only what the user cannot run
    [Theory]
    [MemberData(nameof(Widths))]
    public void THE_FIT_FILTER_SAYS_WHAT_IT_HID(int width)
    {
        var rig = Shelf(width,
            _ => new HubSearchOutcome(
                [Row("bartowski/Qwen3-8B-GGUF", badge: Measured("model-Q4_K_M.gguf"))],
                null, "bartowski", HiddenByFit: 15),
            Esc);

        var seen = Squash(string.Join("\n", rig.Frames));
        //assert the whole sentence, a bare 15 also arrives from a size or a context length
        Assert.Contains("15morehidden", seen, StringComparison.Ordinal);
        Assert.Contains("pressatoincludethem", seen, StringComparison.Ordinal);
    }

    //curation only narrows a default, so the shelf of every approved publisher must stay reachable through the f picker
    [Theory]
    [MemberData(nameof(Widths))]
    public void PRESSING_F_BROADENS_TO_EVERY_PUBLISHER(int width)
    {
        var rig = Shelf(width,
            r => r.View == HubSearchView.Curated
                ? new HubSearchOutcome([Row("bartowski/Qwen3-8B-GGUF")], null, "bartowski")
                : new HubSearchOutcome(
                    [Row("bartowski/Qwen3-8B-GGUF"), Row("unsloth/Llama-3.3-8B-GGUF", "llama-Q4_K_M.gguf")],
                    null, null),
            WizardRig.Ch('f'), WizardRig.Digit('4'), Esc);

        //assert what the flow asked for, the engine's wider view is covered in HubSearchTests so rendered rows here would retest it through a fake
        var seen = Squash(string.Join(" ", rig.Frames));
        Assert.Contains("Llama-3.3-8B", seen, StringComparison.Ordinal);
    }

    private WizardRig ShelfTyped(int width, Func<string, TypedIdOutcome> typed,
        params ConsoleKeyInfo[] after)
    {
        var home = NewHome();
        var rig = new WizardRig(width);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home, typed: typed)), rig.Face(
            [.. WalkOpening.Keys, WizardRig.Digit('1'), .. after]), home);
        return rig;
    }

    //a name outside the curated set must reach the engine and come back as a row, typed through the / control
    [Theory]
    [MemberData(nameof(Widths))]
    public void A_TYPED_ID_REACHES_A_MODEL_OUTSIDE_THE_CURATED_SET(int width)
    {
        var asked = new List<string>();
        const string id = "someone/Custom-8B-GGUF";
        //write the keys as a collection expression, a spread in the argument list parses as an index
        ConsoleKeyInfo[] keys =
            [WizardRig.Ch('/'), .. id.Select(WizardRig.Ch), WizardRig.Enter, Esc];
        var rig = ShelfTyped(width,
            typedId => { asked.Add(typedId); return new TypedIdOutcome.Ok(Row(id)); },
            keys);

        Assert.Contains("someone/Custom-8B-GGUF", asked);
        var seen = Squash(string.Join(" ", rig.Frames));
        Assert.Contains("Custom-8B", seen, StringComparison.Ordinal);
    }

    //the copy is installed but a new terminal cannot find it, so the offer is to finish the install. the screen never says PATH
    [Theory]
    [MemberData(nameof(Widths))]
    public void THE_HALF_DONE_INSTALL_OFFERS_TO_FIX_IT_without_naming_PATH(int width)
    {
        var home = NewHome();
        var probes = SteeredProbes(home, install: Gatto.Cli.InstallState.InstalledButNotOnPath);
        var rig = new WizardRig(width);
        _walkHome = home;
        //the repair screen is the first question of the done step, so the run must reach it, and answering 2 leaves the machine alone
        SetupRunner.Run(new SetupFlow(probes), rig.Face([.. WalkAnsweringInstall('2'), Esc]), home);

        var seen = Squash(string.Join(" ", rig.Frames));
        Assert.Contains("Fixthatforme", seen, StringComparison.Ordinal);
        Assert.Contains("Windowsdoesntknowtolookthereyet", seen.Replace("'", ""), StringComparison.Ordinal);
        //PATH must not appear on this screen, check the whole frame since a heading or footer could reintroduce it
        Assert.DoesNotContain("PATH", string.Join(" ", rig.Frames), StringComparison.Ordinal);
    }

    //a check that could not run is never shown as a pass or a fail, and the screen says what gatto does not claim
    [Theory]
    [MemberData(nameof(Widths))]
    public void A_CHECK_THAT_COULD_NOT_RUN_CLAIMS_NOTHING_ABOUT_THE_MODEL(int width)
    {
        var home = NewHome();
        var probes = SteeredProbes(home, audition: new AuditionCheck(AuditionOutcome.CouldNotRun));
        var rig = new WizardRig(width);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(probes), rig.Face(
        [
            .. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
            WizardRig.Digit('1'),          //answer yes to the check offer.
            //press Esc instead of the screen's own option, it is valid on every choice screen, so a deleted branch fails the assertion
            Esc,
        ]), home);

        var seen = Squash(string.Join(" ", rig.Frames));
        Assert.Contains("Couldntrunthecheckjustnow", seen.Replace("'", ""), StringComparison.Ordinal);
        Assert.Contains("Nothingwasmeasured", seen, StringComparison.Ordinal);
        Assert.Contains("saysnothingaboutthemodel", seen, StringComparison.Ordinal);
        //the screen shows neither verdict, a check that could not run must not read as either result
        Assert.DoesNotContain("struggledtoruncommands", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("Itrancommandsandeditedfiles", seen, StringComparison.Ordinal);
    }

    //count sampling on the rendered screen, a value count stays green when the block is deleted since Q4_K_M is also the fixture's file name
    [Fact]
    public void THE_STAMP_RENDERS_EXACTLY_ONCE_ON_A_PASSING_WALK()
        => Assert.Equal(1, Walk(80, FullWalk).Occurrences("sampling"));

    //a failing run taken anyway reaches the same completion screen, and the evidence belongs on the verdict screen where the decision happens
    [Fact]
    public void THE_STAMP_RENDERS_EXACTLY_ONCE_ON_A_FAILING_WALK_TAKEN_ANYWAY()
    {
        var home = NewHome();
        var probes = SteeredProbes(home, audition: new AuditionCheck(
            AuditionOutcome.Failed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 2, 5)));
        var rig = new WizardRig(80);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(probes), rig.Face(
        [
            .. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
            WizardRig.Digit('1'),          //answer yes to the audition offer.
            WizardRig.Digit('2'),          //choose to use it anyway.
            WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
        ]), home);

        Assert.Equal(1, rig.Occurrences("sampling"));
        //no sentence may point to numbers below the completion screen, the stamp block doesn't sit there
        Assert.DoesNotContain("numbers below", rig.Plain, StringComparison.Ordinal);
    }

    //a run with no audition shows no stamp on the completion screen, and the assertion fails if one appears
    [Fact]
    public void A_WALK_WITH_NO_AUDITION_HAS_NO_STAMP_TO_LOSE()
    {
        var home = NewHome();
        var rig = new WizardRig(80);
        _walkHome = home;
        SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.Face(
        [
            .. WalkOpening.Keys, WizardRig.Digit('1'), WizardRig.Enter,
            WizardRig.Digit('2'),          //skip the audition offer and use the model as it is.
            WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'),
        ]), home);

        Assert.Equal(0, rig.Occurrences("sampling"));
        //a run with no audition still states its verdict in the check row.
        Assert.Contains("it loads and answers", rig.Plain, StringComparison.Ordinal);
    }

    //the state rows must cover each install state, a null InstallTo read as declined would tell an installed machine it can install later
    [Theory]
    [InlineData("NotInstalled", true, "A new terminal will find it", "stays where it is")]
    [InlineData("Installed", false, "Open a terminal in your project folder", "A new terminal will find it")]
    [InlineData("AlreadyInstalledElsewhere", false, "Open a terminal in your project folder", "A new terminal will find it")]
    [InlineData("NotInstalled", false, "stays where it is", "A new terminal will find it")]
    [InlineData("DevBuild", false, "Run gatto from where it is", "A new terminal will find it")]
    public void THE_CLOSING_SENTENCE_MATCHES_WHAT_THE_INSTALL_DID(
        string stateName, bool accepted, string mustSay, string mustNotSay)
    {
        //the theory takes the install state as a name, InstallState is internal and an xunit theory method must be public
        var state = Enum.Parse<Gatto.Cli.InstallState>(stateName);
        var home = NewHome();
        var flow = new SetupFlow(SteeredProbes(home, install: state));
        var rig = new WizardRig(80);

        //the run must reach the done step, that question records the install state the sentence is computed from
        SetupRunner.Run(flow, rig.Face([.. WalkAnsweringInstall(accepted ? '1' : '2'), Esc]), home);

        var sentence = flow.CompletionNextStep.Text;
        Assert.Contains(mustSay, sentence, StringComparison.Ordinal);
        Assert.DoesNotContain(mustNotSay, sentence, StringComparison.Ordinal);
    }

    //the pause clears the write set, so a sentence must not read install intent from it afterwards, and this run resumes past the pause
    [Fact]
    public void THE_ACCEPTED_SENTENCE_SURVIVES_THE_WRITE_PAUSE()
    {
        var home = NewHome();
        var flow = new SetupFlow(SteeredProbes(home, install: Gatto.Cli.InstallState.NotInstalled));
        var rig = new WizardRig(80);

        SetupRunner.Run(flow, rig.Face([.. WalkAnsweringInstall('1'), Esc]), home);

        //every real run that reaches a completion has gone through this step, a completion is reachable only through the pause
        flow.ResumeAfterWrites(null);

        var sentence = flow.CompletionNextStep.Text;
        Assert.Contains("A new terminal will find it", sentence, StringComparison.Ordinal);
        //the forbidden sentence tells an already installed machine that gatto setup can install it later
        Assert.DoesNotContain("stays where it is", sentence, StringComparison.Ordinal);
    }

    //when the install segment never ran, the closing sentence claims nothing about terminals
    [Fact]
    public void A_WALK_THAT_NEVER_ASKED_ABOUT_INSTALLING_CLAIMS_NOTHING_ABOUT_TERMINALS()
    {
        var sentence = new SetupFlow(SteeredProbes(NewHome())).CompletionNextStep.Text;

        Assert.DoesNotContain("terminal", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stays where it is", sentence, StringComparison.Ordinal);
        Assert.Contains("Have fun", sentence, StringComparison.Ordinal);
    }

    //the home needs a default model set, on an empty home the re-read gives null and both builds agree
    private string HomeWithDefault(string modelId = "already-armed")
    {
        var home = NewHome();
        Gatto.Core.Home.GattoConfigWriter.SetEndpointDefaultModel(home, "local", modelId);
        return home;
    }

    //leaving before any write must report no model, a config re-read would report the already-armed one, and the reported id is what gets asserted
    [Fact]
    public void LEAVING_BEFORE_THE_WRITES_REPORTS_NO_MODEL()
    {
        var home = HomeWithDefault();
        var rig = new WizardRig(80);

        SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.Face(Esc), out var added, home,
            modelSegmentOnly: true);

        Assert.Null(added);
    }

    //leaving after the writes has added a model, so this arm needs its own test with the oracle from the model directory
    [Fact]
    public void LEAVING_AFTER_THE_WRITES_REPORTS_THE_MODEL_THE_WRITER_PRODUCED()
    {
        var home = HomeWithDefault();
        var rig = new WizardRig(80);

        //taking the first shelf row applies the writes, so the leave comes after them
        SetupRunner.Run(new SetupFlow(SteeredProbes(home)), rig.Face(WizardRig.Enter, Esc),
            out var added, home, modelSegmentOnly: true);

        Assert.NotNull(added);
        Assert.NotEqual("already-armed", added);
        //take the expected id from the disk the writer wrote, so the guard cannot agree with itself.
        var scaffolded = Gatto.Roles.Model.ListIds(Path.Combine(home, "models"));
        Assert.Equal(added, Assert.Single(scaffolded));
    }
}
