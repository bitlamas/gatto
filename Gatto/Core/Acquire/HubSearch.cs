using System.Reflection;
using System.Text.Json;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//a dated allowlist someone reviewed on a day, which decides which orgs the wizard proposes and never what a typed repo id reaches
internal sealed record UploaderAllowlist(
    string ReviewedDate, IReadOnlyList<string> Orgs, string? DefaultView = null)
{
    private const string ResourceName = "Gatto.Core.Acquire.uploader-allowlist.json";

    //curated searches the one dated publisher, broadened every approved org. an unknown or absent default_view falls back to the whole list
    public IReadOnlyList<string> OrgsFor(HubSearchView view) =>
        CuratedPublisherFor(view) is { } only ? [only] : Orgs;

    //who this view narrowed to, or null when it narrowed to nobody. the decision, which OrgsFor reads, since one org in the list is not a curated view
    public string? CuratedPublisherFor(HubSearchView view) =>
        view == HubSearchView.Curated && DefaultView is { Length: > 0 } d && Orgs.Contains(d) ? d : null;

    //null or empty leaves the shipped default_view alone. no membership check here, since that decision lives in CuratedPublisherFor alone
    public UploaderAllowlist Preferring(string? publisher) =>
        publisher is { Length: > 0 } p ? this with { DefaultView = p } : this;

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
        var view = doc.RootElement.TryGetProperty("default_view", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new UploaderAllowlist(reviewed, orgs, view);
    }
}

//the view decides who is searched and how the rows are ordered, and OrderFor is the one home of that pairing
internal enum HubSearchView
{
    //one dated publisher, most downloaded first
    Curated,

    //every approved org, most downloaded in the last 30 days
    Broadened,
}

//orders rows inside a fit tier, which decides the group, and one weighted score for both would be a rubric rather than a sort
internal enum SearchOrder
{
    //the Hub's rolling 30-day download count over the orgs searched
    MostDownloaded,

    //lastModified, and a repo with no date sorts last. an absent date is neither new nor old, so it falls back to a sort position instead of a made-up date
    RecentlyUpdated,

    //the listing's own gguf.total, which may sort because it is a measurement, and an absent count sorts last
    MostParams,

    //verified rows first, then the view's own axis inside each group, and this one is a user choice that must never become the default
    VerifiedFirst,
}

//what a search produced, with cause null when rows exist or no evidence. count hidden-by-fit and not-checked apart, so the count line can be true

//the view and every control that changes the query sit on one record, so a fake can record exactly what was asked for
internal sealed record HubSearchRequest(
    HubSearchView View, SearchOrder? Axis = null, bool IncludeUnfittable = false,
    int? RowBudget = null, string? Search = null, string? Family = null,
    string? Publisher = null);

//repos refused for what they are, counted so the shelf can say what it hid. keep it apart from hidden-by-fit: there is no control beside this count

//trees fetched this session, keyed by repo id, so a chip switch is free. session-scoped and safe for six threads reading and writing it at once
internal sealed class HubTreeMemo
{
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

//rows below the family's dated pin that the budget never reached, which the count line names. rows above the pin keep their own number

//what a typed repo id found and, when nothing, why: a repo with no weights gets its own sentence, checked before the tree call
internal readonly record struct HubLookup(ShelfRow? Row, bool NoWeights);

internal sealed record HubSearchOutcome(
    IReadOnlyList<ShelfRow> Rows, HubSearchCause? Cause, string? CuratedPublisher = null,
    int HiddenByFit = 0, int NotChecked = 0, int HiddenByKind = 0,
    int HiddenOlder = 0, int HiddenNewer = 0,
    //rows a family chip narrowed away, counted so the shelf can say so, since these did not fail a memory arithmetic
    int HiddenByFamily = 0);

//why a search came back empty: two causes, which get two different screens, and neither is inferred from the other's absence
internal enum HubSearchCause
{
    //repos were listed and priced and none of their quants fit this machine, which is a fact about the arithmetic
    NothingFits,

    //the Hub failed between listing and pricing: every request came back a failure. named for the fact, since every tree call failing is the same outage
    HubFailed,
}

//the result row as data, whose names and byte counts are attacker text sanitized at render, and whose fetched facts stay on the row
internal sealed record ShelfRow(
    string RepoId, string Publisher, HubQuant PickedQuant, FitRegime Fit,
    long? NativeCtx, bool Vision, Badge? Badge, long Downloads, bool Gated,
    DateTimeOffset? LastModified = null, long? Params = null,
    IReadOnlyList<HubQuant>? AllQuants = null, IReadOnlyList<HubQuant>? Projectors = null,
    string? Arch = null, string? Structure = null, (long Total, long Active)? Experts = null,
    int FileCount = 0);

//search proposes and the arithmetic disposes: one honest quant per model, gated repos never shown, and one org's outage never the search's
internal static class HubSearch
{
    //how many rows the assembly stops at, six on the plain path because the screen shows three escapes and numbered rows stop at nine
    public const int DefaultRowBudget = 6;

    //the ceiling on tree calls, since the cap selects among fitting rows and a machine where nothing fits would otherwise read every listing
    internal const int MaxTreeCalls = 100;

    //six Hub requests in flight at once, one gate for both halves of the search. a politeness bound, and a parameter so a test can run at one
    internal const int HubConcurrency = 6;

    //rows for every allowlisted org, and never a throw for a Hub problem: one org failing degrades that org. empty reports the cause the search recorded
    public static async Task<HubSearchOutcome> AssembleAsync(
        HubClient client, UploaderAllowlist allowlist, HardwareClass hw, int ctxForFit,
        Func<string, Badge?> badgeLookup, CancellationToken ct, int rowBudget = DefaultRowBudget,
        long minParams = 0, long maxParams = long.MaxValue,
        HubSearchView view = HubSearchView.Broadened,
        SearchOrder? axis = null, bool includeUnfittable = false,
        int concurrency = HubConcurrency, HubTreeMemo? memo = null,
        string? family = null, Families? families = null, string? search = null)
    {
        var orgs = allowlist.OrgsFor(view);

        //a typed search clears the family, and with it the tier, since the query is the filter and tiers are family-scoped
        if (!string.IsNullOrWhiteSpace(search)) family = null;
        //the view's pairing is the default, and the sort control supplies the exception
        var natural = OrderFor(view);
        var order = axis ?? natural;

        //one gate for both phases, since they never overlap and a second semaphore would be a second number for one link
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var listings = new List<HubListing>();
        var orgFailures = 0;

        //the orgs are listed concurrently and merged in org order, and the token is a budget, so expiry keeps the listings already in hand
        var listed = await Task.WhenAll(orgs.Select(async org =>
        {
            //cancelled while queueing is not a Hub failure, since the org was never asked and counting it would read as an outage
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (Rows: (IReadOnlyList<HubListing>?)null, Failed: false); }

            try
            {
                return (Rows: (IReadOnlyList<HubListing>?)
                    await client.ListAsync(org, minParams, maxParams, ct, search)
                        .ConfigureAwait(false),
                    Failed: false);
            }
            //one org's outage degrades that org, and it is counted because every org failing is the difference between two screens
            catch (HubUnavailableException) { return (Rows: null, Failed: true); }
            //count it as a failure of this org, since a budget that expires with nothing priced ends at HubFailed. to the user a timeout and an outage are one fact
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (Rows: null, Failed: true);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        foreach (var (orgRows, failed) in listed)
        {
            if (failed) orgFailures++;
            if (orgRows is not null) listings.AddRange(orgRows);
        }

        //every filter the listing can answer runs here, before any tree call, since a request on a hidden row is the budget leak
        var kinds = ModelKinds.Load();
        var hiddenByKind = 0;

        //filter the family chip from Arch, which the listing already gives, and treat all and an unknown chip as no question at all
        var chip = families is not null && family is { Length: > 0 } f
            && !string.Equals(f, "all", StringComparison.OrdinalIgnoreCase) ? f : null;
        var hiddenByFamily = 0;

        var servable = new List<HubListing>(listings.Count);
        foreach (var l in listings)
        {
            if (l.Gated || ModelDiscovery.IsWeightless(l.Params)) continue;
            if (kinds.WillNotServe(l.PipelineTag, l.Causal, l.Arch)) { hiddenByKind++; continue; }
            //a row with no architecture cannot answer the chip, and it is counted since the shelf must say what it narrowed away
            if (chip is not null
                && !string.Equals(families!.FamilyOf(l.Arch), chip, StringComparison.OrdinalIgnoreCase))
            { hiddenByFamily++; continue; }
            servable.Add(l);
        }

        var candidates = InAxisOrder(servable, order, natural, l => l.RepoId, Key, badgeLookup)
            .ToList();

        var rows = new List<ShelfRow>();
        var treeCalls = 0;
        var treeFailures = 0;
        var hiddenByFit = 0;
        //resolve the tier pin once before the loop, since the row budget now reads it, and an unversioned family leaves it null
        var pin = family is not null && families is not null ? families.PinFor(family) : ModelTier.Unversioned;
        //a lifted shelf shows every generation, so there is no current tier to seek and the budget stops the search
        var tiered = pin.IsVersioned && families is not null && !includeUnfittable;

        var reached = 0;
        //price in bounded windows and merge in candidate order, so the shelf never depends on which response came back first
        for (var at = 0; at < candidates.Count;)
        {
            //stop and keep what is priced: the rest go in the unreached bucket, so a search out of time reads as there is more
            if (ct.IsCancellationRequested) break;
            //on a tiered family keep pricing until the current generation has a row, since the budget alone would never reach it
            if (treeCalls >= MaxTreeCalls) break;
            if (rows.Count >= rowBudget && !MayReachFurther()) break;

            //the window is what is still needed, capped by the concurrency and the ceiling's room, and a generation search gets the full concurrency
            var needed = MayReachFurther() ? concurrency : rowBudget - rows.Count;
            var window = Math.Min(
                Math.Min(Math.Max(1, concurrency), Math.Max(1, needed)),
                MaxTreeCalls - treeCalls);
            //trim by tier only while over-pricing, since a short shelf comes out when the axis must still fill the budget
            var taking = candidates.Skip(at).Take(window).ToList();
            var chunk = MayReachFurther() ? CapToOneExtraTier(taking) : taking;
            if (chunk.Count == 0) break;
            at += chunk.Count;

            reached += chunk.Count;

            var trees = await Task.WhenAll(chunk.Select(async listing =>
            {
                //the memo first, since a hit spends no request and never takes the gate or counts against the ceiling
                if (memo?.Get(listing.RepoId) is { } remembered)
                    return (Tree: (HubTree?)remembered, Failed: false, Spent: false);

                try { await gate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return (Tree: (HubTree?)null, Failed: true, Spent: false); }

                try
                {
                    var got = await client.TreeAsync(listing.RepoId, ct).ConfigureAwait(false);
                    //a stop during the table reads leaves the tree unremembered, so the next search reads the tables itself
                    try { got = await client.WithStreamedAsync(got, listing, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    { return (Tree: (HubTree?)got, Failed: false, Spent: true); }
                    memo?.Put(listing.RepoId, got);
                    return (Tree: (HubTree?)got, Failed: false, Spent: true);
                }
                catch (HubUnavailableException) { return (Tree: null, Failed: true, Spent: true); }
                //count it as a tree failure, since a budget that died with nothing priced must end at HubFailed
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return (Tree: null, Failed: true, Spent: true);
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);

            //count the calls the window actually spent, since a memoised row spends nothing and the window was already sized against the ceiling's room
            treeCalls += trees.Count(t => t.Spent);

            //decide here, in candidate order: Pick and the row build are pure, so every count stays independent of the scheduler
            foreach (var (listing, got) in chunk.Zip(trees))
            {
                if (got.Failed || got.Tree is not { } tree) { treeFailures++; continue; }

                //counted here, since deriving it from the candidate and row counts would blame this machine's memory for a network outage
                if (Pick(tree.Quants, hw, listing.NativeCtx, ctxForFit, listing.Params, includeUnfittable, listing.Arch)
                    is not { } picked)
                {
                    hiddenByFit++;
                    continue;
                }

                rows.Add(new ShelfRow(
                    listing.RepoId, PublisherOf(listing.RepoId), picked.Quant, picked.Fit,
                    listing.NativeCtx, tree.HasProjector, badgeLookup(listing.RepoId),
                    listing.Downloads, listing.Gated, listing.LastModified, listing.Params,
                    tree.Quants, tree.Projectors, listing.Arch,
                    //every file the tree listed, passed to the pane's heading, which costs nothing since this loop already made the call
                    FileCount: tree.FileCount));
            }
        }

        //sort by tier over the axis order, since a stable sort keeps the axis inside each tier, and only a dated pin is tiered
        var arranged = (IReadOnlyList<ShelfRow>)[.. Arrange(rows, order, natural, badgeLookup)];
        var filled = Tiered(arranged, family, families, rowBudget, includeUnfittable);
        var ordered = filled.Rows;

        //the structure read runs over the rendered rows only, since a read for a row nobody sees is wasted. it shares the gate and the ceiling
        ordered = await WithStructureAsync(
            ordered, client, gate, memo, MaxTreeCalls - treeCalls, ct).ConfigureAwait(false);

        return new HubSearchOutcome(ordered, ordered.Count > 0 ? null : CauseOf(),
            allowlist.CuratedPublisherFor(view), hiddenByFit,
            Math.Max(0, candidates.Count - reached), hiddenByKind,
            filled.HiddenOlder, filled.HiddenNewer, hiddenByFamily);

        //whether the current generation still has no row, asked of the rows since the ceiling already stops the looking
        bool NeedsCurrentTier() =>
            tiered && !rows.Any(r => families!.TierFor(r.Arch, r.RepoId) is var t
                && t.Major == pin.Major && t.Minor == pin.Minor);

        //one extra tier per search, counted as distinct tiers among the rows already priced, so a three-tier listing makes two rounds
        int TiersPriced() =>
            rows.Select(r => families!.TierFor(r.Arch, r.RepoId))
                .Select(t => (t.Major, t.Minor))
                .Distinct()
                .Count();

        //the loop's question in one place, so the break and the window cannot disagree about how far to reach
        bool MayReachFurther() => NeedsCurrentTier() && TiersPriced() <= 1;

        //trim the chunk rather than the round count, since a single round under concurrency can price a listing that spans three tiers
        List<HubListing> CapToOneExtraTier(List<HubListing> chunk)
        {
            if (!tiered || chunk.Count == 0) return chunk;

            var seen = rows.Select(r => families!.TierFor(r.Arch, r.RepoId))
                .Select(t => (t.Major, t.Minor)).ToHashSet();
            var taken = new List<HubListing>();
            foreach (var listing in chunk)
            {
                var t = families!.TierFor(listing.Arch, listing.RepoId);
                var next = new HashSet<(int, int)>(seen) { (t.Major, t.Minor) };
                if (next.Count > 2) break;
                seen = next;
                taken.Add(listing);
            }
            return taken;
        }

        //read the cause off what the search actually did, since each branch below names a fact the loops above recorded
        HubSearchCause? CauseOf()
        {
            //every request failed at either half, since an outage after the listings is still an outage and not this machine's fault
            if (orgs.Count > 0 && orgFailures == orgs.Count) return HubSearchCause.HubFailed;
            if (treeCalls > 0 && treeFailures == treeCalls) return HubSearchCause.HubFailed;

            //repos were listed and priced and none of them fit, which is the only evidence that lets the screen say so
            if (treeCalls > treeFailures) return HubSearchCause.NothingFits;

            //everything else stays unexplained, since naming one of the two causes would be inventing evidence
            return null;
        }
    }

    //the ladder when the family has a dated pin, a plain budget cut otherwise. a row of another family is ordered within the budget and kept
    private static TierFillResult<ShelfRow> Tiered(
        IReadOnlyList<ShelfRow> rows, string? family, Families? families, int budget, bool lifted)
    {
        //the a key lifts the tier default too, since a count line saying a shows all must not keep a generation hidden
        if (lifted) return new([.. rows.Take(budget)], 0, 0);
        if (family is null || families is null) return new([.. rows.Take(budget)], 0, 0);

        var pin = families.PinFor(family);
        if (!pin.IsVersioned) return new([.. rows.Take(budget)], 0, 0);

        return TierFill.Select(rows, r => families.TierFor(r.Arch, r.RepoId), pin, budget);
    }

    //give each rendered row its structure cell from the file gatto picked, and leave it empty when the read fails
    private static async Task<IReadOnlyList<ShelfRow>> WithStructureAsync(
        IReadOnlyList<ShelfRow> rows, HubClient client, SemaphoreSlim gate,
        HubTreeMemo? memo, int room, CancellationToken ct)
    {
        //a short-circuit rather than the bound: the per-row i >= room check is what stops the reads, this only saves the task fan-out
        if (rows.Count == 0 || room <= 0) return rows;

        var reads = await Task.WhenAll(rows.Select(async (row, i) =>
        {
            if (i >= room || ct.IsCancellationRequested)
                return (Index: i, Header: (GgufHeader?)null);

            //the memo first, on the same terms as a tree, and an absence is remembered so an unanswerable file is asked once
            if (memo is not null && memo.TryGetHeader(row.RepoId, row.PickedQuant.FileName, out var known))
                return (Index: i, Header: known);

            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (Index: i, Header: (GgufHeader?)null); }

            try
            {
                var got = await client
                    .StructureHeaderAsync(row.RepoId, row.PickedQuant.RepoPath, ct)
                    .ConfigureAwait(false);
                memo?.PutHeader(row.RepoId, row.PickedQuant.FileName, got);
                return (Index: i, Header: got);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return (Index: i, Header: (GgufHeader?)null); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var built = new ShelfRow[rows.Count];
        foreach (var (index, header) in reads)
            built[index] = header is null
                ? rows[index]
                : rows[index] with
                {
                    Structure = ModelStructure.Cell(header),
                    Experts = ModelStructure.Experts(header),
                };
        return built;
    }

    //the one place a view is paired with its axis, and now only the default, since the sort control lets a user choose another
    public static SearchOrder OrderFor(HubSearchView view) => view switch
    {
        //both views take the downloads axis, since a newest-first curated shelf was overturned by watching it used
        HubSearchView.Curated => SearchOrder.MostDownloaded,
        _ => SearchOrder.MostDownloaded,
    };

    //the sort key both halves use, so the fetch order is the display order, and a null date sorts last
    private static long Key(HubListing l, SearchOrder order) => order switch
    {
        SearchOrder.RecentlyUpdated => (l.LastModified ?? DateTimeOffset.MinValue).UtcTicks,
        //absent is long.MinValue rather than 0, since descending puts it last and zero belongs to a repo that reports no parameters
        SearchOrder.MostParams => l.Params ?? long.MinValue,
        _ => l.Downloads,
    };

    private static long Key(ShelfRow r, SearchOrder order) => order switch
    {
        SearchOrder.RecentlyUpdated => (r.LastModified ?? DateTimeOffset.MinValue).UtcTicks,
        SearchOrder.MostParams => r.Params ?? long.MinValue,
        _ => r.Downloads,
    };

    //tier then axis, in one function both the engine and the face call. the axis is part of the query, so a sort control must not empty the shelf
    public static IEnumerable<ShelfRow> Arrange(
        IReadOnlyList<ShelfRow> rows, SearchOrder order, SearchOrder natural,
        Func<string, Badge?> badgeLookup) =>
        //sort the tier over the axis order, since a stable sort keeps the axis inside each tier and VerifiedFirst stays an ordering
        InAxisOrder(rows, order, natural, r => r.RepoId, Key, badgeLookup)
            .OrderBy(r => TierOf(r.Fit));

    //the axis applied, two-level case included, so the fetch order and the display order cannot drift, with the view's natural axis as the tie-break
    private static IOrderedEnumerable<T> InAxisOrder<T>(
        IEnumerable<T> items, SearchOrder order, SearchOrder tieBreak,
        Func<T, string> idOf, Func<T, SearchOrder, long> key, Func<string, Badge?> badgeLookup) =>
        order == SearchOrder.VerifiedFirst
            ? items.OrderByDescending(i => badgeLookup(idOf(i)) is not null)
                   .ThenByDescending(i => key(i, tieBreak))
            : items.OrderByDescending(i => key(i, order));

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

    //one repo named by the user, priced like a browse row. null means the repo answered and nothing fits, unlike a failed request which throws
    internal static async Task<HubLookup> LookupAsync(
        HubClient client, string repoId, HardwareClass hw, int ctxForFit,
        Func<string, Badge?> badgeLookup, CancellationToken ct)
    {
        //call the model first, then the tree: the listing has context, gated and downloads, and pricing from the tree alone would drop them
        var listing = await client.ModelAsync(repoId, ct).ConfigureAwait(false);
        if (listing is null) return new HubLookup(null, false);

        //a repo that publishes no weights is its own answer. the parameter count arrives on the listing, so this costs no request and comes before the tree call
        if (ModelDiscovery.IsWeightless(listing.Params)) return new HubLookup(null, true);

        var tree = await client.WithStreamedAsync(
            await client.TreeAsync(repoId, ct).ConfigureAwait(false), listing, ct).ConfigureAwait(false);
        if (Pick(tree.Quants, hw, listing.NativeCtx, ctxForFit, listing.Params, arch: listing.Arch) is not { } picked)
            return new HubLookup(null, false);

        return new HubLookup(new ShelfRow(
            listing.RepoId, PublisherOf(listing.RepoId), picked.Quant, picked.Fit,
            listing.NativeCtx, tree.HasProjector, badgeLookup(listing.RepoId),
            listing.Downloads, listing.Gated, listing.LastModified, listing.Params,
            tree.Quants, tree.Projectors), false);
    }

    private static (HubQuant Quant, FitRegime Fit)? Pick(
        IReadOnlyList<HubQuant> quants, HardwareClass hw, long? nativeCtx, int ctxForFit,
        long? repoParams = null, bool includeUnfittable = false, string? arch = null)
    {
        //the context window is resolved inside FitOf, so one place decides what a quant is priced at
        (HubQuant Quant, FitRegime Fit)? bestGpu = null, bestRam = null, bestUnfit = null;

        foreach (var q in Candidates(quants, repoParams))
        {
            //the bytes are the tree's raw claim, the clamp at MaxFileBytes inside Estimate is what rejects an absurd size
            switch (FitOf(q.Bytes, nativeCtx, hw, ctxForFit, arch, q.StreamedBytes))
            {
                case FitRegime.FitsGpu when bestGpu is not { } g || Beats(q, g.Quant):
                    bestGpu = (q, FitRegime.FitsGpu); break;
                case FitRegime.FitsRamOnly when bestRam is not { } r || Beats(q, r.Quant):
                    bestRam = (q, FitRegime.FitsRamOnly); break;
                //the unfittable tier keeps the smallest quant, the one that misses by least, where the tiers above keep the best one that fits
                case FitRegime.DoesNotFit when includeUnfittable
                        && (bestUnfit is not { } u || q.Bytes < u.Quant.Bytes):
                    bestUnfit = (q, FitRegime.DoesNotFit); break;
            }
        }
        //the fit filter is a removable default: unlifted, a model where nothing fits yields null and never becomes a row
        return bestGpu ?? bestRam ?? (includeUnfittable ? bestUnfit : null);
    }

    //inside a tier, a band member outranks a non-member whatever the sizes, bytes decide between two non-members, and nothing is rejected for its size
    private static bool Beats(HubQuant candidate, HubQuant incumbent)
    {
        var a = QuantToken.BandRank(candidate.FileName);
        var b = QuantToken.BandRank(incumbent.FileName);
        return a != b ? a > b : candidate.Bytes > incumbent.Bytes;
    }

    //clamp the native context before Estimate, which throws when it is over the cap, so a listing claiming 50,000,000 still yields a row
    private static int ContextForFit(long? nativeCtx, int ctxForFit)
    {
        var wanted = nativeCtx is { } n && n < ctxForFit ? (int)Math.Max(1, n) : ctxForFit;
        return Math.Clamp(wanted, 1, FitArithmetic.MaxContext);
    }

    //the quant floor lives here so no caller disagrees about which files are quants. the candidate's bytes sum a shard set, so a sharded model is priced whole
    internal static IEnumerable<HubQuant> Candidates(IReadOnlyList<HubQuant> quants, long? repoParams) =>
        quants.Where(q => !ModelDiscovery.IsTooSmallToBeQuantization(q.Bytes, repoParams));

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

    private static string PublisherOf(string repoId)
    {
        var slash = repoId.IndexOf('/');
        return slash > 0 ? repoId[..slash] : repoId;
    }
}
