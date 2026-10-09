using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the three machines, a rich fake Hub and the flow that walks them, shared by the shelf's goldens and its mouse tests
internal static class ShelfFixtures
{
    private const long B = 1_000_000_000L;
    private const ulong GiB = 1UL << 30;

    public enum Entry { Setup, Model }

    //homes made for a caller that brought none, removed when the test process ends
    private static readonly List<string> Homes = [];
    static ShelfFixtures() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        lock (Homes)
            foreach (var h in Homes)
                try { Directory.Delete(h, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    };

    public static HardwareSnapshot Snapshot(string machine) => machine switch
    {
        "discrete" => new HardwareSnapshot(32 * GiB, 32 * GiB, GpuKind.Discrete, 8 * GiB, "0x1002"),
        "unified" => HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"),
        _ => new HardwareSnapshot(32 * GiB, 32 * GiB, GpuKind.None, null),
    };

    //each model's quants from three approved publishers, an encoder where the model sees images, and one model too big for every machine
    public static FakeShelfHub Catalog()
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
    public static (SetupFlow Flow, WizardScreen Screen) Flow(string machine, Entry entry = Entry.Setup, string? home = null,
        IReadOnlyList<FoundModel>? found = null)
    {
        if (home is null)
        {
            home = Directory.CreateTempSubdirectory("gatto-shelf-fixture-").FullName;
            lock (Homes) Homes.Add(home);
        }
        var live = new LiveSetupProbes(home, GlyphSet.Unicode, TextWriter.Null,
            readHardware: () => new ProbeOutcome(Snapshot(machine), "fixture"), hubHandler: Catalog());
        var probes = new WizardProbes
        {
            Snapshot = Snapshot(machine),
            Answer = request => live.SearchModels(request, null, CancellationToken.None),
            Found = found ?? [],
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, RowBudget = Gatto.Cli.Setup.Tui.Shelf.RowBudget(30) };
        return (flow, entry == Entry.Setup ? flow.StartPastEngine() : flow.StartAtModelSegment());
    }

    //the landing shelf of the setup walk, lifted to every regime when asked
    public static WizardScreen.Choice Shelf(string machine, bool lifted = false)
    {
        var (flow, screen) = Flow(machine);
        var shelf = Assert.IsType<WizardScreen.Choice>(screen);
        return lifted ? Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlLift)) : shelf;
    }

    //the 0-based console row an option's label is painted on, read from the settled frame so no layout arithmetic decides it
    public static int RowY(WizardScreen.Choice shelf, int row, int width, int height)
    {
        var rows = Gatto.Tests.Setup.Tui.WalkRender.SettledFrame(shelf, width, height: height).Rows;
        var label = shelf.Options[row].Label;
        var y = rows.ToList().FindIndex(r => r.Contains(label, StringComparison.Ordinal));
        return y >= 0 ? y : throw new InvalidOperationException($"option {row} ({label}) is not on the frame");
    }

    //a target of the frame the rig's face paints at this size after these keys, read off its hit map so a press lands where the user would click
    public static Gatto.Cli.Setup.Tui.HitTarget Target(WizardScreen.Choice c, Gatto.Cli.Setup.Tui.HitKind kind, int index = -1,
        string? key = null, int width = 120, int height = 0, params ConsoleKeyInfo[] keys) =>
        Targets(c, width, height, keys).Single(t => t.Tag.Kind == kind
            && (index < 0 || t.Tag.Index == index) && (key is null || t.Tag.Key == key));

    public static IReadOnlyList<Gatto.Cli.Setup.Tui.HitTarget> Targets(WizardScreen.Choice c, int width = 120, int height = 0,
        params ConsoleKeyInfo[] keys)
    {
        var rig = new WizardRig(width);
        rig.Surface.Height = height;
        var face = rig.TuiFace(keys);
        Assert.Throws<InvalidOperationException>(() => face.Choose(c));
        return face.LastHits.Targets;
    }

    //the 128 GB unified machine, classified, for a local pick that should find room for every file
    public static HardwareClass Big => HardwareClassifier.Classify(Snapshot("unified"));

    //found files with the header fields a local row reads: the architecture and the size label, none at all when both are null
    public static IReadOnlyList<FoundModel> LocalScan(params (string Path, string? Arch, string? Label, int Gb)[] files) =>
    [
        .. files.Select(f => new FoundModel(f.Path, f.Gb * B,
            f.Arch is null && f.Label is null ? null
                : new Gatto.Core.Models.GgufHeader(Gatto.Core.Models.GgufOutcome.Complete, null, f.Arch, null, 32768, null,
                    32, 32, 8, 4096, 128, 128, null, TensorCount: 300, SizeLabel: f.Label))),
    ];

    //one model's two quants in two folders, the local shelf's grouped row
    public static IReadOnlyList<FoundModel> GroupedScan() => LocalScan(
        (@"C:\models\Qwen3-8B-Q4_K_M.gguf", "qwen3", "8B", 5),
        (@"D:\weights\Qwen3-8B-Q8_0.gguf", "qwen3", "8B", 9));

    //a flow over found files, answered to the local shelf from gatto model's Hub landing
    public static (SetupFlow Flow, WizardScreen.Choice Shelf) LocalFlow(params (string, string?, string?, int)[] files)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = Snapshot("unified"),
            Found = LocalScan(files),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)));
    }

    //the plain text a target covers, sliced by display cells. a frame taller than the terminal scrolled, so the painted row is the target's row plus the excess
    public static string TextAt(IReadOnlyList<string> rows, Gatto.Cli.Setup.Tui.HitTarget t, int height = 0)
    {
        var excess = height > 0 ? Math.Max(0, rows.Count - height) : 0;
        var sb = new System.Text.StringBuilder();
        var col = 0;
        foreach (var rune in rows[t.Row + excess].EnumerateRunes())
        {
            var w = UnicodeWidth.Of(rune.ToString());
            if (col >= t.FirstCol && col + w - 1 <= t.LastCol) sb.Append(rune.ToString());
            col += w;
            if (col > t.LastCol) break;
        }
        return sb.ToString();
    }

    //the keys that open the selected row's publisher from the list: Tab round the ring to the files, then Enter
    public static ConsoleKeyInfo[] OpenKeys(WizardScreen.Choice shelf, int width = 120)
    {
        var ring = Gatto.Cli.Setup.Tui.Shelf.Regions(shelf.Shelf!, width, 0, shelf.Door is not null).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Gatto.Cli.Setup.Tui.Shelf.Opening(shelf.Shelf!)) + ring.Count) % ring.Count;
        return [.. Enumerable.Repeat(new ConsoleKeyInfo('\0', ConsoleKey.Tab, false, false, false), tabs),
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)];
    }
}
