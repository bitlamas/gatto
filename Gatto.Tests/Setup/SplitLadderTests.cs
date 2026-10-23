using Gatto.Cli.Setup;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the context a profile gets: a file the shelf marks as a split keeps a rung where the card holds layers, every other file keeps the ladder it had
public sealed class SplitLadderTests : IDisposable
{
    private const long GiB = 1L << 30;
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-splitladder-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    //a laptop with a 4 GiB card and 32 GiB of memory
    private static ProbeOutcome Laptop() => new(
        new HardwareSnapshot(InstalledBytes: 32UL << 30, OsVisibleBytes: 31UL << 30,
            GraphicsKind: GpuKind.Discrete, GraphicsMemoryBytes: 4UL << 30, GraphicsVendorId: "10DE"),
        "ok", CpuName: "test cpu", GpuName: "test gpu");

    private LiveSetupProbes Probes() =>
        new(Path.Combine(_dir, "home"), GlyphSet.Unicode, TextWriter.Null, readHardware: Laptop);

    //32 blocks of 8 kv heads with 256 key and value dims. the f16 cache is half a GiB at 4096 tokens and 8 GiB at the 65536 ceiling, where a 4 GiB card holds no layer
    private string Model(string name, long bytes, bool blocks = true)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        fs.Write(GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.U32("llama.context_length", 65536);
            if (blocks) kv.U32("llama.block_count", 32);
            kv.U32("llama.embedding_length", 4096);
            kv.U32("llama.attention.head_count", 32);
            kv.U32("llama.attention.head_count_kv", 8);
            kv.U32("llama.attention.key_length", 128);
            kv.U32("llama.attention.value_length", 128);
        }, tensorCount: 0));
        fs.SetLength(bytes);
        return path;
    }

    private static FitRegime At(string path, int context)
    {
        var hw = HardwareClassifier.Classify(Laptop().Snapshot!);
        using var fs = File.OpenRead(path);
        var header = GgufHeaderParser.Parse(fs);
        return FitArithmetic.Judge(FitArithmetic.Estimate(header, new FileInfo(path).Length, context, KvCacheKind.F16), hw);
    }

    [Fact]
    public void A_FILE_THE_SHELF_MARKS_AS_A_SPLIT_TAKES_A_SPLIT_RUNG_AND_ITS_LAYERS()
    {
        var path = Model("split.gguf", 4 * GiB);
        Assert.Equal(FitRegime.FitsSplit, At(path, Gatto.Cli.Setup.Shelves.Pane.KvContext));
        //the old ladder would have stopped at the top rung, where the file fits memory alone
        Assert.Equal(FitRegime.FitsRamOnly, At(path, 65536));

        using var probes = Probes();
        var context = probes.ContextFor(path);

        Assert.True(context < 65536, $"the split file took {context}");
        Assert.Equal(FitRegime.FitsSplit, At(path, context));
        Assert.True(probes.SplitLayersFor(path, context) is >= 1, "the split rung carries no layer count");
    }

    private static readonly int[] Rungs = [65536, 32768, 16384, 8192, 4096];

    //the 32 blocks of these files, half of them 16
    private const int Half = 16;

    //layers before context: the largest rung holding at least half the layers on the card, though a larger rung still splits with fewer
    [Fact]
    public void A_SPLIT_TAKES_THE_LARGEST_RUNG_HOLDING_HALF_ITS_LAYERS()
    {
        var path = Model("split.gguf", 4 * GiB);
        using var probes = Probes();
        var layers = Rungs.ToDictionary(r => r, r => probes.SplitLayersFor(path, r));
        Assert.Contains(Rungs, r => layers[r] is { } n && n < Half);

        var context = probes.ContextFor(path);

        Assert.True(layers[context] >= Half, $"{context} holds {layers[context]} layers");
        Assert.All(Rungs.Where(r => r > context), r => Assert.True(layers[r] is null or < Half, $"{r} holds {layers[r]} layers and is larger"));
    }

    //a file no rung half-holds takes the rung holding the most layers, not the largest that splits
    [Fact]
    public void A_SPLIT_NO_RUNG_HALF_HOLDS_TAKES_THE_MOST_LAYERS()
    {
        var path = Model("big.gguf", 12 * GiB);
        Assert.Equal(FitRegime.FitsSplit, At(path, Gatto.Cli.Setup.Shelves.Pane.KvContext));
        using var probes = Probes();
        var layers = Rungs.ToDictionary(r => r, r => probes.SplitLayersFor(path, r) ?? 0);
        Assert.All(Rungs, r => Assert.True(layers[r] < Half, $"{r} holds {layers[r]} layers"));

        var context = probes.ContextFor(path);

        Assert.Equal(layers.Values.Max(), layers[context]);
    }

    //a projector loads beside the model, so a split with one holds fewer layers on the card
    [Fact]
    public void A_PROJECTOR_BESIDE_A_SPLIT_LEAVES_FEWER_LAYERS_ON_THE_CARD()
    {
        var path = Model("split.gguf", 4 * GiB);
        var projector = Path.Combine(_dir, "mmproj.gguf");
        using (var fs = File.Create(projector)) fs.SetLength(GiB / 2);

        using var probes = Probes();
        var alone = probes.SplitLayersFor(path, Gatto.Cli.Setup.Shelves.Pane.KvContext);
        var paired = probes.SplitLayersFor(path, Gatto.Cli.Setup.Shelves.Pane.KvContext, projector);

        Assert.NotNull(alone);
        Assert.NotNull(paired);
        Assert.True(paired < alone, $"the pair holds {paired} layers and the model alone {alone}");
    }

    [Fact]
    public void A_FILE_THE_CARD_HOLDS_WHOLE_ON_THE_SHELF_KEEPS_TODAYS_LADDER()
    {
        var path = Model("whole.gguf", GiB);
        Assert.Equal(FitRegime.FitsGpu, At(path, Gatto.Cli.Setup.Shelves.Pane.KvContext));

        using var probes = Probes();

        Assert.Equal(65536, probes.ContextFor(path));
    }

    [Fact]
    public void A_FILE_WITH_NO_SPLIT_ON_THE_SHELF_KEEPS_TODAYS_LADDER()
    {
        //no block count, so no split is priced and memory takes the file at its ceiling as it always did
        var path = Model("noblocks.gguf", 4 * GiB, blocks: false);
        Assert.Equal(FitRegime.FitsRamOnly, At(path, Gatto.Cli.Setup.Shelves.Pane.KvContext));

        using var probes = Probes();

        Assert.Equal(65536, probes.ContextFor(path));
        Assert.Null(probes.SplitLayersFor(path, 65536));
    }
}
