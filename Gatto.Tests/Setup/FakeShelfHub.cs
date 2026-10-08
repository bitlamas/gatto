using System.Net;
using System.Text;
using System.Web;

namespace Gatto.Tests.Setup;

//the Hub as the shelf search asks it: releaser listings, one tagged query per source, trees and ranged header reads, from tables in memory
internal sealed class FakeShelfHub : HttpMessageHandler
{
    private sealed record Repo(string Id, long? Total, long Downloads, string Arch, bool Gated, string? QuantizedFrom,
        string? Tag, bool Gguf);

    private readonly List<Repo> _releaser = [];
    private readonly Dictionary<string, List<Repo>> _conversions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Path, long Bytes)>> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _headers = new(StringComparer.OrdinalIgnoreCase);

    //rows per conversion page, so a test can make a source span two pages
    public int PageSize = 1000;

    //every listing of an author answers each of its repos whatever was searched, as the Hub's own loose matching can
    public bool LooseSearch;

    //a request whose url this answers true for fails with a 500
    public Func<string, bool>? Fail;

    //called with the request's kind and its number within that kind, before the answer, so a test can cancel mid-search
    public Action<string, int>? OnRequest;

    //every url asked, in request order
    public readonly List<string> Urls = [];

    private readonly Dictionary<string, int> _counts = [];

    public int Count(string kind) { lock (_counts) return _counts.GetValueOrDefault(kind); }

    //each GGUF repo's last change as its listing reports it, one date for all unless a test touches a repo
    public DateTimeOffset Modified = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, DateTimeOffset> _touched = new(StringComparer.OrdinalIgnoreCase);

    public FakeShelfHub Touch(string id, DateTimeOffset at)
    {
        _touched[id] = at;
        return this;
    }

    //a source the releaser publishes, which its non-GGUF listing returns
    public FakeShelfHub Source(string id, string? tag = null)
    {
        _releaser.Add(new Repo(id, null, 0, "", false, null, tag, Gguf: false));
        return this;
    }

    //a GGUF repo the releaser publishes itself, with no quantized tag, returned by both releaser listings
    public FakeShelfHub ReleaserGguf(string id, long? total, long downloads, string arch,
        params (string Path, double Gb)[] files)
    {
        _releaser.Add(new Repo(id, total, downloads, arch, false, null, null, Gguf: true));
        return Tree(id, files);
    }

    //a conversion of one source, found by that source's tagged query
    public FakeShelfHub Conversion(string sourceId, string id, long? total, long downloads = 0,
        string arch = "gemma4", bool gated = false, params (string Path, double Gb)[] files)
    {
        if (!_conversions.TryGetValue(sourceId, out var list)) _conversions[sourceId] = list = [];
        var repo = new Repo(id, total, downloads, arch, gated, sourceId, null, Gguf: true);
        list.Add(repo);
        _releaser.Add(repo);
        return Tree(id, files);
    }

    //a GGUF repo with no quantized tag, reached only by its owner's listing or its id
    public FakeShelfHub Untagged(string id, long? total, long downloads, string arch, params (string Path, double Gb)[] files)
    {
        _releaser.Add(new Repo(id, total, downloads, arch, false, null, null, Gguf: true));
        return Tree(id, files);
    }

    //n more repos in one org's listing, holding the search term and no files, so the listing comes back full
    public FakeShelfHub Pad(string org, string term, int n)
    {
        for (var i = 0; i < n; i++)
            _releaser.Add(new Repo($"{org}/{term}-pad-{i}", 9_000_000_000, 0, "qwen35", false, null, null, Gguf: true));
        return this;
    }

    //the api window's remaining count, counted down per answer and sent in the RateLimit header. null sends no header
    public int? RateRemaining;
    public int RateReset = 120;
    public int MinRemaining = int.MaxValue;

    //the request of this kind and number answers 429
    public (string Kind, int N)? TooMany;

    //the 429 carries no RateLimit header, as a server that names no reset sends it
    public bool TooManyBare;

    public FakeShelfHub Tree(string id, params (string Path, double Gb)[] files)
    {
        _trees[id] = [.. files.Select(f => (f.Path, (long)(f.Gb * 1_000_000_000)))];
        return this;
    }

    //the bytes a ranged read of one file answers with
    public FakeShelfHub Header(string id, string path, byte[] bytes)
    {
        _headers[id + "/" + path] = bytes;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        await Task.Yield();
        var url = r.RequestUri!.ToString();
        var kind = KindOf(r.RequestUri);
        int n;
        lock (_counts) n = _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        lock (Urls) Urls.Add(url);
        OnRequest?.Invoke(kind, n);
        ct.ThrowIfCancellationRequested();
        if (TooMany is { } tm && tm.Kind == kind && tm.N == n)
        {
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            if (!TooManyBare) limited.Headers.TryAddWithoutValidation("RateLimit", "\"api\";r=0;t=230");
            return limited;
        }
        if (Fail?.Invoke(url) == true) return new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var res = kind switch
        {
            "tree" => Json(TreeJson(TreeId(r.RequestUri))),
            "file" => FileAnswer(r.RequestUri),
            "conversions" => Conversions(r.RequestUri),
            "releaser" => Releaser(r.RequestUri),
            "model" => ModelAnswer(r.RequestUri),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        if (kind != "file" && RateRemaining is { } left)
            lock (_counts)
            {
                RateRemaining = --left;
                MinRemaining = Math.Min(MinRemaining, left);
                res.Headers.TryAddWithoutValidation("RateLimit", $"\"api\";r={left};t={RateReset}");
            }
        return res;
    }

    private HttpResponseMessage ModelAnswer(Uri u)
    {
        var id = Uri.UnescapeDataString(u.AbsolutePath)["/api/models/".Length..];
        return _releaser.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is { } repo
            ? Json(RowJson(repo))
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string KindOf(Uri u)
    {
        if (u.AbsolutePath.Contains("/resolve/main/", StringComparison.Ordinal)) return "file";
        if (u.AbsolutePath.EndsWith("/tree/main", StringComparison.Ordinal)) return "tree";
        if (u.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal) && u.Query.Length == 0) return "model";
        var q = HttpUtility.ParseQueryString(u.Query);
        if (q.GetValues("filter")?.Any(f => f.StartsWith("base_model:quantized:", StringComparison.Ordinal)) == true
            || q["cursor"] is not null) return "conversions";
        return q["author"] is not null ? "releaser" : "other";
    }

    private static string TreeId(Uri u)
    {
        var path = Uri.UnescapeDataString(u.AbsolutePath);
        return path["/api/models/".Length..path.IndexOf("/tree/main", StringComparison.Ordinal)];
    }

    private HttpResponseMessage Releaser(Uri u)
    {
        var q = HttpUtility.ParseQueryString(u.Query);
        var author = q["author"] ?? "";
        var term = q["search"] ?? "";
        var gguf = q.GetValues("filter")?.Contains("gguf") == true;
        var rows = _releaser.Where(x => x.Id.StartsWith(author + "/", StringComparison.Ordinal)
            && (LooseSearch || x.Id.Contains(term, StringComparison.OrdinalIgnoreCase))
            && (!gguf || x.Gguf)).DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase);
        return Json("[" + string.Join(",", rows.Select(RowJson)) + "]");
    }

    private HttpResponseMessage Conversions(Uri u)
    {
        var q = HttpUtility.ParseQueryString(u.Query);
        var source = q.GetValues("filter")?.FirstOrDefault(f => f.StartsWith("base_model:quantized:", StringComparison.Ordinal))
            ?["base_model:quantized:".Length..] ?? q["source"] ?? "";
        var from = int.TryParse(q["cursor"], out var c) ? c : 0;
        var all = _conversions.GetValueOrDefault(source) ?? [];
        var page = all.Skip(from).Take(PageSize).ToList();
        var res = Json("[" + string.Join(",", page.Select(RowJson)) + "]");
        if (from + PageSize < all.Count)
            res.Headers.TryAddWithoutValidation("Link",
                $"<https://huggingface.co/api/models?source={Uri.EscapeDataString(source)}&cursor={from + PageSize}>; rel=\"next\"");
        return res;
    }

    private HttpResponseMessage FileAnswer(Uri u)
    {
        var path = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/').Replace("/resolve/main/", "/");
        var bytes = _headers.GetValueOrDefault(path) ?? GgufTestBytes.WithStructure(expertCount: 0, arch: "gemma4");
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
    }

    private string TreeJson(string id) =>
        "[" + string.Join(",", (_trees.GetValueOrDefault(id) ?? []).Select(f =>
            "{\"type\":\"file\",\"path\":\"" + f.Path + "\",\"size\":" + f.Bytes
            + ",\"lfs\":{\"oid\":\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(id + "/" + f.Path))) + "\"}}")) + "]";

    private string RowJson(Repo x)
    {
        var gguf = x.Gguf
            ? ",\"gguf\":{\"architecture\":\"" + x.Arch + "\",\"context_length\":131072"
              + (x.Total is { } t ? ",\"total\":" + t : "") + "}"
              + ",\"lastModified\":\"" + _touched.GetValueOrDefault(x.Id, Modified).ToString("o") + "\""
            : "";
        var tags = x.QuantizedFrom is { } s ? ",\"tags\":[\"gguf\",\"base_model:quantized:" + s + "\"]" : "";
        var tag = x.Tag is { } p ? ",\"pipeline_tag\":\"" + p + "\"" : "";
        return "{\"id\":\"" + x.Id + "\",\"downloads\":" + x.Downloads
            + ",\"gated\":" + (x.Gated ? "\"manual\"" : "false") + gguf + tags + tag + "}";
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
