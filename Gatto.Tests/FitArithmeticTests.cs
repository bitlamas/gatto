using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//compute every expected number from the file's terms rather than a per-model table. a missing term errs conservative or becomes unknown with a widened margin
public class FitArithmeticTests
{
    private static GgufHeader Full() => new(
        GgufOutcome.Complete, null, "qwen3", "t", 32768, null,
        BlockCount: 48, HeadCount: 40, HeadCountKv: 8,
        EmbeddingLength: 5120, KeyLength: 128, ValueLength: 128, ChatTemplate: null);

    [Fact]
    public void Kv_cache_formula_full_terms_f16()
    {
        var e = FitArithmetic.Estimate(Full(), fileBytes: 20_000_000_000, contextLength: 32768, KvCacheKind.F16);
        //the product is 48 × 32768 × 8 × (128+128) × 2. those are the block count, context length, kv heads, key and value sizes, and the f16 byte width
        Assert.Equal(6_442_450_944L, e.KvCacheBytes);
        Assert.Equal(20_000_000_000L, e.WeightsBytes);
        Assert.Empty(e.UnknownTerms); Assert.Empty(e.AssumedTerms);
    }

    [Fact]
    public void Q8_0_prices_the_block_overhead_not_a_naive_byte()
    {
        var e = FitArithmetic.Estimate(Full(), 0, 32768, KvCacheKind.Q8_0);
        //q8_0 costs 34 bytes per 32 elements, computed with integers end to end. the expected value is a literal, so a floating path that drifts by one cannot hide.
        Assert.Equal(3_422_552_064L, e.KvCacheBytes);
    }

    [Fact]
    public void Missing_kv_heads_assumes_no_gqa_and_records_the_assumption()
    {
        var h = Full() with { HeadCountKv = null };
        var e = FitArithmetic.Estimate(h, 0, 32768, KvCacheKind.F16);
        Assert.Equal(48L * 32768 * 40 * 256 * 2, e.KvCacheBytes);   //a missing kv head count assumes no grouped-query attention, so the price uses 40 heads instead of 8.
        Assert.Contains(e.AssumedTerms, t => t.Contains("head_count_kv"));
        Assert.Empty(e.UnknownTerms);                               //a conservative assumption is still a known value, so nothing appears in the unknown list.
    }

    [Fact]
    public void Missing_head_dim_falls_back_to_embedding_over_heads()
    {
        //the override to 6400 makes the fallback route differ from the key and value route. with 5120 both give 256, so the number cannot tell the routes apart
        var h = Full() with { KeyLength = null, ValueLength = null, EmbeddingLength = 6400 };
        var e = FitArithmetic.Estimate(h, 0, 32768, KvCacheKind.F16);
        Assert.Equal(48L * 32768 * 8 * (2 * (6400 / 40)) * 2, e.KvCacheBytes);   //the fallback route gives 320 where the key and value route would give 256.
        Assert.Contains(e.AssumedTerms, t => t.Contains("head_dim"));
    }

    [Fact]
    public void Fallback_head_dim_division_rounds_UP_the_conservative_direction()
    {
        //5120 divided by 30 is 170.67, and truncation would price the cache too low. a ceiling gives 171, so key plus value is 342
        var h = Full() with { KeyLength = null, ValueLength = null, EmbeddingLength = 5120, HeadCount = 30 };
        var e = FitArithmetic.Estimate(h, 0, 32768, KvCacheKind.F16);
        Assert.Equal(48L * 32768 * 8 * 342 * 2, e.KvCacheBytes);
    }

    [Fact]
    public void Unpriceable_kv_is_zero_plus_an_unknown_term_never_a_silent_guess()
    {
        var h = Full() with { BlockCount = null };
        var e = FitArithmetic.Estimate(h, 5_000_000_000, 32768, KvCacheKind.F16);
        Assert.Equal(0L, e.KvCacheBytes);
        Assert.Contains(e.UnknownTerms, t => t.Contains("block_count"));
    }

    //the helper builds the hardware class that Judge reads. the fit boundary is <=, since each budget already holds its reserve
    private static HardwareClass Gpu(ulong gpuBudget, ulong ramBudget) => new(
        MemoryTopology.Unified, ShareKind.CarvedOut, gpuBudget, ramBudget,
        new HardwareSnapshot(0UL, 1UL, GpuKind.Integrated, 0UL), 0, BudgetBound.None);

    //the expected value is an int, since FitRegime is internal and xUnit needs public theory data
    [Theory]
    [InlineData(30_000_000_000UL, 16_000_000_000UL, (int)FitRegime.FitsGpu)]     //the gpu pool alone holds the total.
    [InlineData(20_000_000_000UL, 26_442_450_944UL, (int)FitRegime.FitsRamOnly)] //the pool is too small, and system memory holds the total.
    [InlineData(20_000_000_000UL, 16_000_000_000UL, (int)FitRegime.DoesNotFit)]
    public void Judge_walks_gpu_then_ram_then_honesty(ulong gpu, ulong ram, int expected)
    {
        var e = FitArithmetic.Estimate(Full(), 20_000_000_000, 32768, KvCacheKind.F16);
        Assert.Equal((FitRegime)expected, FitArithmetic.Judge(e, Gpu(gpu, ram)));  //the estimate totals 26_442_450_944 bytes for all three rows.
    }

    [Fact]
    public void Exact_fit_passes_the_boundary_is_lte()
    {
        var e = FitArithmetic.Estimate(Full(), 20_000_000_000, 32768, KvCacheKind.F16);
        Assert.Equal(FitRegime.FitsGpu, FitArithmetic.Judge(e, Gpu(26_442_450_944UL, 0UL)));
    }

    [Fact]
    public void Unknown_terms_widen_the_margin_by_the_named_headroom()
    {
        var h = Full() with { BlockCount = null };                          //a missing block count makes the kv cache unpriceable.
        var e = FitArithmetic.Estimate(h, 20_000_000_000, 32768, KvCacheKind.F16);
        Assert.Equal(FitRegime.DoesNotFit,
            FitArithmetic.Judge(e, Gpu(24_000_000_000UL, 0UL)));            //the widened 20 GB estimate becomes 25 GB, above the 24 GB pool.
        Assert.Equal(FitRegime.FitsGpu,
            FitArithmetic.Judge(e, Gpu(25_000_000_000UL, 0UL)));
    }

    [Fact]
    public void Nonpositive_context_is_a_caller_bug_and_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FitArithmetic.Estimate(Full(), 0, 0, KvCacheKind.F16));
    }

    [Fact]
    public void A_lying_file_size_clamps_instead_of_overflowing_the_judge()
    {
        //clamp the file size, an unclamped one overflows the headroom multiply in Judge and throws OverflowException into the wizard
        var h = Full() with { BlockCount = null };
        var e = FitArithmetic.Estimate(h, long.MaxValue / 4, 32768, KvCacheKind.F16);
        Assert.Equal(4_398_046_511_104L, e.WeightsBytes);                    //the clamp caps the weight size at 4 TB.
        Assert.Equal(FitRegime.DoesNotFit,                                   //the clamped 4 TB fails even a 100 GB budget, so the verdict is a real answer rather than an overflow.
            FitArithmetic.Judge(e, Gpu(100_000_000_000UL, 0UL)));
        //the assumed term must name the clamp and the claimed size. without it, a clamped 4 TB reads like an honest 4 TB model.
        Assert.Contains(e.AssumedTerms, t => t.Contains("file size clamped from"));
        Assert.Contains(e.AssumedTerms, t => t.Contains("2,305,843,009,213,693,951"));
    }

    [Fact]
    public void A_file_size_at_the_cap_passes_through_unclamped()
    {
        var e = FitArithmetic.Estimate(Full(), 4_398_046_511_104L, 32768, KvCacheKind.F16);
        Assert.Equal(4_398_046_511_104L, e.WeightsBytes);
    }

    [Fact]
    public void Implausible_header_terms_are_unknown_never_a_silent_overflow()
    {
        //cap the header terms, an uncapped block count overflows or goes negative in the float cast. an implausible term becomes unknown, the same as an absent one
        var h = Full() with { BlockCount = long.MaxValue / 2 };
        var e = FitArithmetic.Estimate(h, 5_000_000_000, 32768, KvCacheKind.F16);
        Assert.Equal(0L, e.KvCacheBytes);
        Assert.Contains(e.UnknownTerms, t => t.Contains("block_count"));
        Assert.Equal(FitRegime.DoesNotFit,                       //the widened 5 GB estimate exceeds the 6 GB budget.
            FitArithmetic.Judge(e, Gpu(6_000_000_000UL, 0UL)));  //the budget holds the plain 5 GB, so only the widened margin makes it fail.
    }
}
