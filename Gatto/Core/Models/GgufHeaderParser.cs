using System.Buffers.Binary;

namespace Gatto.Core.Models;

internal enum GgufOutcome { Complete, Truncated, Malformed }

//the smallest file that could hold these tensors, so a shorter file cannot be complete (no record when a tensor type can't be priced)
internal sealed record GgufTensors(long MinimumFileBytes, IReadOnlyList<GgufTensor> Entries);

//one entry of the tensor table. the fit reads it to subtract the tensors the engine never places on the GPU
internal sealed record GgufTensor(string Name, long Bytes);

//everything the fit arithmetic wants from a GGUF header, best effort: a truncated header returns what it captured with the flag and never throws
internal sealed record GgufHeader(
    GgufOutcome Outcome, string? Malformation,
    string? Architecture, string? Name, long? ContextLength, long? PoolingType,
    long? BlockCount, long? HeadCount, long? HeadCountKv,
    long? EmbeddingLength, long? KeyLength, long? ValueLength,
    string? ChatTemplate, long? TensorCount = null,   //null only when the header was cut short, and 0 means the file holds no tensors and is no model
    string? SizeLabel = null, long? ExpertCount = null, long? ExpertUsedCount = null,   //the size label is uploader text and reaches a screen only through ModelStructure's anchored shapes, the kind cell and the params cell. a dense model has no expert count at all
    GgufTensors? Tensors = null, long? EmbeddingLengthPerLayerInput = null,
    long? FullAttentionInterval = null)   //a hybrid architecture puts full attention on one block in this many, and only those keep a cache
{
    //an architecture that declares a per-layer input streams a table the GPU never holds, so a fit priced without its tensor table prices too much
    public bool DeclaresPerLayerInput => EmbeddingLengthPerLayerInput is > 0;


    //a file shorter than its own header says cannot be complete (false with no tensor table, a wrong yes accuses a good download)
    public bool IsShorterThanItsHeaderClaims(long fileBytes) =>
        Tensors is { } t && fileBytes < t.MinimumFileBytes;

    //the remote read stops once this is true, every fit term is then derivable
    public bool HasAllFitTerms =>
        Architecture is not null && ContextLength is not null && BlockCount is not null
        && (HeadCountKv is not null || HeadCount is not null)
        && ((KeyLength is not null && ValueLength is not null)
            || (EmbeddingLength is not null && HeadCount is not null));
}

//what one walk of a string array learned, so a later file holding the same array can jump it: its key, element count, byte length and last bytes
internal interface IStringArrayJumps
{
    //the remembered walk of this array, waiting for one in progress. null means walk it, and the caller may now be the one others wait for
    (long Bytes, byte[] Tail)? Remembered(string key, ulong count);

    void Walked(string key, ulong count, long bytes, byte[] tail);

    //a walk that did not finish, so whoever waits on it walks for itself
    void Failed(string key, ulong count);
}

internal static class GgufHeaderParser
{
    private const uint Magic = 0x46554747;
    private const int SupportedVersion = 3;
    private const int MaxStringBytes = 1 << 20;
    //big enough for real vocab arrays (the largest is 514,906), and the byte-bounded walk is the real protection
    private const int MaxArrayCount = 10_000_000;
    private const int MaxArrayDepth = 4;              //arrays never nest in practice, but lying bytes can

    //never throws on bad bytes, a non-seekable stream is a caller bug, and the stream must be positioned at the header's start. jumps lets a remote read skip an array another file walked
    public static GgufHeader Parse(Stream s, IStringArrayJumps? jumps = null)
    {
        if (!s.CanSeek)
            throw new ArgumentException(
                "GgufHeaderParser.Parse requires a seekable stream", nameof(s));
        var walker = new Walker(s, jumps);
        try { return walker.Walk(); }
        catch (IOException) { return walker.Snapshot(GgufOutcome.Truncated); }
        catch (NotSupportedException) { return walker.Snapshot(GgufOutcome.Truncated); }
    }

    //ggml's own ceiling on tensor rank, a header claiming more cannot be a file whose data section this parser can find
    private const uint MaxDims = 4;

    private enum Step { Ok, Truncated, Malformed }

    private sealed class Walker(Stream s, IStringArrayJumps? jumps)
    {
        private readonly byte[] _scratch = new byte[8];
        private readonly Dictionary<string, long> _ints = new(StringComparer.Ordinal);
        private string? _arch, _name, _chatTemplate, _sizeLabel, _malformation;
        private long? _tensorCount;
        private GgufTensors? _tensors;

        //the format's own default when general.alignment is absent (the common case), read as a plain KV key
        private long _alignment = 32;

        private long Remaining => s.Length - s.Position;

        public GgufHeader Walk()
        {
            if (!TryReadU32(out var magic)) return Result(GgufOutcome.Truncated);
            if (magic != Magic) return Malformed("is not a GGUF file (bad magic)");
            if (!TryReadU32(out var version)) return Result(GgufOutcome.Truncated);
            if (version != SupportedVersion)
                return Malformed($"is GGUF v{version}; gatto reads v{SupportedVersion}");
            //a count above long.MaxValue stays null, a file that big is not weightless either
            if (!TryReadU64(out var tensorCount)) return Result(GgufOutcome.Truncated);
            if (tensorCount <= long.MaxValue) _tensorCount = (long)tensorCount;
            if (!TryReadU64(out var kvCount)) return Result(GgufOutcome.Truncated);

            //the loop ends at the first short read, so a kvCount of ulong.MaxValue cannot spin
            for (ulong i = 0; i < kvCount; i++)
            {
                var step = ReadString(out var key);
                if (step != Step.Ok) return Result(Outcome(step));
                if (!TryReadU32(out var type)) return Result(GgufOutcome.Truncated);

                if (type == 8 && key is "general.architecture" or "general.name"
                    or "general.size_label" or "tokenizer.chat_template")
                {
                    step = ReadString(out var text);
                    if (step != Step.Ok) return Result(Outcome(step));
                    switch (key)
                    {
                        case "general.architecture": _arch = text; break;
                        case "general.name": _name = text; break;
                        //stored without interpretation, the rule for whether it may be shown lives in ModelStructure
                        case "general.size_label": _sizeLabel = text; break;
                        default: _chatTemplate = text; break;
                    }
                    continue;
                }

                //any integer whose key ends in a known suffix is kept, the architecture prefix may arrive later in the header
                if (IsIntType(type) && string.Equals(key, "general.alignment", StringComparison.Ordinal))
                {
                    step = ReadInt(type, out var alignment);
                    if (step != Step.Ok) return Result(Outcome(step));
                    //only a power of two up to 1 MB is trusted to locate the data section, anything else leaves the default standing
                    if (alignment is { } a && a > 0 && a <= 1 << 20 && (a & (a - 1)) == 0)
                        _alignment = a;
                    continue;
                }

                if (IsIntType(type) && IsCapturedSuffix(key))
                {
                    step = ReadInt(type, out var v);
                    if (step != Step.Ok) return Result(Outcome(step));
                    if (v is { } captured) _ints[key] = captured;   //a value that reads fine but will not fit a long stays absent
                    continue;
                }

                //sum this suffix alone when it arrives as an array, the other terms are per-model facts where a sum means nothing
                if (type == 9 && key.EndsWith(PerLayerKvHeads, StringComparison.Ordinal))
                {
                    step = SumArray(key);
                    if (step != Step.Ok) return Result(Outcome(step));
                    continue;
                }

                step = type == 9 && jumps is not null ? SkipTopArray(key) : SkipValue(type, depth: 0);
                if (step != Step.Ok) return Result(Outcome(step));
            }

            //best effort, and it must not change the outcome above: the remote read stops before the tensor section by design
            _tensors = WalkTensors();
            return Result(GgufOutcome.Complete);
        }

        //one entry per tensor (name, dimensions, type, offset), and the furthest end rather than the last entry's, the entries need not be ordered
        private GgufTensors? WalkTensors()
        {
            if (_tensorCount is not { } count || count <= 0) return null;

            long furthestEnd = 0;
            var entries = new List<GgufTensor>();
            for (long i = 0; i < count; i++)
            {
                if (ReadString(out var name) != Step.Ok) return null;
                if (!TryReadU32(out var dims) || dims > MaxDims) return null;

                long elements = 1;
                for (var d = 0; d < dims; d++)
                {
                    if (!TryReadU64(out var extent) || extent > long.MaxValue) return null;
                    try { elements = checked(elements * (long)extent); }
                    catch (OverflowException) { return null; }
                }

                if (!TryReadU32(out var type)) return null;
                if (!TryReadU64(out var offset) || offset > long.MaxValue) return null;
                if (GgmlTypes.Bytes(type, elements) is not { } bytes) return null;

                try { furthestEnd = Math.Max(furthestEnd, checked((long)offset + bytes)); }
                catch (OverflowException) { return null; }
                entries.Add(new GgufTensor(name, bytes));
            }

            var dataStart = Align(s.Position, _alignment);
            if (dataStart is not { } start) return null;

            try { return new GgufTensors(checked(start + furthestEnd), entries); }
            catch (OverflowException) { return null; }
        }

        private static long? Align(long position, long alignment)
        {
            var over = position % alignment;
            if (over == 0) return position;
            try { return checked(position + (alignment - over)); }
            catch (OverflowException) { return null; }
        }

        //the two expert suffixes do not collide, the dot is part of the suffix so each key arrives under its own name
        private static readonly string[] CapturedSuffixes =
        [
            ".context_length", ".pooling_type", ".block_count", ".embedding_length",
            ".attention.head_count", PerLayerKvHeads,
            ".attention.key_length", ".attention.value_length",
            ".expert_count", ".expert_used_count", PerLayerInput, ".full_attention_interval",
        ];

        private const string PerLayerInput = ".embedding_length_per_layer_input";

        private static bool IsCapturedSuffix(string key)
        {
            foreach (var suffix in CapturedSuffixes)
                if (key.EndsWith(suffix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsIntType(uint t) => t is 0 or 1 or 2 or 3 or 4 or 5 or 10 or 11;

        //the one suffix whose array form means something, and CapturedSuffixes uses this constant so both paths stay on one key
        private const string PerLayerKvHeads = ".attention.head_count_kv";

        //summed into the term the arithmetic wants, and a non-integer element or a total past long.MaxValue leaves it absent rather than wrapped
        private Step SumArray(string key)
        {
            if (!TryReadU32(out var elemType)) return Step.Truncated;
            if (!TryReadU64(out var count)) return Step.Truncated;
            if (count > MaxArrayCount) return Fail($"implausibly large array count in GGUF: {count}");

            long total = 0;
            var usable = IsIntType(elemType);

            for (ulong i = 0; i < count; i++)
            {
                if (!usable)
                {
                    var skipped = SkipValue(elemType, depth: 1);
                    if (skipped != Step.Ok) return skipped;
                    continue;
                }

                var step = ReadInt(elemType, out var v);
                if (step != Step.Ok) return step;
                if (v is not { } element) { usable = false; continue; }   //an element that will not fit a long leaves the term absent

                //long.MaxValue - total stops discriminating once total is negative, so the negative check must come first
                if (element < 0) { usable = false; continue; }
                if (element > long.MaxValue - total) { usable = false; continue; }
                total += element;
            }

            //an empty array writes no term, zero KV heads would price the cache at nothing
            if (usable && count > 0) _ints[key] = total;
            return Step.Ok;
        }

        //the bytes a later file checks before trusting a jump: the end of the array, and a string at the landing point
        private const int TailBytes = 64;

        //a top-level array on a remote read: fixed widths skip arithmetically, a string array jumps a remembered walk or is walked and remembered
        private Step SkipTopArray(string key)
        {
            if (!TryReadU32(out var elemType)) return Step.Truncated;
            if (!TryReadU64(out var count)) return Step.Truncated;
            if (count > MaxArrayCount) return Fail($"implausibly large array count in GGUF: {count}");
            if (FixedWidth(elemType) is { } width) return TrySkip((long)count * width) ? Step.Ok : Step.Truncated;
            if (elemType != 8)
            {
                for (ulong i = 0; i < count; i++)
                {
                    var step = SkipValue(elemType, depth: 1);
                    if (step != Step.Ok) return step;
                }
                return Step.Ok;
            }

            var start = s.Position;
            if (jumps!.Remembered(key, count) is { } known && Jumped(start, known.Bytes, known.Tail)) return Step.Ok;
            s.Position = start;

            var walked = false;
            try
            {
                for (ulong i = 0; i < count; i++)
                {
                    var step = SkipString();
                    if (step != Step.Ok) return step;
                }
                var end = s.Position;
                var tail = new byte[(int)Math.Min(TailBytes, end - start)];
                s.Position = end - tail.Length;
                s.ReadExactly(tail);
                jumps.Walked(key, count, end - start, tail);
                walked = true;
                return Step.Ok;
            }
            finally { if (!walked) jumps.Failed(key, count); }
        }

        //trust a jump only when the bytes before the landing point are the remembered tail and a string parses there, else the caller walks
        private bool Jumped(long start, long bytes, byte[] tail)
        {
            var landing = start + bytes;
            if (bytes < tail.Length || landing > s.Length || landing - tail.Length < start) return false;
            s.Position = landing - tail.Length;
            var seen = new byte[tail.Length];
            s.ReadExactly(seen);
            if (!seen.AsSpan().SequenceEqual(tail)) return false;
            if (!TryReadU64(out var len) || len == 0 || len > MaxStringBytes || (long)len > Remaining) return false;
            s.Position = landing;
            return true;
        }

        private static int? FixedWidth(uint type) => type switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => null,
        };

        private static GgufOutcome Outcome(Step s) =>
            s == Step.Malformed ? GgufOutcome.Malformed : GgufOutcome.Truncated;

        private GgufHeader Malformed(string detail)
        {
            _malformation = detail;
            return Result(GgufOutcome.Malformed);
        }

        //what was captured so far under this outcome, a truncated result keeps its captures instead of coming back empty
        public GgufHeader Snapshot(GgufOutcome outcome) => Result(outcome);

        private GgufHeader Result(GgufOutcome outcome) => new(
            outcome, outcome == GgufOutcome.Malformed ? _malformation : null,
            _arch, _name, Get(".context_length"), Get(".pooling_type"),
            Get(".block_count"), Get(".attention.head_count"), Get(".attention.head_count_kv"),
            Get(".embedding_length"), Get(".attention.key_length"), Get(".attention.value_length"),
            _chatTemplate, _tensorCount,
            _sizeLabel, Get(".expert_count"), Get(".expert_used_count"), _tensors, Get(PerLayerInput), Get(".full_attention_interval"));

        private long? Get(string suffix) =>
            _arch is not null && _ints.TryGetValue(_arch + suffix, out var v) ? v : null;

        //guarded primitives: Remaining is checked before any read or allocation

        private bool TryReadU32(out uint v)
        {
            v = 0;
            if (Remaining < 4) return false;
            s.ReadExactly(_scratch, 0, 4);
            v = BinaryPrimitives.ReadUInt32LittleEndian(_scratch);
            return true;
        }

        private bool TryReadU64(out ulong v)
        {
            v = 0;
            if (Remaining < 8) return false;
            s.ReadExactly(_scratch, 0, 8);
            v = BinaryPrimitives.ReadUInt64LittleEndian(_scratch);
            return true;
        }

        private bool TrySkip(long n)
        {
            if (n < 0 || n > Remaining) return false;   //never seek past the end
            s.Position += n;
            return true;
        }

        private Step ReadString(out string value)
        {
            value = "";
            if (!TryReadU64(out var len)) return Step.Truncated;
            if (len > MaxStringBytes) return Fail("implausible string length in GGUF header");
            if ((long)len > Remaining) return Step.Truncated;   //check the bytes are there before allocating, a claimed length must not size the buffer
            var b = new byte[len];
            s.ReadExactly(b);
            value = System.Text.Encoding.UTF8.GetString(b);
            return Step.Ok;
        }

        private Step SkipString()
        {
            if (!TryReadU64(out var len)) return Step.Truncated;
            if (len > MaxStringBytes) return Fail("implausible string length in GGUF header");
            return TrySkip((long)len) ? Step.Ok : Step.Truncated;
        }

        //a u64 above long.MaxValue comes back null, reinterpreting it as negative would report a value the bytes did not mean
        private Step ReadInt(uint type, out long? value)
        {
            value = null;
            var width = type switch { 0 or 1 => 1, 2 or 3 => 2, 4 or 5 => 4, _ => 8 };
            if (Remaining < width) return Step.Truncated;
            s.ReadExactly(_scratch, 0, width);
            if (type == 10)
            {
                var raw = BinaryPrimitives.ReadUInt64LittleEndian(_scratch);
                value = raw <= long.MaxValue ? (long)raw : null;
                return Step.Ok;
            }
            value = type switch
            {
                0 => _scratch[0],
                1 => (sbyte)_scratch[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(_scratch),
                3 => BinaryPrimitives.ReadInt16LittleEndian(_scratch),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(_scratch),
                5 => BinaryPrimitives.ReadInt32LittleEndian(_scratch),
                _ => BinaryPrimitives.ReadInt64LittleEndian(_scratch),
            };
            return Step.Ok;
        }

        //one byte off here turns every later read into garbage, so each stride is bounded by the bytes present
        private Step SkipValue(uint type, int depth)
        {
            switch (type)
            {
                case 0: case 1: case 7: return TrySkip(1) ? Step.Ok : Step.Truncated;   //u8/i8/bool
                case 2: case 3: return TrySkip(2) ? Step.Ok : Step.Truncated;           //u16/i16
                case 4: case 5: case 6: return TrySkip(4) ? Step.Ok : Step.Truncated;   //u32/i32/f32
                case 10: case 11: case 12: return TrySkip(8) ? Step.Ok : Step.Truncated;//u64/i64/f64
                case 8: return SkipString();
                case 9:
                    if (depth >= MaxArrayDepth) return Fail("implausibly nested array in GGUF header");
                    if (!TryReadU32(out var elemType)) return Step.Truncated;
                    if (!TryReadU64(out var count)) return Step.Truncated;
                    if (count > MaxArrayCount)
                        return Fail($"implausibly large array count in GGUF: {count}");
                    //fixed-width elements skip arithmetically, the same bound the element loop would hit
                    if (FixedWidth(elemType) is { } width) return TrySkip((long)count * width) ? Step.Ok : Step.Truncated;
                    for (ulong i = 0; i < count; i++)
                    {
                        var step = SkipValue(elemType, depth + 1);
                        if (step != Step.Ok) return step;
                    }
                    return Step.Ok;
                default:
                    return Fail($"unknown GGUF value type {type}");
            }
        }

        private Step Fail(string detail)
        {
            _malformation = detail;
            return Step.Malformed;
        }
    }
}
