using Gatto.Core.Models;

namespace Gatto.Tests;

//the tensor section is read once and both uses share the result. each test works its expected size out from the format's rule
public class GgufTensorWalkTests
{
    private const uint F32 = 0, Q4K = 12, Q8_0 = 8;

    //the two tensors hold 256 F32 values, so 1,024 bytes, and 512 Q4K values in two 144-byte blocks, so 288
    private static (string, ulong[], uint)[] TwoTensors =>
        [("token_embd.weight", [256], F32), ("blk.0.attn_q.weight", [256, 2], Q4K)];

    private const long FirstBytes = 256 * 4;            //the byte size of 256 F32 values
    private const long SecondBytes = 512 / 256 * 144;   //the byte size of 512 Q4K values in two blocks

    private static GgufHeader Parse(byte[] bytes) =>
        GgufHeaderParser.Parse(new MemoryStream(bytes));

    //compute the data start from the fixture's own length and the alignment, so the expectation never borrows the parser's stopping point.
    private static long DataStart(byte[] header, long alignment = 32)
    {
        var over = header.Length % alignment;
        return over == 0 ? header.Length : header.Length + (alignment - over);
    }

    //a parse that reaches the end of a well-formed fixture must report Complete
    [Fact]
    public void A_WELL_FORMED_HEADER_WALKS_TO_COMPLETE()
    {
        var header = Parse(GgufTestBytes.WithTensors(TwoTensors));

        Assert.Equal(GgufOutcome.Complete, header.Outcome);
        Assert.NotNull(header.Tensors);
    }

    //both sizes here are multiples of 32, so a padding-blind rule would pass this case (the padding test covers that)
    [Fact]
    public void THE_WALK_KNOWS_THE_SHORTEST_THE_FILE_COULD_BE()
    {
        var bytes = GgufTestBytes.WithTensors(TwoTensors);

        var header = Parse(bytes);

        Assert.Equal(DataStart(bytes) + FirstBytes + SecondBytes, header.Tensors?.MinimumFileBytes);
    }

    //padding between tensors counts toward the minimum, since a tensor can end off the 32-byte boundary. missing it would understate the file and hide a truncation
    [Fact]
    public void PADDING_BETWEEN_TENSORS_COUNTS_TOWARD_THE_MINIMUM()
    {
        (string, ulong[], uint)[] tensors =
            [("a", [34 * 32], Q8_0), ("b", [256], F32)];
        const long padded = 1184;                   //the first tensor's 1,156 bytes rounded up to the next multiple of 32.
        var bytes = GgufTestBytes.WithTensors(tensors);

        var header = Parse(bytes);

        Assert.Equal(DataStart(bytes) + padded + 256 * 4, header.Tensors?.MinimumFileBytes);
    }

    [Fact]
    public void A_DECLARED_ALIGNMENT_IS_OBEYED()
    {
        var bytes = GgufTestBytes.WithTensors(TwoTensors, alignment: 4096);

        var header = Parse(bytes);

        //the second tensor starts at the 4096 boundary, and a reader that summed sizes would answer 1,312
        Assert.Equal(DataStart(bytes, 4096) + 4096 + SecondBytes, header.Tensors?.MinimumFileBytes);
    }

    //the furthest tensor decides the minimum, the format requires no offset order, so the fixture places the far tensor first
    [Fact]
    public void THE_FURTHEST_TENSOR_DECIDES_EVEN_WHEN_IT_IS_NOT_LAST()
    {
        var bytes = GgufTestBytes.WithTensors(
            [("far", [256], F32), ("near", [256], F32)], offsets: [4096, 0]);

        var header = Parse(bytes);

        Assert.Equal(DataStart(bytes) + 4096 + 256 * 4, header.Tensors?.MinimumFileBytes);
    }

    [Fact]
    public void A_FILE_SHORTER_THAN_ITS_HEADER_CLAIMS_IS_INCOMPLETE()
    {
        var bytes = GgufTestBytes.WithTensors(TwoTensors);
        var header = Parse(bytes);
        var least = header.Tensors!.MinimumFileBytes;

        Assert.True(header.IsShorterThanItsHeaderClaims(least - 1));
        Assert.False(header.IsShorterThanItsHeaderClaims(least));
        //a longer file is ordinary padding (a writer may append), so only a short file is provable
        Assert.False(header.IsShorterThanItsHeaderClaims(least + 4096));
    }

    //an unknown tensor type is a new quantisation format, so report no facts rather than accuse the user's file
    [Fact]
    public void AN_UNPRICEABLE_TENSOR_YIELDS_NO_FACTS_AND_NO_ACCUSATION()
    {
        const uint notAType = 250;
        var bytes = GgufTestBytes.WithTensors([("a", [256], F32), ("b", [256], notAType)]);

        var header = Parse(bytes);

        Assert.Equal(GgufOutcome.Complete, header.Outcome);
        Assert.Null(header.Tensors);
        Assert.False(header.IsShorterThanItsHeaderClaims(1));
    }

    //a remote read fetches the first few KB, so a missing tensor section is by design and never means a truncated model
    [Fact]
    public void A_HEADER_THAT_ENDS_BEFORE_ITS_TENSORS_IS_STILL_COMPLETE()
    {
        var bytes = GgufTestBytes.WithTensors([("a", [256], F32)], declaredCount: 4);

        var header = Parse(bytes);

        Assert.Equal(GgufOutcome.Complete, header.Outcome);
        Assert.Equal(8192, header.ContextLength);   //the key-value reads taken before the failed tensor pass survive it.
        Assert.Null(header.Tensors);
    }

    //zero tensors is the file saying it is not a model, unlike a header that promised tensors and ran out
    [Fact]
    public void A_WEIGHTLESS_GGUF_HAS_NOTHING_TO_WALK()
    {
        var header = Parse(GgufTestBytes.TokenizerSidecar());

        Assert.Equal(GgufOutcome.Complete, header.Outcome);
        Assert.Null(header.Tensors);
    }

    //the dimensions come from untrusted bytes, so a product past long.MaxValue would read as a plausible size. no facts is the safe answer
    [Fact]
    public void DIMENSIONS_THAT_OVERFLOW_YIELD_NOTHING()
    {
        var bytes = GgufTestBytes.WithTensors(
            [("a", [ulong.MaxValue / 2, ulong.MaxValue / 2], F32)]);

        var header = Parse(bytes);

        Assert.Equal(GgufOutcome.Complete, header.Outcome);
        Assert.Null(header.Tensors);
    }
}
