//web_search and web_fetch, bundled with gatto. shell-class, so they are permission-gated
//per project and always prompt. editing this file changes behaviour, not that posture.
using System.Text.RegularExpressions;
using Gatto.Core.Tools;

//shared parsers and decoders, kept as locals so the tool lambdas below capture them.
//every regex over fetched html carries a match timeout: adjacent [^>]+ runs backtrack
//polynomially, so a hostile page must cost a bounded tool error, never minutes of cpu.
var rxTimeout = TimeSpan.FromSeconds(2);
var anchorRx  = new Regex(@"<a[^>]+rel=[""']nofollow[""'][^>]+href=[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, rxTimeout);
var snippetRx = new Regex(@"<td[^>]*class=[""']result-snippet[""'][^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase, rxTimeout);
var tagRx     = new Regex(@"<[^>]+>", RegexOptions.Singleline, rxTimeout);
var numEntRx  = new Regex(@"&#x?[0-9a-fA-F]+;", RegexOptions.IgnoreCase, rxTimeout);

//decode the named set (&amp; &lt; &gt; &quot; &#39;) plus numeric &#nn; and &#xnn;.
//numeric first, then the named ones, with &amp; last so an escaped "&amp;lt;" stays "&lt;".
Func<string, string> decode = s =>
{
    s = numEntRx.Replace(s, m =>
    {
        var v = m.Value;                               // "&#39;" or "&#x27;"
        var inner = v.Substring(2, v.Length - 3);
        try
        {
            int code = inner.Length > 0 && (inner[0] == 'x' || inner[0] == 'X')
                ? Convert.ToInt32(inner.Substring(1), 16)
                : int.Parse(inner);
            return char.ConvertFromUtf32(code);
        }
        catch { return v; }                            // undecodable ⇒ leave verbatim, never throw
    });
    s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
    return s.Replace("&amp;", "&");
};

Func<string, string> strip = s => tagRx.Replace(s, "");
Func<string, string> clean = s => decode(strip(s)).Trim();

//a ddg-lite result href is a redirect wrapper carrying the target in a uddg param.
//unwrap to the decoded param; a plain href passes through untouched.
Func<string, string> unwrap = href =>
{
    if (href.StartsWith("//duckduckgo.com/l/?uddg="))
    {
        var m = Regex.Match(href, @"[?&]uddg=([^&]+)", RegexOptions.None, rxTimeout);
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
    }
    return href;
};

//--- provider chain --------------------------------------------------------------------
//web_search walks the chain from gatto.json's "search" section, validated at startup and
//defaulting to ["ddg"]. a provider succeeds only if it returns at least one result; a throw
//or an empty set falls through to the next one.
//cascading on empty covers rare queries and silent parse rot alike.
var searchCfg = Gatto.Config.Section("search");
var chain = new List<string> { "ddg" };
string? tavilyKey = null;
string? searxngUrl = null;
if (searchCfg is { } cfg)
{
    if (cfg.TryGetProperty("providers", out var pv) && pv.ValueKind == JsonValueKind.Array)
    {
        chain.Clear();
        foreach (var p in pv.EnumerateArray()) chain.Add(p.GetString()!);
    }
    if (cfg.TryGetProperty("tavily", out var tv) && tv.ValueKind == JsonValueKind.Object
        && tv.TryGetProperty("apiKey", out var tk) && tk.ValueKind == JsonValueKind.String)
        tavilyKey = tk.GetString();
    if (cfg.TryGetProperty("searxng", out var sx) && sx.ValueKind == JsonValueKind.Object
        && sx.TryGetProperty("url", out var su) && su.ValueKind == JsonValueKind.String)
        searxngUrl = su.GetString()!.TrimEnd('/');
}

//long api snippets are capped so five results stay a scannable list, not a page.
Func<string, string> capSnippet = s => s.Length <= 400 ? s : s.Substring(0, 400) + "…";

//shared json-results parser: tavily and searxng both answer {"results":[{title,url,content}]}.
Func<string, int, List<(string Title, string Url, string Snippet)>> parseJsonResults = (json, count) =>
{
    var results = new List<(string, string, string)>();
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        foreach (var r in arr.EnumerateArray())
        {
            if (results.Count >= count) break;
            var title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var url = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
            var snip = r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? capSnippet(c.GetString()!) : "";
            if (url != "") results.Add((title, url, snip));
        }
    return results;
};

//ddg, duckduckgo lite html, keyless. its bot-check page arrives with a 2xx status, so only
//the markers tell it apart, and it must throw rather than read as "no results".
Func<string, int, CancellationToken, Task<List<(string Title, string Url, string Snippet)>>> ddg =
    async (query, count, ct) =>
{
    var res = await Gatto.Http.FetchAsync(
        "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query), ct);
    var html = res.Text;
    if (html.Contains("duckduckgo.com/anomaly.js") || html.Contains("anomaly-modal"))
        throw new InvalidOperationException("bot-check challenge (this network is rate-flagged, often for a while)");

    var snippets = snippetRx.Matches(html);
    var results = new List<(string, string, string)>();
    foreach (Match a in anchorRx.Matches(html))
    {
        if (results.Count >= count) break;
        var url = unwrap(a.Groups[1].Value);
        var title = clean(a.Groups[2].Value);
        var snippet = "";
        foreach (Match s in snippets)
            if (s.Index > a.Index) { snippet = clean(s.Groups[1].Value); break; }
        results.Add((title, url, snippet));
    }
    return results;
};

//tavily, a keyed json api. include_answer stays false so the model reads pages itself and
//the citation gate keeps its meaning.
Func<string, int, CancellationToken, Task<List<(string Title, string Url, string Snippet)>>> tavily =
    async (query, count, ct) =>
{
    var body = JsonSerializer.Serialize(new { query = query, max_results = count, include_answer = false });
    var res = await Gatto.Http.FetchAsync(
        "https://api.tavily.com/search",
        new FetchOptions(
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + tavilyKey },
            PostJson: body),
        ct);
    return parseJsonResults(res.Text, count);
};

//searxng, self-hosted metasearch over get json. the instance must list json under
//search.formats in its settings.yml.
//its origin is the one config-blessed ssrf exemption.
Func<string, int, CancellationToken, Task<List<(string Title, string Url, string Snippet)>>> searxng =
    async (query, count, ct) =>
{
    var res = await Gatto.Http.FetchAsync(
        searxngUrl + "/search?q=" + Uri.EscapeDataString(query) + "&format=json", ct);
    return parseJsonResults(res.Text, count);
};

var providers = new Dictionary<string, Func<string, int, CancellationToken, Task<List<(string Title, string Url, string Snippet)>>>>
{
    ["ddg"] = ddg,
    ["tavily"] = tavily,
    ["searxng"] = searxng,
};

Gatto.Register(
    "web_search",
    "Search the web. Returns a numbered list of results (title, URL, snippet). Follow up with web_fetch to read a promising page.",
    """
    {"type":"object","properties":{"query":{"type":"string","description":"The search query."},"count":{"type":"integer","description":"How many results to return (default 5, max 10)."}},"required":["query"]}
    """,
    async (args, ctx, ct) =>
    {
        if (!args.TryGetProperty("query", out var qEl) || qEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("missing required parameter: query");
        var query = qEl.GetString();

        var count = 5;
        if (args.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number && cEl.TryGetInt32(out var n))
            count = n;
        if (count < 1) count = 1;
        if (count > 10) count = 10;                    // clamp, never error

        var failures = new List<string>();
        var threw = false;
        foreach (var name in chain)
        {
            List<(string Title, string Url, string Snippet)> results;
            try { results = await providers[name](query, count, ct); }
            catch (Exception ex)
            {
                threw = true;
                var hint = name == "tavily" && ex.Message.Contains("HTTP 401") ? " (check search.tavily.apiKey)" : "";
                failures.Add($"{name}: {ex.Message}{hint}");
                continue;
            }
            if (results.Count == 0) { failures.Add($"{name}: no results returned"); continue; }

            var blocks = new List<string>();
            foreach (var (title, url, snippet) in results)
                blocks.Add($"{blocks.Count + 1}. {title}\n   {url}\n   {snippet}");
            var text = string.Join("\n", blocks);
            //search-only ledger entry: carries the result urls so a later cited but unopened
            //url resolves as unread, not fabricated.
            Gatto.Ledger.RecordFetch(query, text, searchOnly: true);
            return new ToolResult(text, Gloss: $"{blocks.Count} result{(blocks.Count == 1 ? "" : "s")} · {name}");
        }

        if (!threw)
            return new ToolResult($"no results for '{query}'");
        throw new InvalidOperationException(
            "web_search failed — " + string.Join("; ", failures)
            + ". Try web_fetch on a site you already know, or configure another provider in gatto.json (search.providers).");
    });

Gatto.Register(
    "web_fetch",
    "Fetch a URL and return its readable text (HTML converted to text; links become [text](url)). Use after web_search to read a promising page.",
    """
    {"type":"object","properties":{"url":{"type":"string","description":"The URL to fetch."}},"required":["url"]}
    """,
    async (args, ctx, ct) =>
    {
        if (!args.TryGetProperty("url", out var uEl) || uEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("missing required parameter: url");
        var url = uEl.GetString();

        var res = await Gatto.Http.FetchAsync(url, ct);   // ledger recording comes free from the guarded fetch
        var s = res.Text;

        s = Regex.Replace(s, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, rxTimeout);
        s = Regex.Replace(s, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, rxTimeout);
        s = Regex.Replace(s, @"<a[^>]+href=""([^""]*)""[^>]*>(.*?)</a>",
            m => "[" + m.Groups[2].Value + "](" + m.Groups[1].Value + ")",
            RegexOptions.Singleline | RegexOptions.IgnoreCase, rxTimeout);
        s = strip(s);
        s = decode(s);
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"\n{3,}", "\n\n", RegexOptions.None, rxTimeout);
        s = s.Trim();

        const int cap = 64 * 1024;                     // 64 KB of UTF-16 chars
        var truncated = s.Length > cap;
        if (truncated)
            s = s.Substring(0, cap) + "\n[truncated at 64 KB]";

        var kb = Math.Max(1, (s.Length + 1023) / 1024);
        return new ToolResult(s, Gloss: $"{kb} KB text{(truncated ? " (truncated)" : "")}");
    });