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

    //a repo that reports zero parameters is dropped from the listing, before any tree call is spent on it
    [Fact]
    public async Task A_REPO_WITH_ZERO_PARAMETERS_IS_NOT_OFFERED_and_absent_is_not_zero()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Listing("o/real", downloads: 300, prms: 1_000_000_000),
            Listing("o/vocabs", downloads: 900, prms: 0),          //the zero-parameter row is also the most-downloaded, so popularity alone would never drop it.
            Listing("o/unknown", downloads: 100)) + "]");
        foreach (var id in new[] { "o/real", "o/vocabs", "o/unknown" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Equal(["o/real", "o/unknown"], outcome.Rows.Select(r => r.RepoId).Order());
        //the weightless row was never priced, so it cannot count as hidden by fit
        Assert.Equal(0, outcome.HiddenByFit);
        //two tree calls against three listings prove no request was spent on the dropped row.
        Assert.Equal(2, hub.TreeCalls);
    }

    //the untagged row is the most-downloaded, so refusing by tag count cannot pass here, and kind refusals stay out of HiddenByFit
    [Fact]
    public async Task A_KIND_GATTO_CANNOT_SERVE_IS_REFUSED_and_an_untagged_repo_passes_through()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Listing("o/untagged", downloads: 900),
            Listing("o/text", downloads: 800, tag: "text-generation"),
            Listing("o/vision", downloads: 700, tag: "image-text-to-text"),           //the tag real vision GGUFs carry, which the deny-set keeps
            Listing("o/omni", downloads: 600, tag: "any-to-any"),                     //a real tag, seen on the qat variants
            Listing("o/diffusion", downloads: 500, tag: "text-to-image", arch: "flux"),
            Listing("o/embedder", downloads: 400, tag: "sentence-similarity"),
            //the tag and the architecture are both kept, so only the causal flag refuses this row
            Listing("o/diffusion-lm", downloads: 300, tag: "image-text-to-text", causal: false, arch: "diffusion-gemma"),
            //no tag and no causal flag, so the embedding architecture alone refuses it
            Listing("o/embeddinggemma", downloads: 200, arch: "gemma-embedding")) + "]");
        foreach (var id in new[] { "o/untagged", "o/text", "o/vision", "o/omni",
                                   "o/diffusion", "o/embedder", "o/diffusion-lm", "o/embeddinggemma" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Equal(["o/omni", "o/text", "o/untagged", "o/vision"], outcome.Rows.Select(r => r.RepoId).Order());
        Assert.Equal(4, outcome.HiddenByKind);
        Assert.Equal(0, outcome.HiddenByFit);
        //four listings with four tree calls prove the refusal ran before them
        Assert.Equal(4, hub.TreeCalls);
    }

    //unknown tags pass, and any list of kinds to keep would hide whatever the hub invents later
    [Fact]
    public async Task AN_UNREVIEWED_KIND_IS_KEPT_because_an_unknown_tag_is_a_form_of_not_told()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Listing("o/invented-tomorrow", downloads: 900, tag: "text-to-agent"),
            Listing("o/mistagged-rp-model", downloads: 800, tag: "summarization"),
            Listing("o/translator", downloads: 700, tag: "translation")) + "]");
        foreach (var id in new[] { "o/invented-tomorrow", "o/mistagged-rp-model", "o/translator" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Equal(3, outcome.Rows.Count);
        Assert.Equal(0, outcome.HiddenByKind);
    }


    //the fixture's families differ on purpose, or every row matches and a filter that hid nothing passes
    [Fact]
    public async Task A_FAMILY_CHIP_KEEPS_ITS_OWN_ROWS_AND_COUNTS_WHAT_IT_HID()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" +
            Listing("o/gemma-4-12b", downloads: 300, arch: "gemma3") + "," +
            Listing("o/qwen3-flash", downloads: 200, arch: "qwen3moe") + "," +
            Listing("o/gemma-4-e4b", downloads: 100, arch: "gemma3n") + "]");
        foreach (var id in new[] { "o/gemma-4-12b", "o/qwen3-flash", "o/gemma-4-e4b" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            family: "gemma", families: Families.Load());

        Assert.Equal(["o/gemma-4-12b", "o/gemma-4-e4b"], outcome.Rows.Select(r => r.RepoId).Order());
        Assert.Equal(1, outcome.HiddenByFamily);
        Assert.Equal(0, outcome.HiddenByFit);
        //two tree calls for three listings prove the filtered row cost no request.
        Assert.Equal(2, hub.TreeCalls);
    }

    //a filter that hid everything would still pass the test above, so the all chip runs on its own shelf
    [Fact]
    public async Task THE_ALL_CHIP_HIDES_NOTHING()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" +
            Listing("o/gemma-4-12b", downloads: 300, arch: "gemma3") + "," +
            Listing("o/qwen3-flash", downloads: 200, arch: "qwen3moe") + "," +
            Listing("o/mistral-small", downloads: 100, arch: "mistral3") + "]");
        foreach (var id in new[] { "o/gemma-4-12b", "o/qwen3-flash", "o/mistral-small" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var lit = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            family: "all", families: Families.Load());

        Assert.Equal(3, lit.Rows.Count);
        Assert.Equal(0, lit.HiddenByFamily);
    }

    //a row with no architecture cannot answer the chip, so it is hidden but counted
    [Fact]
    public async Task A_LISTING_WITH_NO_ARCHITECTURE_IS_HIDDEN_UNDER_A_CHIP_AND_COUNTED()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + Listing("o/mystery", downloads: 100, ctx: null) + "]");
        hub.ByRepo["o/mystery"] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            family: "gemma", families: Families.Load());

        Assert.Empty(outcome.Rows);
        Assert.Equal(1, outcome.HiddenByFamily);
    }

    //one row per family, so a renamed stem turns exactly one of these into a no-op
    [Theory]
    [InlineData("gemma", "o/gemma-4-12b")]
    [InlineData("qwen", "o/qwen3-flash")]
    [InlineData("deepseek", "o/deepseek-v3")]
    [InlineData("glm", "o/glm-4")]
    [InlineData("mistral", "o/mistral-small")]
    public async Task EVERY_CHIP_FILTERS_TO_THE_ROWS_FAMILYOF_MAPS_TO_IT(string chip, string kept)
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" +
            Listing("o/gemma-4-12b", downloads: 500, arch: "gemma3") + "," +
            Listing("o/qwen3-flash", downloads: 400, arch: "qwen3moe") + "," +
            Listing("o/deepseek-v3", downloads: 300, arch: "deepseek2") + "," +
            Listing("o/glm-4", downloads: 200, arch: "glm4") + "," +
            Listing("o/mistral-small", downloads: 100, arch: "mistral3") + "]");
        foreach (var id in new[] { "o/gemma-4-12b", "o/qwen3-flash", "o/deepseek-v3",
                                   "o/glm-4", "o/mistral-small" })
            hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4.gguf", 2_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            family: chip, families: Families.Load());

        Assert.Equal([kept], outcome.Rows.Select(r => r.RepoId));
        Assert.Equal(4, outcome.HiddenByFamily);
    }

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

    private static async Task<IReadOnlyList<ShelfRow>> Rows(
        Hub hub, string[] orgs, HardwareClass machine, SearchOrder? axis = null,
        bool lift = false, Func<string, Badge?>? badges = null) =>
        (await HubSearch.AssembleAsync(Client(hub), List(orgs), machine, 8192,
            badges ?? NoBadges, CancellationToken.None, axis: axis, includeUnfittable: lift)).Rows;

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

    [Fact]
    public async Task THE_AXIS_IS_A_CHOICE_and_no_longer_derived_from_the_view()
    {
        //two calls on the same view, so an engine that ignored the axis cannot pass this
        var byDownloads = await Rows(ThreeFitting(), ["o"], Machine(6_000_000_000, 8_000_000_000));
        var byParams = await Rows(ThreeFitting(), ["o"], Machine(6_000_000_000, 8_000_000_000),
            axis: SearchOrder.MostParams);

        Assert.Equal(["o/a", "o/b", "o/c"], byDownloads.Select(r => r.RepoId));
        Assert.Equal(["o/b", "o/a", "o/c"], byParams.Select(r => r.RepoId));
    }

    [Fact]
    public async Task AN_UNCHOSEN_AXIS_STILL_COMES_FROM_THE_VIEWS_OWN_PAIRING()
    {
        //the assert compares against OrderFor itself, so a changed pairing moves both sides
        var natural = HubSearch.OrderFor(HubSearchView.Broadened);
        var implicitly_ = await Rows(ThreeFitting(), ["o"], Machine(6_000_000_000, 8_000_000_000));
        var explicitly = await Rows(ThreeFitting(), ["o"], Machine(6_000_000_000, 8_000_000_000), axis: natural);

        Assert.Equal(explicitly.Select(r => r.RepoId), implicitly_.Select(r => r.RepoId));
    }

    [Fact]
    public async Task MostParams_PLACES_A_COUNT_WE_WERE_NEVER_GIVEN_LAST()
    {
        //the row with no gguf.total sorts last, an absent count is not a zero
        var rows = await Rows(ThreeFitting(), ["o"], Machine(6_000_000_000, 8_000_000_000),
            axis: SearchOrder.MostParams);

        Assert.Equal("o/c", rows[^1].RepoId);
        Assert.Null(rows[^1].Params);
    }

    [Fact]
    public async Task VerifiedFirst_IS_TWO_LEVEL_badged_first_then_the_views_own_axis()
    {
        //the badged row is the least downloaded, so only a two-level sort gives this order
        Badge? Badges(string id) => id == "o/c"
            ? new Badge(id, new DateOnly(2026, 8, 1), "b", "greedy", Passed: 5, Ran: 5) : null;

        var rows = await Rows(ThreeFitting(), ["o"], Machine(6_000_000_000, 8_000_000_000),
            axis: SearchOrder.VerifiedFirst, badges: Badges);

        Assert.Equal(["o/c", "o/a", "o/b"], rows.Select(r => r.RepoId));
    }

    [Fact]
    public async Task THE_OUTCOME_COUNTS_WHAT_THE_FIT_FILTER_HID()
    {
        //models that were priced and produced no row, which is what the screen's hidden count says
        var outcome = await HubSearch.AssembleAsync(Client(OneFitsOneDoesNot()), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Equal("o/fits", Assert.Single(outcome.Rows).RepoId);
        Assert.Equal(1, outcome.HiddenByFit);
    }

    [Fact]
    public async Task LIFTING_THE_FILTER_HIDES_NOTHING_and_the_count_says_so()
    {
        //the lift makes the pick return a row, so the count cannot stay at one
        var outcome = await HubSearch.AssembleAsync(Client(OneFitsOneDoesNot()), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            includeUnfittable: true);

        Assert.Equal(2, outcome.Rows.Count);
        Assert.Equal(0, outcome.HiddenByFit);
    }

    [Fact]
    public async Task WHAT_WAS_NEVER_PRICED_IS_COUNTED_SEPARATELY_from_what_did_not_fit()
    {
        //rows the search never reached are counted apart, or the count line blames them for a fit check nobody ran
        var outcome = await HubSearch.AssembleAsync(Client(ThreeFitting()), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 1);

        Assert.Single(outcome.Rows);
        Assert.Equal(0, outcome.HiddenByFit);
        Assert.Equal(2, outcome.NotChecked);
    }

    [Fact]
    public async Task A_REPO_WE_COULD_NOT_REACH_IS_NOT_COUNTED_AS_ONE_THAT_DID_NOT_FIT()
    {
        //an unreachable repo does not count as hidden by fit, or the count line blames an outage on this machine's memory
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Listing("o/fits", downloads: 100), Listing("o/unreachable", downloads: 900)) + "]");
        hub.ByRepo["o/fits"] = () => Ok($"[{Quant("s-Q4.gguf", 2_000_000_000)}]");
        hub.ByRepo["o/unreachable"] = () => Status(HttpStatusCode.ServiceUnavailable);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        //the fixture has to fail that tree call, or a quiet success satisfies the zero count for the wrong reason
        Assert.Equal("o/fits", Assert.Single(outcome.Rows).RepoId);
        Assert.Equal(0, outcome.HiddenByFit);
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

    [Fact]
    public async Task UNLIFTED_a_model_where_nothing_fits_is_still_ABSENT_ENTIRELY()
    {
        //without the lift the exclusion still applies, or the lift working would be true of an engine that stopped excluding
        var rows = await Rows(OneFitsOneDoesNot(), ["o"], Machine(6_000_000_000, 8_000_000_000));

        Assert.Equal("o/fits", Assert.Single(rows).RepoId);
    }

    [Fact]
    public async Task LIFTED_the_row_ARRIVES_LAST_and_carries_the_SMALLEST_unfittable_quant()
    {
        //the huge model has more downloads, so the row arriving last shows the fit tier outranks the axis
        var rows = await Rows(OneFitsOneDoesNot(), ["o"], Machine(6_000_000_000, 8_000_000_000), lift: true);

        Assert.Equal(["o/fits", "o/huge"], rows.Select(r => r.RepoId));
        Assert.Equal(FitRegime.DoesNotFit, rows[^1].Fit);
        Assert.Equal("less-big-Q4.gguf", rows[^1].PickedQuant.FileName);
    }

    [Fact]
    public async Task The_largest_GPU_fitting_quant_wins()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("small-Q2.gguf", 1_000_000_000)}," +
                                     $"{Quant("mid-Q4.gguf", 4_000_000_000)}," +
                                     $"{Quant("huge-Q8.gguf", 900_000_000_000)}]");

        //1.25x on the file size, since a listing has no kv term, so a 4 GB quant needs 5 GB of gpu
        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            8192, NoBadges, CancellationToken.None)).Rows;

        var row = Assert.Single(rows);
        Assert.Equal("mid-Q4.gguf", row.PickedQuant.FileName);
        Assert.Equal(FitRegime.FitsGpu, row.Fit);
        Assert.Equal("o", row.Publisher);
    }

    [Fact]
    public async Task When_nothing_fits_the_GPU_the_largest_RAM_fitting_quant_wins()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("a-Q4.gguf", 4_000_000_000)},{Quant("b-Q6.gguf", 6_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(1_000_000_000, 20_000_000_000),
            8192, NoBadges, CancellationToken.None)).Rows;

        var row = Assert.Single(rows);
        Assert.Equal("b-Q6.gguf", row.PickedQuant.FileName);
        Assert.Equal(FitRegime.FitsRamOnly, row.Fit);
    }

    [Fact]
    public async Task A_model_where_nothing_fits_is_absent_from_the_rows_entirely()
    {
        //no row stands for a model that does not fit, the caller just sees none
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("huge-Q8.gguf", 500_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_quant_claiming_more_than_four_TB_is_never_picked()
    {
        //the row keeps the tree's raw size, the Estimate clamp at MaxFileBytes is what rejects the absurd one
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("sane-Q4.gguf", 2_000_000_000)}," +
                                     $"{Quant("absurd.gguf", 9_000_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("sane-Q4.gguf", Assert.Single(rows).PickedQuant.FileName);
    }

    [Fact]
    public async Task A_native_context_of_fifty_million_still_assembles()
    {
        //fit uses min(native, ctxForFit) and leaves the clamp to its own test, so removing either mechanism shows up
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10, ctx: 50_000_000)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal(50_000_000L, Assert.Single(rows).NativeCtx);   //the row reports the raw context, but fit uses the bounded value.
    }

    [Fact]
    public async Task A_caller_context_above_MaxContext_is_clamped_rather_than_thrown()
    {
        //native context is omitted so only the cap applies, and Estimate would throw ArgumentOutOfRangeException without the clamp
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10, ctx: null)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            50_000_000, NoBadges, CancellationToken.None)).Rows;

        Assert.Single(rows);
        Assert.Null(Assert.Single(rows).NativeCtx);
    }

    //a machine with room for everything, so the quant token is the only variable these fixtures test
    private static HardwareClass Roomy() => Machine(40_000_000_000, 80_000_000_000);

    [Fact]
    public async Task A_BAND_QUANT_BEATS_A_BIGGER_NON_BAND_ONE_THAT_ALSO_FITS()
    {
        //both quants fit, so this pick shows the band outranking size
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 4_000_000_000)}," +
                                     $"{Quant("m-BF16.gguf", 16_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Roomy(),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("m-Q4_K_M.gguf", Assert.Single(rows).PickedQuant.FileName);
    }

    [Fact]
    public async Task Q6_K_IS_THE_TOP_OF_THE_BAND_and_beats_Q5_K_M()
    {
        //the Q6_K file is the smaller one on purpose, so it can only win by band rank
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("m-Q5_K_M.gguf", 5_000_000_000)}," +
                                     $"{Quant("m-Q6_K.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Roomy(),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("m-Q6_K.gguf", Assert.Single(rows).PickedQuant.FileName);
    }

    [Fact]
    public async Task THE_BAND_IS_A_PREFERENCE_AND_NOT_A_FILTER()
    {
        //no band member here, so the pick falls back to the largest quant that fits
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("m-IQ2_XXS.gguf", 2_000_000_000)}," +
                                     $"{Quant("m-Q8_0.gguf", 8_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Roomy(),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("m-Q8_0.gguf", Assert.Single(rows).PickedQuant.FileName);
    }

    [Fact]
    public async Task THE_BAND_ONLY_PREFERS_AMONG_QUANTS_THAT_FIT()
    {
        //a Q6_K no machine can hold must not beat a Q4_K_M that fits, so fit decides before the band orders
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 2_000_000_000)}," +
                                     $"{Quant("m-Q6_K.gguf", 900_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("m-Q4_K_M.gguf", Assert.Single(rows).PickedQuant.FileName);
    }

    [Theory]
    //real vendor names, and a Contains or regex match would take every one of them
    [InlineData("m-IQ4_XS.gguf")]          //this name is one segment and equals no band entry.
    [InlineData("m-UD-Q4_K_XL.gguf")]      //it splits into two segments, and neither is a band entry.
    [InlineData("m-Q4_0.gguf")]            //a Q4 name that is not a K-quant.
    [InlineData("m-Q4_K_L.gguf")]          //a K-quant outside the five ruled band names.
    public async Task A_LOOKALIKE_TOKEN_IS_NOT_A_BAND_MEMBER(string lookalike)
    {
        //the band member is the smaller file, so it can only win by band membership
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("m-Q4_K_S.gguf", 1_000_000_000)}," +
                                     $"{Quant(lookalike, 9_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Roomy(),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("m-Q4_K_S.gguf", Assert.Single(rows).PickedQuant.FileName);
    }

    [Fact]
    public async Task THE_BAND_AND_THE_SHARD_RULE_COMPOSE()
    {
        //the quant token is read with the shard suffix stripped, so a sharded band member still counts
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Shards("m-Q4_K_M", 3, 2_000_000_000)}," +
                                     $"{Quant("m-BF16.gguf", 20_000_000_000)}]");

        var row = Assert.Single((await HubSearch.AssembleAsync(Client(hub), List("o"), Roomy(),
            8192, NoBadges, CancellationToken.None)).Rows);

        Assert.Equal("m-Q4_K_M-00001-of-00003.gguf", row.PickedQuant.FileName);
        Assert.Equal(6_000_000_000, row.PickedQuant.Bytes);
    }

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
    public async Task A_SHARD_SET_IS_PRICED_AT_THE_SUM_so_a_set_that_cannot_fit_loses_every_pick()
    {
        //the set is priced at the sum, or thirteen 4 GB shards would pass as a 4 GB model
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/big", 10)}]");
        hub.ByRepo["o/big"] = () => Ok($"[{Shards("big-Q4_K_M", 13, 4_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_SET_THAT_FITS_IS_ONE_ROW_NAMED_BY_SHARD_ONE_and_a_tail_shard_is_unconstructable()
    {
        //the row names shard one, states the sum and says how many files the set needs
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Shards("m-Q4_K_M", 3, 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(9_000_000_000, 16_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        var row = Assert.Single(rows);
        Assert.Equal("m-Q4_K_M-00001-of-00003.gguf", row.PickedQuant.FileName);
        Assert.Equal(3_000_000_000, row.PickedQuant.Bytes);
        Assert.Equal(3, row.PickedQuant.ShardCount);
        //the detail list holds one entry, or the shelf offers the shards it already collapsed
        Assert.Single(row.AllQuants!);
    }

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
    public async Task THE_TYPED_ID_DOOR_GROUPS_SETS_TOO()
    {
        //the typed-id lookup is the second way into Pick, and it is how a user reaches models the allowlisted shelf never lists
        var hub = new Hub();
        hub.ByModel["someone/unlisted"] = () => Ok(Listing("someone/unlisted", 5));
        hub.ByRepo["someone/unlisted"] = () => Ok($"[{Shards("u-Q4_K_M", 5, 1_000_000_000)}]");

        var row = (await HubSearch.LookupAsync(Client(hub), "someone/unlisted",
            Machine(9_000_000_000, 16_000_000_000), 8192, NoBadges, CancellationToken.None)).Row;

        Assert.NotNull(row);
        Assert.Equal("u-Q4_K_M-00001-of-00005.gguf", row.PickedQuant.FileName);
        Assert.Equal(5_000_000_000, row.PickedQuant.Bytes);
        Assert.Equal(5, row.PickedQuant.ShardCount);
    }

    [Fact]
    public async Task THE_TYPED_ID_DOOR_ALSO_REFUSES_A_SET_THAT_CANNOT_FIT()
    {
        //the repo answered and nothing fits here, so the model endpoint stays stubbed or the call quits before Pick
        var hub = new Hub();
        hub.ByModel["someone/unlisted"] = () => Ok(Listing("someone/unlisted", 5));
        hub.ByRepo["someone/unlisted"] = () => Ok($"[{Shards("u-Q4_K_M", 13, 4_000_000_000)}]");

        var lookup = await HubSearch.LookupAsync(Client(hub), "someone/unlisted",
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Null(lookup.Row);
        //both refusals return a null row, so only this flag separates nothing-fits from no-weights
        Assert.False(lookup.NoWeights);
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

    [Fact]
    public async Task Gated_repos_are_filtered_BEFORE_any_tree_call_is_spent()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/open", 5)},{Listing("o/walled", 900, gated: true)}]");
        hub.ByRepo["o/open"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("o/open", Assert.Single(rows).RepoId);
        Assert.Equal(1, hub.TreeCalls);        //the tree call is never spent on a row that will not be shown.
    }

    [Fact]
    public async Task The_display_cap_bounds_the_number_of_tree_calls()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",",
            Enumerable.Range(0, 25).Select(i => Listing($"o/m{i}", 100 - i))) + "]");
        for (var i = 0; i < 25; i++)
            hub.ByRepo[$"o/m{i}"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"), Machine(6_000_000_000, 8_000_000_000),
            8192, NoBadges, CancellationToken.None, rowBudget: 3)).Rows;

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, hub.TreeCalls);
    }

    [Fact]
    public async Task One_orgs_outage_degrades_that_org_and_not_the_search()
    {
        var hub = new Hub();
        hub.ByOrg["down"] = () => Status(HttpStatusCode.InternalServerError);
        hub.ByOrg["up"] = () => Ok($"[{Listing("up/m", 10)}]");
        hub.ByRepo["up/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("down", "up"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("up/m", Assert.Single(rows).RepoId);
    }

    [Fact]
    public async Task Every_org_failing_yields_no_rows_and_no_throw()
    {
        //all orgs failing yields no rows and no throw, since the wizard can still finish through discovery or a typed id
        var hub = new Hub();
        hub.ByOrg["a"] = () => throw new HttpRequestException("offline");
        hub.ByOrg["b"] = () => Status(HttpStatusCode.ServiceUnavailable);

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("a", "b"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Empty(rows);
    }

    [Fact]
    public async Task An_empty_allowlist_returns_empty_without_throwing()
    {
        var rows = (await HubSearch.AssembleAsync(Client(new Hub()), List(),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;
        Assert.Empty(rows);
    }

    [Fact]
    public async Task An_empty_org_result_is_not_an_error()
    {
        //author= matches exactly, so a renamed org answers nothing and looks like an empty one, which is no failure
        var hub = new Hub();
        hub.ByOrg["gone"] = () => Ok("[]");
        hub.ByOrg["here"] = () => Ok($"[{Listing("here/m", 10)}]");
        hub.ByRepo["here/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("gone", "here"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Single(rows);
    }

    //a flat row list let nothing-fits and hub-unreachable arrive as one signal, so each cause is pinned to its own evidence

    [Fact]
    public async Task NOTHING_FITS_is_evidenced_by_repos_that_were_listed_and_priced()
    {
        //only a pass that listed and priced repos can say nothing fits, an org failure leaves the question open
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10)}]");
        hub.ByRepo["o/m"] = () => Ok($"[{Quant("huge-Q8.gguf", 500_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Empty(outcome.Rows);
        Assert.Equal(HubSearchCause.NothingFits, outcome.Cause);
    }

    [Fact]
    public async Task EVERY_ORG_FAILING_is_an_outage_and_never_a_verdict_on_the_machine()
    {
        var hub = new Hub();
        hub.ByOrg["a"] = () => throw new HttpRequestException("offline");
        hub.ByOrg["b"] = () => Status(HttpStatusCode.ServiceUnavailable);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("a", "b"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Equal(HubSearchCause.HubFailed, outcome.Cause);
    }

    [Fact]
    public async Task AN_OUTAGE_THAT_STARTS_AFTER_THE_LISTINGS_ARRIVE_is_still_an_outage()
    {
        //every tree call dies after the listings arrive, so the empty shelf is an outage rather than a verdict on the machine
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/a", 20)},{Listing("o/b", 10)}]");
        hub.ByRepo["o/a"] = () => Status(HttpStatusCode.ServiceUnavailable);
        hub.ByRepo["o/b"] = () => throw new HttpRequestException("offline");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Empty(outcome.Rows);
        Assert.Equal(HubSearchCause.HubFailed, outcome.Cause);
    }

    [Fact]
    public async Task A_PARTIAL_OUTAGE_THAT_LEFT_NO_CANDIDATES_names_no_cause_at_all()
    {
        //a rename looks exactly like an empty org, so neither sentence is supported and the cause stays null
        var hub = new Hub();
        hub.ByOrg["down"] = () => Status(HttpStatusCode.InternalServerError);
        hub.ByOrg["renamed"] = () => Ok("[]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("down", "renamed"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Empty(outcome.Rows);
        Assert.Null(outcome.Cause);
    }

    [Fact]
    public async Task EVERY_LISTED_REPO_BEING_GATED_names_no_cause_either()
    {
        //gated repos are dropped before any tree call, so nothing is priced and nothing failed.
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/m", 10, gated: true)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Null(outcome.Cause);
    }

    [Fact]
    public async Task An_empty_allowlist_names_no_cause()
    {
        var outcome = await HubSearch.AssembleAsync(Client(new Hub()), List(),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Null(outcome.Cause);   //nothing was asked, so no cause is evidenced.
    }

    [Fact]
    public async Task A_SHELF_WITH_ROWS_ON_IT_carries_no_cause()
    {
        //a dead org does not matter while rows exist, the cause is only for an empty shelf
        var hub = new Hub();
        hub.ByOrg["down"] = () => Status(HttpStatusCode.InternalServerError);
        hub.ByOrg["up"] = () => Ok($"[{Listing("up/m", 10)}]");
        hub.ByRepo["up/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("down", "up"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Single(outcome.Rows);
        Assert.Null(outcome.Cause);
    }

    [Fact]
    public async Task THE_AXIS_DISCRIMINATES_same_rows_two_orders()
    {
        //downloads and dates are opposed on purpose, so an engine that ignores the axis fails either assertion
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok(
            $"[{Listing("o/popular-and-old", 900, modified: "2024-01-01T00:00:00.000Z")}," +
            $"{Listing("o/fresh-and-obscure", 3, modified: "2026-08-01T00:00:00.000Z")}]");
        hub.ByRepo["o/popular-and-old"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        hub.ByRepo["o/fresh-and-obscure"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var broad = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            view: HubSearchView.Broadened)).Rows;
        //the axis is named outright, so the test still covers it if the view pairing changes
        var byRecency = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            axis: SearchOrder.RecentlyUpdated)).Rows;

        Assert.Equal("o/popular-and-old", broad[0].RepoId);
        Assert.Equal("o/fresh-and-obscure", byRecency[0].RepoId);
    }

    [Fact]
    public async Task THE_AXIS_ALSO_DECIDES_WHICH_MODELS_ARE_LOOKED_AT_not_only_their_order()
    {
        //the same key orders the final sort and picks what gets fetched, so a cap of one here pins the selection
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok(
            $"[{Listing("o/old-and-popular", 900, modified: "2024-01-01T00:00:00.000Z")}," +
            $"{Listing("o/fresh-and-obscure", 1, modified: "2026-08-01T00:00:00.000Z")}]");
        hub.ByRepo["o/old-and-popular"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        hub.ByRepo["o/fresh-and-obscure"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var curated = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 1, axis: SearchOrder.RecentlyUpdated)).Rows;

        Assert.Equal("o/fresh-and-obscure", Assert.Single(curated).RepoId);
        //the popular model was never fetched, so the tree-call count is the evidence here
        Assert.Equal(1, hub.TreeCalls);
    }

    [Fact]
    public async Task A_REPO_WITH_NO_DATE_SORTS_LAST_and_is_never_given_one()
    {
        //an unknown date sorts last and the field stays null, so nothing renders a date we were not given
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok(
            $"[{Listing("o/undated", 900)},{Listing("o/dated", 1, modified: "2025-05-05T00:00:00.000Z")}]");
        hub.ByRepo["o/undated"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        hub.ByRepo["o/dated"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            axis: SearchOrder.RecentlyUpdated)).Rows;

        Assert.Equal("o/dated", rows[0].RepoId);
        Assert.Equal(new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero), rows[0].LastModified);
        Assert.Null(rows[1].LastModified);
    }

    [Fact]
    public async Task THE_PARAMETER_COUNT_IS_PARSED_FROM_THE_GGUF_BLOCK_and_absent_stays_null()
    {
        //the count is the listing's own gguf.total, at no extra request, and an absent one stays null
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok(
            "[{\"id\":\"o/measured\",\"downloads\":9,\"gated\":false,"
            + "\"gguf\":{\"architecture\":\"qwen3\",\"context_length\":262144,\"total\":30532122624}},"
            + "{\"id\":\"o/silent-30B\",\"downloads\":1,\"gated\":false,"
            + "\"gguf\":{\"architecture\":\"qwen3\",\"context_length\":262144}}]");
        //keep the sizes plausible, an implausibly small file is refused as a non-quantization, so do not lower the count to fix a failure
        hub.ByRepo["o/measured"] = () => Ok($"[{Quant("q.gguf", 18_000_000_000)}]");
        hub.ByRepo["o/silent-30B"] = () => Ok($"[{Quant("q.gguf", 18_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(24_000_000_000, 64_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal(30_532_122_624L, rows.Single(r => r.RepoId == "o/measured").Params);
        Assert.Null(rows.Single(r => r.RepoId == "o/silent-30B").Params);
    }

    [Fact]
    public void The_view_is_paired_with_its_axis_in_exactly_one_place()
    {
        //both views pair with most-downloaded, and OrderFor is the only place that pair is written
        Assert.Equal(SearchOrder.MostDownloaded, HubSearch.OrderFor(HubSearchView.Curated));
        Assert.Equal(SearchOrder.MostDownloaded, HubSearch.OrderFor(HubSearchView.Broadened));
    }

    [Fact]
    public async Task THE_CURATED_VIEW_SEARCHES_ONE_PUBLISHER_and_says_which()
    {
        var hub = new Hub();
        hub.ByOrg["chosen"] = () => Ok($"[{Listing("chosen/m", 10)}]");
        hub.ByOrg["other"] = () => Ok($"[{Listing("other/m", 99)}]");
        hub.ByRepo["chosen/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        hub.ByRepo["other/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(
            Client(hub), new UploaderAllowlist("2026-08-09", ["chosen", "other"], "chosen"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            view: HubSearchView.Curated);

        Assert.Equal("chosen/m", Assert.Single(outcome.Rows).RepoId);
        Assert.Equal("chosen", outcome.CuratedPublisher);
    }

    [Fact]
    public async Task A_CURATION_SLUG_THAT_IS_NO_LONGER_APPROVED_falls_back_to_searching_everyone()
    {
        //two orgs, since a one-org list cannot tell a fallback from a narrowing
        var hub = new Hub();
        hub.ByOrg["still-here"] = () => Ok($"[{Listing("still-here/m", 10)}]");
        hub.ByOrg["also-here"] = () => Ok($"[{Listing("also-here/m", 5)}]");
        hub.ByRepo["still-here/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        hub.ByRepo["also-here/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(
            Client(hub),
            new UploaderAllowlist("2026-08-09", ["still-here", "also-here"], "dropped-at-review"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            view: HubSearchView.Curated);

        //the fallback is a wide search, so both orgs were searched.
        Assert.Equal(2, outcome.Rows.Count);
        Assert.Null(outcome.CuratedPublisher);
    }

    [Fact]
    public async Task The_broadened_view_reports_no_curated_publisher_even_from_a_one_org_list()
    {
        //the publisher follows from a narrowing, so a broadened search over one org still names nobody
        var hub = new Hub();
        hub.ByOrg["only"] = () => Ok($"[{Listing("only/m", 10)}]");
        hub.ByRepo["only/m"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        var list = new UploaderAllowlist("2026-08-09", ["only"], "only");

        var outcome = await HubSearch.AssembleAsync(Client(hub), list,
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            view: HubSearchView.Broadened);

        Assert.Single(outcome.Rows);
        Assert.Null(outcome.CuratedPublisher);
        Assert.Equal(["only"], list.OrgsFor(HubSearchView.Broadened));
    }

    [Fact]
    public async Task The_badge_lookup_is_an_injected_func_called_with_the_repo_id()
    {
        //an injected func rather than the register itself, so the key format is pinned here
        var seen = new List<string>();
        var badge = new Badge("a/one", new DateOnly(2026, 8, 1), "b", "n");
        Badge? Lookup(string key) { seen.Add(key); return key == "a/one" ? badge : null; }

        var hub = new Hub();
        hub.ByOrg["a"] = () => Ok($"[{Listing("a/one", 20)},{Listing("a/two", 10)}]");
        hub.ByRepo["a/one"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");
        hub.ByRepo["a/two"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("a"),
            Machine(6_000_000_000, 8_000_000_000), 8192, Lookup, CancellationToken.None)).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Same(badge, rows.Single(r => r.RepoId == "a/one").Badge);
        Assert.Null(rows.Single(r => r.RepoId == "a/two").Badge);
        Assert.Equal(["a/one", "a/two"], seen);          //the lookup key is the repo id.
    }

    //the listing's own architecture has to reach the row, which a screen test on a hand-built row cannot see
    [Fact]
    public async Task THE_ROW_CARRIES_THE_ARCHITECTURE_the_listing_reported()
    {
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/a", 20)}]");
        hub.ByRepo["o/a"] = () => Ok($"[{Quant("a-Q4_K_M.gguf", 4_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(24_000_000_000, 64_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.Equal("qwen3", outcome.Rows.Single().Arch);
    }

    [Fact]
    public void The_embedded_allowlist_loads_dated_with_the_validated_orgs()
    {
        var list = UploaderAllowlist.Load();
        Assert.Equal("2026-08-09", list.ReviewedDate);
        Assert.Equal(11, list.Orgs.Count);
        Assert.Contains("lmstudio-community", list.Orgs);
        Assert.Contains("nvidia", list.Orgs);            //lowercase, since an uppercase slug answers zero rows
        Assert.Contains("CohereLabs", list.Orgs);        //the current slug, spelled the way the hub does
        Assert.DoesNotContain("NVIDIA", list.Orgs);
        //deliberately excluded, its converted models do not tool-call reliably, the allowlist's own comment holds the reasoning
        Assert.DoesNotContain("TheBloke", list.Orgs);

        //the default publisher must be one of the orgs, or the opening screen quietly un-curates to a wide search
        Assert.Equal("unsloth", list.DefaultView);
        Assert.Contains(list.DefaultView, list.Orgs);
    }

    [Fact]
    public async Task A_typed_repo_id_reaches_a_model_with_no_allowlist_involved()
    {
        //the allowlist shapes browsing only, so a tree call and a range read have to work with no allowlist at all
        var hub = new Hub();
        hub.ByRepo["someone/unlisted"] = () => Ok($"[{Quant("q.gguf", 1_000_000_000)}]");

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
    public async Task A_MODEL_THAT_FITS_OUTRANKS_A_MORE_POPULAR_ONE_THAT_ONLY_FITS_IN_RAM()
    {
        //fit tier first and popularity only inside it, a single weighted score would make this a rubric
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/popular-big", 9_000_000)},{Listing("o/quiet-small", 12)}]");
        hub.ByRepo["o/popular-big"] = () => Ok($"[{Quant("big-Q6.gguf", 9_000_000_000)}]");
        hub.ByRepo["o/quiet-small"] = () => Ok($"[{Quant("small-Q4.gguf", 2_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 40_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal("o/quiet-small", rows[0].RepoId);        //the row that fits the gpu wins although it is 750,000 times less popular.
        Assert.Equal(FitRegime.FitsGpu, rows[0].Fit);
        Assert.Equal(FitRegime.FitsRamOnly, rows[1].Fit);
    }

    [Fact]
    public async Task POPULARITY_STILL_ORDERS_WITHIN_A_TIER()
    {
        //without this test, the tier test above passes by ignoring downloads entirely.
        var hub = new Hub();
        hub.ByOrg["o"] = () => Ok($"[{Listing("o/quiet", 5)},{Listing("o/loud", 5_000_000)}]");
        hub.ByRepo["o/quiet"] = () => Ok($"[{Quant("a-Q4.gguf", 2_000_000_000)}]");
        hub.ByRepo["o/loud"] = () => Ok($"[{Quant("b-Q4.gguf", 2_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 40_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Equal("o/loud", rows[0].RepoId);
        Assert.Equal("o/quiet", rows[1].RepoId);
    }

    [Fact]
    public async Task THE_CAP_SELECTS_AMONG_FITTING_ROWS_not_among_candidates()
    {
        //the cap selects among fitting rows, or a model that fits sits below it and is never fetched
        var hub = new Hub();
        var listings = string.Join(",",
            Enumerable.Range(1, 10).Select(n => Listing($"o/giant{n}", 1_000_000 + n)));
        hub.ByOrg["o"] = () => Ok($"[{listings},{Listing("o/tiny", 1)}]");
        for (var n = 1; n <= 10; n++)
            hub.ByRepo[$"o/giant{n}"] = () => Ok($"[{Quant("g-Q8.gguf", 900_000_000_000)}]");
        hub.ByRepo["o/tiny"] = () => Ok($"[{Quant("t-Q4.gguf", 2_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2)).Rows;

        Assert.Equal("o/tiny", Assert.Single(rows).RepoId);
    }

    [Fact]
    public async Task THE_OVER_FETCH_IS_BOUNDED()
    {
        //with nothing fitting, the search stops at a ceiling instead of asking for every model on the hub
        var hub = new Hub();
        var listings = string.Join(",",
            Enumerable.Range(1, 200).Select(n => Listing($"o/giant{n}", 1_000_000 + n)));
        hub.ByOrg["o"] = () => Ok($"[{listings}]");
        for (var n = 1; n <= 200; n++)
            hub.ByRepo[$"o/giant{n}"] = () => Ok($"[{Quant("g-Q8.gguf", 900_000_000_000)}]");

        var rows = (await HubSearch.AssembleAsync(Client(hub), List("o"),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None)).Rows;

        Assert.Empty(rows);
        //read the ceiling from the code's own constant, a literal copy would drift when the number changes
        Assert.Equal(HubSearch.MaxTreeCalls, hub.TreeCalls);
        Assert.True(hub.TreeCalls > HubSearch.DefaultRowBudget,
            "it must over-fetch at all, or the cap is back to selecting among candidates");
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

    //an expired budget keeps the rows it priced and names no outage, and the fixture cancels only after rows are priced
    [Fact]
    public async Task A_BUDGET_THAT_EXPIRES_MID_ASSEMBLY_KEEPS_WHAT_IT_PRICED()
    {
        var hub = ManyRepos(50);
        using var cts = new CancellationTokenSource();
        hub.OnTree = n => { if (n == 4) cts.Cancel(); };

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, cts.Token, rowBudget: 20, concurrency: 1);

        //the three rows priced before the cancel are what comes back
        Assert.Equal(3, outcome.Rows.Count);
        //no outage cause, since the link answered every request it got.
        Assert.Null(outcome.Cause);
        //50 candidates minus the 4 reached, which the screen already has a sentence for
        Assert.Equal(46, outcome.NotChecked);
    }

    //the test above cancels inside the call and this one between candidates, so each guard needs its own fixture
    [Fact]
    public async Task A_BUDGET_THAT_EXPIRES_BETWEEN_CANDIDATES_KEEPS_WHAT_IT_PRICED()
    {
        var hub = ManyRepos(50);
        hub.TreeThrowsOnCancel = false;
        using var cts = new CancellationTokenSource();
        hub.OnTree = n => { if (n == 4) cts.Cancel(); };

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, cts.Token, rowBudget: 20, concurrency: 1);

        //the fourth call answers before the cancel takes effect, so one more row is priced than in the in-flight case
        Assert.Equal(4, outcome.Rows.Count);
        Assert.Null(outcome.Cause);
        Assert.Equal(46, outcome.NotChecked);
    }

    //nothing priced means no evidence separates a dead link from a slow one, so HubFailed is still the honest answer
    [Fact]
    public async Task A_BUDGET_THAT_EXPIRES_BEFORE_ANYTHING_IS_PRICED_IS_STILL_AN_OUTAGE()
    {
        var hub = ManyRepos(50);
        using var cts = new CancellationTokenSource();
        hub.OnTree = _ => cts.Cancel();

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, cts.Token, rowBudget: 20, concurrency: 1);

        Assert.Empty(outcome.Rows);
        Assert.Equal(HubSearchCause.HubFailed, outcome.Cause);
    }

    //the tree count is the oracle, since a row count also fits a fixture where everything fits early
    [Fact]
    public async Task THE_CEILING_REACHES_THE_SHELF_THE_DESIGN_WAS_APPROVED_ON()
    {
        //no repo fits, so the assembly prices all 48 while looking for rows.
        var hub = ManyRepos(48, fileBytes: 900_000_000_000);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 10);

        Assert.Equal(48, hub.TreeCalls);
        Assert.Equal(48, outcome.HiddenByFit);
        Assert.Equal(0, outcome.NotChecked);
    }

    //the ceiling bounds the sweep, and what it left is counted rather than silently dropped
    [Fact]
    public async Task THE_UNREACHED_BUCKET_COUNTS_WHAT_THE_CEILING_LEFT()
    {
        var hub = ManyRepos(200, fileBytes: 900_000_000_000);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 10);

        Assert.Equal(100, hub.TreeCalls);
        Assert.Equal(100, outcome.NotChecked);
    }

    //a chip switch re-searches the same repos, so the oracle is the requests the second search does not make
    [Fact]
    public async Task A_SECOND_SEARCH_PRICES_FROM_MEMORY_AND_SPENDS_NO_REQUESTS()
    {
        var hub = ManyRepos(12);
        var memo = new HubTreeMemo();

        var first = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 6, memo: memo);
        var spent = hub.TreeCalls;

        var second = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 6, memo: memo);

        Assert.Equal(6, spent);
        //the second search spends not one more request and returns the same rows.
        Assert.Equal(spent, hub.TreeCalls);
        Assert.Equal(6, memo.Hits);
        Assert.Equal(first.Rows.Select(r => r.RepoId), second.Rows.Select(r => r.RepoId));
    }

    //a structure read is for rows that render, so the fixture has to price more candidates than it shows
    [Fact]
    public async Task THE_STRUCTURE_READ_RUNS_ONLY_FOR_ROWS_THAT_RENDER()
    {
        var hub = ManyRepos(40, fileBytes: 1_000_000_000);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 3);

        Assert.Equal(3, outcome.Rows.Count);
        Assert.Equal(3, hub.FileCalls);
        //the sweep priced at least as many repos as it rendered, or the two counts would prove nothing
        Assert.True(hub.TreeCalls >= 3);
    }

    //the structure facts sit on the row, so the face cannot pair a model with another model's structure
    [Fact]
    public async Task A_RENDERED_ROW_CARRIES_ITS_STRUCTURE()
    {
        var hub = ManyRepos(2, fileBytes: 1_000_000_000);
        hub.ByFile["o/m000/m-Q4_K_M.gguf"] = () => Header(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B"));

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2);

        var row = outcome.Rows.Single(r => r.RepoId == "o/m000");
        Assert.Equal("MoE A4B", row.Structure);
        Assert.Equal((128L, 8L), row.Experts);
    }

    //a header read that fails costs the column, and the row still shows, so this pairs with the positive test above
    [Fact]
    public async Task A_FAILED_HEADER_READ_LEAVES_THE_ROW_WITH_AN_EMPTY_COLUMN()
    {
        var hub = ManyRepos(2, fileBytes: 1_000_000_000);
        hub.ByFile["o/m000/m-Q4_K_M.gguf"] = () => Status(HttpStatusCode.InternalServerError);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2);

        var row = outcome.Rows.Single(r => r.RepoId == "o/m000");
        Assert.Null(row.Structure);
        Assert.Equal(2, outcome.Rows.Count);
    }

    //header hits are counted on HeaderHits, so folding them into the tree hits would redefine a number other guards assert on
    [Fact]
    public async Task A_SECOND_SEARCH_RE_READS_NO_HEADERS()
    {
        var hub = ManyRepos(3, fileBytes: 1_000_000_000);
        var memo = new HubTreeMemo();

        await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 3, memo: memo);
        var spent = hub.FileCalls;

        await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 3, memo: memo);

        Assert.Equal(3, spent);
        Assert.Equal(spent, hub.FileCalls);
        Assert.Equal(3, memo.HeaderHits);
    }

    //a header served from the memo costs no request and none of the ceiling, like a remembered tree
    [Fact]
    public async Task AN_UNANSWERABLE_HEADER_IS_ASKED_ABOUT_ONCE()
    {
        var hub = ManyRepos(2, fileBytes: 1_000_000_000);
        //this file's header read fails, and the absence is what the memo keeps
        hub.ByFile["o/m000/m-Q4_K_M.gguf"] = () => Status(HttpStatusCode.InternalServerError);
        var memo = new HubTreeMemo();

        for (var i = 0; i < 2; i++)
            await HubSearch.AssembleAsync(Client(hub), List(["o"]),
                Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
                rowBudget: 2, memo: memo);

        //the failure is remembered too, or every unanswerable file re-fetches on each chip switch
        Assert.Equal(2, hub.FileCalls);
    }

    //rows past the ceiling keep their place with no header read, so the fixture has to spend the ceiling while keeping rows
    [Fact]
    public async Task A_SEARCH_THAT_SPENT_ITS_CEILING_READS_NO_HEADERS()
    {
        var hub = MostlyTooBig(160, fitFrom: 90, fitTo: 99);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 20);

        Assert.Equal(HubSearch.MaxTreeCalls, hub.TreeCalls);   //the fixture has to spend the ceiling, or the empty column proves nothing
        Assert.NotEmpty(outcome.Rows);                         //rows exist here, so the absent column is a choice rather than nothing to read
        Assert.Equal(0, hub.FileCalls);
    }

    //only a room above zero and below the row count exercises the per-row bound, so assert a relation rather than an exact count
    [Fact]
    public async Task ROWS_PAST_THE_CEILINGS_ROOM_KEEP_AN_EMPTY_COLUMN()
    {
        var hub = EveryNthFits(160, n: 3);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 30);

        var room = HubSearch.MaxTreeCalls - hub.TreeCalls;
        Assert.InRange(room, 1, outcome.Rows.Count - 1);   //the only state where the per-row bound acts
        Assert.Equal(room, hub.FileCalls);                 //the reads spend exactly the remaining room, no more.
        Assert.Contains(outcome.Rows, r => r.Structure is null);   //at least one row stays unread, the other half of the relation
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

    //the ladder runs through the real assembly here, and the older rows rank first so its effect is visible
    [Fact]
    public async Task A_GEMMA_SHELF_FILLS_FROM_THE_CURRENT_GENERATION_DOWN()
    {
        var hub = new Hub();
        //older repos rank first by downloads, so the axis alone would fill the shelf with gemma3.
        var ids = new[] { "o/g3a", "o/g3b", "o/g3c", "o/g4a", "o/g4b" };
        var arch = new[] { "gemma3", "gemma3", "gemma3", "gemma4", "gemma4" };
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",", ids.Select((id, i) =>
            Listing(id, downloads: 100 - i, prms: 1_000_000_000, arch: arch[i]))) + "]");
        foreach (var id in ids) hub.ByRepo[id] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 1_000_000_000)}]");

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2, family: "gemma", families: Families.Load());

        //a stop at the budget would price no gemma4 at all, so the ladder has to reach the current generation
        Assert.Contains(outcome.Rows, r => r.Arch == "gemma4");

        //the ladder picks which rows appear without sorting them, so the axis winner still leads
        Assert.Equal("o/g3a", outcome.Rows[0].RepoId);

        //older rows the budget left off are counted, since the count line and the a key name them
        Assert.True(outcome.HiddenOlder > 0, "gemma3 rows the budget did not reach must be counted");
        Assert.Equal(0, outcome.HiddenNewer);
    }

    //twelve older repos above the newer two, so the sweep has to price across several windows
    [Fact]
    public async Task THE_WALK_KEEPS_PRICING_ACROSS_WINDOWS_TO_REACH_THE_CURRENT_GENERATION()
    {
        var hub = TwoGenerations(older: 12, newer: 2);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2, family: "gemma", families: Families.Load());

        Assert.Contains(outcome.Rows, r => r.Arch == "gemma4");
        //more requests than one window holds, so the sweep ran past the budget
        Assert.True(hub.TreeCalls > HubSearch.HubConcurrency,
            $"expected several windows of pricing, got {hub.TreeCalls} tree calls");
    }

    //the current generation may never appear, so only the ceiling stops this search
    [Fact]
    public async Task A_MISSING_GENERATION_IS_STOPPED_BY_THE_CEILING()
    {
        //the fixture has no gemma4 at all, so the ladder's stop condition can never be met
        var hub = TwoGenerations(older: 160, newer: 0);

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2, family: "gemma", families: Families.Load());

        Assert.Equal(HubSearch.MaxTreeCalls, hub.TreeCalls);
        //the shelf still fills from the older tier when the current one never appears.
        Assert.NotEmpty(outcome.Rows);
    }

    //the a key lifts the tier default too, or the shelf still hides rows the key promised to show
    [Fact]
    public async Task THE_SHOW_ALL_KEY_LIFTS_THE_TIER_DEFAULT_TOO()
    {
        var hub = TwoGenerations(older: 12, newer: 2);

        var lifted = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 4, family: "gemma", families: Families.Load(), includeUnfittable: true);

        //with the default lifted, the axis alone picks the rows and nothing counts as hidden
        Assert.All(lifted.Rows, r => Assert.Equal("gemma3", r.Arch));
        Assert.Equal(0, lifted.HiddenOlder);
        Assert.Equal(0, lifted.HiddenNewer);
    }

    //zero parameters is its own refusal, and the listing answers it before any tree call
    [Fact]
    public async Task A_REPO_WITH_NO_WEIGHTS_IS_ITS_OWN_REFUSAL()
    {
        var hub = new Hub();
        hub.ByModel["ggml-org/vocabs"] = () => Ok(Listing("ggml-org/vocabs", 5, prms: 0));
        hub.ByRepo["ggml-org/vocabs"] = () => Ok($"[{Quant("v-Q4_K_M.gguf", 1_000_000)}]");

        var lookup = await HubSearch.LookupAsync(Client(hub), "ggml-org/vocabs",
            Machine(9_000_000_000, 16_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.True(lookup.NoWeights);
        Assert.Null(lookup.Row);
        Assert.Equal(0, hub.TreeCalls);
    }

    //the same repo with a real count resolves, or the guard above would pass with everything refused
    [Fact]
    public async Task A_REPO_WITH_WEIGHTS_IS_NOT_REFUSED()
    {
        var hub = new Hub();
        hub.ByModel["ggml-org/vocabs"] = () => Ok(Listing("ggml-org/vocabs", 5, prms: 1_000_000_000));
        hub.ByRepo["ggml-org/vocabs"] = () => Ok($"[{Quant("v-Q4_K_M.gguf", 1_000_000_000)}]");

        var lookup = await HubSearch.LookupAsync(Client(hub), "ggml-org/vocabs",
            Machine(9_000_000_000, 16_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.False(lookup.NoWeights);
        Assert.NotNull(lookup.Row);
        Assert.Equal(1, hub.TreeCalls);
    }

    //an absent count means the expand did not answer, so the repo takes the ordinary path
    [Fact]
    public async Task AN_ABSENT_PARAMETER_COUNT_IS_NOT_NO_WEIGHTS()
    {
        var hub = new Hub();
        hub.ByModel["o/unknown"] = () => Ok(Listing("o/unknown", 5, prms: null));
        hub.ByRepo["o/unknown"] = () => Ok($"[{Quant("m-Q4_K_M.gguf", 1_000_000_000)}]");

        var lookup = await HubSearch.LookupAsync(Client(hub), "o/unknown",
            Machine(9_000_000_000, 16_000_000_000), 8192, NoBadges, CancellationToken.None);

        Assert.False(lookup.NoWeights);
        Assert.NotNull(lookup.Row);
    }

    //the term goes to the server beside author=, so a search narrows inside a publisher and costs one request per org
    [Fact]
    public async Task A_SEARCH_NARROWS_WITHIN_EACH_PUBLISHER_AT_ONE_REQUEST_EACH()
    {
        var hub = new Hub();
        hub.ByOrg["a"] = () => Ok("[]");
        hub.ByOrg["b"] = () => Ok("[]");

        await HubSearch.AssembleAsync(Client(hub), List(["a", "b"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            search: "gemma 4");

        Assert.Equal(2, hub.OrgCalls);                       //one request per org, however many words the search has
        //assert the parsed query value, since the display form is unescaped and a hunt for %20 would fail
        Assert.All(hub.OrgUrls, u =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(u).Query);
            Assert.Equal("gemma 4", q["search"]);
            Assert.False(string.IsNullOrEmpty(q["author"]));   //the author scope must survive the added search term.
        });
    }

    //with no search term, the parameter must be absent entirely. an empty search= is a different query to the hub
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_BROWSE_SENDS_NO_SEARCH_PARAMETER(string? term)
    {
        var hub = new Hub();
        hub.ByOrg["a"] = () => Ok("[]");

        await HubSearch.AssembleAsync(Client(hub), List(["a"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            search: term);

        var query = new Uri(Assert.Single(hub.OrgUrls)).Query;
        Assert.Null(System.Web.HttpUtility.ParseQueryString(query)["search"]);
        Assert.DoesNotContain("search=", query, StringComparison.Ordinal);
    }

    //the search term must be escaped, since an unescaped & ends the parameter and starts a different request
    [Fact]
    public async Task A_TYPED_TERM_CANNOT_ADD_A_PARAMETER()
    {
        var hub = new Hub();
        hub.ByOrg["a"] = () => Ok("[]");

        await HubSearch.AssembleAsync(Client(hub), List(["a"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            search: "x&author=someone-else&full=true");

        var q = System.Web.HttpUtility.ParseQueryString(new Uri(Assert.Single(hub.OrgUrls)).Query);

        //the whole typed line, ampersands and all, arrives as one search value
        Assert.Equal("x&author=someone-else&full=true", q["search"]);
        //the single author parameter is still gatto's own, which is the oracle that matters.
        Assert.Equal(["a"], q.GetValues("author") ?? []);
        Assert.Null(q["full"]);
    }

    //tier logic must never touch a search's results, since the query is itself the filter. the tier lift falls out of the family lift, so it needs its own guard
    [Fact]
    public async Task A_SEARCH_IS_NEVER_REORDERED_OR_NARROWED_BY_GENERATION()
    {
        var hub = TwoGenerations(older: 12, newer: 2);

        var searched = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2, family: "gemma", families: Families.Load(), search: "gemma");

        //the axis's own top two rows, with no gemma4 injected and nothing counted hidden.
        Assert.All(searched.Rows, r => Assert.Equal("gemma3", r.Arch));
        Assert.Equal(0, searched.HiddenOlder);
        Assert.Equal(0, searched.HiddenNewer);
    }

    //the same hub and family browsed must give the current generation a slot, or the guard above passes against a ladder that never runs.
    [Fact]
    public async Task THE_SAME_SHELF_BROWSED_DOES_APPLY_THE_LADDER()
    {
        var hub = TwoGenerations(older: 12, newer: 2);

        var browsed = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2, family: "gemma", families: Families.Load());

        Assert.Contains(browsed.Rows, r => r.Arch == "gemma4");
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

    //the next-tier fill must stop after one extra tier, or a long listing gets priced behind a waiting screen. two tree calls is the oracle
    [Fact]
    public async Task THE_NEXT_TIER_FILL_IS_CAPPED_AT_ONE_EXTRA_TIER()
    {
        var hub = ThreeGenerations();

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 1, family: "deepseek", families: Families.Load());

        Assert.Equal(2, hub.TreeCalls);
        Assert.NotEmpty(outcome.Rows);
    }

    //the cap trims only over-priced chunks. the oracle is the rows, since a call count would miss a short shelf
    [Fact]
    public async Task A_BUDGET_STILL_BEING_FILLED_IS_NEVER_TRIMMED_BY_THE_CAP()
    {
        var hub = ThreeGenerations();

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 3, family: "deepseek", families: Families.Load());

        Assert.Equal(3, outcome.Rows.Count);
    }

    //without a family the loop must stop at its budget as always, so the tier cap never becomes a general budget.
    [Fact]
    public async Task AN_UNTIERED_SEARCH_STILL_STOPS_AT_ITS_BUDGET()
    {
        var hub = ThreeGenerations();

        await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 1);

        Assert.Equal(1, hub.TreeCalls);
    }

    //the ladder works with a bigger pool, so a failure above sits in the pricing loop rather than in TierFill
    [Fact]
    public async Task THE_LADDER_REACHES_THE_CURRENT_GENERATION_WHEN_THE_POOL_IS_BIGGER()
    {
        var hub = new Hub();
        var ids = new[] { "o/g3a", "o/g3b", "o/g3c", "o/g4a", "o/g4b" };
        var arch = new[] { "gemma3", "gemma3", "gemma3", "gemma4", "gemma4" };
        hub.ByOrg["o"] = () => Ok("[" + string.Join(",", ids.Select((id, i) =>
            Listing(id, downloads: 100 - i, prms: 1_000_000_000, arch: arch[i]))) + "]");
        for (var i = 0; i < ids.Length; i++)
        {
            //only the gemma4 files fit, so the sweep must price past the gemma3 rows to find rows.
            var bytes = arch[i] == "gemma3" ? 900_000_000_000L : 1_000_000_000L;
            hub.ByRepo[ids[i]] = () => Ok($"[{Quant("m-Q4_K_M.gguf", bytes)}]");
        }

        var outcome = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 2, family: "gemma", families: Families.Load());

        Assert.All(outcome.Rows, r => Assert.Equal("gemma4", r.Arch));
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

    //a tree answered from memory costs no request, so it must not spend the ceiling. otherwise a second search stops early on a budget it never touched
    [Fact]
    public async Task A_REMEMBERED_TREE_DOES_NOT_SPEND_THE_CEILING()
    {
        //no repo fits, so the sweep prices every candidate its budget allows.
        var hub = ManyRepos(160, fileBytes: 900_000_000_000);
        var memo = new HubTreeMemo();

        await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 10, memo: memo);
        Assert.Equal(100, hub.TreeCalls);      //this pins that the first pass spent exactly the ceiling.

        var second = await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 10, memo: memo);

        //the remembered hundred are free, so the second search prices the remaining sixty with the same budget and repeats no request.
        Assert.Equal(160, hub.TreeCalls);
        Assert.Equal(100, memo.Hits);
        Assert.Equal(160, second.HiddenByFit);
        Assert.Equal(0, second.NotChecked);
    }

    //the high-water mark is the oracle, since totals hide overlap. six stays a literal on both sides, since a test reading the constant cannot fail
    [Fact]
    public async Task SIX_REQUESTS_FLY_AT_ONCE_AND_NEVER_MORE()
    {
        var hub = ManyRepos(60, fileBytes: 900_000_000_000);
        hub.RendezvousAt = 6;

        await HubSearch.AssembleAsync(Client(hub), List(["o"]),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None,
            rowBudget: 10);

        Assert.Equal(6, hub.MaxInFlight);
        //the constant's own value is pinned here once, so a deliberate change must be made here.
        Assert.Equal(6, HubSearch.HubConcurrency);
    }

    //org listing shares the same concurrency gate, since a serial listing would eat the clock. zero tree calls leave only the listing phase to move the number
    [Fact]
    public async Task THE_ORG_LISTINGS_SHARE_THAT_GATE_TOO()
    {
        var hub = new Hub();
        var orgs = Enumerable.Range(0, 12).Select(i => $"org{i}").ToArray();
        foreach (var o in orgs) hub.ByOrg[o] = () => Ok("[]");
        //six stays a literal here too, because a test written from the constant can never fail on its value.
        hub.RendezvousOrgsAt = 6;

        await HubSearch.AssembleAsync(Client(hub), List(orgs),
            Machine(6_000_000_000, 8_000_000_000), 8192, NoBadges, CancellationToken.None);

        //the listing is empty, so every measured in-flight request is a listing call.
        Assert.Equal(0, hub.TreeCalls);
        Assert.Equal(12, hub.OrgCalls);
        Assert.Equal(6, hub.MaxOrgsInFlight);
    }

    //the defect site builds its own client, so no test seam reaches it and a source census guards it instead. the matcher needs a known match and a known miss
    [Fact]
    public void NO_CALLER_TURNS_AN_EXPIRED_BUDGET_BACK_INTO_AN_EMPTY_OUTCOME()
    {
        var source = File.ReadAllText(Path.Combine(
            Gatto.Tests.Census.SourceTree.RepoRoot(), "Gatto", "Cli", "Setup", "LiveSetupProbes.cs"));

        Assert.False(Discards(source), "the search discards priced rows on its own budget again");

        //use the exact shape that shipped here, so the matcher is proven against the real defect
        Assert.True(Discards("""
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return new HubSearchOutcome([], HubSearchCause.HubFailed);
            }
            """));

        //prose about the defect must not fire the matcher, so a known miss keeps it from matching everything
        Assert.False(Discards(
            "// this used to catch OperationCanceledException and return HubFailed; it does not now"));

        static bool Discards(string text)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("catch (OperationCanceledException",
                        StringComparison.Ordinal)) continue;
                //scan a few lines past the catch, where the returned outcome sits.
                var window = string.Join("\n", lines.Skip(i).Take(6));
                if (window.Contains("HubSearchCause.HubFailed", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
