using Gatto.Core.Models;

namespace Gatto.Tests;

//gemma 4, Kimi-Linear and Ling 3 write head_count_kv per layer, so the term is kept layer by layer rather than summed
public class PerLayerKvHeadsTests
{
    //the layers in the fixture differ on purpose, identical counts could not tell a kept array from a sum, a first-element read or a layer count
    [Fact]
    public void A_PER_LAYER_ARRAY_IS_KEPT_LAYER_BY_LAYER()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1])));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Equal(new long[] { 4, 4, 2, 8, 1 }, h.HeadCountKvPerLayer!);
        Assert.Null(h.HeadCountKv);   //the scalar term is for the scalar form, so no sum reaches it
    }

    //the two headers differ only in the element type, which isolates the integer arm as the reason one has the term
    [Fact]
    public void ONLY_THE_TRUSTED_ELEMENT_TYPE_YIELDS_THE_TERM()
    {
        var kept = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1])));
        //an untrusted element type parses fine and leaves the term out on purpose
        var absent = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1], elemType: 6)));

        Assert.Equal(5, kept.HeadCountKvPerLayer!.Count);
        Assert.Null(absent.HeadCountKvPerLayer);

        //these two keep the rest of both headers equal, so the term is the only difference left
        Assert.Equal(absent.BlockCount, kept.BlockCount);
        Assert.Equal(GgufOutcome.Complete, absent.Outcome);
    }

    //an array of an untrusted type leaves the term absent, and the array is still consumed so the estimate stays conservative
    [Fact]
    public void AN_ARRAY_OF_A_TYPE_WE_DO_NOT_TRUST_LEAVES_THE_TERM_ABSENT()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 2], elemType: 6)));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.HeadCountKvPerLayer);
        //the header after the array still parses, which is how we know the array was consumed
        Assert.Equal(2, h.BlockCount);
    }

    //an empty array means absent rather than zero layers, since a cache of no layers is not a fact about any model
    [Fact]
    public void AN_EMPTY_ARRAY_IS_ABSENT_NOT_ZERO()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([])));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.HeadCountKvPerLayer);
    }

    //only the per-layer suffixes keep an array, the other captured terms are per-model facts, and the fixture puts the array under one to catch a widening
    [Fact]
    public void NO_OTHER_TERM_IS_KEPT_FROM_AN_ARRAY()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(
            GgufTestBytes.WithPerLayerKvHeads([4, 2, 8], key: "gemma4.block_count")));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.BlockCount);      //the array is consumed, and the term stays absent instead of summing to 14
        Assert.Null(h.HeadCountKvPerLayer);

        //an array under a suffix nobody captures is skipped
        var other = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithArrayCount(3)));
        Assert.Null(other.HeadCountKvPerLayer);
    }

    //a negative element must leave the term absent, since no layer has fewer than zero heads
    [Theory]
    [InlineData(long.MinValue, -1L)]
    [InlineData(4L, -9223372036854775803L)]
    public void A_NEGATIVE_ELEMENT_LEAVES_THE_TERM_ABSENT(long first, long second)
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithSignedKvHeadArray([first, second])));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.HeadCountKvPerLayer);
    }

    //a signed array of ordinary counts must still be kept, or the negative rule quietly turns into never reading signed arrays
    [Fact]
    public void A_SIGNED_ARRAY_OF_ORDINARY_COUNTS_IS_KEPT()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithSignedKvHeadArray([4, 4, 2, 8, 1])));

        Assert.Equal(new long[] { 4, 4, 2, 8, 1 }, h.HeadCountKvPerLayer!);
    }

    //the per-layer arm shares the cap on element count, so this fixture reaches it through that path
    [Fact]
    public void A_LYING_ELEMENT_COUNT_IS_REFUSED_BY_THE_PER_LAYER_PATH()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(
            GgufTestBytes.WithPerLayerKvHeads([4, 2], declaredCount: 20_000_000)));

        Assert.Equal(GgufOutcome.Malformed, h.Outcome);
        Assert.Contains("implausibly large array count", h.Malformation ?? "", StringComparison.Ordinal);
    }

    //each layer's own count prices 19 layer-heads, where a sum multiplied by the blocks would price five layers of 19 heads
    [Fact]
    public void THE_CACHE_IS_PRICED_FROM_EACH_LAYERS_OWN_COUNT()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1]))) with { KeyLength = 128, ValueLength = 128 };

        Assert.Equal(19L * 4096 * 256 * 2, FitArithmetic.Estimate(h, 0, 4096, KvCacheKind.F16).KvCacheBytes);
    }
}
