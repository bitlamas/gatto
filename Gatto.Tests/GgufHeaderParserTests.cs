using Gatto.Core.Home;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the GgufReaderTests class stays untouched, and its green run guards the public shape. this class pins the message text those tests never asserted
public class GgufHeaderParserTests
{
    private static byte[] SyntheticHeader(Action<GgufTestBytes.Kv> build) =>
        GgufTestBytes.SyntheticHeader(build);

    [Fact]
    public void Captures_every_fit_term_from_a_synthetic_header()
    {
        var bytes = SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3"); kv.Str("general.name", "test");
            kv.U32("qwen3.context_length", 32768); kv.U32("qwen3.block_count", 48);
            kv.U32("qwen3.embedding_length", 5120);
            kv.U32("qwen3.attention.head_count", 40); kv.U32("qwen3.attention.head_count_kv", 8);
            kv.U32("qwen3.attention.key_length", 128); kv.U32("qwen3.attention.value_length", 128);
        });
        var h = GgufHeaderParser.Parse(new MemoryStream(bytes));
        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);
        Assert.Equal(48L, h.BlockCount); Assert.Equal(8L, h.HeadCountKv);
        Assert.Equal(128L, h.KeyLength); Assert.Equal(128L, h.ValueLength);
        Assert.Equal(40L, h.HeadCount); Assert.Equal(5120L, h.EmbeddingLength);
        Assert.True(h.HasAllFitTerms);
    }

    //capture the tensor count instead of dropping it, null and zero differ here. a reader that conflates them drops every unreadable file
    [Fact]
    public void The_tensor_count_is_captured_and_zero_is_distinguishable_from_absent()
    {
        var model = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.Rich()));
        Assert.Equal(291L, model.TensorCount);

        var sidecar = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.TokenizerSidecar()));
        Assert.Equal(GgufOutcome.Complete, sidecar.Outcome);
        Assert.Equal("gemma4", sidecar.Architecture);   //the sidecar names a real, supported architecture, exactly the file a conflating reader would drop.
        Assert.Equal(0L, sidecar.TensorCount);

        //a file truncated before the count must read as absent rather than zero tensors declared
        var stub = GgufHeaderParser.Parse(new MemoryStream(GgufTestBytes.Rich().Take(12).ToArray()));
        Assert.Equal(GgufOutcome.Truncated, stub.Outcome);
        Assert.Null(stub.TensorCount);
    }

    [Fact]
    public void Chat_template_is_captured_when_present()
    {
        var bytes = SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.Str("tokenizer.chat_template", "{{ messages }}");
        });
        Assert.Equal("{{ messages }}", GgufHeaderParser.Parse(new MemoryStream(bytes)).ChatTemplate);
    }

    [Fact]
    public void Truncation_is_a_normal_outcome_that_keeps_what_was_captured()
    {
        var bytes = SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.block_count", 48);
            kv.Str("general.name", "never-reached");
        });
        var cut = bytes[..^10];                            //the cut falls inside the last KV pair.
        var h = GgufHeaderParser.Parse(new MemoryStream(cut));
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Equal("qwen3", h.Architecture);             //values captured before the cut must survive the truncation.
        Assert.Equal(48L, h.BlockCount);
        Assert.False(h.HasAllFitTerms);
    }

    [Fact]
    public void Bad_magic_is_malformed_with_the_reader_wrappers_exact_detail()
    {
        var h = GgufHeaderParser.Parse(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Equal(GgufOutcome.Malformed, h.Outcome);
        Assert.Equal("is not a GGUF file (bad magic)", h.Malformation);
    }

    [Fact]
    public void HasAllFitTerms_accepts_the_embedding_fallback_shape()   //no key_length or value_length terms in this shape
    {
        var bytes = SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.U32("llama.context_length", 8192); kv.U32("llama.block_count", 32);
            kv.U32("llama.embedding_length", 4096); kv.U32("llama.attention.head_count", 32);
        });
        Assert.True(GgufHeaderParser.Parse(new MemoryStream(bytes)).HasAllFitTerms);
    }

    [Fact]
    public void A_complete_header_without_fit_terms_is_valid_not_an_error()
    {
        //a bare embedding-model header is a real input that parses to Complete, so fit pricing has to report the missing terms
        var bytes = SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "bert");
            kv.U32("bert.pooling_type", 1);
        });
        var h = GgufHeaderParser.Parse(new MemoryStream(bytes));
        Assert.Equal(GgufOutcome.Complete, h.Outcome);
        Assert.False(h.HasAllFitTerms);
        Assert.Equal("bert", h.Architecture);
    }

    [Fact]
    public void A_non_seekable_stream_is_a_caller_bug_not_a_byte_problem()
    {
        using var s = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() => GgufHeaderParser.Parse(s));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    //every malformation message uses one path-prefixed template, so each message names the file. these pins cover the message text the older tests never asserted

    [Theory]
    [InlineData("bad-magic", "is not a GGUF file (bad magic)")]
    [InlineData("wrong-version", "is GGUF v2; gatto reads v3")]
    [InlineData("implausible-string", "implausible string length in GGUF header")]
    [InlineData("implausible-array", "implausibly large array count in GGUF: 20000000")]
    [InlineData("unknown-type", "unknown GGUF value type 99")]
    public void Read_wrapper_throws_path_prefixed_malformations(string shape, string detail)
    {
        var p = WriteTempGguf(shape);
        try
        {
            var ex = Assert.Throws<GattoConfigException>(() => GgufReader.Read(p));
            Assert.Equal($"{p} {detail}", ex.Message);         //the message must equal the path plus the detail.
        }
        finally { File.Delete(p); }
    }

    private static string WriteTempGguf(string shape)
    {
        byte[] bytes = shape switch
        {
            "bad-magic" => [1, 2, 3, 4, 5, 6, 7, 8],
            "wrong-version" => WrongVersion(),
            "implausible-string" => GgufTestBytes.WithStringLen(2 << 20),
            "implausible-array" => GgufTestBytes.WithArrayCount(20_000_000),
            "unknown-type" => GgufTestBytes.WithValueType(99),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown fixture shape"),
        };
        var p = Path.GetTempFileName();
        File.WriteAllBytes(p, bytes);
        return p;

        static byte[] WrongVersion()
        {
            var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x46554747u); w.Write(2u); w.Write(0UL); w.Write(0UL);
            w.Flush(); return ms.ToArray();
        }
    }
}
