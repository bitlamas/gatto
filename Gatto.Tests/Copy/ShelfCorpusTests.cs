using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Copy;

//the populated shelf from a real walk: the engine prices a rich fake Hub for each machine, the flow builds the screen and the face paints it
public sealed class ShelfCorpusTests : IDisposable
{
    private const long B = 1_000_000_000L;
    private const ulong GiB = 1UL << 30;
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-shelf-corpus-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    public static TheoryData<string, CorpusMode> Machines() => new()
    {
        { "discrete", CorpusMode.Dark }, { "discrete", CorpusMode.Plain },
        { "unified", CorpusMode.Dark }, { "unified", CorpusMode.Plain },
        { "nocard", CorpusMode.Dark }, { "nocard", CorpusMode.Plain },
    };

    public static TheoryData<CorpusMode> Modes() => new() { CorpusMode.Dark, CorpusMode.Plain };

    private static HardwareSnapshot Snapshot(string machine) => machine switch
    {
        "discrete" => new HardwareSnapshot(32 * GiB, 32 * GiB, GpuKind.Discrete, 8 * GiB, "0x1002"),
        "unified" => HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"),
        _ => new HardwareSnapshot(32 * GiB, 32 * GiB, GpuKind.None, null),
    };

    //each model's quants from three approved publishers, an encoder where the model sees images, and one model too big for every machine
    private static FakeShelfHub Catalog()
    {
        var hub = new FakeShelfHub();
        void Model(string source, string name, long total, string arch, double q4, bool vision,
            (long Experts, long Used, string Active)? moe = null)
        {
            hub.Source(source);
            foreach (var (org, downloads) in new[] { ("unsloth", 900L), ("bartowski", 90L), ("lmstudio-community", 40L) })
            {
                var files = new List<(string, double)>
                {
                    ($"{name}-Q3_K_M.gguf", q4 * 0.8), ($"{name}-Q4_K_M.gguf", q4),
                    ($"{name}-Q5_K_M.gguf", q4 * 1.18), ($"{name}-Q6_K.gguf", q4 * 1.36), ($"{name}-Q8_0.gguf", q4 * 1.76),
                };
                if (vision) files.Add(("mmproj-F16.gguf", 0.9));
                hub.Conversion(source, $"{org}/{name}-GGUF", total, downloads, arch, files: [.. files]);
                //the fake answers every other header read as a dense model, so a MoE says so on each of its files
                if (moe is { } m)
                    foreach (var (path, _) in files)
                        hub.Header($"{org}/{name}-GGUF", path,
                            GgufTestBytes.WithStructure(expertCount: m.Experts, expertUsed: m.Used, sizeLabel: m.Active, arch: arch));
            }
        }
        Model("Qwen/Qwen3.8-27B", "Qwen3.8-27B", 27 * B, "qwen35", 16.5, vision: true);
        Model("Qwen/Qwen3.6-35B-A3B", "Qwen3.6-35B-A3B", 35 * B, "qwen35moe", 21.2, vision: false, moe: (256, 8, "35B-A3B"));
        Model("Qwen/Qwen3.5-9B", "Qwen3.5-9B", 9 * B, "qwen35", 5.6, vision: true);
        Model("Qwen/Qwen3.5-397B-A17B", "Qwen3.5-397B-A17B", 397 * B, "qwen35moe", 228, vision: false, moe: (512, 10, "397B-A17B"));
        Model("google/gemma-4-31B-it", "gemma-4-31B-it", 31 * B, "gemma4", 18.3, vision: true);
        Model("google/gemma-4-E4B-it", "gemma-4-E4B-it", 8 * B, "gemma4", 5.0, vision: true);
        return hub;
    }

    //the flow over fake probes whose one search runs the real engine against the catalog, so nothing on this machine reaches a golden
    private (SetupFlow Flow, WizardScreen.Choice Shelf) Walk(string machine)
    {
        var live = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null,
            readHardware: () => new ProbeOutcome(Snapshot(machine), "fixture"), hubHandler: Catalog());
        var probes = new WizardProbes
        {
            Snapshot = Snapshot(machine),
            Answer = request => live.SearchModels(request, null, CancellationToken.None),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, RowBudget = Shelf.RowBudget(30) };
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()));
    }

    private static ConsoleKeyInfo Key(ConsoleKey k, char c = '\0') => new(c, k, false, false, false);

    //the settled frame at 120 by 30, inked in the dark mode so a wrong colour moves a golden. the plain mode pins text, since the face has no uncoloured theme
    private static string Frame(WizardScreen.Choice c, CorpusMode mode, IEnumerable<ConsoleKeyInfo>? keys = null) =>
        mode == CorpusMode.Dark
            ? CorpusDriver.Visible(string.Join("\n",
                WalkRender.Inked(c, 120, 30, new Theme(new TermCaps(true, true), ThemeMode.Dark), keys))) + "\n"
            : string.Join("\n", WalkRender.SettledFrame(c, 120, script: keys, height: 30).Rows.Select(r => r.TrimEnd())) + "\n";

    [Theory]
    [MemberData(nameof(Machines))]
    public void THE_LANDING_SHELF(string machine, CorpusMode mode)
    {
        var (_, shelf) = Walk(machine);
        CopyGolden.Check($"shelf-landing-{machine}-{mode}", Frame(shelf, mode));
    }

    //the a view on the discrete card, every regime counted on the count row
    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_LIFTED_SHELF(CorpusMode mode)
    {
        var (flow, _) = Walk("discrete");
        var lifted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlLift));
        CopyGolden.Check($"shelf-lifted-discrete-{mode}", Frame(lifted, mode));
    }

    //the row's publisher open on its pick, the keys in the pane
    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_OPEN_ACCORDION(CorpusMode mode)
    {
        var (_, shelf) = Walk("discrete");
        var ring = Shelf.Regions(shelf.Shelf!, 120, 0, shelf.Door is not null).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Shelf.Opening(shelf.Shelf!)) + ring.Count) % ring.Count;
        CopyGolden.Check($"shelf-open-discrete-{mode}",
            Frame(shelf, mode, [.. Enumerable.Repeat(Key(ConsoleKey.Tab), tabs), Key(ConsoleKey.Enter)]));
    }
}
