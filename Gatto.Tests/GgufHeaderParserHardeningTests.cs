using Gatto.Core.Models;

namespace Gatto.Tests;

//this parser reads bytes picked by the network. each case asks whether a lying header makes the parse throw, spin, or allocate past its bytes
public class GgufHeaderParserHardeningTests
{
    [Fact]
    public void Every_truncation_of_a_valid_header_returns_instead_of_throwing()
    {
        var bytes = GgufTestBytes.Rich();   //the array sits before the fit terms, so the skip path is on the parse route for most prefixes.
        Assert.True(bytes.Length >= 4096, "Rich() documents a >= 4 KB minimum");
        Assert.Equal(GgufOutcome.Complete, GgufHeaderParser.Parse(new MemoryStream(bytes)).Outcome);
        for (var len = 0; len < bytes.Length; len++)
        {
            var h = GgufHeaderParser.Parse(new MemoryStream(bytes[..len]));
            Assert.NotEqual(GgufOutcome.Complete, h.Outcome);  //a strict prefix of a valid header must never report Complete
        }
    }

    [Fact]
    public void A_lying_string_length_on_the_skip_path_is_truncation_not_a_throw()
    {
        //the second string claims 500,000 bytes, inside the cap yet past the buffer. parsing must stop with Truncated and keep what it captured before the lie
        var bytes = GgufTestBytes.WithLyingSkipString(claimedLen: 500_000);
        var h = GgufHeaderParser.Parse(new MemoryStream(bytes));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);
    }

    [Fact]
    public void A_lying_capture_length_never_allocates_past_the_real_bytes()
    {
        //the general.name claim of MaxStringBytes-1 sits over 40 real bytes, so the result must be Truncated with the architecture kept
        var bytes = GgufTestBytes.WithLyingCaptureString();
        var h = GgufHeaderParser.Parse(new MemoryStream(bytes));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);
    }

    [Fact]
    public void A_lying_array_ELEMENT_COUNT_under_the_cap_is_truncation_not_a_throw()
    {
        //50 elements are claimed over 3 real ones, under the cap that makes a count Malformed. reading must end as Truncated with the architecture kept
        var bytes = GgufTestBytes.WithLyingArrayElementCount(claimedCount: 50, realElements: 3);
        var h = GgufHeaderParser.Parse(new MemoryStream(bytes));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);
    }

    [Fact]
    public void A_lying_KEY_string_length_is_truncation_not_an_allocation()
    {
        //the key is the first read of every iteration, so its guard needs its own proof. the claim stays under the cap, so the result is Truncated
        var bytes = GgufTestBytes.WithLyingKeyString(claimedLen: 500_000);
        var h = GgufHeaderParser.Parse(new MemoryStream(bytes));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);   //the architecture came from the first honest entry, before the lie.
    }

    [Fact]
    public void Implausible_lengths_and_counts_are_malformed_with_todays_text()
    {
        Assert.Equal(GgufOutcome.Malformed,
            GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithStringLen(2 << 20))).Outcome);
        Assert.Equal(GgufOutcome.Malformed,
            GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithArrayCount(20_000_000))).Outcome);
    }

    [Fact]
    public void An_array_count_between_the_old_and_new_caps_is_NOT_malformed()
    {
        //5,000,000 is the discriminating point, Truncated under the new cap. exceeding the cap refuses a valid model, so the constant needs a test
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithArrayCount(5_000_000)));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
    }

    [Fact]
    public void Nested_arrays_past_the_depth_cap_are_malformed()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.NestedArrays(depth: 10)));
        Assert.Equal(GgufOutcome.Malformed, h.Outcome);
        Assert.Contains("nested", h.Malformation);
    }

    [Fact]
    public void A_kv_count_of_u64_max_terminates_at_the_bytes_not_the_claim()
    {
        var bytes = GgufTestBytes.WithKvCount(ulong.MaxValue);
        Assert.Equal(GgufOutcome.Truncated, GgufHeaderParser.Parse(new MemoryStream(bytes)).Outcome);
    }

    [Fact]
    public void Unknown_value_type_is_malformed_never_a_guess()
    {
        Assert.Equal(GgufOutcome.Malformed,
            GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.WithValueType(99))).Outcome);
    }

    //the Remaining figure is computed from Stream.Length, which is itself a claim. the battery above bounds parsing by Remaining but never questioned that length

    //a stream reporting a length it cannot deliver, like a content-range total. every Remaining check becomes a no-op, so only the I/O backstop stops it
    private sealed class LyingLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 20_000_000_000L;
    }

    private sealed class ThrowOnLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => throw new NotSupportedException("no length here");
    }

    [Fact]
    public void A_stream_that_lies_about_its_Length_is_truncation_not_a_throw()
    {
        var h = GgufHeaderParser.Parse(new LyingLengthStream(GgufTestBytes.WithLyingCaptureString()));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);        //data captured before the failure must survive the truncation.
    }

    [Fact]
    public void A_stream_whose_Length_throws_is_truncation_not_a_throw()
    {
        var h = GgufHeaderParser.Parse(new ThrowOnLengthStream(GgufTestBytes.Rich()));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
    }

    [Fact]
    public void Every_truncation_of_a_KITCHEN_SINK_header_returns_instead_of_throwing()
    {
        //the Rich() fixture covers only three value shapes. this sweep covers every type 0-12 and the array-of-arrays, so the whole SkipValue table is exercised
        var bytes = GgufTestBytes.KitchenSink();
        Assert.Equal(GgufOutcome.Complete, GgufHeaderParser.Parse(new MemoryStream(bytes)).Outcome);
        for (var len = 0; len < bytes.Length; len++)
        {
            var h = GgufHeaderParser.Parse(new MemoryStream(bytes[..len]));
            Assert.NotEqual(GgufOutcome.Complete, h.Outcome);
        }
    }

    [Fact]
    public void Byte_mutation_fuzz_never_throws_and_keeps_outcome_and_detail_in_lockstep()
    {
        //use a fixed seed, so every run mutates the same bytes and a failure reproduces. the invariant: Malformed always has a detail, and no other outcome has one
        var rng = new Random(20260809);
        foreach (var seedBytes in new[] { GgufTestBytes.KitchenSink(), GgufTestBytes.Rich() })
            for (var i = 0; i < 2500; i++)
            {
                var b = (byte[])seedBytes.Clone();
                for (var f = rng.Next(1, 7); f > 0; f--) b[rng.Next(b.Length)] = (byte)rng.Next(256);
                var cut = rng.Next(0, b.Length + 1);
                var h = GgufHeaderParser.Parse(new MemoryStream(b[..cut]));
                Assert.Equal(h.Outcome == GgufOutcome.Malformed, h.Malformation is not null);
            }
    }

    [Theory]
    [InlineData(4, false)]   //at the cap is still accepted.
    [InlineData(5, true)]    //one past the cap: the boundary where an off-by-one would hide.
    public void Nested_array_depth_boundary_is_exactly_the_cap(int depth, bool malformed)
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.NestedArrays(depth)));
        Assert.Equal(malformed, h.Outcome == GgufOutcome.Malformed);
    }

    [Fact]
    public void A_u64_term_above_long_max_is_absent_not_a_negative()
    {
        var h = GgufHeaderParser.Parse(
            new MemoryStream(GgufTestBytes.WithU64Term("qwen3.context_length", ulong.MaxValue)));
        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Null(h.ContextLength);       //the value must read as absent rather than a negative
    }

    [Fact]
    public void Int_terms_are_captured_when_architecture_arrives_LAST()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.ArchAfterIntTerms()));
        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Equal(32768L, h.ContextLength);   //the int terms precede the architecture key and must still be captured.
        Assert.True(h.HasAllFitTerms);
    }

    [Fact]
    public void A_duplicate_architecture_orphans_the_first_arch_terms()
    {
        //duplicate keys appear only in hostile files, so they are tolerated. last-writer-wins only allows a downgrade to unknown context
        var h = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.DuplicateArchitecture()));
        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Equal("bert", h.Architecture);
        Assert.Null(h.ContextLength);
    }

    [Fact]
    public void Parse_walks_from_the_streams_CURRENT_position()
    {
        //the parser must not seek to 0 silently. the caller has to position the stream at the header start, and a silent seek would hide that caller bug.
        var s = new MemoryStream(GgufTestBytes.Rich()) { Position = 4 };
        Assert.Equal(GgufOutcome.Malformed, GgufHeaderParser.Parse(s).Outcome);
    }
}
