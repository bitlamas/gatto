using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the table read walks a tokenizer once a launch: a second file with the same arrays jumps them after checking the landing, and fixed-width arrays are skipped arithmetically
public class TokenizerSkipTests
{
    //the arrays are planted well above the first window, so a jump that saves nothing cannot pass the byte bound
    private const int Tokens = 200_000;
    private const long Bound = 1 << 20;

    //a first shard of a model: tokens, their types and merges, then a table whose streamed tensor holds rows times one million F32 elements
    private static byte[] File(int rows, char last = 'z', bool tokenizer = true, int typeCount = Tokens)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8);
        void Str(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        w.Write(0x46554747u); w.Write(3u); w.Write(2UL); w.Write(tokenizer ? 4UL : 2UL);
        Str("general.architecture"); w.Write(8u); Str("qwen4exp");
        if (tokenizer)
        {
            Str("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write((ulong)Tokens);
            for (var i = 0; i < Tokens; i++) Str(i == Tokens - 1 ? "token-" + last : $"token-{i:D6}");
        }
        Str("tokenizer.ggml.token_type"); w.Write(9u); w.Write(5u); w.Write((ulong)typeCount);
        for (var i = 0; i < typeCount; i++) w.Write(1);
        if (tokenizer)
        {
            Str("tokenizer.ggml.merges"); w.Write(9u); w.Write(8u); w.Write((ulong)Tokens);
            for (var i = 0; i < Tokens; i++) Str($"merge {i:D6}");
        }
        Str("per_layer_token_embd.weight"); w.Write(2u); w.Write(1_000_000UL); w.Write((ulong)rows); w.Write(0u); w.Write(0UL);
        Str("output.weight"); w.Write(1u); w.Write(64UL); w.Write(0u); w.Write(4_000_000UL * (ulong)rows);
        w.Flush();
        return ms.ToArray();
    }

    //a fetch over the planted bytes that counts what it was asked for
    private sealed class Counted(byte[] file)
    {
        public long Asked;
        public RangeFetch Fetch => (offset, count, ct) =>
        {
            Interlocked.Add(ref Asked, count);
            var from = (int)Math.Min(offset, file.Length);
            var to = (int)Math.Min(offset + count, file.Length);
            return Task.FromResult(file[from..to]);
        };
    }

    private static long? WholeWalk(byte[] file) =>
        StreamedTensors.Load().BytesIn([GgufHeaderParser.Parse(new MemoryStream(file)).Tensors]);

    private static Task<long?> Read(byte[] file, Counted counted, TokenizerMemory? memory) =>
        StreamedTableRead.BytesAsync(new HubQuant("m.gguf", file.Length, null), _ => counted.Fetch,
            StreamedTensors.Load(), CancellationToken.None, tokenizers: memory);

    [Fact]
    public async Task A_SECOND_FILE_JUMPS_THE_TOKENIZER_AND_PRICES_THE_SAME()
    {
        var memory = new TokenizerMemory();
        byte[] first = File(rows: 1), second = File(rows: 2);
        Counted a = new(first), b = new(second);

        Assert.Equal(WholeWalk(first), await Read(first, a, memory));
        Assert.Equal(WholeWalk(second), await Read(second, b, memory));
        Assert.Equal(8_000_000, WholeWalk(second));
        Assert.True(a.Asked > 4 * Bound, $"the first file asked {a.Asked:N0} bytes, so it never walked");
        Assert.True(b.Asked < Bound, $"the second file asked {b.Asked:N0} bytes");
    }

    //the same count with a different last string moves the tail, so the jump is refused and the file walks and still prices right
    [Fact]
    public async Task A_DIFFERENT_TOKENIZER_OF_THE_SAME_COUNT_WALKS()
    {
        var memory = new TokenizerMemory();
        byte[] first = File(rows: 1), other = File(rows: 3, last: 'y');
        await Read(first, new Counted(first), memory);
        Counted c = new(other);

        Assert.Equal(WholeWalk(other), await Read(other, c, memory));
        Assert.Equal(12_000_000, WholeWalk(other));
        Assert.True(c.Asked > 4 * Bound, $"the other file asked {c.Asked:N0} bytes, so it jumped a tokenizer it does not hold");
    }

    //six files read at once walk the tokenizer once, since the later ones wait for the first walk
    [Fact]
    public async Task SIX_FILES_AT_ONCE_WALK_ONCE()
    {
        var memory = new TokenizerMemory();
        var files = Enumerable.Range(1, 6).Select(rows => File(rows)).ToList();
        var counts = files.Select(f => new Counted(f)).ToList();

        var read = await Task.WhenAll(files.Select((f, i) => Task.Run(() => Read(f, counts[i], memory))));

        for (var i = 0; i < files.Count; i++) Assert.Equal(WholeWalk(files[i]), read[i]);
        Assert.Equal(1, counts.Count(c => c.Asked > Bound));
    }

    //a fixed-width array is skipped by count times width, so a file with no string array asks little and prices as a whole walk does
    [Fact]
    public async Task A_FIXED_WIDTH_ARRAY_IS_SKIPPED_ARITHMETICALLY()
    {
        var file = File(rows: 1, tokenizer: false, typeCount: 2_000_000);
        Counted c = new(file);

        Assert.Equal(WholeWalk(file), await Read(file, c, null));
        Assert.Equal(4_000_000, WholeWalk(file));
        Assert.True(c.Asked < Bound, $"the file asked {c.Asked:N0} bytes for an 8 MB array it can skip");
    }
}
