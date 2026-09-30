using Gatto.Core.Models;

namespace Gatto.Tests;

//gemma 4 writes head_count_kv per layer, so the term is the sum of the layers and a missing one over-prices the cache
public class PerLayerKvHeadsTests
{
    //the layers in the fixture differ on purpose, identical counts could not tell a sum from a first-element read or a layer count
    [Fact]
    public void A_PER_LAYER_ARRAY_IS_SUMMED_INTO_THE_TERM()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1])));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Equal(19, h.HeadCountKv);

        //4 is the first element, 5 the layer count and 8 the largest, so each NotEqual rules out one wrong reading
        Assert.NotEqual(4, h.HeadCountKv);
        Assert.NotEqual(5, h.HeadCountKv);
        Assert.NotEqual(8, h.HeadCountKv);
    }

    //the two headers differ only in the element type, which isolates the summing arm as the reason one has the term
    [Fact]
    public void ONLY_THE_TRUSTED_ELEMENT_TYPE_YIELDS_THE_TERM()
    {
        var summed = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1])));
        //an untrusted element type parses fine and leaves the term out on purpose
        var absent = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 4, 2, 8, 1], elemType: 6)));

        Assert.Equal(19, summed.HeadCountKv);
        Assert.Null(absent.HeadCountKv);

        //these two keep the rest of both headers equal, so the term is the only difference left
        Assert.Equal(absent.BlockCount, summed.BlockCount);
        Assert.Equal(GgufOutcome.Complete, absent.Outcome);
    }

    //an array of an untrusted type leaves the term absent, and the array is still consumed so the estimate stays conservative
    [Fact]
    public void AN_ARRAY_OF_A_TYPE_WE_DO_NOT_TRUST_LEAVES_THE_TERM_ABSENT()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([4, 2], elemType: 6)));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.HeadCountKv);
        //the header after the array still parses, which is how we know the array was consumed
        Assert.Equal(2, h.BlockCount);
    }

    //an empty array means absent rather than zero, since zero KV heads is not a fact about any model
    [Fact]
    public void AN_EMPTY_ARRAY_IS_ABSENT_NOT_ZERO()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithPerLayerKvHeads([])));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.HeadCountKv);
    }

    //only head_count_kv is summed, the other captured terms are per-model facts, and the fixture puts the array under one to catch a widening
    [Fact]
    public void NO_OTHER_TERM_IS_SUMMED_FROM_AN_ARRAY()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(
            GgufTestBytes.WithPerLayerKvHeads([4, 2, 8], key: "gemma4.block_count")));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.BlockCount);      //the array is consumed, and the term stays absent instead of summing to 14
        Assert.Null(h.HeadCountKv);

        //an array under a suffix nobody captures is skipped
        var other = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithArrayCount(3)));
        Assert.Null(other.HeadCountKv);
    }

    //a negative element must leave the term absent, since a wrapped sum can reach a plausible head count that passes every later check
    [Theory]
    [InlineData(long.MinValue, -1L)]
    [InlineData(long.MinValue, -9223372036854775803L)]
    public void A_NEGATIVE_ELEMENT_LEAVES_THE_TERM_ABSENT(long first, long second)
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithSignedKvHeadArray([first, second])));

        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.HeadCountKv);
    }

    //a signed array of ordinary counts must still sum, or the negative rule quietly turns into never summing signed arrays
    [Fact]
    public void A_SIGNED_ARRAY_OF_ORDINARY_COUNTS_STILL_SUMS()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithSignedKvHeadArray([4, 4, 2, 8, 1])));

        Assert.Equal(19, h.HeadCountKv);
    }

    //the summing arm shares the cap on element count, so this fixture reaches it through the summing path
    [Fact]
    public void A_LYING_ELEMENT_COUNT_IS_REFUSED_BY_THE_SUMMING_PATH()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(
            GgufTestBytes.WithPerLayerKvHeads([4, 2], declaredCount: 20_000_000)));

        Assert.Equal(GgufOutcome.Malformed, h.Outcome);
        Assert.Contains("implausibly large array count", h.Malformation ?? "", StringComparison.Ordinal);
    }
}
