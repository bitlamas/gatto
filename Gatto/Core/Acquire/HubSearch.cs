using System.Reflection;
using System.Text.Json;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//a dated allowlist someone reviewed on a day, which decides which orgs the wizard proposes and never what a typed repo id reaches
internal sealed record UploaderAllowlist(string ReviewedDate, IReadOnlyList<string> Orgs)
{
    private const string ResourceName = "Gatto.Core.Acquire.uploader-allowlist.json";

    //a missing embedded list throws, since an empty allowlist is a legitimate state and must not pass for a broken build
    public static UploaderAllowlist Load()
    {
        using var s = typeof(UploaderAllowlist).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing — the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var reviewed = doc.RootElement.TryGetProperty("reviewed", out var r) ? r.GetString() ?? "" : "";
        var orgs = new List<string>();
        if (doc.RootElement.TryGetProperty("orgs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var o in arr.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } slug)
                    orgs.Add(slug);
        return new UploaderAllowlist(reviewed, orgs);
    }
}

//trees fetched this session, keyed by repo id, so a chip switch is free. session-scoped and safe for six threads reading and writing it at once
internal sealed class HubTreeMemo(HubReadStore? disk = null)
{
    //the reads kept between runs, asked after this memo and before the network. null keeps every read in memory only
    public HubReadStore? Disk { get; } = disk;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HubTree> _trees =
        new(StringComparer.OrdinalIgnoreCase);

    private int _hits;

    //how many prices came from the memo, the only sign that no requests were made. count with Interlocked, since six threads would lose an increment
    public int Hits => Volatile.Read(ref _hits);

    public HubTree? Get(string repoId)
    {
        if (!_trees.TryGetValue(repoId, out var tree)) return null;
        Interlocked.Increment(ref _hits);
        return tree;
    }

    public void Put(string repoId, HubTree tree) => _trees[repoId] = tree;

    //the tokenizers the table reads walked this launch, so a second file of the same model jumps its arrays
    public TokenizerMemory Tokenizers { get; } = new();

    //listings asked this session, keyed by what was asked, so a lifts from the landing without asking its listings again. a new launch asks afresh
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<HubListing>> _listings =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HubListing>? Listing(string key) => _listings.TryGetValue(key, out var rows) ? rows : null;

    public void PutListing(string key, IReadOnlyList<HubListing> rows) => _listings[key] = rows;

    //structure reads remembered like the trees, keyed by repo id and file name. remember an unanswerable read too, so a chip switch does not ask again
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GgufHeader?> _headers =
        new(StringComparer.OrdinalIgnoreCase);

    //header hits stay apart from tree hits, since a search saves two kinds of request and one number cannot say which half. four guards assert on Hits
    public int HeaderHits => Volatile.Read(ref _headerHits);

    private int _headerHits;

    public bool TryGetHeader(string repoId, string fileName, out GgufHeader? header)
    {
        var got = _headers.TryGetValue(Key(repoId, fileName), out header);
        if (got) Interlocked.Increment(ref _headerHits);
        return got;
    }

    public void PutHeader(string repoId, string fileName, GgufHeader? header) =>
        _headers[Key(repoId, fileName)] = header;

    //the first bar always ends the repo id, since RepoIdShape allows no bar, so the key cannot collide whatever the file name holds
    private static string Key(string repoId, string fileName) => repoId + "|" + fileName;
}

//which half of a search is running, the listing of the publishers or the reading of the models' files
internal enum SearchStage { Listing, Reading }

//one moment of a search for the screen that waits on it. both counts are measured while it runs, and the total can grow
internal sealed record SearchProgress(SearchStage Stage, int Done, int Total, IReadOnlyList<string> Publishers);

//why a search came back empty: two causes, which get two different screens, and neither is inferred from the other's absence
internal enum HubSearchCause
{
    //repos were listed and priced and none of their quants fit this machine, which is a fact about the arithmetic
    NothingFits,

    //the Hub failed between listing and pricing: every request came back a failure. named for the fact, since every tree call failing is the same outage
    HubFailed,
}

//search proposes and the arithmetic disposes: one honest quant per model, gated repos never shown, and one org's outage never the search's
internal static class HubSearch
{
    //how many rows the assembly stops at, six on the plain path because the screen shows three escapes and numbered rows stop at nine
    public const int DefaultRowBudget = 6;

    //the ceiling on tree calls, since the cap selects among fitting rows and a machine where nothing fits would otherwise read every listing
    internal const int MaxTreeCalls = 100;

    //six Hub requests in flight at once, one gate for both halves of the search. a politeness bound, and a parameter so a test can run at one
    internal const int HubConcurrency = 6;

    //fit as a display tier: GPU, RAM, unknown, then does not fit, since an uncomputed fit is a weaker claim than a failed one
    private static int TierOf(FitRegime fit) => fit switch
    {
        FitRegime.FitsGpu => 0,
        FitRegime.FitsRamOnly => 1,
        //the Unknown rank sits above DoesNotFit, since a weaker claim must not sit below a definite one. a local file whose GGUF header can't be read produces it
        FitRegime.DoesNotFit => 3,
        _ => 2,
    };

    //one quant per model, the best that fits the GPU then RAM, and no row when nothing fits, and inside a tier the knee

    //one repo named by the user as a model row of one publisher. the row comes back with no pick when the floor hid every file, so the pane can still choose one
    internal static async Task<(ModelRow? Row, bool NoWeights)> LookupModelAsync(
        HubClient client, string repoId, HardwareClass hw, int ctxForFit, CancellationToken ct)
    {
        var listing = await client.ModelAsync(repoId, ct).ConfigureAwait(false);
        if (listing is null) return (null, false);
        //gatto downloads no gated file, so a gated repo is refused here with the licence sentence rather than priced into a fetch that fails
        if (listing.Gated)
            throw new HubUnavailableException(repoId, (int)System.Net.HttpStatusCode.Forbidden, "gated", gated: true);
        if (ModelDiscovery.IsWeightless(listing.Params)) return (null, true);

        var tree = await client.WithStreamedAsync(
            await client.TreeAsync(repoId, ct).ConfigureAwait(false), listing, ct,
            couldMove: TableCouldMove(listing, hw, ctxForFit)).ConfigureAwait(false);
        var row = ShelfSearch.Price(ModelNameOf(listing.QuantizedFrom ?? listing.RepoId), null, 0, null, [(listing, tree)],
            Families.Load(), hw, ctxForFit, lifted: false);

        //the kind is read from the file the row shows, or the one the floor-lifted re-price will show, as every shelf row reads its own
        var shown = row.RowFile is null ? ShelfSearch.Lifted(row, hw, ctxForFit) : row;
        var read = (await ShelfSearch.WithStructureAsync([shown], client, null, async request =>
        {
            try { return await request().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        }, ct).ConfigureAwait(false))[0];
        row = row with
        {
            Structure = read.Structure, Experts = read.Experts,
            Active = row.RowFile is null ? row.Active : read.Active,
        };
        return (row, false);
    }

    //a typed search keeps the per-org listing, since finetunes appear only here, and groups the repos into models by their quantized source
    internal static async Task<ShelfOutcome> TypedSearchAsync(HubClient client, UploaderAllowlist allowlist,
        Families families, string search, HardwareClass hw, int ctxForFit, CancellationToken ct, HubTreeMemo? memo = null,
        bool lifted = false, CancellationToken caller = default)
    {
        using var gate = new SemaphoreSlim(HubConcurrency);
        var kinds = ModelKinds.Load();
        int requests = 0, failures = 0;

        var lists = await Task.WhenAll(allowlist.Orgs.Select(async org =>
        {
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (Org: org, Rows: (IReadOnlyList<HubListing>?)null); }
            try
            {
                Interlocked.Increment(ref requests);
                return (Org: org, Rows: await client.ListAsync(org, 0, long.MaxValue, ct, search).ConfigureAwait(false));
            }
            catch (HubUnavailableException) { Interlocked.Increment(ref failures); return (Org: org, Rows: null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (Org: org, Rows: null); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        //the Hub answers at most one page, so an org that filled it may hold more than the shelf could see
        var fullPage = lists.Where(l => l.Rows is { Count: >= FullPage }).Select(l => l.Org).ToList();
        var hiddenByKind = 0;
        var servable = new List<HubListing>();
        foreach (var l in lists.SelectMany(l => l.Rows ?? []))
        {
            //the Hub's search matches more loosely than the user typed, so a repo stays only when its name holds every typed word
            if (!NameHoldsEveryWord(l.RepoId, search)) continue;
            if (l.Gated || ModelDiscovery.IsWeightless(l.Params) || families.RoleOf(l.Arch) == ArchRole.Variant) continue;
            if (kinds.WillNotServe(l.PipelineTag, l.Causal, l.Arch)) { hiddenByKind++; continue; }
            servable.Add(l);
        }

        //a tagged repo joins its source's model, an untagged one is a model of its own, and the trees go to the most downloaded first
        var groups = servable
            .GroupBy(l => l.QuantizedFrom ?? l.RepoId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(l => l.Downloads))
            .ToList();
        var trees = new System.Collections.Concurrent.ConcurrentDictionary<string, HubTree>(StringComparer.OrdinalIgnoreCase);
        var asked = new List<HubListing>();
        foreach (var listing in groups.SelectMany(g => g.OrderByDescending(l => l.Downloads)))
        {
            if (memo?.Get(listing.RepoId) is { } remembered) trees[listing.RepoId] = remembered;
            else if (asked.Count < MaxTreeCalls) asked.Add(listing);
        }
        await Task.WhenAll(asked.Select(async listing =>
        {
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                //a tree kept on disk under the repo's unchanged date costs no api request
                var disk = memo?.Disk;
                HubTree tree;
                if (disk is not null && disk.TryGetTree(listing.RepoId, listing.LastModified, out var kept))
                {
                    HubTrace.Hit("tree", "disk", listing.RepoId);
                    tree = kept;
                }
                else
                {
                    Interlocked.Increment(ref requests);
                    tree = await client.TreeAsync(listing.RepoId, ct).ConfigureAwait(false);
                    disk?.PutTree(listing.RepoId, listing.LastModified, tree);
                }
                var got = await client.WithStreamedAsync(tree, listing, ct, disk, TableCouldMove(listing, hw, ctxForFit),
                    memo?.Tokenizers).ConfigureAwait(false);
                memo?.Put(listing.RepoId, got);
                trees[listing.RepoId] = got;
            }
            catch (HubUnavailableException) { Interlocked.Increment(ref failures); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        //a adds the unfittable tier here too, and a group with no fitting file above the floor is what a promises
        var priced = groups
            .Select(g => ShelfSearch.Price(ModelNameOf(g.Key), null, 0, null,
                [.. g.Where(l => trees.ContainsKey(l.RepoId)).Select(l => (l, trees[l.RepoId]))],
                families, hw, ctxForFit, lifted))
            .Where(r => r.Publishers.Count > 0)
            .ToList();
        //the shown rows read their headers as the shelf's do, before the order, since a memory row's active count comes from it
        IReadOnlyList<ModelRow> rows = await ShelfSearch.WithStructureAsync([.. priced.Where(r => r.RowFile is not null)], client, memo,
            async read =>
            {
                try { await gate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                try { return await read().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
                finally { gate.Release(); }
            }, ct).ConfigureAwait(false);
        //a deadline that cut the search still leaves its rows their kind, read on a bound of their own
        if (ShelfSearch.CutByTheDeadline(ct, caller)) rows = await ShelfSearch.AfterTheCutAsync(rows, client, memo, caller).ConfigureAwait(false);
        var ordered = lifted ? ShelfSearch.Flat(rows) : ShelfSearch.ByWhereItRuns(rows);
        var cause = ordered.Count > 0 ? (HubSearchCause?)null
            : requests > 0 && failures == requests ? HubSearchCause.HubFailed
            : trees.Count > 0 ? HubSearchCause.NothingFits : null;
        return new ShelfOutcome(ordered,
            ordered.Count(r => r.Fit == FitRegime.FitsGpu), ordered.Count(r => r.Fit == FitRegime.FitsRamOnly),
            ordered.Count(r => r.Fit == FitRegime.DoesNotFit),
            MoreBehindA: !lifted && priced.Any(ShelfSearch.TooBigAboveTheFloor), hiddenByKind, fullPage, cause, ct.IsCancellationRequested);
    }

    //the rows one listing request answers at most
    internal const int FullPage = 1000;

    //each typed word is found in the repo's name, the org left out, ignoring case and the separators - _ . and space on both sides, so gemma-4 finds Gemma4-31b
    internal static bool NameHoldsEveryWord(string repoId, string search)
    {
        var name = Squeezed(repoId[(repoId.IndexOf('/') + 1)..]);
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Squeezed)
            .All(word => word.Length == 0 || name.Contains(word, StringComparison.Ordinal));
    }

    private static string Squeezed(string text) =>
        string.Concat(text.Where(c => c is not ('-' or '_' or '.' or ' ')).Select(char.ToLowerInvariant));

    //a model's name from a source id or a repo id: the name after the org, less a -GGUF a conversion repo adds. an org inside the name, as bartowski keeps it, stays
    internal static string ModelNameOf(string id)
    {
        var name = id[(id.IndexOf('/') + 1)..];
        const string Gguf = "-GGUF";
        return name.Length > Gguf.Length && name.EndsWith(Gguf, StringComparison.OrdinalIgnoreCase) ? name[..^Gguf.Length] : name;
    }

    //lifted admits the unfittable tier and unlifted drops a model where nothing fits. both keep the quality floor unless the floor itself is lifted
    internal static (HubQuant Quant, FitRegime Fit)? Pick(
        IReadOnlyList<HubQuant> quants, HardwareClass hw, long? nativeCtx, int ctxForFit,
        long? totalParams, bool lifted, string? arch = null, bool floorLifted = false) =>
        Pick(quants, q => q, hw, nativeCtx, ctxForFit, totalParams, lifted, arch, floorLifted) is { } p ? (p.Item, p.Fit) : null;

    //generic over the candidate, so a caller that tags each quant with its repo gets the tag back with the pick
    internal static (T Item, FitRegime Fit)? Pick<T>(
        IReadOnlyList<T> items, Func<T, HubQuant> quantOf, HardwareClass hw, long? nativeCtx, int ctxForFit,
        long? totalParams, bool lifted, string? arch = null, bool floorLifted = false)
    {
        //the context window is resolved inside FitOf, so one place decides what a quant is priced at
        (T Item, FitRegime Fit)? bestGpu = null, bestRam = null, bestUnfit = null;

        foreach (var item in items)
        {
            var q = quantOf(item);
            if (ModelDiscovery.IsTooSmallToBeQuantization(q.Bytes, totalParams)) continue;
            if (!QuantToken.AtFloor(q.FileName, totalParams, floorLifted)) continue;
            //the bytes are the tree's raw claim, the clamp at MaxFileBytes inside Estimate is what rejects an absurd size
            switch (FitOf(q.Bytes, nativeCtx, hw, ctxForFit, arch, q.StreamedBytes))
            {
                case FitRegime.FitsGpu when bestGpu is not { } g || Beats(q, quantOf(g.Item)):
                    bestGpu = (item, FitRegime.FitsGpu); break;
                case FitRegime.FitsRamOnly when bestRam is not { } r || Beats(q, quantOf(r.Item)):
                    bestRam = (item, FitRegime.FitsRamOnly); break;
                //the unfittable tier keeps the smallest quant, the one that misses by least, where the tiers above keep the best one that fits
                case FitRegime.DoesNotFit when lifted
                        && (bestUnfit is not { } u || Smaller(q, quantOf(u.Item))):
                    bestUnfit = (item, FitRegime.DoesNotFit); break;
            }
        }
        //the fit filter is a removable default: unlifted, a model where nothing fits yields null and never becomes a row
        return bestGpu ?? bestRam ?? (lifted ? bestUnfit : null);
    }

    //inside a regime: the band, then the band's own range largest first, then above it smallest first, then below it largest first. equal bytes fall to the name, so list order never decides
    internal static bool Beats(HubQuant candidate, HubQuant incumbent)
    {
        var a = QuantToken.BandRank(candidate.FileName);
        var b = QuantToken.BandRank(incumbent.FileName);
        if (a != b) return a > b;
        if (a < 0)
        {
            var ga = Reach(candidate.FileName);
            var gb = Reach(incumbent.FileName);
            if (ga != gb) return ga > gb;
            if (ga == 1) return Smaller(candidate, incumbent);
        }
        return candidate.Bytes != incumbent.Bytes
            ? candidate.Bytes > incumbent.Bytes
            : string.CompareOrdinal(candidate.FileName, incumbent.FileName) < 0;
    }

    //where a non-band file sits against the band: 2 its own range, 1 above it, 0 below it
    private static int Reach(string fileName) => QuantToken.ClassOf(QuantToken.Of(fileName)) switch
    {
        > 6 => 1,
        >= 4 => 2,
        _ => 0,
    };

    private static bool Smaller(HubQuant candidate, HubQuant incumbent) =>
        candidate.Bytes != incumbent.Bytes
            ? candidate.Bytes < incumbent.Bytes
            : string.CompareOrdinal(candidate.FileName, incumbent.FileName) < 0;

    //clamp the native context before Estimate, which throws when it is over the cap, so a listing claiming 50,000,000 still yields a row
    private static int ContextForFit(long? nativeCtx, int ctxForFit)
    {
        var wanted = nativeCtx is { } n && n < ctxForFit ? (int)Math.Max(1, n) : ctxForFit;
        return Math.Clamp(wanted, 1, FitArithmetic.MaxContext);
    }

    //the quant floor lives here so no caller disagrees about which files are quants. the candidate's bytes sum a shard set, so a sharded model is priced whole
    internal static IEnumerable<HubQuant> Candidates(IReadOnlyList<HubQuant> quants, long? repoParams) =>
        quants.Where(q => !ModelDiscovery.IsTooSmallToBeQuantization(q.Bytes, repoParams));

    //with no discrete card the verdict moves one way as the table grows, so equal ends mean no read can change it. beside one, only a whole file that fits the card and the RAM budget is safe unread
    internal static Func<HubQuant, bool> TableCouldMove(HubListing listing, HardwareClass hw, int ctxForFit) =>
        q => hw.Topology == MemoryTopology.Discrete
            ? !(FitOf(q.Bytes, listing.NativeCtx, hw, ctxForFit, listing.Arch, null) == FitRegime.FitsGpu
                && q.Bytes <= (long)hw.RamBudgetBytes)
            : FitOf(q.Bytes, listing.NativeCtx, hw, ctxForFit, listing.Arch, null)
                != FitOf(q.Bytes, listing.NativeCtx, hw, ctxForFit, listing.Arch, q.Bytes);

    //which regime one quant falls in, with one home so Pick and the pane's quants zone cannot disagree
    internal static FitRegime FitOf(long bytes, long? nativeCtx, HardwareClass hw, int ctxForFit,
        string? arch = null, long? streamedBytes = null) =>
        FitArithmetic.Judge(EstimateOf(bytes, nativeCtx, ctxForFit, arch, streamedBytes), hw);

    //the estimate behind a Hub row's regime, exposed so the assumed terms of a row priced without its table can be read
    internal static FitEstimate EstimateOf(long bytes, long? nativeCtx, int ctxForFit, string? arch, long? streamedBytes) =>
        FitArithmetic.Estimate(BareHeader(nativeCtx, arch), bytes,
            ContextForFit(nativeCtx, ctxForFit), KvCacheKind.F16, streamedBytes);

    //a listing knows only the native context, so every other field stays null and Estimate widens its margin
    private static GgufHeader BareHeader(long? nativeCtx, string? arch = null) => new(
        GgufOutcome.Complete, null, arch, null, nativeCtx, null,
        null, null, null, null, null, null, null);
}
