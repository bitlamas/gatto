using System.Text;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the engine never places a per-layer input table on the GPU, so the fit prices the file without that table
public sealed class StreamedTensorFitTests : IDisposable
{
    private const long SetBytes = 102_474_187_584;
    private const long TableBytes = 28_800_138_240;

    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-streamed-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "dn4", name);

    private static string Shard(int i) => $"Qwen3.8-Flash-Next-Q4_K_XL-DN4-{i:D5}-of-00004.gguf";

    private static GgufTensors? TableOf(string path)
    {
        using var fs = File.OpenRead(path);
        return GgufHeaderParser.Parse(fs).Tensors;
    }

    private static GgufHeader Header(long? perLayerInput) => new(
        GgufOutcome.Complete, null, "qwen4exp", "Qwen3.8 Flash Next", 262144, null,
        BlockCount: 48, HeadCount: 32, HeadCountKv: 2, EmbeddingLength: 4096, KeyLength: 256, ValueLength: 256,
        ChatTemplate: null, EmbeddingLengthPerLayerInput: perLayerInput);

    private static HardwareClass Unified(ulong gpuBudget) => new(
        MemoryTopology.Unified, ShareKind.Dynamic, gpuBudget, gpuBudget,
        new HardwareSnapshot(null, 1UL, GpuKind.Integrated, null), 0, BudgetBound.Heap);

    //the metadata-only first shard of a set, with the key that marks a per-layer input and no tensors
    private static byte[] MarkerShard()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        w.Write(0x46554747u); w.Write(3u); w.Write(0UL); w.Write(2UL);
        Str("general.architecture"); w.Write(8u); Str("qwen4exp");
        Str("qwen4exp.embedding_length_per_layer_input"); w.Write(4u); w.Write(160u);
        return ms.ToArray();
    }

    [Fact]
    public void THE_REAL_TABLES_HOLD_ONE_STREAMED_TENSOR_OF_28_8_GB()
    {
        var tables = new[] { 2, 3, 4 }.Select(i => TableOf(Fixture(Shard(i)))).ToList();
        Assert.All(tables, Assert.NotNull);
        Assert.Equal(TableBytes, StreamedTensors.Load().BytesIn(tables));
    }

    [Fact]
    public void THE_FIT_PRICES_THE_RESIDENT_WEIGHTS_AND_THE_MODEL_FITS_THE_MACHINE_THAT_SERVES_IT()
    {
        var e = FitArithmetic.Estimate(Header(160), SetBytes, 4096, KvCacheKind.F16, TableBytes);

        Assert.Equal(73_674_049_344, e.WeightsBytes);
        Assert.Empty(e.AssumedTerms);
        //the budget gatto computes for this machine: 82.56 GiB of heap less the 8 % reserve
        Assert.Equal(FitRegime.FitsGpu, FitArithmetic.Judge(e, Unified(81_561_000_000)));
    }

    //a hybrid architecture keeps a cache only on its attention blocks. the model declares one in every four of its 48 blocks
    [Fact]
    public void A_HYBRID_ARCHITECTURE_PRICES_THE_CACHE_OF_ITS_ATTENTION_BLOCKS_ONLY()
    {
        var hybrid = Header(160) with { HeadCountKv = 2, KeyLength = 256, ValueLength = 256, FullAttentionInterval = 4 };
        Assert.Equal(3_422_552_064, FitArithmetic.Estimate(hybrid, SetBytes, 262_144, KvCacheKind.Q8_0, TableBytes).KvCacheBytes);
    }

    //an interval that divides nothing, or none at all, is every block, which is the conservative reading
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-4L)]
    [InlineData(1L)]
    [InlineData(96L)]
    public void AN_ABSENT_OR_IMPLAUSIBLE_INTERVAL_PRICES_EVERY_BLOCK(long? interval)
    {
        var header = Header(160) with { HeadCountKv = 2, KeyLength = 256, ValueLength = 256, FullAttentionInterval = interval };
        Assert.Equal(13_690_208_256, FitArithmetic.Estimate(header, SetBytes, 262_144, KvCacheKind.Q8_0, TableBytes).KvCacheBytes);
    }

    [Fact]
    public void THE_PARSER_READS_THE_INTERVAL()
    {
        var bytes = GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen4exp");
            kv.U32("qwen4exp.full_attention_interval", 4);
        });
        Assert.Equal(4, GgufHeaderParser.Parse(new MemoryStream(bytes)).FullAttentionInterval);
    }

    [Fact]
    public void AN_UNREAD_TABLE_ON_A_MARKED_MODEL_IS_PRICED_WHOLE_AND_SAYS_SO()
    {
        var e = FitArithmetic.Estimate(Header(160), SetBytes, 4096, KvCacheKind.F16);

        Assert.Equal(SetBytes, e.WeightsBytes);
        Assert.Contains(e.AssumedTerms, t => t.StartsWith("priced as the whole file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_MODEL_WITHOUT_THE_MARKER_IS_PRICED_AS_BEFORE_AND_ASSUMES_NOTHING()
    {
        var e = FitArithmetic.Estimate(Header(null) with { Architecture = "qwen3" }, SetBytes, 4096, KvCacheKind.F16);

        Assert.Equal(SetBytes, e.WeightsBytes);
        Assert.Empty(e.AssumedTerms);
    }

    [Fact]
    public void A_TABLE_CLAIMING_MORE_THAN_THE_FILE_CANNOT_MAKE_THE_WEIGHTS_NEGATIVE() =>
        Assert.Equal(0, FitArithmetic.Estimate(Header(160), 1_000, 4096, KvCacheKind.F16, 5_000).WeightsBytes);

    [Fact]
    public void DISCOVERY_SUMS_THE_TABLES_ACROSS_THE_SET_AND_READS_THE_EMPTY_FIRST_SHARD_AS_EMPTY()
    {
        File.WriteAllBytes(Path.Combine(_dir, Shard(1)), MarkerShard());
        foreach (var i in new[] { 2, 3, 4 }) File.Copy(Fixture(Shard(i)), Path.Combine(_dir, Shard(i)));
        var first = Path.Combine(_dir, Shard(1));
        using var fs = File.OpenRead(first);
        var header = GgufHeaderParser.Parse(fs);

        Assert.True(header.DeclaresPerLayerInput);
        Assert.Equal(TableBytes, ModelDiscovery.StreamedBytesOrNull(first, header));
    }

    //a partial sum prices the model as streaming less than it does, so one unread table leaves the whole sum unknown
    [Fact]
    public void ONE_UNREAD_TABLE_MAKES_THE_SUM_UNKNOWN() =>
        Assert.Null(StreamedTensors.Load().BytesIn([TableOf(Fixture(Shard(2))), null]));

    [Fact]
    public void DISCOVERY_READS_NO_TABLE_FOR_A_MODEL_WITHOUT_THE_MARKER() =>
        Assert.Null(ModelDiscovery.StreamedBytesOrNull(Fixture(Shard(2)), Header(null)));

    //the list is tied to the engine pin, so a build that stops streaming a table needs the list re-read
    [Fact]
    public void THE_LIST_WAS_REVIEWED_AT_THE_PINNED_RELEASE()
    {
        var list = StreamedTensors.Load();
        Assert.Equal(Gatto.Roles.LlamaAssetSteering.PinnedRelease, list.Release);
        Assert.Contains("per_layer_token_embd.weight", list.Names);
    }
}
