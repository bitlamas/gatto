using Gatto.Core.Models;

namespace Gatto.Tests;

//the KV term against what llama-server allocated, each header built from a real file's values and parsed from bytes, so the parser and the arithmetic are pinned together
public class KvGeometryTests
{
    private const long MiB = 1L << 20;

    private static GgufHeader Header(string arch, params (string Key, object Value)[] terms) =>
        GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", arch);
            foreach (var (key, value) in terms)
            {
                var name = $"{arch}.{key}";
                switch (value)
                {
                    case uint n: kv.U32(name, n); break;
                    case int n: kv.U32(name, (uint)n); break;
                    case uint[] list: kv.U32Array(name, list); break;
                    case bool[] flags: kv.BoolArray(name, flags); break;
                    default: throw new ArgumentException(key);
                }
            }
        })));

    private static long Kv(GgufHeader h, int context, bool q8) =>
        FitArithmetic.Estimate(h, 0, context, q8 ? KvCacheKind.Q8_0 : KvCacheKind.F16).KvCacheBytes;

    //gemma 4 e4b: 42 blocks, the last 18 reuse another layer's cache, and of the 24 that keep one, every sixth is full attention
    private static GgufHeader Gemma4E4b() => Header("gemma4",
        ("block_count", 42), ("context_length", 131072), ("embedding_length", 2560),
        ("attention.head_count", 8), ("attention.head_count_kv", 2),
        ("attention.key_length", 512), ("attention.value_length", 512),
        ("attention.sliding_window", 512), ("attention.shared_kv_layers", 18),
        ("embedding_length_per_layer_input", 256),
        ("attention.sliding_window_pattern", Enumerable.Range(0, 42).Select(i => i % 6 != 5).ToArray()),
        ("attention.key_length_swa", 256), ("attention.value_length_swa", 256));

    //measured by llama-server at -np 1 and its default ubatch: 4 full layers at the context and 20 window layers at 1024 cells
    [Theory]
    [InlineData(4096, false, 104 * MiB)]
    [InlineData(32768, false, 552 * MiB)]
    [InlineData(65536, false, 1064 * MiB)]
    [InlineData(131072, false, 2088 * MiB)]
    [InlineData(4096, true, 57_933_824L)]
    [InlineData(32768, true, 307_494_912L)]
    [InlineData(65536, true, 592_707_584L)]
    [InlineData(131072, true, 1_163_132_928L)]
    public void GEMMA_4_PRICES_ONLY_THE_LAYERS_THAT_KEEP_A_CACHE_AND_THE_WINDOW_AT_ITS_CELLS(int context, bool q8, long measured)
    {
        Assert.Equal(measured, Kv(Gemma4E4b(), context, q8));
    }

    //qwen 3.8 flash next: 12 attention layers, and an indexer cache of 2 x 128 per token beside them, a quarter of the main cache
    private static GgufHeader Qwen4Exp() => Header("qwen4exp",
        ("block_count", 48), ("context_length", 262144), ("embedding_length", 2560),
        ("attention.head_count", 24), ("attention.head_count_kv", 2),
        ("attention.key_length", 256), ("attention.value_length", 256), ("full_attention_interval", 4),
        ("attention.indexer.head_count", 4), ("attention.indexer.key_length", 128),
        ("attention.compress_ratios", Enumerable.Range(0, 48).Select(i => i % 4 == 3 ? 4u : 0u).ToArray()),
        ("embedding_length_per_layer_input", 160));

    [Theory]
    [InlineData(4096, false, 120 * MiB)]
    [InlineData(32768, false, 960 * MiB)]
    [InlineData(65536, false, 1920 * MiB)]
    [InlineData(131072, false, 3840 * MiB)]
    [InlineData(4096, true, 66_846_720L)]
    [InlineData(32768, true, 510 * MiB)]
    [InlineData(65536, true, 1020 * MiB)]
    [InlineData(131072, true, 2040 * MiB)]
    public void THE_INDEXER_CACHE_IS_PRICED_BESIDE_THE_MAIN_ONE(int context, bool q8, long measured)
    {
        Assert.Equal(measured, Kv(Qwen4Exp(), context, q8));
    }

    //the qwen35 hybrids were already exact, and stay so
    [Theory]
    [InlineData("qwen35", 32, 4, 4096, false, 128 * MiB)]
    [InlineData("qwen35", 32, 4, 131072, false, 4096 * MiB)]
    [InlineData("qwen35", 32, 4, 131072, true, 2176 * MiB)]
    [InlineData("qwen35", 64, 4, 131072, false, 8192 * MiB)]
    [InlineData("qwen35", 64, 4, 131072, true, 4352 * MiB)]
    [InlineData("qwen35moe", 41, 2, 131072, false, 2560 * MiB)]
    [InlineData("qwen35moe", 41, 2, 131072, true, 1360 * MiB)]
    [InlineData("qwen35moe", 41, 2, 4096, true, 44_564_480L)]
    public void THE_HYBRIDS_KEEP_THEIR_EXACT_PRICE(string arch, int blocks, int kvHeads, int context, bool q8, long measured)
    {
        var h = Header(arch, ("block_count", blocks), ("context_length", 262144),
            ("attention.head_count", 16), ("attention.head_count_kv", kvHeads),
            ("attention.key_length", 256), ("attention.value_length", 256), ("full_attention_interval", 4),
            ("nextn_predict_layers", arch == "qwen35moe" ? 1 : 0));
        Assert.Equal(measured, Kv(h, context, q8));
    }

    //the main context keeps no cache for a nextn layer, so a 48-block hybrid whose last block is both nextn and an interval end prices 11 attention layers, not 12
    [Fact]
    public void A_NEXTN_LAYER_KEEPS_NO_CACHE_IN_THE_MAIN_CONTEXT()
    {
        var h = Header("qwen35moe", ("block_count", 48), ("attention.head_count_kv", 2),
            ("attention.key_length", 256), ("attention.value_length", 256), ("full_attention_interval", 4),
            ("nextn_predict_layers", 1));
        Assert.Equal(11L * 4096 * 2 * 512 * 2, Kv(h, 4096, q8: false));
    }

    //deepseek4 drops its nextn count and graniteswitch attends the layer it names, so both keep the cache on every block
    [Theory]
    [InlineData("deepseek4")]
    [InlineData("graniteswitch")]
    public void A_FAMILY_THAT_ATTENDS_ITS_NEXTN_LAYER_KEEPS_ITS_CACHE(string arch)
    {
        var h = Header(arch, ("block_count", 10), ("attention.head_count_kv", 2),
            ("attention.key_length", 128), ("attention.value_length", 128), ("nextn_predict_layers", 1));
        Assert.Equal(10L * 4096 * 2 * 256 * 2, Kv(h, 4096, q8: false));
    }

    private static uint[] EveryFourth(int blocks, int[]? also = null) =>
        Enumerable.Range(0, blocks).Select(i => i % 4 == 3 || (also?.Contains(i) ?? false) ? 1u : 0u).ToArray();

    //one real header per family at 65536 tokens and q8_0, priced as the source allocates, with the KDA recurrent state outside the KV term
    private static GgufHeader Family(string model) => model switch
    {
        "DeepSeek V3.1" => Header("deepseek2", ("block_count", 61), ("attention.head_count", 128), ("attention.head_count_kv", 1),
            ("attention.kv_lora_rank", 512), ("attention.key_length", 576), ("attention.value_length", 512),
            ("attention.key_length_mla", 192), ("attention.value_length_mla", 128), ("rope.dimension_count", 64)),
        "Kimi-Linear-48B-A3B" => Header("kimi-linear", ("block_count", 27), ("context_length", 1048576), ("attention.head_count", 32),
            ("attention.head_count_kv", EveryFourth(27, [26])),
            ("attention.key_length", 576), ("attention.value_length", 72), ("attention.kv_lora_rank", 512),
            ("rope.dimension_count", 64), ("attention.key_length_mla", 192), ("attention.value_length_mla", 128)),
        "GLM-4.6" => Header("glm4moe", ("block_count", 93), ("attention.head_count", 96), ("attention.head_count_kv", 8),
            ("attention.key_length", 128), ("attention.value_length", 128), ("nextn_predict_layers", 1)),
        "GLM-5.3" => Header("glm-dsa", ("block_count", 79), ("context_length", 1048576), ("attention.head_count", 64),
            ("attention.head_count_kv", 1), ("attention.key_length", 576), ("attention.value_length", 512),
            ("attention.kv_lora_rank", 512), ("attention.key_length_mla", 256), ("attention.value_length_mla", 256),
            ("rope.dimension_count", 64), ("nextn_predict_layers", 1),
            ("attention.indexer.head_count", 32), ("attention.indexer.key_length", 128)),
        "Ling-flash-2.0" => Header("bailingmoe2", ("block_count", 32), ("attention.head_count", 32), ("attention.head_count_kv", 4),
            ("attention.key_length", 128), ("attention.value_length", 128)),
        "Ling-3.0-tiny" => Header("bailingmoe3", ("block_count", 24), ("attention.head_count", 16),
            ("attention.head_count_kv", EveryFourth(24)), ("attention.key_length", 576), ("attention.value_length", 128),
            ("attention.kv_lora_rank", 512), ("rope.dimension_count", 64),
            ("attention.key_length_mla", 192), ("attention.value_length_mla", 128)),
        "gpt-oss-20b" => Header("gpt-oss", ("block_count", 24), ("attention.head_count", 64), ("attention.head_count_kv", 8),
            ("attention.key_length", 64), ("attention.value_length", 64), ("attention.sliding_window", 128)),
        "gpt-oss-120b" => Header("gpt-oss", ("block_count", 36), ("attention.head_count", 64), ("attention.head_count_kv", 8),
            ("attention.key_length", 64), ("attention.value_length", 64), ("attention.sliding_window", 128)),
        "Qwen3-Coder-30B-A3B" => Header("qwen3moe", ("block_count", 48), ("attention.head_count", 32), ("attention.head_count_kv", 4),
            ("attention.key_length", 128), ("attention.value_length", 128)),
        "Devstral-Small-2-24B" => Header("mistral3", ("block_count", 40), ("attention.head_count", 32), ("attention.head_count_kv", 8),
            ("attention.key_length", 128), ("attention.value_length", 128)),
        "Laguna-XS-2.1" => Header("laguna", ("block_count", 40), ("attention.head_count_kv", 8),
            ("attention.key_length", 128), ("attention.value_length", 128), ("attention.sliding_window", 512)),
        "Laguna-S-2.1" => Header("laguna", ("block_count", 48), ("attention.head_count_kv", 8),
            ("attention.key_length", 128), ("attention.value_length", 128), ("attention.sliding_window", 512)),
        "Muse-Glimmer-30B" => Header("muse-glimmer", ("block_count", 52), ("attention.head_count", 32), ("attention.head_count_kv", 2),
            ("attention.key_length", 128), ("attention.value_length", 128),
            ("attention.sliding_window", 2048), ("attention.sliding_window_pattern", 4)),
        _ => throw new ArgumentException(model),
    };

    [Theory]
    [InlineData("DeepSeek V3.1", 2_446_589_952L)]
    [InlineData("Kimi-Linear-48B-A3B", 280_756_224L)]
    [InlineData("GLM-4.6", 13_119_782_912L)]
    [InlineData("GLM-5.3", 3_315_597_312L)]
    [InlineData("Ling-flash-2.0", 2_281_701_376L)]
    [InlineData("Ling-3.0-tiny", 240_648_192L)]
    [InlineData("gpt-oss-20b", 865_665_024L)]
    [InlineData("gpt-oss-120b", 1_298_497_536L)]
    [InlineData("Qwen3-Coder-30B-A3B", 3_422_552_064L)]
    [InlineData("Devstral-Small-2-24B", 5_704_253_440L)]
    [InlineData("Laguna-XS-2.1", 1_492_910_080L)]
    [InlineData("Laguna-S-2.1", 1_791_492_096L)]
    [InlineData("Muse-Glimmer-30B", 517_783_552L)]
    public void EACH_FAMILY_PRICES_WHAT_ITS_SOURCE_ALLOCATES(string model, long f6)
    {
        Assert.Equal(f6, Kv(Family(model), 65536, q8: true));
    }

    //a window the code has no pattern for keeps the full price, since a wrong pattern would price low
    [Fact]
    public void AN_ARCHITECTURE_WITHOUT_A_KNOWN_PATTERN_PRICES_ITS_WINDOW_LAYERS_AT_THE_CONTEXT()
    {
        var h = Header("cohere2", ("block_count", 40), ("attention.head_count_kv", 8),
            ("attention.key_length", 128), ("attention.value_length", 128), ("attention.sliding_window", 4096));
        Assert.Equal(40L * 65536 * 8 * 256 * 2, Kv(h, 65536, q8: false));
    }

    //without the two MLA head lengths the engine keeps K and V, so the price does too
    [Fact]
    public void MLA_WITHOUT_ITS_HEAD_LENGTHS_KEEPS_K_AND_V()
    {
        var h = Header("deepseek2", ("block_count", 61), ("attention.head_count_kv", 1),
            ("attention.key_length", 576), ("attention.value_length", 512), ("attention.kv_lora_rank", 512));
        Assert.Equal(61L * 65536 * (576 + 512) * 2, Kv(h, 65536, q8: false));
    }

    //an indexer key on a family the code does not model adds nothing, and its main cache keeps K and V
    [Fact]
    public void AN_UNMODELLED_FAMILY_IS_PRICED_AS_BEFORE()
    {
        var h = Header("deepseek32", ("block_count", 61), ("attention.head_count_kv", 1),
            ("attention.key_length", 576), ("attention.value_length", 512),
            ("attention.key_length_mla", 192), ("attention.value_length_mla", 128), ("attention.indexer.key_length", 128));
        Assert.Equal(61L * 65536 * (576 + 512) * 2, Kv(h, 65536, q8: false));
    }

    //a per-layer head count is each layer's own, so five layers of 4, 4, 2, 8 and 1 price 19 layer-heads, not 19 on every layer
    [Fact]
    public void A_PER_LAYER_HEAD_COUNT_IS_PRICED_ONCE_PER_LAYER()
    {
        var h = Header("llama", ("block_count", 5), ("attention.head_count_kv", new uint[] { 4, 4, 2, 8, 1 }),
            ("attention.key_length", 128), ("attention.value_length", 128));
        Assert.Equal(19L * 4096 * 256 * 2, Kv(h, 4096, q8: false));
    }

    //an array that does not cover the blocks prices its largest count on every block, the conservative reading
    [Fact]
    public void A_PER_LAYER_ARRAY_OF_THE_WRONG_LENGTH_PRICES_ITS_LARGEST_ON_EVERY_BLOCK()
    {
        var h = Header("llama", ("block_count", 6), ("attention.head_count_kv", new uint[] { 4, 4, 2, 8, 1 }),
            ("attention.key_length", 128), ("attention.value_length", 128));
        var e = FitArithmetic.Estimate(h, 0, 4096, KvCacheKind.F16);
        Assert.Equal(6L * 8 * 4096 * 256 * 2, e.KvCacheBytes);
        Assert.Contains(e.AssumedTerms, t => t.Contains("head_count_kv"));
    }

    //a window wider than the context holds the context, so at 768 tokens every layer holds 768 cells
    [Fact]
    public void A_WINDOW_WIDER_THAN_THE_CONTEXT_HOLDS_THE_CONTEXT()
    {
        Assert.Equal((4L * 768 * 2 * 1024 + 20L * 768 * 2 * 512) * 2, Kv(Gemma4E4b(), 768, q8: false));
    }
}
