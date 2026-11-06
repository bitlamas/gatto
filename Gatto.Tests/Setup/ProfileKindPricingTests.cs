using Gatto.Cli.Setup;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//every fit prices the cache type the scaffold writes, so the ladder keeps a rung an f16 price would have lost
public sealed class ProfileKindPricingTests : IDisposable
{
    private const long GiB = 1L << 30;
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-profilekind-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    //the kind every fit prices is the one the scaffold writes into a new profile, so the two cannot drift apart
    [Fact]
    public void THE_PRICED_KIND_IS_THE_ONE_THE_SCAFFOLD_WRITES()
    {
        var models = Path.Combine(_dir, "models");
        var id = Gatto.Roles.Models.ModelScaffold.Create(models, Model("any.gguf", GiB), port: 1235, id: "any");
        var profile = Gatto.Roles.Models.Model.Load(models, id).Profile;

        Assert.Equal(KvCacheKind.Q8_0, FitArithmetic.ProfileKindFor(HardwareClassifier.Classify(new HardwareSnapshot(32UL << 30, 32UL << 30, GpuKind.Discrete, 8UL << 30))));
        Assert.Equal("q8_0", profile.CacheTypeK);
        Assert.Equal("q8_0", profile.CacheTypeV);
    }

    //a desktop with a 24 GiB card and 32 GiB of memory, where only the card decides
    private static ProbeOutcome Desktop() => new(
        new HardwareSnapshot(InstalledBytes: 32UL << 30, OsVisibleBytes: 31UL << 30,
            GraphicsKind: GpuKind.Discrete, GraphicsMemoryBytes: 24UL << 30, GraphicsVendorId: "10DE"),
        "ok", CpuName: "test cpu", GpuName: "test gpu");

    //32 blocks of 8 kv heads with 256 key and value dims: 8 GiB of f16 cache at the 65536 ceiling, 4.25 GiB at q8_0
    private string Model(string name, long bytes)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        fs.Write(GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.U32("llama.context_length", 65536);
            kv.U32("llama.block_count", 32);
            kv.U32("llama.embedding_length", 4096);
            kv.U32("llama.attention.head_count", 32);
            kv.U32("llama.attention.head_count_kv", 8);
            kv.U32("llama.attention.key_length", 128);
            kv.U32("llama.attention.value_length", 128);
        }, tensorCount: 0));
        fs.SetLength(bytes);
        return path;
    }

    private static FitRegime At(string path, int context, KvCacheKind kind)
    {
        var hw = HardwareClassifier.Classify(Desktop().Snapshot!);
        using var fs = File.OpenRead(path);
        return FitArithmetic.Judge(FitArithmetic.Estimate(GgufHeaderParser.Parse(fs), new FileInfo(path).Length, context, kind), hw);
    }

    //a 16 GiB file on a 22.5 GiB budget: whole on the card with its q8_0 cache at the ceiling, a split at the f16 price
    [Fact]
    public void THE_LADDER_KEEPS_THE_RUNG_THE_F16_PRICE_LOST()
    {
        var path = Model("tight.gguf", 16 * GiB);
        Assert.Equal(FitRegime.FitsSplit, At(path, 65536, KvCacheKind.F16));
        Assert.Equal(FitRegime.FitsGpu, At(path, 65536, KvCacheKind.Q8_0));

        using var probes = new LiveSetupProbes(Path.Combine(_dir, "home"), GlyphSet.Unicode, TextWriter.Null, readHardware: Desktop);

        Assert.Equal(65536, probes.ContextFor(path));
        Assert.Null(probes.SplitLayersFor(path, 65536));
    }
}
