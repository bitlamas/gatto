using System.Buffers.Binary;
using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the Hub shelf prices a listed architecture without its streamed table, read from each member's header before the pick
public class HubStreamedTableTests
{
    private const string Repo = "unsloth/Qwen3.8-Flash-Next-GGUF";
    private const long TableBytes = 28_800_138_240;

    private static readonly (string Name, long Bytes)[] Dn4 =
    [
        (Shard(1), 10_946_624), (Shard(2), 47_447_858_336), (Shard(3), 44_552_691_904), (Shard(4), 10_462_690_720),
    ];

    private static string Shard(int i) => $"Qwen3.8-Flash-Next-Q4_K_XL-DN4-{i:D5}-of-00004.gguf";

    //shard one's real first 24 bytes: the magic, version 3, no tensors, 67 key-value pairs
    private static byte[] ShardOneProbe()
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x46554747);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 67);
        return b;
    }

    private static byte[] HeaderOf(string name) => name == Shard(1)
        ? ShardOneProbe()
        : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dn4", name));

    //a machine whose budget the set fits only without its table: 73.7 GB at the bare margin is 92.1, the whole file is 128
    private static HardwareClass Machine() => new(
        MemoryTopology.Unified, ShareKind.Dynamic, 95_000_000_000UL, 95_000_000_000UL,
        new HardwareSnapshot(null, 1UL, GpuKind.Integrated, null), 0, BudgetBound.Heap);

    private static string ModelJson(string arch) =>
        $"{{\"id\":\"{Repo}\",\"downloads\":1211625,\"gated\":false,\"pipeline_tag\":\"text-generation\","
        + $"\"gguf\":{{\"architecture\":\"{arch}\",\"context_length\":262144,\"total\":176943899520}}}}";

    //the set sits in a folder like unsloth's real ones, so a URL built from the bare name answers 404
    private const string Folder = "UD-Q4_K_XL/";

    private static string TreeJson() => "[" + string.Join(",", Dn4.Select(f =>
        $"{{\"type\":\"file\",\"path\":\"{Folder}{f.Name}\",\"size\":{f.Bytes},\"lfs\":{{\"oid\":\"abc\"}}}}")) + "]";

    private sealed class Hub(string arch, string? failing = null) : HttpMessageHandler
    {
        public readonly List<(string File, long From, long To)> Ranges = [];
        public readonly List<string> NotFound = [];

        //a round trip for every range read, and the most reads that were ever in flight at once
        public int DelayMs { get; init; }
        private int _inFlight;
        public int MaxInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Headers.Range is null) return await Answer(r);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref MaxInFlight)) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, ct);
                return await Answer(r);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        private Task<HttpResponseMessage> Answer(HttpRequestMessage r)
        {
            var url = Uri.UnescapeDataString(r.RequestUri!.ToString());
            if (url.Contains("/resolve/"))
            {
                var path = url[(url.IndexOf("/resolve/main/", StringComparison.Ordinal) + "/resolve/main/".Length)..];
                if (!path.StartsWith(Folder, StringComparison.Ordinal))
                {
                    lock (NotFound) NotFound.Add(path);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                var file = path[Folder.Length..];
                var range = r.Headers.Range!.Ranges.Single();
                lock (Ranges) Ranges.Add((file, range.From!.Value, range.To!.Value));
                if (file == failing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                var bytes = HeaderOf(file);
                var from = (int)Math.Min(range.From!.Value, bytes.Length);
                var to = (int)Math.Min(range.To!.Value + 1, bytes.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(bytes[from..to]) });
            }
            var body = url.Contains("/tree/main") ? TreeJson()
                : url.Contains("filter=gguf") ? "[" + ModelJson(arch) + "]"
                : ModelJson(arch);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static HubClient Client(Hub hub) => new(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan });

    private static Task<HubLookup> Lookup(Hub hub) =>
        HubSearch.LookupAsync(Client(hub), Repo, Machine(), 4096, _ => null, CancellationToken.None);

    //the test drives HubSearch.LookupAsync, the path a typed id takes
    [Fact]
    public async Task A_LISTED_ARCHITECTURE_IS_PRICED_WITHOUT_ITS_TABLE_AND_FITS()
    {
        var hub = new Hub("qwen4exp");
        var row = (await Lookup(hub)).Row;

        Assert.NotNull(row);
        Assert.Equal(FitRegime.FitsGpu, row!.Fit);
        Assert.Equal(TableBytes, row.PickedQuant.StreamedBytes);
        Assert.Empty(hub.NotFound);
    }

    //the reads overlap to stay inside the search's budget, and a run that overlaps asks for the same ranges as one that doesn't
    [Fact]
    public async Task THE_READS_OF_A_REPO_OVERLAP_AND_ASK_FOR_NOTHING_MORE()
    {
        var sequentialBudget = new Hub("qwen4exp");
        await Lookup(sequentialBudget);
        var hub = new Hub("qwen4exp") { DelayMs = 40 };

        var row = (await Lookup(hub)).Row;

        Assert.Equal(TableBytes, row!.PickedQuant.StreamedBytes);
        Assert.True(hub.MaxInFlight >= 2, $"at most {hub.MaxInFlight} read was in flight at once");
        Assert.Equal(sequentialBudget.Ranges.Count, hub.Ranges.Count);
        Assert.Equal(sequentialBudget.Ranges.Sum(r => r.To - r.From + 1), hub.Ranges.Sum(r => r.To - r.From + 1));
    }

    [Fact]
    public async Task SHARD_ONE_IS_PROBED_FOR_24_BYTES_AND_NEVER_READ_FURTHER()
    {
        var hub = new Hub("qwen4exp");
        await Lookup(hub);

        Assert.Equal([(Shard(1), 0L, 23L)], hub.Ranges.Where(r => r.File == Shard(1)));
        Assert.All(new[] { 2, 3, 4 }, i => Assert.Contains(hub.Ranges, r => r.File == Shard(i) && r.From > 0 || r.File == Shard(i) && r.To > 23));
    }

    [Fact]
    public async Task AN_UNLISTED_ARCHITECTURE_MAKES_NO_HEADER_REQUEST_AND_PRICES_AS_BEFORE()
    {
        var hub = new Hub("qwen3");
        var row = (await Lookup(hub)).Row;

        Assert.Empty(hub.Ranges);
        Assert.Null(row);
    }

    //a read that fails prices the quant whole rather than subtracting a table it never read
    [Fact]
    public async Task A_FAILED_READ_PRICES_THE_QUANT_WHOLE()
    {
        var hub = new Hub("qwen4exp", failing: Shard(3));
        var lookup = await Lookup(hub);

        Assert.Null(lookup.Row);
        Assert.False(lookup.NoWeights);
    }

    [Fact]
    public void A_LISTED_ARCHITECTURE_PRICED_WITHOUT_ITS_TABLE_SAYS_SO()
    {
        var e = HubSearch.EstimateOf(102_474_187_584, 262144, 4096, "qwen4exp", streamedBytes: null);

        Assert.Equal(102_474_187_584, e.WeightsBytes);
        Assert.Contains(e.AssumedTerms, t => t.StartsWith("priced as the whole file", StringComparison.Ordinal));
        Assert.DoesNotContain(HubSearch.EstimateOf(102_474_187_584, 262144, 4096, "qwen3", null).AssumedTerms,
            t => t.StartsWith("priced as the whole file", StringComparison.Ordinal));
    }

    //the test drives the browse walk inside HubSearch.AssembleAsync
    [Fact]
    public async Task THE_BROWSE_WALK_PRICES_THE_SAME_WAY()
    {
        var hub = new Hub("qwen4exp");
        var outcome = await HubSearch.AssembleAsync(Client(hub), new UploaderAllowlist("2026-09-21", ["unsloth"]),
            Machine(), 4096, _ => null, CancellationToken.None);

        var row = Assert.Single(outcome.Rows);
        Assert.Equal(FitRegime.FitsGpu, row.Fit);
        Assert.Equal(TableBytes, row.PickedQuant.StreamedBytes);
        //the pick probes shard one once, and the structure column reads 16 KB of the same file after the pick
        Assert.Single(hub.Ranges, r => r.File == Shard(1) && r.To == 23);
        Assert.DoesNotContain(hub.Ranges, r => r.File == Shard(1) && r.To > 16383);
        //the structure column's read reached the picked file through its folder too
        Assert.Contains(hub.Ranges, r => r.File == Shard(1) && r.To == 16383);
        Assert.Empty(hub.NotFound);
    }

    //the pane's quants zone prices each file from the value the pick used, so a zone row and the shelf row cannot disagree
    [Fact]
    public async Task THE_PANE_PRICES_FROM_THE_SAME_STREAMED_BYTES()
    {
        var row = (await Lookup(new Hub("qwen4exp"))).Row!;
        var zone = Assert.Single(Gatto.Cli.Setup.SetupFlow.QuantsOf(row, Machine())!);

        Assert.Equal(FitRegime.FitsGpu, row.Fit);
        Assert.Equal(row.Fit, zone.Fit);
    }

    //a single-file quant's table sits after about 10.9 MB of tokenizer, so the window that looks for it is capped at 16 MB
    [Fact]
    public async Task A_SINGLE_FILE_MEMBER_IS_READ_WITHIN_16_MB()
    {
        var file = SingleFileWithTokenizer(10_900_000);
        long requested = 0;
        RangeFetch fetch = (offset, count, ct) =>
        {
            requested += count;
            var from = (int)Math.Min(offset, file.Length);
            var to = (int)Math.Min(offset + count, file.Length);
            return Task.FromResult(file[from..to]);
        };

        var bytes = await StreamedTableRead.BytesAsync(new HubQuant("single.gguf", file.Length, null), _ => fetch,
            StreamedTensors.Load(), CancellationToken.None);

        Assert.Equal(4_000_000, bytes);
        Assert.True(requested <= (16 << 20) + 24, $"the read asked for {requested:N0} bytes");
    }

    //a GGUF with a tokenizer array of the given size, then a table with a 4 MB streamed tensor and a small resident one
    private static byte[] SingleFileWithTokenizer(int tokenizerBytes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8);
        void Str(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        const int tokenBytes = 91;
        var tokens = tokenizerBytes / (tokenBytes + 8);
        w.Write(0x46554747u); w.Write(3u); w.Write(2UL); w.Write(2UL);
        Str("general.architecture"); w.Write(8u); Str("qwen4exp");
        Str("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write((ulong)tokens);
        var token = new string('t', tokenBytes);
        for (var i = 0; i < tokens; i++) Str(token);
        Str("per_layer_token_embd.weight"); w.Write(2u); w.Write(1UL); w.Write(1_000_000UL); w.Write(0u); w.Write(0UL);
        Str("output.weight"); w.Write(1u); w.Write(64UL); w.Write(0u); w.Write(4_000_000UL);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void THE_LIST_NAMES_THE_TWO_PER_LAYER_EMBEDDING_ARCHITECTURES()
    {
        var list = StreamedTensors.Load();
        Assert.True(list.Streams("qwen4exp"));
        Assert.True(list.Streams("gemma3n"));
        Assert.False(list.Streams("qwen3"));
        Assert.False(list.Streams(null));
    }
}
