using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//two sets of identical bytes that differ only in the per-layer marker, so every site that prices them must answer differently
public sealed class StreamedShardFitTests : IDisposable
{
    private const long TableBytes = 1_000_000_000;
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-streamedfit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    //a small machine whose 1.5 GB card holds a set less its 1 GB table, with that table beside it in a 1.2 GB RAM budget
    private static ProbeOutcome SmallMachine() => new(
        new HardwareSnapshot(InstalledBytes: 4_000_000_000, OsVisibleBytes: 2_400_000_000,
            GraphicsKind: GpuKind.Discrete, GraphicsMemoryBytes: 3_110_000_000, GraphicsVendorId: "1002"),
        "ok", CpuName: "test cpu", GpuName: "test gpu");

    private LiveSetupProbes Probes() =>
        new(Path.Combine(_dir, "home"), GlyphSet.Unicode, TextWriter.Null, readHardware: SmallMachine);

    private string Write(string name, byte[] header, long bytes)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        fs.Write(header);
        if (bytes > header.Length) fs.SetLength(bytes);
        return path;
    }

    //shard one holds the fit terms and no tensors, shard two holds a 1 GB table under the streamed name
    private string Set(string stem, bool marked, long secondShardBytes)
    {
        var one = Write($"{stem}-00001-of-00002.gguf", GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen4exp");
            kv.U32("qwen4exp.context_length", 32768);
            kv.U32("qwen4exp.block_count", 8);
            kv.U32("qwen4exp.embedding_length", 512);
            kv.U32("qwen4exp.attention.head_count", 8);
            kv.U32("qwen4exp.attention.head_count_kv", 2);
            kv.U32("qwen4exp.attention.key_length", 64);
            kv.U32("qwen4exp.attention.value_length", 64);
            if (marked) kv.U32("qwen4exp.embedding_length_per_layer_input", 160);
        }, tensorCount: 0), 10_000_000);
        Write($"{stem}-00002-of-00002.gguf",
            GgufTestBytes.WithTensors([("per_layer_token_embd.weight", [1UL, (ulong)(TableBytes / 4)], 0u)]),
            secondShardBytes);
        return one;
    }

    [Fact]
    public void THE_STREAMED_BYTES_OF_THE_FIXTURE_SET_ARE_ITS_TABLE()
    {
        var one = Set("marked", marked: true, 1_490_000_000);
        using var fs = File.OpenRead(one);
        Assert.Equal(TableBytes, ModelDiscovery.StreamedBytesOrNull(one, GgufHeaderParser.Parse(fs)));
    }

    //the test drives ModelScaffold.Create: a marked set maps its file lazily, and every other model keeps mmap off
    [Fact]
    public void THE_SCAFFOLD_WRITES_LAZY_MMAP_FOR_A_MARKED_SET_ONLY()
    {
        var models = Path.Combine(_dir, "models");
        var marked = Gatto.Roles.ModelScaffold.Create(models, Set("marked", marked: true, 1_490_000_000), port: 1235, id: "marked");
        var plain = Gatto.Roles.ModelScaffold.Create(models, Set("plain", marked: false, 1_490_000_000), port: 1235, id: "plain");

        var markedArgs = Gatto.Roles.Model.Load(models, marked).Profile.ExtraArgs;
        var plainArgs = Gatto.Roles.Model.Load(models, plain).Profile.ExtraArgs;
        Assert.Equal(["-np", "1", "-fa", "on", "-lm", "mmap", "--lazy-mode", "on", "--jinja", "--no-context-shift", "--cache-reuse", "0"], markedArgs);
        Assert.Equal(["-np", "1", "-fa", "on", "-lm", "none", "--jinja", "--no-context-shift", "--cache-reuse", "0"], plainArgs);

        var pin = int.Parse(Gatto.Roles.LlamaAssetSteering.PinnedRelease[1..]);
        Assert.Equal(markedArgs, LoadModeDialect.Load().Rewrite(markedArgs, pin));
        Assert.Equal(plainArgs, LoadModeDialect.Load().Rewrite(plainArgs, pin));
    }

    //the test drives LiveSetupProbes.PairFit
    [Fact]
    public void PAIR_FIT_SUBTRACTS_THE_TABLE_OF_A_MARKED_SET()
    {
        using var probes = Probes();
        var marked = probes.PairFit(Set("marked", marked: true, 1_490_000_000), projectorBytes: 0);
        var plain = probes.PairFit(Set("plain", marked: false, 1_490_000_000), projectorBytes: 0);

        Assert.NotNull(marked);
        Assert.NotNull(plain);
        Assert.NotEqual(plain!.Value.Alone, marked!.Value.Alone);
    }

    //the test drives LiveSetupProbes.ContextFor
    [Fact]
    public void THE_CONTEXT_LADDER_SUBTRACTS_THE_TABLE_OF_A_MARKED_SET()
    {
        using var probes = Probes();
        var marked = probes.ContextFor(Set("marked", marked: true, 1_400_000_000));
        var plain = probes.ContextFor(Set("plain", marked: false, 1_400_000_000));

        Assert.True(marked > plain, $"the marked set took {marked} and the plain set took {plain}; the ladder is not subtracting the table");
    }

    //the test drives ModelDiscovery.Scan, which hands the table to LocalShelf.Row
    [Fact]
    public void THE_LOCAL_SHELF_PRICES_A_SCANNED_MARKED_SET_WITHOUT_ITS_TABLE()
    {
        Set("marked", marked: true, 1_490_000_000);
        var found = Assert.Single(ModelDiscovery.Scan([_dir], depth: 0));
        Assert.Equal(TableBytes, found.StreamedBytes);

        var hw = HardwareClassifier.Classify(SmallMachine().Snapshot!);
        var withTable = LocalShelf.Row(found, hw).Fit;
        var withoutTable = LocalShelf.Row(found with { StreamedBytes = null }, hw).Fit;
        Assert.NotEqual(withoutTable, withTable);
    }
}
