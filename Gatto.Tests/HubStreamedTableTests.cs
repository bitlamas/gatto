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

    //a machine whose budget the set fits only without its table: 73.7 GB at the bare margin is 92.1, the whole file is 128. its pool holds the table beside it
    private static HardwareClass Machine(ulong budget = 95_000_000_000UL) => new(
        MemoryTopology.Unified, ShareKind.Dynamic, budget, budget,
        new HardwareSnapshot(null, 160_000_000_000UL, GpuKind.Integrated, null), 0, BudgetBound.Heap);

    private static string ModelJson(string arch) =>
        $"{{\"id\":\"{Repo}\",\"downloads\":1211625,\"gated\":false,\"pipeline_tag\":\"text-generation\","
        + $"\"gguf\":{{\"architecture\":\"{arch}\",\"context_length\":262144,\"total\":176943899520}}}}";

    //the set sits in a folder like unsloth's real ones, so a URL built from the bare name answers 404
    private const string Folder = "UD-Q4_K_XL/";

    private static string TreeJson(string oid) => "[" + string.Join(",", Dn4.Select(f =>
        $"{{\"type\":\"file\",\"path\":\"{Folder}{f.Name}\",\"size\":{f.Bytes},\"lfs\":{{\"oid\":\"{oid}{f.Name}\"}}}}")) + "]";

    private sealed class Hub(string arch, string? failing = null) : HttpMessageHandler
    {
        //the hash prefix every member's oid carries, so a test can change the files between two runs
        public string Oid { get; set; } = "abc";

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
            var body = url.Contains("/tree/main") ? TreeJson(Oid)
                : url.Contains("filter=gguf") ? "[" + ModelJson(arch) + "]"
                : ModelJson(arch);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static HubClient Client(Hub hub) => new(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan });

    //the table's reads: the 24-byte probe of shard one and every read of the shards that hold tensors. the row's kind reads shard one's header from 0 on its own
    private static IEnumerable<(string File, long From, long To)> TableReads(Hub hub) =>
        hub.Ranges.Where(r => r.File != Shard(1) || r.To == 23);

    private static Task<(ModelRow? Row, bool NoWeights)> Lookup(Hub hub) =>
        HubSearch.LookupModelAsync(Client(hub), Repo, Machine(), 4096, CancellationToken.None);

    //the test drives HubSearch.LookupModelAsync, the path a typed id takes
    [Fact]
    public async Task A_LISTED_ARCHITECTURE_IS_PRICED_WITHOUT_ITS_TABLE_AND_FITS()
    {
        var hub = new Hub("qwen4exp");
        var row = (await Lookup(hub)).Row;

        Assert.NotNull(row);
        Assert.Equal(FitRegime.FitsGpu, row!.Fit);
        Assert.Equal(TableBytes, row.RowQuant!.StreamedBytes);
        Assert.Empty(hub.NotFound);
    }

    //a file that fits the card whole needs no table, since no table could change that verdict, so it costs no read
    [Fact]
    public async Task A_FILE_THAT_FITS_WHOLE_READS_NO_TABLE()
    {
        var hub = new Hub("qwen4exp");
        var row = (await HubSearch.LookupModelAsync(Client(hub), Repo, Machine(200_000_000_000UL), 4096, CancellationToken.None)).Row;

        Assert.Empty(TableReads(hub));
        Assert.Equal(FitRegime.FitsGpu, row!.Fit);
        Assert.Null(row.RowQuant!.StreamedBytes);
    }

    //the reads overlap to stay inside the search's budget, and a run that overlaps asks for the same ranges as one that doesn't
    [Fact]
    public async Task THE_READS_OF_A_REPO_OVERLAP_AND_ASK_FOR_NOTHING_MORE()
    {
        var sequentialBudget = new Hub("qwen4exp");
        await Lookup(sequentialBudget);
        var hub = new Hub("qwen4exp") { DelayMs = 40 };

        var row = (await Lookup(hub)).Row;

        Assert.Equal(TableBytes, row!.RowQuant!.StreamedBytes);
        Assert.True(hub.MaxInFlight >= 2, $"at most {hub.MaxInFlight} read was in flight at once");
        Assert.Equal(sequentialBudget.Ranges.Count, hub.Ranges.Count);
        Assert.Equal(sequentialBudget.Ranges.Sum(r => r.To - r.From + 1), hub.Ranges.Sum(r => r.To - r.From + 1));
    }

    [Fact]
    public async Task SHARD_ONE_IS_PROBED_FOR_24_BYTES_AND_NEVER_READ_FURTHER()
    {
        var hub = new Hub("qwen4exp");
        await Lookup(hub);

        Assert.Equal([(Shard(1), 0L, 23L)], TableReads(hub).Where(r => r.File == Shard(1)));
        Assert.All(new[] { 2, 3, 4 }, i => Assert.Contains(hub.Ranges, r => r.File == Shard(i) && r.From > 0 || r.File == Shard(i) && r.To > 23));
    }

    [Fact]
    public async Task AN_UNLISTED_ARCHITECTURE_MAKES_NO_TABLE_REQUEST_AND_PRICES_AS_BEFORE()
    {
        var hub = new Hub("qwen3");
        var row = (await Lookup(hub)).Row;

        //priced as the whole file the set does not fit, and the typed door still returns the row with its regime
        Assert.Empty(TableReads(hub));
        Assert.Equal(FitRegime.DoesNotFit, row!.Fit);
        Assert.Null(row.RowFile);
    }

    //a read that fails prices the quant whole rather than subtracting a table it never read
    [Fact]
    public async Task A_FAILED_READ_PRICES_THE_QUANT_WHOLE()
    {
        var hub = new Hub("qwen4exp", failing: Shard(3));
        var lookup = await Lookup(hub);

        Assert.Equal(FitRegime.DoesNotFit, lookup.Row!.Fit);
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

    //one typed search over a fresh memo and the store, which is what a new gatto model is. it is the engine path that reads the store over this hub's exact ranges
    private static Task<ShelfOutcome> Search(Hub hub, HubReadStore store) =>
        HubSearch.TypedSearchAsync(Client(hub), new UploaderAllowlist("2026-09-21", ["unsloth"]), Families.Load(),
            "Qwen3.8-Flash-Next", Machine(), 4096, CancellationToken.None, memo: new HubTreeMemo(store));

    //the typed search prices the set the way the lookup does, and reads the structure from shard one through its folder
    [Fact]
    public async Task THE_SEARCH_PRICES_THE_SAME_WAY()
    {
        var home = NewHome();
        try
        {
            var hub = new Hub("qwen4exp");
            var row = Assert.Single((await Search(hub, new HubReadStore(home))).Rows);

            Assert.Equal(FitRegime.FitsGpu, row.Fit);
            Assert.Equal(TableBytes, row.RowQuant!.StreamedBytes);
            Assert.Single(hub.Ranges, r => r.File == Shard(1) && r.To == 23);
            Assert.Empty(hub.NotFound);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    //the sharded set has no hash of its own, so this also proves the key built from the members' hashes finds it
    [Fact]
    public async Task A_SECOND_RUN_READS_NO_HEADER_OVER_THE_NETWORK_and_the_sharded_set_hits()
    {
        var home = NewHome();
        try
        {
            var first = new Hub("qwen4exp");
            var before = Assert.Single((await Search(first, new HubReadStore(home))).Rows);
            Assert.NotEmpty(first.Ranges);

            var second = new Hub("qwen4exp");
            var after = Assert.Single((await Search(second, new HubReadStore(home))).Rows);

            Assert.Empty(second.Ranges);
            Assert.Equal(TableBytes, after.RowQuant!.StreamedBytes);
            Assert.Equal(before.Fit, after.Fit);
            Assert.Equal(before.Structure, after.Structure);
            Assert.Equal(before.Experts, after.Experts);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    //the hash from the tree is the file's identity, so a changed file is read again rather than priced from the old one
    [Fact]
    public async Task A_CHANGED_FILE_IS_READ_AGAIN()
    {
        var home = NewHome();
        try
        {
            await Search(new Hub("qwen4exp"), new HubReadStore(home));
            var changed = new Hub("qwen4exp") { Oid = "def" };

            await Search(changed, new HubReadStore(home));

            Assert.NotEmpty(changed.Ranges);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    //a failed read kept on disk would price this file whole until it changes, so only a whole read is stored
    [Fact]
    public async Task A_FAILED_READ_IS_NOT_KEPT_and_a_missing_home_gets_nothing_written()
    {
        var home = NewHome();
        try
        {
            await Search(new Hub("qwen4exp", failing: Shard(3)), new HubReadStore(home));
            var retry = new Hub("qwen4exp");

            await Search(retry, new HubReadStore(home));

            Assert.Contains(retry.Ranges, r => r.File == Shard(3));
        }
        finally { Directory.Delete(home, recursive: true); }

        var absent = Path.Combine(Path.GetTempPath(), "gatto-hubreads-" + Guid.NewGuid().ToString("N"));
        await Search(new Hub("qwen4exp"), new HubReadStore(absent));
        Assert.False(Directory.Exists(absent));
    }

    //an entry another writer broke is a miss, never a throw, since a throw from the store would empty the whole shelf
    [Theory]
    [InlineData("key")]
    [InlineData("values")]
    public async Task A_MALFORMED_ENTRY_READS_AS_A_MISS_and_the_row_is_priced_from_the_network(string broken)
    {
        var home = NewHome();
        try
        {
            var store = new HubReadStore(home);
            await Search(new Hub("qwen4exp"), store);
            var entries = Directory.GetFiles(store.Dir, "*.json");
            Assert.NotEmpty(entries);
            foreach (var entry in entries)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(entry));
                var key = doc.RootElement.GetProperty("key").GetString();
                File.WriteAllText(entry, broken == "key"
                    ? "{\"key\":5,\"streamed\":1}"
                    : "{\"key\":" + System.Text.Json.JsonSerializer.Serialize(key)
                      + ",\"streamed\":\"x\",\"experts_total\":\"x\",\"experts_active\":[]}");
            }
            var again = new Hub("qwen4exp");

            var row = Assert.Single((await Search(again, new HubReadStore(home))).Rows);

            Assert.NotEmpty(again.Ranges);
            Assert.Equal(TableBytes, row.RowQuant!.StreamedBytes);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    //a home that exists, since the store writes nothing into a home the wizard has not created
    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "gatto-hubreads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    //a move refused while another gatto holds the entry open keeps the old entry and leaves no temp file behind
    [Fact]
    public void A_REFUSED_WRITE_LEAVES_NO_TEMP_FILE()
    {
        var home = NewHome();
        try
        {
            var store = new HubReadStore(home);
            var quant = new HubQuant("m-Q4_K_M.gguf", 1_000, "f00d");
            store.PutStructure("o/m", quant, new StructureFacts("dense", null));
            var entry = Assert.Single(Directory.GetFiles(store.Dir, "*.json"));

            using (File.Open(entry, FileMode.Open, FileAccess.Read, FileShare.None))
                store.PutStructure("o/m", quant, new StructureFacts("MoE", (128, 8)));

            Assert.Empty(Directory.GetFiles(store.Dir, "*.tmp"));
            Assert.True(store.TryGetStructure("o/m", quant, out var kept));
            Assert.Equal("dense", kept!.Cell);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    //the pane's quants zone prices each file from the value the pick used, so a zone row and the shelf row cannot disagree
    [Fact]
    public async Task THE_PANE_PRICES_FROM_THE_SAME_STREAMED_BYTES()
    {
        var row = (await HubSearch.LookupModelAsync(Client(new Hub("qwen4exp")), Repo, Machine(), 4096,
            CancellationToken.None)).Row!;
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
