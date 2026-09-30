using Gatto.Core.Home;
using Gatto.Core.Models;

namespace Gatto.Tests;

public class GgufReaderTests
{
    //resolve fixtures from AppContext.BaseDirectory, another collection changes the process cwd in parallel
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Read_TinyGguf_ExtractsArchNameAndContext()
    {
        var md = GgufReader.Read(Fixture("tiny.gguf"));
        Assert.Equal("qwen3", md.Architecture);
        Assert.Equal("Tiny Qwen", md.Name);
        Assert.Equal(131072, md.ContextLength);
        Assert.Null(md.PoolingType);   //generative models declare no pooling_type, so it reads null
    }

    [Fact]
    public void Read_EmbeddingModelHeader_ExposesPoolingType()
    {
        //the bge-m3 header shape: bert arch with a context_length and a pooling_type, the tell that keeps embedding models out of the model-new scans
        var p = Path.GetTempFileName();
        try
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x46554747U); w.Write(3U); w.Write(0UL); w.Write(3UL);

                Key(w, "general.architecture"); w.Write(8U); Key(w, "bert");   //gguf keys and string values share the length-prefixed shape.
                Key(w, "bert.context_length"); w.Write(4U); w.Write(8192U);
                Key(w, "bert.pooling_type"); w.Write(4U); w.Write(2U);
            }
            File.WriteAllBytes(p, ms.ToArray());

            var md = GgufReader.Read(p);
            Assert.Equal("bert", md.Architecture);
            Assert.Equal(8192, md.ContextLength);
            Assert.Equal(2, md.PoolingType);
        }
        finally { File.Delete(p); }

        static void Key(BinaryWriter w, string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            w.Write((ulong)b.Length); w.Write(b);
        }
    }

    [Fact]
    public void Read_BadMagic_ThrowsFriendly()
    {
        var p = Path.GetTempFileName();
        File.WriteAllBytes(p, new byte[] { 0x4E, 0x4F, 0x50, 0x45, 0, 0, 0, 0 });
        var ex = Assert.Throws<GattoConfigException>(() => GgufReader.Read(p));
        Assert.Contains("not a GGUF", ex.Message);
        File.Delete(p);
    }

    [Fact]
    public void Read_Truncated_ThrowsFriendly()
    {
        var p = Path.GetTempFileName();
        File.WriteAllBytes(p, "GGUF"u8.ToArray());   //the file holds the magic only.
        Assert.Throws<GattoConfigException>(() => GgufReader.Read(p));
        File.Delete(p);
    }

    [Fact]
    public void Read_MissingFile_ThrowsFriendly()
    {
        var ex = Assert.Throws<GattoConfigException>(() => GgufReader.Read(@"C:\nope\missing.gguf"));
        Assert.Contains("missing.gguf", ex.Message);
    }

    [Fact]
    public void Read_NoContextLength_ReturnsNullNotZero()
    {
        var md = GgufReader.Read(Fixture("tiny-noctx.gguf"));
        Assert.Equal("qwen3", md.Architecture);
        Assert.Null(md.ContextLength);   //a file with no context_length reads as null, so a missing term can't look like 0
    }


    [Fact]
    public void Read_CorruptStringLength_ThrowsConfigException()
    {
        var p = Path.GetTempFileName();
        try
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(0x46554747U);  //this is the GGUF magic.
            w.Write(3U);           //the format version, 3.
            w.Write(0UL);          //this is the tensor count.
            w.Write(1UL);          //the KV count is 1.

            //the KV claims a string length that is absurd yet a valid uint64.
            w.Write(14UL);  //the key length, 14.
            w.Write("corrupt.string"u8.ToArray());
            w.Write(8U);   //the string value type.
            w.Write(ulong.MaxValue / 2);  //an absurdly large claimed length.
            //the string data itself is never written.

            File.WriteAllBytes(p, ms.ToArray());

            //the failure must surface as a GattoConfigException, so no raw runtime exception escapes
            var ex = Assert.Throws<GattoConfigException>(() => GgufReader.Read(p));
            Assert.NotNull(ex);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void Read_CorruptArrayCount_ThrowsConfigException()
    {
        var p = Path.GetTempFileName();
        try
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(0x46554747U);  //the GGUF magic in the first four bytes
            w.Write(3U);           //the header version field, 3
            w.Write(0UL);          //the tensor count, zero
            w.Write(1UL);          //the key-value count, one pair in this test

            //an array header with a huge element count and no data, so only the count can fail the parse
            w.Write(13UL);  //the byte length of the key that follows
            w.Write("corrupt.array"u8.ToArray());
            w.Write(9U);   //the type code for an array, 9
            w.Write(8U);   //the type code of each element, a string
            w.Write(ulong.MaxValue / 2);  //an element count far beyond the file, so the parser must refuse it
            //leave the declared array data out, so only the count is on disk.

            File.WriteAllBytes(p, ms.ToArray());

            //a corrupt count must surface as a GattoConfigException, so no ArgumentOutOfRangeException or OverflowException escapes
            var ex = Assert.Throws<GattoConfigException>(() => GgufReader.Read(p));
            Assert.NotNull(ex);
        }
        finally
        {
            File.Delete(p);
        }
    }

    //count only the bytes read, so a seek costs nothing and skipping a tensor block stays allowed
    private sealed class TallyingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    //assert on the bytes read, since a timing check fails for unrelated reasons. the 64 MB cap sits above a header read and far below a body read
    [Fact]
    public void Read_RealGemma4Model_ParsesWithoutReadingTheWholeFile()
    {
        var modelPath = Environment.GetEnvironmentVariable("GATTO_LIVE_GGUF");
        if (modelPath is null || !File.Exists(modelPath))
        {
            //the live model file is optional, so return without asserting when it is absent.
            return;
        }

        //call GgufHeaderParser.Parse, since only the parser takes a stream a counter can wrap
        using var tally = new TallyingStream(
            new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        var h = GgufHeaderParser.Parse(tally);

        Assert.Equal("gemma4", h.Architecture);
        Assert.Equal(262144u, h.ContextLength);

        const long cap = 64L * 1024 * 1024;
        Assert.True(tally.BytesRead < cap,
            $"header parse read {tally.BytesRead:N0} bytes (cap {cap:N0}) — that is tensor data, not a header");

        //check the public entry point too, so the tested path cannot drift from the shipped path.
        var metadata = GgufReader.Read(modelPath);
        Assert.Equal("gemma4", metadata.Architecture);
        Assert.Equal(262144, metadata.ContextLength);
    }

    //the fixture keeps an array before context_length, so a wrong skip corrupts the next read. read the committed fixture, since regenerating it races other tests
    [Fact]
    public void Fixture_ArrayPlacedBeforeContextLength_WillCatchSkipErrors()
    {
        var fixturePath = Fixture("tiny.gguf");
        using var fs = new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(fs);

        r.ReadUInt32();  //skip the GGUF magic field
        r.ReadUInt32();  //skip the version field.
        r.ReadUInt64();  //skip the tensor count field.
        var kvCount = r.ReadUInt64();

        var keys = new List<string>();
        for (ulong i = 0; i < kvCount; i++)
        {
            var keyLen = r.ReadUInt64();
            var keyBytes = r.ReadBytes((int)keyLen);
            keys.Add(System.Text.Encoding.UTF8.GetString(keyBytes));

            var type = r.ReadUInt32();
            //skip the value with a copy of the reader's own size rule.
            SkipValueInTest(r, type);
        }

        var arrayIdx = keys.IndexOf("tokenizer.ggml.tokens");
        var contextIdx = keys.IndexOf("qwen3.context_length");

        Assert.NotEqual(-1, arrayIdx);
        Assert.NotEqual(-1, contextIdx);
        Assert.True(arrayIdx < contextIdx,
            $"Array at index {arrayIdx}, context_length at {contextIdx}. Array must come first so " +
            $"skip errors corrupt the context_length read, causing test failure.");
    }

    //skip each value by the same size rule the reader uses, so the next key starts at the right byte.
    private static void SkipValueInTest(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: case 1: case 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;
            case 2: case 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;
            case 4: case 5: case 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;
            case 10: case 11: case 12: r.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 8: //type 8 is a string
                var len = r.ReadUInt64();
                r.BaseStream.Seek((long)len, SeekOrigin.Current);
                break;
            case 9: //type 9 is an array
                var elemType = r.ReadUInt32();
                var count = r.ReadUInt64();
                for (ulong i = 0; i < count; i++) SkipValueInTest(r, elemType);
                break;
            default:
                throw new InvalidOperationException($"unknown type {type}");
        }
    }
}
