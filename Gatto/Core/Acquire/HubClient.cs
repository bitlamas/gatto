using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//one repo from a listing call. the repo id is attacker text and needs sanitizing at render, and a null field is a fact rather than a default
internal sealed record HubListing(
    string RepoId, string? Arch, long? NativeCtx, bool Gated, long Downloads,
    DateTimeOffset? LastModified = null, long? Params = null,
    string? PipelineTag = null, bool? Causal = null);

//one quant candidate: a single file or a whole shard set. the set is grouped here, so its bytes and shard count are the whole set rather than one file
internal sealed record HubQuant(string FileName, long Bytes, string? Sha256, int ShardCount = 1,
    IReadOnlyList<HubFile>? Files = null,
    long? StreamedBytes = null,   //bytes the engine never places on the GPU, read only for a listed architecture. the pick and the pane price from this one value
    string? Path = null)   //the path in the repo, folder included. a URL built from the bare name answers 404 for a file kept in a folder
{
    //the files to fetch in shard order, one entry for a plain file, so no caller branches on whether this is a set
    public IReadOnlyList<HubFile> Members => Files ?? [new HubFile(FileName, Bytes, Sha256, Path)];

    public string RepoPath => Path ?? FileName;
}

//one file with the fingerprint the hub published for it, so a set can verify file by file
internal sealed record HubFile(string FileName, long Bytes, string? Sha256,
    string? Path = null)   //the path in the repo, folder included. every URL is built from it, and every name a user or a disk sees is FileName
{
    public string RepoPath => Path ?? FileName;
}

//a repo's GGUF tree split into weights and vision encoders. the projectors are a list, since a repo may ship two encoders and picking between them is a rule
internal sealed record HubTree(IReadOnlyList<HubQuant> Quants, IReadOnlyList<HubQuant> Projectors,
    int FileCount = 0)
{
    //the vision marker, derived from the projector list rather than repeated on every quant
    public bool HasProjector => Projectors.Count > 0;
}

//where a human goes to fetch one file. every segment is escaped, and the repo id is split so its slash survives

//what a user's typed line means. the classification lives here, so the two readers cannot disagree about the same paste
internal abstract record HubRef
{
    //a repo, and the file inside it when the link named one
    internal sealed record Model(string RepoId, string? File) : HubRef;

    //a collection is many models and gatto cannot pick for the user. it holds no id, since nothing downstream could act on one
    internal sealed record Collection : HubRef;
}

internal static class HubUrl
{
    //classify a typed line as a repo id, a hub URL or neither, where false means it is a search. the host is checked rather than the string's start
    public static bool TryParse(string? text, out HubRef reference)
    {
        reference = null!;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        //a bare id, which is what most people paste
        if (HubClient.LooksLikeRepoId(trimmed))
        {
            reference = new HubRef.Model(trimmed, null);
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (!IsHub(uri.Host)) return false;

        //split the Uri's own path rather than the typed text, since a %2F cannot become a separator here
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        //a collection link looks like huggingface.co/collections/<owner>/<slug>
        if (parts[0].Equals("collections", StringComparison.OrdinalIgnoreCase))
        {
            reference = new HubRef.Collection();
            return true;
        }

        if (parts.Length < 2) return false;
        var repoId = parts[0] + "/" + parts[1];
        if (!HubClient.LooksLikeRepoId(repoId)) return false;

        //blob and resolve name a file, tree names the repo, and the revision segment is skipped since gatto always fetches main
        string? file = null;
        if (parts.Length >= 4 && parts[2] is var verb
            && (verb.Equals("blob", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("resolve", StringComparison.OrdinalIgnoreCase)))
        {
            var named = string.Join('/', parts.Skip(4));
            if (named.Length > 0) file = named;
        }

        reference = new HubRef.Model(repoId, file);
        return true;
    }

    //the host exactly, plus the www form a browser hands back, since a subdomain is a different server
    private static bool IsHub(string host) =>
        host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.huggingface.co", StringComparison.OrdinalIgnoreCase);

    public static string Download(string repoId, string path) =>
        $"https://huggingface.co/{Segments(repoId)}/resolve/main/{FilePath(path)}?download=true";

    //the same file for a ranged read, without the download parameter a browser link sets
    public static string Resolve(string repoId, string path) =>
        $"https://huggingface.co/{Segments(repoId)}/resolve/main/{FilePath(path)}";

    //each segment is escaped and the slash between them survives. a dot segment would let a path climb out of the repo, so it is refused rather than escaped
    private static string FilePath(string path)
    {
        var parts = path.Split('/');
        if (!IsRepoPath(path))
            throw new ArgumentException($"'{path}' is not a path inside a repo", nameof(path));
        return string.Join("/", parts.Select(Uri.EscapeDataString));
    }

    //a path the tree may hand on, with no empty, "." or ".." segment
    public static bool IsRepoPath(string path) =>
        path.Length > 0 && path.Split('/').All(s => s.Length > 0 && s is not ("." or ".."));

    //the .. segment cannot escape the host, and that property lives in Segments, which Download, Resolve and Tree all use

    //the repo's file list, where a user fetches a shard set, since a single-file link can only be one of them
    public static string Tree(string repoId) => $"https://huggingface.co/{Segments(repoId)}/tree/main";

    private static string Segments(string repoId) =>
        string.Join("/", repoId.Split('/').Select(Uri.EscapeDataString));
}

//a hub call did not produce usable data. typed, so a caller can isolate one org's failure and route the gated case
internal sealed class HubUnavailableException(string subject, int? status, string message, bool gated)
    : Exception($"Hub request for {subject} failed{(status is { } s ? $" ({s})" : "")}: {message}")
{
    public string Subject { get; } = subject;
    public int? Status { get; } = status;
    public bool Gated { get; } = gated;
}

//the measured hub surface. it never retries and never pages past the first, the display cap belongs to the caller
internal sealed class HubClient(HttpClient http)
{
    //a repo id is two segments of URL-safe characters, so it goes into the request path unescaped. escaping it would encode the separating slash

    //the search row tells a repo id from a search with this same shape, exposed as a predicate so the two cannot drift apart
    internal static bool LooksLikeRepoId(string candidate) => RepoIdShape.IsMatch(candidate);

    private static readonly Regex RepoIdShape =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    //one org per call, since author= takes one value, and the http the caller supplies must set its own timeout
    public async Task<IReadOnlyList<HubListing>> ListAsync(
        string org, long minParams, long maxParams, CancellationToken ct, string? search = null)
    {
        var url = "https://huggingface.co/api/models"
            + $"?author={Uri.EscapeDataString(org)}"
            + "&filter=gguf"
            + $"&num_parameters={Uri.EscapeDataString($"min:{minParams},max:{maxParams}")}"
            + $"&expand{Uri.EscapeDataString("[]")}=gguf"
            + $"&expand{Uri.EscapeDataString("[]")}=gated"
            //request downloads by name, the expand switch hides defaults. it counts the last 30 days, so an all-time total would float old catalogs to the top
            + $"&expand{Uri.EscapeDataString("[]")}=downloads"
            //lastModified vanishes with the expand switch too, so ask by name or the curated view sorts every row by a null
            + $"&expand{Uri.EscapeDataString("[]")}=lastModified"
            //the tag comes back as data for ModelKinds to read, since a server-side filter on it hid 1549 repos and cannot take two values
            + $"&expand{Uri.EscapeDataString("[]")}=pipeline_tag"
            //the search term sits alongside author= rather than replacing it, so a typed search narrows within the active org
            + (string.IsNullOrWhiteSpace(search)
                ? ""
                : $"&search={Uri.EscapeDataString(search.Trim())}");

        using var doc = await GetJsonAsync(url, org, ct).ConfigureAwait(false);

        var rows = new List<HubListing>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var el in doc.RootElement.EnumerateArray())
            if (ListingOf(el) is { } listing) rows.Add(listing);
        return rows;
    }

    //one element to a listing, or null when it has no id. the same parse serves the browse listing and the single-model read
    private static HubListing? ListingOf(JsonElement el)
    {
        if (!el.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        string? arch = null;
        long? ctx = null;
        long? total = null;
        bool? causal = null;
        if (el.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
        {
            if (gguf.TryGetProperty("architecture", out var a) && a.ValueKind == JsonValueKind.String)
                arch = a.GetString();
            if (gguf.TryGetProperty("context_length", out var c) && c.TryGetInt64(out var cv))
                ctx = cv;
            //the reported total count, already in this response. zero is a fact and stays, while a negative is nonsense and is refused
            if (gguf.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv) && tv >= 0)
                total = tv;
            //present-and-false is evidence of a non-generative model, while absent is silence, which is why this is a bool?
            if (gguf.TryGetProperty("causal", out var cs) && cs.ValueKind is JsonValueKind.False or JsonValueKind.True)
                causal = cs.GetBoolean();
        }
        var downloads = el.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var dv) ? dv : 0L;
        //an unparseable or absent date stays null, since a manufactured one would sort
        var modified = el.TryGetProperty("lastModified", out var m) && m.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(m.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var mv)
            ? mv : (DateTimeOffset?)null;
        //absent stays null, so the kind filter reads an untagged repo as one the Hub said nothing about rather than one it excluded
        var tag = el.TryGetProperty("pipeline_tag", out var pt) && pt.ValueKind == JsonValueKind.String
            ? pt.GetString() : null;
        return new HubListing(
            id.GetString()!, arch, ctx, IsGated(el), downloads, modified, total, tag, causal);
    }

    //one model by id, so a typed name skips the browse search. a missing repo answers 401 like a gated one, so the caller's copy covers both
    public async Task<HubListing?> ModelAsync(string repoId, CancellationToken ct)
    {
        if (!RepoIdShape.IsMatch(repoId))
            throw new HubUnavailableException(repoId, null, "not a valid repo id (expected org/name)", gated: false);

        using var doc = await GetJsonAsync(
            $"https://huggingface.co/api/models/{repoId}", repoId, ct).ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? ListingOf(doc.RootElement) : null;
    }

    //one call returns the whole tree, with each quant's exact bytes and published sha and vision from an mmproj file. the id is validated before the request

    //the dense or MoE A4B header: 16 KB, one retry to 256 KB, then nothing. return null on any failure, and reuse HttpRangeFetch and RemoteGgufHeader
    public async Task<GgufHeader?> StructureHeaderAsync(
        string repoId, string fileName, CancellationToken ct)
    {
        if (!RepoIdShape.IsMatch(repoId)) return null;

        try
        {
            return await RemoteGgufHeader.ReadAsync(
                HttpRangeFetch.Create(http, new Uri(HubUrl.Resolve(repoId, fileName))),
                HeaderWindow.Structure, ModelStructure.Answered, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException)
        {
            return null;
        }
    }

    //each candidate of a listed architecture comes back with its streamed bytes, and a repo of any other architecture costs no request here
    public Task<HubTree> WithStreamedAsync(HubTree tree, HubListing listing, CancellationToken ct,
        HubReadStore? disk = null) =>
        StreamedTableRead.WithStreamedAsync(tree, listing.Arch, listing.Params,
            file => HttpRangeFetch.Create(http, new Uri(HubUrl.Resolve(listing.RepoId, file))), ct,
            disk, listing.RepoId);

    public async Task<HubTree> TreeAsync(string repoId, CancellationToken ct)
    {
        if (!RepoIdShape.IsMatch(repoId))
            throw new HubUnavailableException(repoId, null, "not a valid repo id (expected org/name)", gated: false);

        var url = $"https://huggingface.co/api/models/{repoId}/tree/main?recursive=true";
        using var doc = await GetJsonAsync(url, repoId, ct).ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new HubTree([], []);

        var quants = new List<HubQuant>();
        var projectors = new List<HubQuant>();
        var files = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
            if (!el.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;
            var path = p.GetString()!;
            //count every file in the repo here, above the gguf filter, so the files heading's count doesn't become the quant count
            files++;
            if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HubUrl.IsRepoPath(path)) continue;   //a path gatto cannot turn into a URL inside this repo is not a file it can offer

            var name = path[(path.LastIndexOf('/') + 1)..];
            var bytes = el.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0L;
            string? sha = null;
            if (el.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object
                && lfs.TryGetProperty("oid", out var oid) && oid.ValueKind == JsonValueKind.String)
                sha = oid.GetString();

            //drop companion artifacts before anything prices them, since a sidecar that fits the GPU wins the pick over every real quant
            if (ModelDiscovery.IsCompanionArtifact(name)) continue;

            //keep the encoder with its size and sha, and classify it with ModelDiscovery.IsProjector, whose header is null since a tree listing has no headers
            (ModelDiscovery.IsProjector(name, null) ? projectors : quants)
                .Add(new HubQuant(name, bytes, sha, Path: path));
        }

        //the fold to one candidate per shard set runs on both lists, so a sharded encoder cannot arrive as N one-shard projectors
        return new HubTree(AsCandidates(quants), AsCandidates(projectors), files);
    }

    //collapse a shard set to one candidate keyed on ShardName's canonical shard-one name, and show the lowest shard the listing had

    //a member keeps its own file's fields, including the hash the folded summary drops
    private static HubFile Member(HubQuant f) => new(f.FileName, f.Bytes, f.Sha256, f.Path);

    private static IReadOnlyList<HubQuant> AsCandidates(IReadOnlyList<HubQuant> files)
    {
        var result = new List<HubQuant>(files.Count);
        var slot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            if (ShardName.Parse(f.FileName) is not { IsSet: true } shard) { result.Add(f); continue; }

            var key = shard.FileNameFor(1);
            if (!slot.TryGetValue(key, out var at))
            {
                slot[key] = result.Count;
                lowest[key] = shard.Index;
                //a one-file set keeps its published hash, and a multi-file set's summary drops it while the members keep theirs
                result.Add(f with
                {
                    Sha256 = shard.Count > 1 ? null : f.Sha256,
                    ShardCount = shard.Count,
                    Files = [Member(f)],
                });
                continue;
            }

            var have = result[at];
            var first = shard.Index < lowest[key];
            if (first) lowest[key] = shard.Index;
            result[at] = have with
            {
                FileName = first ? f.FileName : have.FileName,
                Path = first ? f.Path : have.Path,
                Bytes = have.Bytes + f.Bytes,
                Sha256 = null,
                Files = [.. have.Members, Member(f)],
            };
        }

        //sort the members by shard index, since the fetch numbers them off this list and listing order can differ from the names
        for (var i = 0; i < result.Count; i++)
            if (result[i].Files is { Count: > 1 } members)
                result[i] = result[i] with
                {
                    Files = [.. members.OrderBy(m => ShardName.Parse(m.FileName)?.Index ?? 0)],
                };

        return result;
    }

    //HF reports gated as false, "auto" or "manual", so any value that is not false counts as gated
    private static bool IsGated(JsonElement el) =>
        el.TryGetProperty("gated", out var g) && g.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => !string.IsNullOrEmpty(g.GetString()),
            _ => false,
        };

    private async Task<JsonDocument> GetJsonAsync(string url, string subject, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new HubUnavailableException(subject, null, ex.Message, gated: false);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new HubUnavailableException(subject, (int)res.StatusCode, res.ReasonPhrase ?? "request failed",
                    gated: res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

            try
            {
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                //wrap the parse failure so the wizard reads it as an unreadable Hub response instead of a gatto bug
                throw new HubUnavailableException(subject, (int)res.StatusCode, $"unreadable response: {ex.Message}",
                    gated: false);
            }
        }
    }
}
