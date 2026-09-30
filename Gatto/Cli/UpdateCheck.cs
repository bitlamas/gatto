using System.Text.Json;

namespace Gatto.Cli;

//the cached result of the last update check, and the weekly gate reads CheckedAt rather than its own clock
public sealed record UpdateState(DateTimeOffset CheckedAt, string? Latest, bool Security);

//the digest field is the only home for this hash, don't also read the body or the .sha256 sidecar
internal sealed record ReleaseAsset(string Name, string Url, string? Sha256, long Size);

//one payload for the weekly line and the updater, so the two cannot disagree about a release
internal sealed record Release(UpdateState State, IReadOnlyList<ReleaseAsset> Assets, string NotesUrl);

//no request without a yes in update_check. doctor runs anyway (the user typed it), and both timeouts must be set on the client
internal static class UpdateCheck
{
    //what gatto asks: an API endpoint, so the reply is JSON
    internal const string ReleasesUrl = "https://api.github.com/repos/bitlamas/gatto/releases/latest";

    //what a person is sent to: this one has to be a page, since the reader cannot run gatto update
    internal const string ReleasesPage = "https://github.com/bitlamas/gatto/releases/latest";
    internal const string CacheFile = "update_check.json";

    //how stale a cached answer may be before the REPL asks again. releases come at most weekly, and a check that runs more often only costs a request
    internal static readonly TimeSpan Weekly = TimeSpan.FromDays(7);

    //a release whose name holds this marker is a security release, and both the release script and the reader cite this one definition
    internal const string SecurityMarker = "[security]";

    //due when the user agreed and there is no cached answer or the cached one is older than Weekly
    public static bool DueForRepl(bool? consent, UpdateState? cached, DateTimeOffset now) =>
        consent == true && (cached is null || now - cached.CheckedAt >= Weekly);

    //the REPL's start line, or null when there is nothing worth saying. display only so it never enters a prompt, and installed takes no default
    public static string? Line(UpdateState? state, string running, bool installed)
    {
        if (state?.Latest is not { Length: > 0 } latest) return null;

        //strictly newer, by the same comparison UpdateCommand makes, so the line and the command agree. an unreadable tag stays silent for the same reason
        if (!ReleaseVersion.TryParse(latest, out var theirs)
            || !ReleaseVersion.TryParseRunning(running, out var mine)
            || !theirs.IsNewerThan(mine)) return null;

        //installed gatto gets the command and a folder copy gets the page, since gatto update refuses when it isn't installed
        var next = installed ? "run gatto update" : $"see what changed: {ReleasesPage}";

        return state.Security
            //a security release says so, since a reader who sees that word acts differently from one who reads "update available"
            ? $"gatto {latest} is out and it's a SECURITY release (you're on {running}): {next}"
            : $"gatto {latest} is out (you're on {running}): {next}";
    }

    //what to say when Line is silent, using the same comparison so the two surfaces can't disagree. an unreadable tag is never the latest release
    public static string SilenceNote(string? latest, string running)
    {
        if (latest is not { Length: > 0 })
            return $"couldn't compare this build ({running}) with GitHub's answer";

        if (!ReleaseVersion.TryParse(latest, out var theirs) || !ReleaseVersion.TryParseRunning(running, out var mine))
            return $"couldn't compare this build ({running}) with the tag GitHub reported ({latest})";

        return mine.IsNewerThan(theirs)
            ? $"this build ({running}) is ahead of the latest release ({latest})"
            : $"you're on the latest release ({running})";
    }

    //null on anything unexpected, a shape we don't recognise is not an update

    //a projection of ParseRelease, so the weekly line and the update command never read one payload two ways
    public static UpdateState? Parse(string json, DateTimeOffset now) => ParseRelease(json, now)?.State;

    //everything the weekly line and the update command need from one payload, or null on anything unexpected
    public static Release? ParseRelease(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "" : "";

            //html_url is always there, but this fallback is a URL a person reads, so it points at the page
            var notes = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() ?? ReleasesPage : ReleasesPage;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var an = a.TryGetProperty("name", out var x) && x.ValueKind == JsonValueKind.String
                        ? x.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                        ? u.GetString() : null;
                    if (an is not { Length: > 0 } || url is not { Length: > 0 }) continue;

                    var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                        && s.TryGetInt64(out var bytes) ? bytes : 0L;

                    var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null;
                    assets.Add(new ReleaseAsset(an, url, Sha256Of(digest), size));
                }

            return new Release(
                new UpdateState(now, tag, name.Contains(SecurityMarker, StringComparison.OrdinalIgnoreCase)),
                assets, notes);
        }
        catch (JsonException) { return null; }
    }

    //the 64 hex characters after sha256:, lower-cased, or null for every other shape
    private static string? Sha256Of(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var hex = digest[prefix.Length..];
        if (hex.Length != 64) return null;
        foreach (var c in hex)
            if (!char.IsAsciiHexDigit(c)) return null;

        return hex.ToLowerInvariant();
    }

    //exactly one -win-x64.zip asset, else null (zero means nothing gatto recognises, two would be a choice)
    public static ReleaseAsset? WindowsZip(IReadOnlyList<ReleaseAsset> assets) =>
        assets.Where(a => a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)).ToList()
            is [var one] ? one : null;

    public static UpdateState? ReadCache(string home)
    {
        try
        {
            var path = Path.Combine(home, CacheFile);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            return new UpdateState(
                DateTimeOffset.TryParse(r.GetProperty("checked_at").GetString(), out var at) ? at : default,
                r.TryGetProperty("latest", out var l) ? l.GetString() : null,
                r.TryGetProperty("security", out var s) && s.ValueKind == JsonValueKind.True);
        }
        catch (Exception) { return null; }   //an unreadable cache is simply no answer yet
    }

    public static void WriteCache(string home, UpdateState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                checked_at = state.CheckedAt.ToString("o"),
                latest = state.Latest,
                security = state.Security,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(home, CacheFile), json);
        }
        catch (Exception) { } //a cache we cannot write costs one extra check next week
    }

    //both budgets, always (a client with only one timeout is not a budget)
    public static HttpClient Client(TimeSpan total, TimeSpan connect) =>
        new(new SocketsHttpHandler { ConnectTimeout = connect }) { Timeout = total };

    //silent on every failure, a courtesy that interrupts a launch to report itself has stopped being one
    public static Task<Release?> FetchAsync(HttpClient http, DateTimeOffset now, CancellationToken ct) =>
        FetchAsync(http, ReleasesUrl, now, ct);

    //one request shape and one parser for any repo's release, a copy could lose the user agent GitHub refuses
    public static async Task<Release?> FetchAsync(
        HttpClient http, string url, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            //a request with no user agent is refused, and naming ourselves is the honest thing to send
            req.Headers.TryAddWithoutValidation("User-Agent", "gatto/" + Gatto.Core.GattoVersion.String);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;   //any non-success status gives null, a 404 while the repo is private is expected
            return ParseRelease(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false), now);
        }
        catch (Exception) { return null; }
    }
}
