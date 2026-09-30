namespace Gatto.Tests;

//synthetic GGUF headers for the parser and remote-read tests. a builder counts the items it wrote, and a lying fixture must say so in its doc
internal static class GgufTestBytes
{
    private static void Str(BinaryWriter w, string s)
    { var b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }

    //the default tensor count is non-zero, so pass tensorCount only for a weightless fixture
    private static byte[] Build(ulong kvCount, Action<BinaryWriter> kvs, ulong tensorCount = 291)
    {
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46554747u); w.Write(3u);        //the GGUF magic, then the format version
        w.Write(tensorCount); w.Write(kvCount);
        kvs(w); w.Flush(); return ms.ToArray();
    }

    //collects the key-value emitters, so the declared count comes from what was actually written
    internal sealed class Kv
    {
        private readonly List<Action<BinaryWriter>> _items = [];
        internal int Count => _items.Count;
        internal void WriteAll(BinaryWriter w) { foreach (var a in _items) a(w); }

        public void Str(string key, string value) =>
            _items.Add(w => { GgufTestBytes.Str(w, key); w.Write(8u); GgufTestBytes.Str(w, value); });

        public void U32(string key, uint value) =>
            _items.Add(w => { GgufTestBytes.Str(w, key); w.Write(4u); w.Write(value); });

        //emit one raw key-value pair, for shapes the typed helpers do not cover, such as arrays.
        public void Raw(Action<BinaryWriter> emitOneKv) => _items.Add(emitOneKv);
    }

    //build an honest header whose declared count comes from the items written. a tensorCount of zero makes a weightless sidecar rather than a model
    public static byte[] SyntheticHeader(Action<Kv> build, ulong tensorCount = 291)
    {
        var kv = new Kv();
        build(kv);
        return Build((ulong)kv.Count, kv.WriteAll, tensorCount);
    }


    //emit a tensor-info section, each tensor at the next alignment boundary and no data, since only the header's claim is read
    public static byte[] WithTensors((string Name, ulong[] Dims, uint Type)[] tensors,
        uint? alignment = null, ulong? declaredCount = null, long[]? offsets = null)
    {
        var kv = new Kv();
        kv.Str("general.architecture", "qwen3");
        kv.Str("general.name", "tensored");
        kv.U32("qwen3.context_length", 8192);
        if (alignment is { } a) kv.U32("general.alignment", a);

        var align = (long)(alignment ?? 32);
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46554747u); w.Write(3u);
        w.Write(declaredCount ?? (ulong)tensors.Length); w.Write((ulong)kv.Count);
        kv.WriteAll(w);

        long at = 0;
        for (var i = 0; i < tensors.Length; i++)
        {
            var (name, dims, type) = tensors[i];
            Str(w, name);
            w.Write((uint)dims.Length);
            foreach (var d in dims) w.Write(d);
            w.Write(type);
            w.Write((ulong)(offsets is null ? at : offsets[i]));
            at += BytesOf(type, dims);
            var over = at % align;
            if (over != 0) at += align - over;
        }

        w.Flush();
        return ms.ToArray();
    }

    //keep a second size table here, asking GgmlTypes would make the fixture agree with the parser however wrong both are
    private static long BytesOf(uint type, ulong[] dims)
    {
        long elements = 1;
        foreach (var d in dims) elements *= (long)d;
        return type switch
        {
            0 => elements * 4,          //type 0 is F32, four bytes per element
            1 => elements * 2,          //type 1 is F16, two bytes per element
            8 => elements / 32 * 34,    //type 8 is Q8_0, 34 bytes per 32 elements
            12 => elements / 256 * 144, //type 12 is Q4_K, 144 bytes per 256 elements
            //an unpriced type advances nothing, since the parser stops there and inventing a size would invent the thing under test
            _ => 0,
        };
    }

    //build a GGUF that declares zero tensors but a real, supported architecture, so only the tensor count can tell it is not a model
    public static byte[] TokenizerSidecar(string arch = "gemma4") => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", arch);
        kv.Str("general.name", "gemma-4-e4b-it");
        kv.U32($"{arch}.context_length", 8192);
    }, tensorCount: 0);

    //every fit term with a 100-string array before them, so the skip path sits on the parsed route. the file stays over 4 KB for the slice test
    public static byte[] Rich() => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "qwen3");
        kv.Raw(w =>                                             //a string array that forces the skip path.
        {
            Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
            w.Write(8u); w.Write((ulong)100);                   //one hundred strings of 48 characters, so about 5.6 KB in total.
            for (var i = 0; i < 100; i++) Str(w, new string('x', 48));
        });
        kv.Str("general.name", "rich");
        kv.U32("qwen3.context_length", 32768);
        kv.U32("qwen3.block_count", 48);
        kv.U32("qwen3.embedding_length", 5120);
        kv.U32("qwen3.attention.head_count", 40);
        kv.U32("qwen3.attention.head_count_kv", 8);
        kv.U32("qwen3.attention.key_length", 128);
        kv.U32("qwen3.attention.value_length", 128);
    });

    //place the fit terms behind the padding, so they fall past the first fetch window and the remote loop must extend
    public static byte[] RichWithLeadingPadding(int paddingBytes) => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "qwen3");
        var elems = Math.Max(1, paddingBytes / 1008);           //each element costs 1,008 bytes, an 8-byte length plus 1,000 characters.
        kv.Raw(w =>
        {
            Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
            w.Write(8u); w.Write((ulong)elems);
            for (var i = 0; i < elems; i++) Str(w, new string('x', 1000));
        });
        kv.Str("general.name", "padded");
        kv.U32("qwen3.context_length", 32768);
        kv.U32("qwen3.block_count", 48);
        kv.U32("qwen3.embedding_length", 5120);
        kv.U32("qwen3.attention.head_count", 40);
        kv.U32("qwen3.attention.head_count_kv", 8);
        kv.U32("qwen3.attention.key_length", 128);
        kv.U32("qwen3.attention.value_length", 128);
    });

    //the fit terms come first, then padding past the fetch window, so the parse is truncated with every fit term already captured
    public static byte[] RichWithTrailingPadding(int paddingBytes) => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "qwen3");
        kv.U32("qwen3.context_length", 32768);
        kv.U32("qwen3.block_count", 48);
        kv.U32("qwen3.embedding_length", 5120);
        kv.U32("qwen3.attention.head_count", 40);
        kv.U32("qwen3.attention.head_count_kv", 8);
        kv.U32("qwen3.attention.key_length", 128);
        kv.U32("qwen3.attention.value_length", 128);
        kv.Str("general.name", "trailing");
        var elems = Math.Max(1, paddingBytes / 1008);
        kv.Raw(w =>
        {
            Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
            w.Write(8u); w.Write((ulong)elems);
            for (var i = 0; i < elems; i++) Str(w, new string('x', 1000));
        });
    });

    //declare 20 key-value pairs while writing fewer, so the read runs out with every fit term captured and the loop stops on satisfied terms
    public static byte[] RichWithOverstatedKvCount()
    {
        var kv = new Kv();
        kv.Str("general.architecture", "qwen3");
        kv.Str("general.name", "overstated");
        kv.U32("qwen3.context_length", 32768);
        kv.U32("qwen3.block_count", 48);
        kv.U32("qwen3.embedding_length", 5120);
        kv.U32("qwen3.attention.head_count", 40);
        kv.U32("qwen3.attention.head_count_kv", 8);
        kv.U32("qwen3.attention.key_length", 128);
        kv.U32("qwen3.attention.value_length", 128);
        return Build(20, kv.WriteAll);          //declare twenty pairs while nine are written.
    }

    //write a claimed length with five real bytes behind it, so the skip hits the end of the file. the parse must come back truncated with the architecture kept
    public static byte[] WithLyingSkipString(int claimedLen = 500_000) => Build(3, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
        w.Write(8u); w.Write((ulong)3);
        Str(w, "ok");
        w.Write((ulong)claimedLen);                              //write a claimed length far past the bytes that follow.
        w.Write(System.Text.Encoding.UTF8.GetBytes("short"));    //only five bytes of the claimed string exist.
        Str(w, "never-reached");
        Str(w, "general.name"); w.Write(8u); Str(w, "never-parsed");
    });

    //claim a capture-path string just under the cap with 40 real bytes behind it. no allocation sized by the claim may happen before the remaining-bytes check
    public static byte[] WithLyingCaptureString() => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "general.name"); w.Write(8u);
        w.Write((ulong)((1 << 20) - 1)); w.Write(new byte[40]);
    });

    //declare more string elements than the bytes hold. the count stays under the cap, so the parse is truncated
    public static byte[] WithLyingArrayElementCount(int claimedCount, int realElements) => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
        w.Write(8u); w.Write((ulong)claimedCount);
        for (var i = 0; i < realElements; i++) Str(w, "e");
    });

    //let the key length lie with a few real bytes behind it, since the key is the first read of each pair. the parse is truncated with the architecture kept
    public static byte[] WithLyingKeyString(int claimedLen = 500_000) => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        w.Write((ulong)claimedLen);                              //declare a key length larger than the bytes written.
        w.Write(System.Text.Encoding.UTF8.GetBytes("key"));      //only three bytes of the key exist.
    });

    //a declared string length past the 1 MB cap is malformed with an implausible-length message
    public static byte[] WithStringLen(int declaredLen) => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "general.name"); w.Write(8u);
        w.Write((ulong)declaredLen);
    });

    //a declared element count past the 1,000,000 cap is malformed with an implausible-count message
    public static byte[] WithArrayCount(ulong declaredCount) => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
        w.Write(8u); w.Write(declaredCount);
    });

    //nest arrays depth deep with one element each, a shape only lying bytes produce, since recursion over a claimed structure would exhaust the stack
    public static byte[] NestedArrays(int depth) => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        //name the key arr, since the test looks for nested in the message and an echoed key would pass by accident
        Str(w, "arr"); w.Write(9u);
        for (var i = 0; i < depth - 1; i++) { w.Write(9u); w.Write((ulong)1); }   //write an array holding one array.
        w.Write(4u); w.Write((ulong)1); w.Write(7u);                              //the innermost value is one u32
    });

    //declare any count over two real pairs, so the loop must stop at the bytes and a ulong.MaxValue claim must not spin
    public static byte[] WithKvCount(ulong declaredCount) => Build(declaredCount, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "general.name"); w.Write(8u); Str(w, "two");
    });

    //put every value type from 0 to 12 on the parsed route, plus two array shapes. the Rich fixture reaches three types, so the rest of the table stays unproven
    public static byte[] KitchenSink() => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "qwen3");
        kv.Raw(w => { Str(w, "t.u8");   w.Write(0u);  w.Write((byte)1); });
        kv.Raw(w => { Str(w, "t.i8");   w.Write(1u);  w.Write((sbyte)-1); });
        kv.Raw(w => { Str(w, "t.u16");  w.Write(2u);  w.Write((ushort)2); });
        kv.Raw(w => { Str(w, "t.i16");  w.Write(3u);  w.Write((short)-2); });
        kv.Raw(w => { Str(w, "t.u32");  w.Write(4u);  w.Write(3u); });
        kv.Raw(w => { Str(w, "t.i32");  w.Write(5u);  w.Write(-3); });
        kv.Raw(w => { Str(w, "t.f32");  w.Write(6u);  w.Write(1.5f); });
        kv.Raw(w => { Str(w, "t.bool"); w.Write(7u);  w.Write((byte)1); });
        kv.Raw(w => { Str(w, "t.str");  w.Write(8u);  Str(w, "s"); });
        kv.Raw(w => { Str(w, "t.u64");  w.Write(10u); w.Write((ulong)4); });
        kv.Raw(w => { Str(w, "t.i64");  w.Write(11u); w.Write((long)-4); });
        kv.Raw(w => { Str(w, "t.f64");  w.Write(12u); w.Write(2.5d); });
        kv.Raw(w =>                                                   //an array of i32 elements
        {
            Str(w, "t.arr.i32"); w.Write(9u); w.Write(5u); w.Write((ulong)4);
            for (var i = 0; i < 4; i++) w.Write(i);
        });
        kv.Raw(w =>                                                   //an array whose elements are arrays.
        {
            Str(w, "t.arr.arr"); w.Write(9u); w.Write(9u); w.Write((ulong)2);
            w.Write(8u); w.Write((ulong)2); Str(w, "a"); Str(w, "b");  //the inner array holds strings.
            w.Write(0u); w.Write((ulong)3); w.Write((byte)1); w.Write((byte)2); w.Write((byte)3);
        });
        kv.Str("general.name", "kitchen");
        kv.U32("qwen3.context_length", 32768);
        kv.U32("qwen3.block_count", 48);
        kv.U32("qwen3.attention.head_count", 40);
        kv.U32("qwen3.attention.head_count_kv", 8);
        kv.U32("qwen3.embedding_length", 5120);
    });

    //write one arch-suffixed term as a u64, so a value above long.MaxValue reads as absent
    public static byte[] WithU64Term(string key, ulong value) => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "qwen3");
        kv.Raw(w => { Str(w, key); w.Write(10u); w.Write(value); });
    });

    //builds a header with the three structure keys. fitTerms writes the block that makes HasAllFitTerms true, which tells no experts from a short read
    public static byte[] WithStructure(
        long? expertCount = null, long? expertUsed = null, string? sizeLabel = null,
        bool fitTerms = true, string arch = "qwen3moe", int paddingBytes = 0,
        int trailingPaddingBytes = 0) =>
        SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", arch);
            //put tokenizer padding before the structure keys, so a prefix read can stop short of them.
            if (paddingBytes > 0)
            {
                var elems = Math.Max(1, paddingBytes / 1008);
                kv.Raw(w =>
                {
                    Str(w, "tokenizer.ggml.tokens"); w.Write(9u);
                    w.Write(8u); w.Write((ulong)elems);
                    for (var i = 0; i < elems; i++) Str(w, new string('x', 1000));
                });
            }
            if (sizeLabel is not null) kv.Str("general.size_label", sizeLabel);
            if (expertCount is { } n) kv.U32($"{arch}.expert_count", (uint)n);
            if (expertUsed is { } u) kv.U32($"{arch}.expert_used_count", (uint)u);
            if (fitTerms)
            {
                kv.U32($"{arch}.context_length", 32768);
                kv.U32($"{arch}.block_count", 48);
                kv.U32($"{arch}.embedding_length", 5120);
                kv.U32($"{arch}.attention.head_count", 40);
                kv.U32($"{arch}.attention.head_count_kv", 8);
                kv.U32($"{arch}.attention.key_length", 128);
                kv.U32($"{arch}.attention.value_length", 128);
            }
            //put the padding last, so it applies with or without the fit terms. one builder then makes both large-file cases the stop predicate must tell apart
            if (trailingPaddingBytes > 0)
            {
                var tail = Math.Max(1, trailingPaddingBytes / 1008);
                kv.Raw(w =>
                {
                    Str(w, "tokenizer.ggml.merges"); w.Write(9u);
                    w.Write(8u); w.Write((ulong)tail);
                    for (var i = 0; i < tail; i++) Str(w, new string('y', 1000));
                });
            }
        });

    //writes the kv-head count per layer as an array, the form gemma 4 uses. the element type, key and count are overridable, so a test can feed an untrusted array
    public static byte[] WithPerLayerKvHeads(
        uint[] perLayer, uint elemType = 4,
        string key = "gemma4.attention.head_count_kv", ulong? declaredCount = null) =>
        SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "gemma4");
            if (key != "gemma4.block_count") kv.U32("gemma4.block_count", (uint)perLayer.Length);
            kv.Raw(w =>
            {
                Str(w, key);
                w.Write(9u);                       //type 9 marks an array
                w.Write(elemType);
                w.Write(declaredCount ?? (ulong)perLayer.Length);
                foreach (var n in perLayer)
                {
                    if (elemType == 4) w.Write(n);          //type 4 writes an unsigned 32-bit element
                    else if (elemType == 6) w.Write((float)n);   //type 6 writes a float, which reads but is not an integer type
                    else w.Write(n);
                }
            });
        });

    //use signed long elements, so a test can write a negative count. a header is somebody else's bytes, so the overflow guard can't assume a non-negative product
    public static byte[] WithSignedKvHeadArray(long[] perLayer) => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "gemma4");
        kv.U32("gemma4.block_count", (uint)perLayer.Length);
        kv.Raw(w =>
        {
            Str(w, "gemma4.attention.head_count_kv");
            w.Write(9u);                  //type 9 marks an array
            w.Write(11u);                 //type 11 marks signed 64-bit elements
            w.Write((ulong)perLayer.Length);
            foreach (var n in perLayer) w.Write(n);
        });
    });

    //repeat general.architecture after int terms were captured under the first value. a real writer never does this, so only a hostile file can
    public static byte[] DuplicateArchitecture() => SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "llama");
        kv.U32("llama.context_length", 8192);
        kv.U32("llama.block_count", 32);
        kv.Str("general.architecture", "bert");
    });

    //place the int terms before general.architecture, so a test drives the unordered-key path the parser claims
    public static byte[] ArchAfterIntTerms() => SyntheticHeader(kv =>
    {
        kv.U32("qwen3.context_length", 32768);
        kv.U32("qwen3.block_count", 48);
        kv.U32("qwen3.attention.head_count_kv", 8);
        kv.U32("qwen3.attention.key_length", 128);
        kv.U32("qwen3.attention.value_length", 128);
        kv.Str("general.architecture", "qwen3");
    });

    //a value type outside the table is malformed, since the reader can't advance past a value it can't size. a guessed stride makes every later read garbage
    public static byte[] WithValueType(uint type) => Build(2, w =>
    {
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
        Str(w, "weird"); w.Write(type); w.Write((ulong)0);
    });
}
