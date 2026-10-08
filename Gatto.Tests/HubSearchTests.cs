using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//search proposes and the fit arithmetic decides, with every hub response stubbed so no test touches the network
public class HubSearchTests
{

    private sealed class Hub : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> ByOrg = new();
        public readonly Dictionary<string, Func<HttpResponseMessage>> ByRepo = new();

        //the single-model endpoint the typed-id lookup calls before the tree, so a typed-id test has to answer it or the lookup quits before Pick
        public readonly Dictionary<string, Func<HttpResponseMessage>> ByModel = new();

        //the ranged structure read's responses, keyed by org/name/file.gguf
        public readonly Dictionary<string, Func<HttpResponseMessage>> ByFile = new();

        //every listing url the search requested, in request order.
        public readonly List<string> OrgUrls = [];

        //counted apart from the tree calls, one number cannot say which half of the search spent a request
        private int _fileCalls;

        public int FileCalls => Volatile.Read(ref _fileCalls);

        //six requests run in flight at once, so the count uses Interlocked, a plain ++ loses increments
        private int _treeCalls;

        public int TreeCalls => Volatile.Read(ref _treeCalls);

        //the peak in flight is the oracle for the bound, a total cannot tell six in sequence from six at once
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        //the listing phase has its own counters, a shared number could not say which half the peak came from
        private int _orgCalls;
        private int _orgsInFlight;
        private int _maxOrgsInFlight;

        public int OrgCalls => Volatile.Read(ref _orgCalls);
        public int MaxOrgsInFlight => Volatile.Read(ref _maxOrgsInFlight);

        //called with the tree-call number before the response, so a test can expire the budget mid-assembly
        public Action<int>? OnTree;

        //false leaves the cancel to the top-of-pass check, true fails the call so the catch handles it
        public bool TreeThrowsOnCancel = true;

        //a tree request waits until this many have arrived, so the overlap is a fact rather than a timing hope
        public int RendezvousAt;

        //the same rendezvous for the listing half, so both phases are measured alike
        public int RendezvousOrgsAt;

        private int _arrived;
        private int _orgsArrived;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseOrgs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            const string prefix = "https://huggingface.co/api/models/";
            if (url.Contains("/tree/main"))
            {
                var n = Interlocked.Increment(ref _treeCalls);
                //updated by compare-and-swap, a plain max would lose updates when two threads write the same peak
                var now = Interlocked.Increment(ref _inFlight);
                for (var seen = Volatile.Read(ref _maxInFlight); now > seen;
                     seen = Volatile.Read(ref _maxInFlight))
                    if (Interlocked.CompareExchange(ref _maxInFlight, now, seen) == seen) break;
                try
                {
                    OnTree?.Invoke(n);
                    if (RendezvousAt > 0)
                    {
                        if (Interlocked.Increment(ref _arrived) >= RendezvousAt) _release.TrySetResult();
                        //the wait is bounded, so a too-small bound fails the assertion instead of hanging the run.
                        try { await _release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                        catch (TimeoutException) { }
                    }
                    //the token is checked after the hook, so a cancel takes effect on this call the way a real one does
                    if (TreeThrowsOnCancel) ct.ThrowIfCancellationRequested();
                    var repo = url[prefix.Length..];
                    repo = repo[..repo.IndexOf("/tree/main", StringComparison.Ordinal)];
                    return ByRepo.TryGetValue(repo, out var t) ? t() : Ok("[]");
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
            //the ranged structure read must route before the org fall-through, or the org branch would count it as a listing.
            if (url.Contains("/resolve/main/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _fileCalls);
                var key = url["https://huggingface.co/".Length..].Replace("/resolve/main/", "/");
                return ByFile.TryGetValue(key, out var file) ? file() : Header(GgufTestBytes.Rich());
            }
            //a browse listing always has a query, and the single-model call is a bare path, so the query separates them.
            if (url.StartsWith(prefix, StringComparison.Ordinal) && r.RequestUri.Query.Length == 0)
            {
                var byId = url[prefix.Length..];
                return ByModel.TryGetValue(byId, out var m) ? m() : Status(HttpStatusCode.NotFound);
            }
            var org = System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query)["author"] ?? "";
            //what a search asked for is invisible in the response, so the listing urls are kept here
            lock (OrgUrls) OrgUrls.Add(url);
            Interlocked.Increment(ref _orgCalls);
            var flying = Interlocked.Increment(ref _orgsInFlight);
            for (var seen = Volatile.Read(ref _maxOrgsInFlight); flying > seen;
                 seen = Volatile.Read(ref _maxOrgsInFlight))
                if (Interlocked.CompareExchange(ref _maxOrgsInFlight, flying, seen) == seen) break;
            try
            {
                //a synchronous handler completes inline and shows one in flight whatever the gate allows, so yield before the rendezvous
                await Task.Yield();
                if (RendezvousOrgsAt > 0)
                {
                    if (Interlocked.Increment(ref _orgsArrived) >= RendezvousOrgsAt)
                        _releaseOrgs.TrySetResult();
                    try { await _releaseOrgs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                    catch (TimeoutException) { }
                }
                return ByOrg.TryGetValue(org, out var f) ? f() : Ok("[]");
            }
            finally { Interlocked.Decrement(ref _orgsInFlight); }
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode c) => new(c);

    //answers 206 with the given bytes as the requested slice of a GGUF.
    private static HttpResponseMessage Header(byte[] gguf) =>
        new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(gguf) };

    private static string Listing(
        string repoId, long downloads, bool gated = false, long? ctx = 262144, string? modified = null,
        long? prms = null, string? tag = null, bool? causal = null, string arch = "qwen3")
    {
        var gatedJson = gated ? "\"manual\"" : "false";
        //the parameter count defaults to absent, so a test that leaves it out exercises nulls-sort-last
        var totalJson = prms is null ? "" : ",\"total\":" + prms;
        //causal defaults to absent, as most real listings do, so a test can leave it unanswered
        var causalJson = causal is null ? "" : ",\"causal\":" + (causal.Value ? "true" : "false");
        var ggufJson = ctx is null
            ? ""
            : ",\"gguf\":{\"architecture\":\"" + arch + "\",\"context_length\":" + ctx
              + totalJson + causalJson + "}";
        var tagJson = tag is null ? "" : ",\"pipeline_tag\":\"" + tag + "\"";
        //the modified date defaults to absent, so a test that leaves it out still runs the nulls-sort-last rule
        var modJson = modified is null ? "" : ",\"lastModified\":\"" + modified + "\"";
        return "{\"id\":\"" + repoId + "\",\"downloads\":" + downloads
             + ",\"gated\":" + gatedJson + ggufJson + modJson + tagJson + "}";
    }

    private static string Quant(string name, long bytes) =>
        "{\"type\":\"file\",\"path\":\"" + name + "\",\"size\":" + bytes + ",\"lfs\":{\"oid\":\"abc\"}}";

    //the same fixture with a named fingerprint, so a set's files can differ by hash
    private static string QuantSha(string name, long bytes, string oid) =>
        "{\"type\":\"file\",\"path\":\"" + name + "\",\"size\":" + bytes + ",\"lfs\":{\"oid\":\"" + oid + "\"}}";


    //the chip stems and the ladder must be the same words, or a chip filters to nothing
    [Fact]
    public void THE_CHIP_VOCABULARY_AND_FAMILYOFS_ARE_THE_SAME_WORDS()
    {
        Assert.Equal(
            Families.Stems.OrderBy(x => x, StringComparer.Ordinal),
            Families.Load().Ladder.Where(f => f != "all").OrderBy(x => x, StringComparer.Ordinal));
    }

    private static HubClient Client(Hub h) => new(new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan });

    private static UploaderAllowlist List(params string[] orgs) => new("2026-08-09", orgs);

    //a machine with real gpu and ram budgets, so every fit regime is reachable in one fixture.
    private static HardwareClass Machine(ulong gpu, ulong ram) =>
        new(MemoryTopology.Discrete, ShareKind.None, gpu, ram,
            new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 0UL), 0, BudgetBound.None);

    private static Badge? NoBadges(string _) => null;


    //the search engine only, the axis controls belong to the screen's tests

    //three fitting models that differ on every axis, so no two orderings agree by accident
    private static Hub ThreeFitting()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Listing("o/a", downloads: 300, prms: 1_000_000_000),
            Listing("o/b", downloads: 200, prms: 30_000_000_000, modified: "2026-08-01T00:00:00Z"),
            Listing("o/c", downloads: 100)) + "]");
        foreach (var id in new[] { "o/a", "o/b", "o/c" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");
        return hub;
    }

    //one model whose quants are all too large and one that fits, so the lift adds a row rather than changing one
    private static Hub OneFitsOneDoesNot()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Listing("o/fits", downloads: 100), Listing("o/huge", downloads: 900)) + "]");
        hub.ByRepo["o/fits"] = () => Ok($"[{Quant("s-Q4.gguf", 2_000_000_000)}]");
        hub.ByRepo["o/huge"] = () => Ok($"[{Quant("big-Q8.gguf", 400_000_000_000)}," +
                                        $"{Quant("less-big-Q4.gguf", 90_000_000_000)}]");
        return hub;
    }

    //a machine with room for everything, so the quant token is the only variable these fixtures test
    private static HardwareClass Roomy() => Machine(40_000_000_000, 80_000_000_000);

    [Fact]
    public void THE_BAND_TOKEN_RULE_READS_THE_MODEL_NAME_THROUGH_THE_SHARD_SUFFIX()
    {
        //the two rules stated on their own, so the composition test is not their only guard
        Assert.Equal("Q4_K_M", QuantToken.Of("m-Q4_K_M-00001-of-00013.gguf"));
        Assert.True(QuantToken.InBand("m-Q4_K_M-00001-of-00013.gguf"));
        Assert.Null(QuantToken.Of("m-00001-of-00013.gguf"));
        Assert.False(QuantToken.InBand("m-IQ4_XS.gguf"));
    }

    //the shards come back in scrambled order, since the tree promises none, so the pick cannot depend on enumeration
    private static string Shards(string stem, int count, long each) =>
        string.Join(",", Enumerable.Range(1, count).Reverse()
            .Select(i => Quant($"{stem}-{i:D5}-of-{count:D5}.gguf", each)));

    [Fact]
    public async Task A_SET_HAS_NO_SINGLE_SHA_and_a_plain_file_keeps_its_own()
    {
        //a set has no single sha and a plain file keeps its own, and nothing verifies a sha yet
        var hub = new Hub();
        hub.ByRepo["o/set"] = () => Ok($"[{Shards("m", 2, 1_000_000_000)}]");
        hub.ByRepo["o/plain"] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 1_000_000_000)}]");

        var client = Client(hub);
        Assert.Null(Assert.Single((await client.TreeAsync("o/set", CancellationToken.None)).Quants).Sha256);
        Assert.Equal("abc",
            Assert.Single((await client.TreeAsync("o/plain", CancellationToken.None)).Quants).Sha256);
    }

    [Fact]
    public async Task A_SET_WHOSE_FILES_DISAGREE_ABOUT_CASE_is_still_one_candidate()
    {
        //the fixture's two file names disagree about case, so this pins a convention that folds it
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok(
            $"[{Quant("M-00001-OF-00002.GGUF", 1_000_000_000)},{Quant("m-00002-of-00002.gguf", 1_000_000_000)}]");

        var q = Assert.Single((await Client(hub).TreeAsync("o/m", CancellationToken.None)).Quants);
        Assert.Equal(2_000_000_000, q.Bytes);
        Assert.Equal("M-00001-OF-00002.GGUF", q.FileName);   //the name is the tree's own, a regenerated one would differ in case
    }

    [Fact]
    public async Task AN_INCOMPLETE_LISTING_PRICES_WHAT_THE_TREE_SHOWED_and_counts_what_the_NAME_asserts()
    {
        //the sum of what the tree showed, so an incomplete listing under-prices, and the count still comes from the name
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok(
            $"[{Quant("m-00001-of-00004.gguf", 1_000_000_000)},{Quant("m-00002-of-00004.gguf", 1_000_000_000)}]");

        var q = Assert.Single((await Client(hub).TreeAsync("o/m", CancellationToken.None)).Quants);
        Assert.Equal(2_000_000_000, q.Bytes);
        Assert.Equal(4, q.ShardCount);
    }

    //a set has no hash of its own, so each file keeps its own fingerprint and the summary keeps its shape
    [Fact]
    public async Task A_SETS_FILES_EACH_KEEP_THEIR_OWN_FINGERPRINT()
    {
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok(
            $"[{QuantSha("m-00001-of-00002.gguf", 1_000, "aaa1")},{QuantSha("m-00002-of-00002.gguf", 2_000, "bbb2")}]");

        var q = Assert.Single((await Client(hub).TreeAsync("o/m", CancellationToken.None)).Quants);

        //the summary is unchanged by the members, no sha and the summed bytes
        Assert.Null(q.Sha256);
        Assert.Equal(3_000, q.Bytes);
        //the members are what the fetch verifies against.
        Assert.Equal(["aaa1", "bbb2"], q.Members.Select(m => m.Sha256));
        Assert.Equal([1_000, 2_000], q.Members.Select(m => m.Bytes));
    }

    //the fetch numbers files from this list, so members come back in shard order
    [Fact]
    public async Task AND_THE_MEMBERS_COME_BACK_IN_SHARD_ORDER()
    {
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok(
            $"[{QuantSha("m-00002-of-00002.gguf", 2_000, "bbb2")},{QuantSha("m-00001-of-00002.gguf", 1_000, "aaa1")}]");

        var q = Assert.Single((await Client(hub).TreeAsync("o/m", CancellationToken.None)).Quants);

        Assert.Equal(["m-00001-of-00002.gguf", "m-00002-of-00002.gguf"],
            q.Members.Select(m => m.FileName));
    }

    //a plain file becomes a one-file set, so the fetch needs one code path
    [Fact]
    public async Task A_PLAIN_FILE_IS_ITS_OWN_SINGLE_MEMBER()
    {
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok($"[{QuantSha("m-Q4_K_M.gguf", 4_000, "ccc3")}]");

        var q = Assert.Single((await Client(hub).TreeAsync("o/m", CancellationToken.None)).Quants);

        var only = Assert.Single(q.Members);
        Assert.Equal("m-Q4_K_M.gguf", only.FileName);
        Assert.Equal("ccc3", only.Sha256);
        Assert.Equal(4_000, only.Bytes);
    }

    [Fact]
    public async Task A_PROJECTOR_SET_GROUPS_BY_THE_SAME_RULE()
    {
        //no encoder is sharded, and one rule for both lists keeps a sharded one from offering N projectors
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok(
            $"[{Quant("m-Q4_K_M.gguf", 1_000_000_000)}," +
            $"{Quant("mmproj-f16-00001-of-00002.gguf", 500_000_000)}," +
            $"{Quant("mmproj-f16-00002-of-00002.gguf", 500_000_000)}]");

        var tree = await Client(hub).TreeAsync("o/m", CancellationToken.None);

        var p = Assert.Single(tree.Projectors);
        Assert.Equal(1_000_000_000, p.Bytes);
        Assert.Equal(2, p.ShardCount);
    }

    [Fact]
    public async Task A_SHARD_SHAPED_NAME_ASSERTING_ZERO_SHARDS_IS_LEFT_ALONE()
    {
        //a name ending -of-00000 is still one file, and the disk side shares this definition of a set
        var hub = new Hub();
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("m-00001-of-00000.gguf", 1_000_000_000)}]");

        var q = Assert.Single((await Client(hub).TreeAsync("o/m", CancellationToken.None)).Quants);
        Assert.Equal(1, q.ShardCount);
        Assert.Equal("abc", q.Sha256);
    }

    //a flat row list let nothing-fits and hub-unreachable arrive as one signal, so each cause is pinned to its own evidence

    [Fact]
    public async Task A_typed_repo_id_reaches_a_model_with_no_allowlist_involved()
    {
        //the allowlist shapes browsing only, so a tree call and a range read have to work with no allowlist at all
        var hub = new Hub();
        hub.ByRepo["someone/unlisted"] = () => Ok($"[{Quant("q-Q4_K_M.gguf", 1_000_000_000)}]");

        var quants = (await Client(hub).TreeAsync("someone/unlisted", CancellationToken.None)).Quants;
        Assert.Single(quants);

        var bytes = GgufTestBytes.Rich();
        var header = await RemoteGgufHeader.ReadAsync(
            (off, count, ct) => Task.FromResult(bytes.AsSpan((int)off,
                (int)Math.Max(0, Math.Min(count, bytes.Length - off))).ToArray()),
            CancellationToken.None);

        Assert.True(header.HasAllFitTerms);
    }

    [Fact]
    public void THE_DISPLAY_CAP_LEAVES_ROOM_FOR_THE_ESCAPE_ROWS()
    {
        //numbered rows stop at nine, escape rows included, so the budget has to leave the screen its escapes
        Assert.True(HubSearch.DefaultRowBudget <= 7,
            "model rows + the search screen's two escape rows must not exceed nine");
    }

    //repos under one org, each with one file that fits, all through the real listing parser

    //the budget tests pin concurrency to 1, since arrival order varies in flight and cancelling on a call number would measure the scheduler
    private static Hub ManyRepos(int count, long fileBytes = 2_000_000_000)
    {
        var hub = new Hub();
        var ids = Enumerable.Range(0, count).Select(i => $"o/m{i:D3}").ToList();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            ids.Select((id, i) => Listing(id, downloads: count - i, prms: 1_000_000_000))) + "]");
        foreach (var id in ids) hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4_K_M.gguf", fileBytes)}]");
        return hub;
    }

    //reported on the caller's thread as it happens, since Progress<T> posts to a context and would deliver a moment late
    private sealed class Moments : IProgress<SearchProgress>
    {
        public readonly List<SearchProgress> Seen = [];
        public void Report(SearchProgress p) { lock (Seen) Seen.Add(p); }
    }

    //every nth repo fits, so the sweep pays many requests per row and ends with rows but little room
    private static Hub EveryNthFits(int count, int n)
    {
        var hub = new Hub();
        var ids = Enumerable.Range(0, count).Select(i => $"o/m{i:D3}").ToList();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            ids.Select((id, i) => Listing(id, downloads: count - i, prms: 1_000_000_000))) + "]");
        for (var i = 0; i < count; i++)
        {
            var bytes = i % n == 0 ? 1_000_000_000L : 900_000_000_000L;
            hub.ByRepo[ids[i]] = () => Ok($"[{Quant("m-Q4_K_M.gguf", bytes)}]");
        }
        return hub;
    }

    //a hub with two generations where older repos rank first by downloads, so the axis and the ladder disagree.
    private static Hub TwoGenerations(int older, int newer)
    {
        var hub = new Hub();
        var ids = new List<string>();
        var arch = new List<string>();
        for (var i = 0; i < older; i++) { ids.Add($"o/g3{i:D3}"); arch.Add("gemma3"); }
        for (var i = 0; i < newer; i++) { ids.Add($"o/g4{i:D3}"); arch.Add("gemma4"); }

        hub.ByOrg["o"] = () => Ok("[" + string.Join(",", ids.Select((id, i) =>
            Listing(id, downloads: ids.Count - i, prms: 1_000_000_000, arch: arch[i]))) + "]");
        foreach (var id in ids)
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 1_000_000_000)}]");
        return hub;
    }

    //generations are read from the arch_tiers table rather than from arch names. deepseek has three tiers here, so the cap has tiers to refuse
    private static Hub ThreeGenerations()
    {
        var hub = new Hub();
        string[] ids = ["o/ds2-old", "o/ds32-mid", "o/ds4-new"];
        string[] arch = ["deepseek2", "deepseek32", "deepseek4"];

        hub.ByOrg["o"] = () => Ok("[" + string.Join(",", ids.Select((id, i) =>
            Listing(id, downloads: ids.Length - i, prms: 1_000_000_000, arch: arch[i]))) + "]");
        foreach (var id in ids)
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 1_000_000_000)}]");
        return hub;
    }

    //a hub where every repo is too big except a named band. the sweep can spend the whole request ceiling and still return with rows.
    private static Hub MostlyTooBig(int count, int fitFrom, int fitTo)
    {
        var hub = new Hub();
        var ids = Enumerable.Range(0, count).Select(i => $"o/m{i:D3}").ToList();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            ids.Select((id, i) => Listing(id, downloads: count - i, prms: 1_000_000_000))) + "]");
        for (var i = 0; i < count; i++)
        {
            var bytes = i >= fitFrom && i <= fitTo ? 1_000_000_000L : 900_000_000_000L;
            hub.ByRepo[ids[i]] = () => Ok($"[{Quant("m-Q4_K_M.gguf", bytes)}]");
        }
        return hub;
    }

    //the memo keys a header by its file, or one quant's header answers for another. the check sits at the memo type, since no search can produce the collision
    [Fact]
    public void THE_MEMO_REMEMBERS_A_FILE_NOT_A_REPO()
    {
        var memo = new HubTreeMemo();
        var q6 = GgufHeaderParser.Parse(new MemoryStream(
            GgufTestBytes.WithStructure(expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B")));

        memo.PutHeader("o/m", "m-Q6_K.gguf", q6);

        Assert.True(memo.TryGetHeader("o/m", "m-Q6_K.gguf", out var same));
        Assert.Equal("MoE A4B", ModelStructure.Cell(same!));
        //the other file under the same repo must miss, since the key holds both repo and file
        Assert.False(memo.TryGetHeader("o/m", "m-Q4_K_M.gguf", out _));
    }

}
