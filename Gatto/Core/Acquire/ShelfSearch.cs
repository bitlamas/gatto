using System.Globalization;
using System.Text.RegularExpressions;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//what a shelf search produced: the rows, the count per regime, whether a shows more, and why the list is short or empty
internal sealed record ShelfOutcome(
    IReadOnlyList<ModelRow> Rows, int OnCard, int InMemory, int TooBig,
    bool MoreBehindA, int HiddenByKind, IReadOnlyList<string> FullPageOrgs,
    HubSearchCause? Cause, bool Cut = false,   //cut is a stop at the deadline or the rate-limit reserve
    int? RateLimitedFor = null,   //the server's seconds to the window's reset, null when it named none
    bool RateLimited = false);   //a 429 answered or the reserve was reached

//what the shelf asks for: the lit families, whether a lifted the floor and the generations, or a typed search that replaces both
internal sealed record ModelSearchRequest(
    IReadOnlySet<string> Lit, bool Lifted = false, string? Search = null);

//the shelf lists original models: each lit family's releaser gives the originals, one tagged query per source the conversions
internal static partial class ShelfSearch
{
    //on a machine with no card a model is fast below this total
    internal const long FastOnCpuBelow = 20_000_000_000;

    //the api requests a search leaves in the Hub's window, so the user's next search and download still answer
    internal const int Reserve = 20;

    //per lit family, the newest generation's models with a pick, and a walk back one generation at a time while none of them is fast
    internal static async Task<ShelfOutcome> AssembleAsync(
        HubClient client, UploaderAllowlist allowlist, Families families,
        IReadOnlySet<string> lit, HardwareClass hw, int ctxForFit, bool lifted,
        CancellationToken ct, int concurrency = HubSearch.HubConcurrency,
        HubTreeMemo? memo = null, IProgress<SearchProgress>? progress = null, TimeProvider? clock = null,
        IReadOnlyList<ModelRow>? landing = null,   //the unlifted shelf of the same lit families, which a lifted search keeps whatever stops it
        CancellationToken caller = default)   //the caller's own token inside ct, so a leave or a restart is told apart from the deadline
    {
        var search = new Search(client, allowlist, families, hw, ctxForFit, lifted, ct, concurrency, memo, progress,
            clock ?? TimeProvider.System);
        var rows = new List<ModelRow>();
        var moreBehind = false;
        //a card gatto cannot use is no card for this rule: a unified machine with no share prices nothing on it
        var noCard = hw.GpuBudgetBytes == 0;

        var litEntries = families.Ladder
            .Where(f => lit.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Select(f => (Family: f, Entry: families.Entries.GetValueOrDefault(f)))
            .Where(e => e.Entry is { Generations.Count: > 0 })
            .Select(e => (e.Family, Entry: e.Entry!))
            .ToList();

        if (lifted)
        {
            //generation outer and family inner, so a stop leaves every lit family's newer generations priced before any older one
            var depth = litEntries.Select(e => e.Entry.Generations.Count).DefaultIfEmpty(0).Max();
            for (var g = 0; g < depth && !search.Stopped; g++)
                foreach (var (family, entry) in litEntries)
                {
                    if (search.Stopped) break;
                    if (g >= entry.Generations.Count) continue;
                    //a row shows a file, so a model whose every file is under the floor has no row here either
                    var found = (await search.GenerationAsync(family, entry, g, under20B: false).ConfigureAwait(false))
                        .Where(r => r.RowFile is not null).ToList();
                    rows.AddRange(await search.WithStructureAsync(found).ConfigureAwait(false));
                }
            //a lifted search only adds: a landing row its stop never reached stays, and a row it priced again replaces it
            if (landing is not null)
                rows.AddRange(landing.Where(l => !rows.Any(r => string.Equals(r.Model, l.Model, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var (family, entry) in litEntries.Where(_ => !lifted))
        {
            if (search.Stopped) break;

            var newest = await search.GenerationAsync(family, entry, 0, under20B: false).ConfigureAwait(false);
            var shown = newest.Where(r => r.RowFile is not null).ToList();
            //the header reads follow each generation, so a deadline after it leaves these rows their kind
            rows.AddRange(await search.WithStructureAsync(shown).ConfigureAwait(false));
            //a model with no pick is not shown, and a would show it as too big when it has a file above the floor
            moreBehind |= newest.Any(TooBigAboveTheFloor);

            var deepest = 0;
            if (!shown.Any(r => IsFast(r, noCard)))
                for (var g = 1; g < entry.Generations.Count && !search.Stopped; g++)
                {
                    deepest = g;
                    var fast = (await search.GenerationAsync(family, entry, g, under20B: noCard).ConfigureAwait(false))
                        .Where(r => r.RowFile is not null && IsFast(r, noCard))
                        .OrderByDescending(r => r.Params ?? 0)
                        .FirstOrDefault();
                    if (fast is null) continue;
                    rows.AddRange(await search.WithStructureAsync([fast]).ConfigureAwait(false));
                    break;
                }
            moreBehind |= deepest < entry.Generations.Count - 1;
        }

        //a deadline that cut the search still leaves the rows it priced their kind, read on a bound of their own before the order, which reads the active count
        IReadOnlyList<ModelRow> read = CutByTheDeadline(ct, caller) ? await AfterTheCutAsync(rows, client, memo, caller).ConfigureAwait(false) : rows;
        var ordered = lifted ? Flat(read) : ByWhereItRuns(read);
        return new ShelfOutcome(ordered,
            ordered.Count(r => r.Fit == FitRegime.FitsGpu),
            ordered.Count(r => r.Fit == FitRegime.FitsRamOnly),
            ordered.Count(r => r.Fit == FitRegime.DoesNotFit),
            !lifted && moreBehind, search.HiddenByKind, [],
            ordered.Count > 0 ? null : search.CauseOf(), search.Stopped, search.RateLimitedFor, search.RateLimited);
    }

    //fast is the card, and on a machine with no card a total under 20B
    private static bool IsFast(ModelRow r, bool noCard) =>
        noCard ? r.Params is { } p && p < FastOnCpuBelow : r.Fit == FitRegime.FitsGpu;

    //a model with no pick but a file above the floor, which a shows as too big. a model whose files are all under the floor has no row either way
    internal static bool TooBigAboveTheFloor(ModelRow r) =>
        r.RowFile is null
        && r.Publishers.SelectMany(p => p.Repos)
            .Any(x => HubSearch.Candidates(x.Quants, r.Params).Any(q => QuantToken.AtFloor(q.FileName, r.Params, lifted: false)));

    //the card largest first, then memory by the parameters read per token, smallest first, a model with no active count last by total
    internal static IReadOnlyList<ModelRow> ByWhereItRuns(IReadOnlyList<ModelRow> rows) =>
        [.. rows.Where(r => r.Fit == FitRegime.FitsGpu).OrderByDescending(r => r.Params ?? 0).ThenBy(r => r.Generation)
                .ThenBy(r => r.Model, StringComparer.Ordinal),
            .. rows.Where(r => r.Fit == FitRegime.FitsRamOnly)
                .OrderBy(r => r.Active is null)
                .ThenBy(r => r.Active ?? 0)
                .ThenByDescending(r => r.Params ?? 0)
                .ThenBy(r => r.Model, StringComparer.Ordinal)];

    //one list by regime, then largest first, a tie to the newer generation
    internal static IReadOnlyList<ModelRow> Flat(IReadOnlyList<ModelRow> rows) =>
        [.. rows.OrderBy(r => r.Fit switch { FitRegime.FitsGpu => 0, FitRegime.FitsRamOnly => 1, _ => 2 })
            .ThenByDescending(r => r.Params ?? 0)
            .ThenBy(r => r.Generation)
            .ThenBy(r => r.Model, StringComparer.Ordinal)];

    //one search's requests, counts and stop, shared by every generation it reads
    private sealed class Search(
        HubClient client, UploaderAllowlist allowlist, Families families, HardwareClass hw, int ctxForFit,
        bool lifted, CancellationToken ct, int concurrency, HubTreeMemo? memo, IProgress<SearchProgress>? progress,
        TimeProvider clock)
    {
        private readonly SemaphoreSlim _gate = new(Math.Max(1, concurrency));
        private readonly HashSet<string> _approved = new(allowlist.Orgs, StringComparer.OrdinalIgnoreCase);
        private readonly ModelKinds _kinds = ModelKinds.Load();
        private readonly object _counts = new();
        private int _requests, _failures, _listingDone, _listingTotal, _readingDone, _readingTotal, _modelsListed;
        private volatile bool _rateStopped;

        public int HiddenByKind { get; private set; }

        //the server's seconds to the window's reset, set when a 429 answered or the reserve was reached, null when it named none
        public int? RateLimitedFor { get; private set; }

        public bool RateLimited => _rateStopped;

        //the token or the Hub's window ended the search: what is priced stays, nothing more is asked
        public bool Stopped => _rateStopped || ct.IsCancellationRequested;

        private void RateStop(int? resetSeconds)
        {
            lock (_counts) RateLimitedFor ??= resetSeconds;
            _rateStopped = true;
        }

        //every request failed is an outage, models listed and none picked is this machine's fit, anything else says nothing
        public HubSearchCause? CauseOf()
        {
            lock (_counts)
            {
                if (_requests > 0 && _failures == _requests) return HubSearchCause.HubFailed;
                return _modelsListed > 0 ? HubSearchCause.NothingFits : null;
            }
        }

        private void Report(SearchStage stage)
        {
            if (progress is null) return;
            lock (_counts)
                progress.Report(stage == SearchStage.Listing
                    ? new SearchProgress(stage, _listingDone, _listingTotal, allowlist.Orgs)
                    : new SearchProgress(stage, _readingDone, _readingTotal, allowlist.Orgs));
        }

        //one request through the gate. null is a failure or a stop, and a stop is never counted as a failure. a header read spends another window than api
        private async Task<T?> AskAsync<T>(Func<Task<T>> request, SearchStage stage, bool api = true) where T : class
        {
            lock (_counts) { if (stage == SearchStage.Listing) _listingTotal++; else _readingTotal++; }
            try
            {
                if (api ? Stopped : ct.IsCancellationRequested) return null;
                try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                try
                {
                    if (api && client.Window is { } w && w.Remaining <= Reserve) { RateStop(w.ResetSeconds); return null; }
                    if (api ? Stopped : ct.IsCancellationRequested) return null;
                    lock (_counts) _requests++;
                    return await request().ConfigureAwait(false);
                }
                catch (HubRateLimitedException ex) { lock (_counts) _requests--; RateStop(ex.ResetSeconds); return null; }
                catch (HubUnavailableException) { lock (_counts) _failures++; return null; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { lock (_counts) _requests--; return null; }
                finally { _gate.Release(); }
            }
            finally
            {
                lock (_counts) { if (stage == SearchStage.Listing) _listingDone++; else _readingDone++; }
                Report(stage);
            }
        }

        //one generation of one family: its originals, their approved conversions, one tree per repo and a priced row per model with a publisher
        public async Task<IReadOnlyList<ModelRow>> GenerationAsync(string family, FamilyEntry entry, int g, bool under20B)
        {
            //a generation is a group of line prefixes, each listed by its own two releaser requests and read under its own prefix
            Report(SearchStage.Listing);
            var listed = await Task.WhenAll(entry.Generations[g].Select(async prefix =>
            {
                var term = prefix.TrimEnd('-');
                var both = await Task.WhenAll(
                    ReleaserAsync(entry.Releaser, term, gguf: false),
                    ReleaserAsync(entry.Releaser, term, gguf: true))
                    .ConfigureAwait(false);
                return (Prefix: prefix, Rows: both[0] ?? [], Ggufs: both[1] ?? []);
            })).ConfigureAwait(false);
            if (Stopped) return [];

            var sources = new List<Source>();
            var builds = new List<(string ModelKey, HubListing Repo)>();
            foreach (var (prefix, releaserRows, ggufRows) in listed)
            {
                var found = Originals.Of(entry, prefix, releaserRows, ggufRows, _kinds);
                sources.AddRange(found);
                builds.AddRange(Originals.ReleaserBuilds(entry, prefix, ggufRows, found));
                var ggufIds = ggufRows.Select(r => r.RepoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                HiddenByKind += releaserRows.Count(r => !ggufIds.Contains(r.RepoId)
                    && Originals.InGeneration(r.RepoId[(r.RepoId.IndexOf('/') + 1)..], prefix)
                    && _kinds.WillNotServe(r.PipelineTag, r.Causal, r.Arch));
            }

            var bySource = sources.DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
            var conversions = await Task.WhenAll(bySource.Select(s => ConversionsAsync(s.Id, newest: g == 0)))
                .ConfigureAwait(false);
            if (Stopped) return [];

            var byModel = new Dictionary<string, List<HubListing>>(StringComparer.OrdinalIgnoreCase);
            void Keep(string model, HubListing repo)
            {
                if (!_approved.Contains(repo.Owner) || repo.Gated) return;
                if (!byModel.TryGetValue(model, out var list)) byModel[model] = list = [];
                list.Add(repo);
            }
            foreach (var (source, found) in bySource.Zip(conversions))
                foreach (var repo in found ?? []) Keep(source.ModelKey, repo);
            foreach (var (model, repo) in builds) Keep(model, repo);

            //on a machine with no card an older generation is read only where it could be fast, which the listing's total already says
            if (under20B)
                foreach (var model in byModel.Keys.ToList())
                    if (MostFrequentTotal(byModel[model]
                            .Where(l => families.RoleOf(l.Arch) != ArchRole.Variant).Select(l => l.Params))
                        is not { } total || total >= FastOnCpuBelow)
                        byModel.Remove(model);

            var repos = byModel.Values.SelectMany(l => l).DistinctBy(l => l.RepoId, StringComparer.OrdinalIgnoreCase).ToList();
            var trees = await Task.WhenAll(repos.Select(TreeAsync)).ConfigureAwait(false);
            var treeOf = new Dictionary<string, HubTree>(StringComparer.OrdinalIgnoreCase);
            foreach (var (repo, tree) in repos.Zip(trees))
                if (tree is not null) treeOf[repo.RepoId] = tree;

            var rows = new List<ModelRow>();
            foreach (var (model, listings) in byModel)
            {
                var priced = listings
                    .Where(l => treeOf.ContainsKey(l.RepoId))
                    .Select(l => (l, treeOf[l.RepoId]))
                    .ToList();
                var row = Price(model, family, g, entry.Releaser, priced, families, hw, ctxForFit, lifted);
                if (row.Publishers.Count == 0) continue;
                lock (_counts) _modelsListed++;
                rows.Add(row);
            }
            return rows;
        }

        //a releaser listing asked once a session, so a and a chip switch ask none the landing already asked
        private async Task<IReadOnlyList<HubListing>?> ReleaserAsync(string releaser, string term, bool gguf)
        {
            var key = $"releaser|{releaser}|{term}|{gguf}";
            if (memo?.Listing(key) is { } kept)
            {
                HubTrace.Hit(gguf ? "releaser-gguf" : "releaser", "memo", releaser + ":" + term);
                return kept;
            }
            var got = await AskAsync(() => client.ReleaserAsync(releaser, term, gguf, ct), SearchStage.Listing).ConfigureAwait(false);
            if (got is not null) memo?.PutListing(key, got);
            return got;
        }

        //the newest generation is asked once a launch, so a new upload shows the day it lands. an older one comes from disk while it is under a day old
        private async Task<IReadOnlyList<HubListing>?> ConversionsAsync(string sourceId, bool newest)
        {
            var key = "conversions|" + sourceId;
            if (memo?.Listing(key) is { } asked)
            {
                HubTrace.Hit("conversions", "memo", sourceId);
                return asked;
            }
            var disk = memo?.Disk;
            if (!newest && disk is not null && disk.TryGetListing(sourceId, clock.GetUtcNow(), out var kept))
            {
                HubTrace.Hit("conversions", "disk", sourceId);
                return kept;
            }
            var got = await AskAsync(() => client.ConversionsAsync(sourceId, ct), SearchStage.Listing).ConfigureAwait(false);
            if (got is not null && disk is not null) disk.PutListing(sourceId, got, clock.GetUtcNow());
            if (got is not null) memo?.PutListing(key, got);
            return got;
        }

        //the memo first, then the tree and its streamed tables, kept in the memo for the next search
        private async Task<HubTree?> TreeAsync(HubListing listing)
        {
            if (memo?.Get(listing.RepoId) is { } remembered)
            {
                HubTrace.Hit("tree", "memo", listing.RepoId);
                return remembered;
            }
            //a tree kept on disk under the repo's unchanged date costs no api request, and its tables come from disk too
            if (memo?.Disk is { } disk && disk.TryGetTree(listing.RepoId, listing.LastModified, out var kept))
            {
                HubTrace.Hit("tree", "disk", listing.RepoId);
                if (ct.IsCancellationRequested) return null;
                var streamed = await client.WithStreamedAsync(kept, listing, ct, disk,
                    HubSearch.TableCouldMove(listing, hw, ctxForFit), memo.Tokenizers).ConfigureAwait(false);
                memo.Put(listing.RepoId, streamed);
                return streamed;
            }
            return await AskAsync(async () =>
            {
                var got = await client.TreeAsync(listing.RepoId, ct).ConfigureAwait(false);
                memo?.Disk?.PutTree(listing.RepoId, listing.LastModified, got);
                got = await client.WithStreamedAsync(got, listing, ct, memo?.Disk,
                    HubSearch.TableCouldMove(listing, hw, ctxForFit), memo?.Tokenizers).ConfigureAwait(false);
                memo?.Put(listing.RepoId, got);
                return got;
            }, SearchStage.Reading).ConfigureAwait(false);
        }

        //the header reads go through this search's gate, so the step line counts them and a stop ends them
        public Task<IReadOnlyList<ModelRow>> WithStructureAsync(IReadOnlyList<ModelRow> rows) =>
            ShelfSearch.WithStructureAsync(rows, client, memo, read => AskAsync(read, SearchStage.Reading, api: false), ct);
    }

    //a reference to hold a header that may be null, so a stop and an unanswerable file stay apart
    internal sealed record HeaderBox(GgufHeader? Value);

    //how long the header reads of a cut search may run after the deadline, about forty 16 KB reads six at a time
    internal static readonly TimeSpan AfterTheCut = TimeSpan.FromSeconds(5);

    //the search's token is spent and the caller's is not, so the deadline stopped it. a leave or a restarted search cancels the caller's too
    internal static bool CutByTheDeadline(CancellationToken search, CancellationToken caller) =>
        search.IsCancellationRequested && !caller.IsCancellationRequested;

    //the rows with no kind read their headers under a token of their own, since the search's token is already spent. memo and disk answer first, and the caller can still end it
    internal static async Task<IReadOnlyList<ModelRow>> AfterTheCutAsync(IReadOnlyList<ModelRow> rows, HubClient client,
        HubTreeMemo? memo, CancellationToken caller = default, TimeSpan? bound = null)
    {
        var missing = rows.Where(r => r.Structure is null && r.RowFile is not null).ToList();
        if (missing.Count == 0) return rows;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        cts.CancelAfter(bound ?? AfterTheCut);
        using var gate = new SemaphoreSlim(HubSearch.HubConcurrency);
        var read = await WithStructureAsync(missing, client, memo, async request =>
        {
            try { await gate.WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            try { return await request().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return null; }
            finally { gate.Release(); }
        }, cts.Token).ConfigureAwait(false);
        var fresh = new Dictionary<ModelRow, ModelRow>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < missing.Count; i++) fresh[missing[i]] = read[i];
        return [.. rows.Select(r => fresh.TryGetValue(r, out var f) ? f : r)];
    }

    //one header read per shown row, memo and disk first, ask running each network read through the caller's gate. a memory row with no A-term takes its active count here
    internal static async Task<IReadOnlyList<ModelRow>> WithStructureAsync(IReadOnlyList<ModelRow> rows, HubClient client,
        HubTreeMemo? memo, Func<Func<Task<HeaderBox>>, Task<HeaderBox?>> ask, CancellationToken ct)
    {
        var read = await Task.WhenAll(rows.Select(async row =>
        {
            if (row.RowFile is not { } file || row.QuantOf(file) is not { } quant) return row;
            var facts = await FactsAsync(file.RepoId, quant, client, memo, ask, ct).ConfigureAwait(false);
            if (facts is null) return row;
            var active = row.Active;
            if (active is null && row.Fit == FitRegime.FitsRamOnly)
                active = facts.Cell == "dense" ? row.Params
                    : facts.Cell is { } cell && cell.StartsWith("MoE ", StringComparison.Ordinal) ? ActiveOf(cell[4..]) : null;
            return row with { Structure = facts.Cell, Experts = facts.Experts, Active = active };
        })).ConfigureAwait(false);
        return read;
    }

    private static async Task<StructureFacts?> FactsAsync(string repoId, HubQuant quant, HubClient client,
        HubTreeMemo? memo, Func<Func<Task<HeaderBox>>, Task<HeaderBox?>> ask, CancellationToken ct)
    {
        if (memo is not null && memo.TryGetHeader(repoId, quant.FileName, out var known))
        {
            HubTrace.Hit("header", "memo", repoId, quant.RepoPath);
            return known is null ? null : new StructureFacts(ModelStructure.Cell(known), ModelStructure.Experts(known));
        }
        if (memo?.Disk is { } disk && disk.TryGetStructure(repoId, quant, out var kept))
        {
            HubTrace.Hit("header", "disk", repoId, quant.RepoPath);
            return kept;
        }
        if (ct.IsCancellationRequested) return null;
        var header = await ask(async () =>
            new HeaderBox(await client.StructureHeaderAsync(repoId, quant.RepoPath, ct).ConfigureAwait(false))).ConfigureAwait(false);
        if (header is null) return null;
        memo?.PutHeader(repoId, quant.FileName, header.Value);
        if (header.Value is not { } h) return null;
        var facts = new StructureFacts(ModelStructure.Cell(h), ModelStructure.Experts(h));
        memo?.Disk?.PutStructure(repoId, quant, facts);
        return facts;
    }
    //the org whose pick the row prefers after the releaser's
    internal const string RowPublisherOrg = "unsloth";

    //the publishers of one model, picked, in pane order, with the row's publisher chosen. family and releaser are null on the typed paths
    internal static ModelRow Price(string model, string? family, int generation, string? releaser,
        IReadOnlyList<(HubListing Listing, HubTree Tree)> repos, Families families,
        HardwareClass hw, int ctxForFit, bool lifted)
    {
        //one entry per repo whichever road reached it, a draft model's repo is no publisher repo, and a gated repo cannot be fetched
        var kept = repos
            .DistinctBy(r => r.Listing.RepoId, StringComparer.OrdinalIgnoreCase)
            .Where(r => families.RoleOf(r.Listing.Arch) != ArchRole.Variant && !r.Listing.Gated)
            .ToList();

        //the total first, since the floor reads it
        var total = MostFrequentTotal(kept.Select(r => r.Listing.Params));

        var publishers = kept
            .GroupBy(r => r.Listing.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(g => Offer(g.Key, [.. g], total, hw, ctxForFit, lifted))
            .OrderBy(p => string.Equals(p.Org, releaser, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(p => p.Downloads)
            .ThenBy(p => p.Org, StringComparer.Ordinal)
            .ToList();

        var rowAt = RowPublisherOf(publishers, releaser);
        var row = rowAt >= 0 ? publishers[rowAt] : null;
        var rowRepo = row?.Pick is { } pick
            ? kept.First(r => string.Equals(r.Listing.RepoId, pick.RepoId, StringComparison.OrdinalIgnoreCase))
            : kept.FirstOrDefault();

        return new ModelRow(
            model, family, generation,
            total, ActiveOf(model), rowRepo.Listing?.Arch, rowRepo.Listing?.NativeCtx,
            row?.Pick is not null && rowRepo.Tree.HasProjector,
            publishers, rowAt, row?.Pick, row?.Fit ?? FitRegime.DoesNotFit);
    }

    //one publisher's repos and the best file across them. each repo is priced at its own listing's context and arch
    private static PublisherOffer Offer(string org, IReadOnlyList<(HubListing Listing, HubTree Tree)> repos,
        long? total, HardwareClass hw, int ctxForFit, bool lifted)
    {
        (string RepoId, HubQuant Quant, FitRegime Fit)? best = null;
        foreach (var (listing, tree) in repos)
        {
            if (HubSearch.Pick(tree.Quants, hw, listing.NativeCtx, ctxForFit, total, lifted, listing.Arch) is not { } p)
                continue;
            if (best is not { } b || Better(p.Quant, p.Fit, b.Quant, b.Fit))
                best = (listing.RepoId, p.Quant, p.Fit);
        }

        var files = repos
            .Select(r => new RepoFiles(r.Listing.RepoId, r.Tree.Quants, r.Tree.Projectors, r.Tree.FileCount, r.Listing.Downloads))
            .ToList();
        var name = repos[0].Listing.Owner;
        return new PublisherOffer(name, files,
            best is { } w ? new FileRef(name, w.RepoId, w.Quant.RepoPath) : null,
            best?.Quant, best?.Fit, files.Sum(f => f.Downloads));
    }

    //a typed repo id re-priced with the floor lifted, each publisher's pick taken from its own files, the row's file the first publisher's pick. no request is made
    internal static ModelRow Lifted(ModelRow row, HardwareClass hw, int ctxForFit)
    {
        var offers = row.Publishers.Select(p =>
        {
            var pick = HubSearch.Pick([.. p.Repos.SelectMany(r => r.Quants.Select(q => (r.RepoId, q)))], x => x.q,
                hw, row.NativeCtx, ctxForFit, row.Params, lifted: true, row.Arch, floorLifted: true);
            return pick is { } w
                ? p with { Pick = new FileRef(p.Org, w.Item.RepoId, w.Item.q.RepoPath), PickQuant = w.Item.q, Fit = w.Fit }
                : p;
        }).ToList();
        var at = offers.FindIndex(p => p.Pick is not null);
        return row with
        {
            Publishers = offers, RowPublisher = at,
            RowFile = at >= 0 ? offers[at].Pick : null,
            Fit = at >= 0 ? offers[at].Fit!.Value : FitRegime.DoesNotFit,
        };
    }

    //where it runs first, then the pick's own order. the unfittable keep the one that misses by least
    internal static bool Better(HubQuant candidate, FitRegime candidateFit, HubQuant incumbent, FitRegime incumbentFit)
    {
        if (candidateFit != incumbentFit) return candidateFit < incumbentFit;
        return candidateFit == FitRegime.DoesNotFit
            ? candidate.Bytes < incumbent.Bytes
            : HubSearch.Beats(candidate, incumbent);
    }

    //the better pick of the releaser and unsloth, a tie to the releaser. when neither has one, the first by downloads that does
    private static int RowPublisherOf(IReadOnlyList<PublisherOffer> publishers, string? releaser)
    {
        int At(string? org) => org is null ? -1
            : publishers.ToList().FindIndex(p => string.Equals(p.Org, org, StringComparison.OrdinalIgnoreCase) && p.Pick is not null);

        var rel = At(releaser);
        var uns = At(RowPublisherOrg);
        if (rel >= 0 && uns >= 0 && rel != uns)
        {
            var r = publishers[rel];
            var u = publishers[uns];
            return Better(u.PickQuant!, u.Fit!.Value, r.PickQuant!, r.Fit!.Value) ? uns : rel;
        }
        if (rel >= 0) return rel;
        if (uns >= 0) return uns;

        var byDownloads = publishers
            .Select((p, i) => (p, i))
            .Where(x => x.p.Pick is not null)
            .OrderByDescending(x => x.p.Downloads)
            .ThenBy(x => x.p.Org, StringComparer.Ordinal);
        return byDownloads.Select(x => x.i).DefaultIfEmpty(-1).First();
    }

    //the most frequent total among the kept repos, a tie to the larger, null when no repo carries one
    internal static long? MostFrequentTotal(IEnumerable<long?> totals) =>
        totals.OfType<long>().Where(t => t > 0)
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .Select(g => (long?)g.Key)
            .FirstOrDefault();

    //the size label's A-term, so 35B-A3B reads 3B a token. a key with none says nothing here
    [GeneratedRegex(@"(?:^|-)A(\d{1,4}(?:\.\d{1,2})?)B(?:-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveTerm();

    internal static long? ActiveOf(string modelKey) =>
        ActiveTerm().Match(modelKey) is { Success: true } m
        && decimal.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b)
            ? (long)(b * 1_000_000_000m)
            : null;
}
