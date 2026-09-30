using System.Net;
using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Gatto.Extensions;

namespace Gatto.Tests;

//the bundled web_search.csx tools load through the real ExtensionHost and the DDG-Lite page comes from a fake handler, so no test touches the network

//real compiles bump the process-global ExtensionHost.CompileCount that other tests assert exact deltas against, so this class joins e2e
[Collection("e2e")]
public sealed class WebSearchExtensionTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-websearch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { } //best effort, a failed delete is fine here
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static string Fixture =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ddg-lite.html"));

    //the live markup's shape, single-quoted class attributes and the snippet in the second td of its row
    private static string LiveFixture =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ddg-lite-live.html"));

    //the bot-check page comes back with zero result anchors, a challenge is not an empty result set
    private static string ChallengeFixture =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ddg-challenge.html"));

    private sealed class FakeCtx : IToolContext
    {
        public string Cwd => Path.GetTempPath();
        public string HomePath => Path.GetTempPath();
        public IUserPrompter? Prompter => null;
    }

    //serves a canned response per request uri, with a default responder when the uri isn't scripted
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Responder(request));
        }
    }

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string JsonFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    //every host resolves to a public address so the SSRF guard lets the fake handler through
    private static readonly Func<string, Task<IPAddress[]>> PublicResolver =
        _ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });

    private sealed class FakePermissionPrompter(params PermissionAnswer[] answers) : IPermissionPrompter
    {
        private readonly Queue<PermissionAnswer> _answers = new(answers);
        public List<PermissionRequest> Requests { get; } = new();
        public PermissionAnswer Ask(PermissionRequest request)
        {
            Requests.Add(request);
            return _answers.Count > 0 ? _answers.Dequeue() : PermissionAnswer.Deny;
        }
    }

    private string Canonical => ShippedExtensions.Files["extensions/web_search.csx"];

    //writes the shipped script into a fresh extensions folder and loads it through the real host
    private (ITool Search, ITool Fetch, CitationLedger Ledger,
        IReadOnlyList<(LoadedExtension Extension, string Name, bool ReadClass)> Committed)
        Load(ScriptedHandler handler, string? searchJson = null)
    {
        var extDir = Path.Combine(_home, "extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "web_search.csx"), Canonical);

        JsonElement? section = searchJson is null ? null : JsonDocument.Parse(searchJson).RootElement.Clone();
        var (api, ledger, _) = ExtensionHost.BuildApi(
            _home, Path.GetTempPath(),
            ledgerPath: () => null,
            prompter: () => null,
            log: _ => { },
            handler: handler,
            resolver: PublicResolver,
            configSection: n => n == "search" ? section : null);

        var diags = new List<string>();
        var loaded = ExtensionHost.LoadAll(extDir, api, diags.Add);
        Assert.Empty(diags);
        Assert.Single(loaded);

        var tools = new ToolRegistry();
        var hooks = new HookBus();
        var (committed, _) = ExtensionHost.Commit(loaded, tools, hooks, diags.Add);
        Assert.Empty(diags);

        var search = tools.Get("web_search");
        var fetch = tools.Get("web_fetch");
        Assert.NotNull(search);
        Assert.NotNull(fetch);
        return (search!, fetch!, ledger, committed);
    }

    //web_search

    [Fact]
    public async Task Search_returns_numbered_blocks_in_expected_line_shape()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        var (search, _, _, _) = Load(handler);

        var r = await search.ExecuteAsync(Args("""{"query":"dotnet regex"}"""), new FakeCtx(), default);

        Assert.False(r.IsError);
        var lines = r.Text.Split('\n');
        //3 valid results, 3 lines each, and the row with no href is skipped
        Assert.Equal(9, lines.Length);
        Assert.StartsWith("1. First & Best <Result>", lines[0]);
        Assert.Equal("   https://example.com/page-one?a=1&b=2", lines[1]);
        Assert.StartsWith("2. ", lines[3]);
        Assert.StartsWith("3. ", lines[6]);
        //the fetched URL holds the query, percent-encoded
        Assert.Contains("lite.duckduckgo.com/lite/?q=dotnet%20regex", handler.Requested[0]);
    }

    [Fact]
    public async Task Search_unwraps_uddg_and_passes_direct_href_through()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        var (search, _, _, _) = Load(handler);

        var r = await search.ExecuteAsync(Args("""{"query":"q"}"""), new FakeCtx(), default);

        //result 1 comes out of the uddg wrapper with %26 decoded to &
        Assert.Contains("https://example.com/page-one?a=1&b=2", r.Text);
        Assert.Contains("https://docs.example.org/guide", r.Text);
        //result 3 has a plain href and passes through verbatim
        Assert.Contains("https://direct.example.net/no-redirect", r.Text);
        Assert.DoesNotContain("duckduckgo.com/l/", r.Text);
    }

    [Fact]
    public async Task Search_decodes_html_entities_in_title_and_snippet()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        var (search, _, _, _) = Load(handler);

        var r = await search.ExecuteAsync(Args("""{"query":"q"}"""), new FakeCtx(), default);

        Assert.Contains("First & Best <Result>", r.Text);                 //the amp, lt and gt entities in the title all decode
        Assert.Contains("\"quotes\"", r.Text);                            //the quot entity decodes to a double quote
        Assert.Contains("'apostrophe'", r.Text);                          //the numeric entity 39 decodes to an apostrophe
        Assert.Contains("❤", r.Text);                                //the numeric entity x2764 decodes to a heart
        Assert.Contains("€", r.Text);                                //the numeric entity 8364 decodes to a euro sign
        Assert.DoesNotContain("&amp;", r.Text);
        Assert.DoesNotContain("&#39;", r.Text);
        Assert.DoesNotContain("&#x2764;", r.Text);
        //the b tags around result 2's title are stripped
        Assert.Contains("Second Highlighted Title", r.Text);
    }

    [Fact]
    public async Task Search_count_is_clamped_to_ten_and_defaults_below_available()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        var (search, _, _, _) = Load(handler);

        //count=1 returns only the first block
        var one = await search.ExecuteAsync(Args("""{"query":"q","count":1}"""), new FakeCtx(), default);
        Assert.Equal(3, one.Text.Split('\n').Length);
        Assert.StartsWith("1. ", one.Text);

        //count=999 clamps to 10 and never errors, so the 3 valid fixture rows are all that come back
        var many = await search.ExecuteAsync(Args("""{"query":"q","count":999}"""), new FakeCtx(), default);
        Assert.Equal(9, many.Text.Split('\n').Length);
    }

    [Fact]
    public async Task Search_malformed_html_yields_fewer_results_never_an_error()
    {
        //garbage the parser can find nothing in still answers "no results", without throwing
        var handler = new ScriptedHandler { Responder = _ => Html("<html><body><p>nothing here</p></body></html>") };
        var (search, _, _, _) = Load(handler);

        var r = await search.ExecuteAsync(Args("""{"query":"widgets"}"""), new FakeCtx(), default);

        Assert.False(r.IsError);
        Assert.Equal("no results for 'widgets'", r.Text);
    }

    [Fact]
    public async Task Search_parses_live_2026_07_markup_with_single_quoted_class_attributes()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(LiveFixture) };
        var (search, _, _, _) = Load(handler);

        var r = await search.ExecuteAsync(Args("""{"query":"rust borrow checker"}"""), new FakeCtx(), default);

        Assert.False(r.IsError);
        var lines = r.Text.Split('\n');
        Assert.Equal(9, lines.Length);                                        //3 results of 3 lines each
        Assert.StartsWith("1. A field guide to the borrow checker", lines[0]);
        Assert.Equal("   https://example.com/notes-borrow-checker/", lines[1]);
        //the snippet must survive the single-quoted class attribute
        Assert.Contains("keeps references valid", lines[2]);
        //entity decoding still applies on the live markup, in the title and in the snippet
        Assert.Contains("Ownership & The Borrow Checker", r.Text);
        Assert.Contains("Rust's", r.Text);
        //the third result has a plain href and no snippet row, so its snippet line stays empty
        Assert.Equal("   https://direct.example.net/live-no-redirect", lines[7]);
        Assert.Equal("   ", lines[8]);
    }

    [Fact]
    public async Task Search_surfaces_ddg_bot_challenge_page_as_error_not_no_results()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(ChallengeFixture) };
        var (search, _, _, _) = Load(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            search.ExecuteAsync(Args("""{"query":"rust borrow checker"}"""), new FakeCtx(), default));

        //the message names the bot-check and points at another provider, so it can't pass as an empty result set
        Assert.Contains("ddg: bot-check challenge", ex.Message);
        Assert.Contains("configure another provider", ex.Message);
        Assert.DoesNotContain("no results", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    //provider chain

    [Fact]
    public async Task Chain_falls_through_ddg_challenge_to_tavily()
    {
        string? authHeader = null;
        string? postBody = null;
        var handler = new ScriptedHandler();
        handler.Responder = req =>
        {
            if (req.RequestUri!.Host == "lite.duckduckgo.com") return Html(ChallengeFixture);
            authHeader = req.Headers.TryGetValues("Authorization", out var v) ? v.Single() : null;
            postBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(JsonFixture("tavily-search.json"));
        };
        var (search, _, _, _) = Load(handler,
            """{"providers":["ddg","tavily"],"tavily":{"apiKey":"test-key"}}""");

        var result = await search.ExecuteAsync(Args("""{"query":"test query"}"""), new FakeCtx(), default);

        Assert.Contains("1. Example Tavily One - local inference notes", result.Text);
        Assert.Contains("https://example.com/tavily/result-1/", result.Text);
        Assert.Contains("2. Example Tavily Two - building from source", result.Text);
        Assert.Equal("5 results · tavily", result.Gloss);
        Assert.Equal("Bearer test-key", authHeader);
        Assert.Contains("\"query\":\"test query\"", postBody);
        Assert.Contains("\"include_answer\":false", postBody);
    }

    [Fact]
    public async Task Chain_short_circuits_on_first_success()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(LiveFixture) };
        var (search, _, _, _) = Load(handler,
            """{"providers":["ddg","tavily"],"tavily":{"apiKey":"test-key"}}""");

        var result = await search.ExecuteAsync(Args("""{"query":"x"}"""), new FakeCtx(), default);

        Assert.EndsWith("· ddg", result.Gloss);
        Assert.DoesNotContain(handler.Requested, u => u.Contains("api.tavily.com"));
    }

    [Fact]
    public async Task Zero_results_cascade_and_all_empty_reports_no_results()
    {
        var handler = new ScriptedHandler();
        handler.Responder = req => req.RequestUri!.Host == "lite.duckduckgo.com"
            ? Html("<html><body>nothing here</body></html>")
            : Json("""{"results":[]}""");
        var (search, _, _, _) = Load(handler,
            """{"providers":["ddg","tavily"],"tavily":{"apiKey":"test-key"}}""");

        var result = await search.ExecuteAsync(Args("""{"query":"zq"}"""), new FakeCtx(), default);

        Assert.Contains(handler.Requested, u => u.Contains("api.tavily.com"));   //ddg answered with an empty page, so the chain went on to tavily
        Assert.False(result.IsError);
        Assert.Equal("no results for 'zq'", result.Text);
    }

    [Fact]
    public async Task All_rungs_erroring_throws_an_aggregate_the_model_can_act_on()
    {
        var handler = new ScriptedHandler();
        handler.Responder = req => req.RequestUri!.Host == "lite.duckduckgo.com"
            ? Html(ChallengeFixture)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
        var (search, _, _, _) = Load(handler,
            """{"providers":["ddg","tavily"],"tavily":{"apiKey":"bad-key"}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            search.ExecuteAsync(Args("""{"query":"x"}"""), new FakeCtx(), default));

        Assert.Contains("web_search failed", ex.Message);
        Assert.Contains("ddg: bot-check challenge", ex.Message);
        Assert.Contains("tavily: ", ex.Message);
        Assert.Contains("check search.tavily.apiKey", ex.Message);
    }

    [Fact]
    public async Task Searxng_provider_parses_and_glosses()
    {
        var handler = new ScriptedHandler();
        handler.Responder = req =>
        {
            Assert.Contains("/search?q=", req.RequestUri!.AbsoluteUri);
            Assert.Contains("format=json", req.RequestUri.AbsoluteUri);
            return Json(JsonFixture("searxng-search.json"));
        };
        var (search, _, _, _) = Load(handler,
            """{"providers":["searxng"],"searxng":{"url":"http://searx.example.com:8888"}}""");

        var result = await search.ExecuteAsync(Args("""{"query":"pi"}"""), new FakeCtx(), default);

        //a payload with 34 results, so this pins that the default count of 5 caps a longer response
        Assert.Contains("1. Example Result One - local inference notes", result.Text);
        Assert.Contains("https://example.com/searxng/result-01/", result.Text);
        Assert.Contains("2. Example Result Two - building from source", result.Text);
        Assert.Equal("5 results · searxng", result.Gloss);
    }

    [Fact]
    public async Task Search_records_search_only_ledger_entry_carrying_result_urls()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        var (search, _, ledger, _) = Load(handler);

        await search.ExecuteAsync(Args("""{"query":"dotnet regex"}"""), new FakeCtx(), default);

        var searchOnly = ledger.Entries.Where(e => e.SearchOnly).ToList();
        var entry = Assert.Single(searchOnly);
        Assert.Equal("dotnet regex", entry.Ref);
        Assert.NotNull(entry.Content);
        Assert.Contains("https://example.com/page-one?a=1&b=2", entry.Content);
        Assert.Contains("https://direct.example.net/no-redirect", entry.Content);
        //the guarded fetch records the page itself, so its entry is not search-only
        Assert.Contains(ledger.Entries, e => !e.SearchOnly && e.Ref.Contains("lite.duckduckgo.com"));
    }

    //web_fetch

    [Fact]
    public async Task Fetch_strips_script_and_style_and_keeps_markdown_links()
    {
        const string page = """
            <html><head><style>body{color:red}</style></head>
            <body>
            <script>var secret = 42; alert('boom');</script>
            <h1>Hello</h1>
            <p>See <a href="https://x.example/docs">the docs</a> for more.</p>
            </body></html>
            """;
        var handler = new ScriptedHandler { Responder = _ => Html(page) };
        var (_, fetch, _, _) = Load(handler);

        var r = await fetch.ExecuteAsync(Args("""{"url":"https://x.example/"}"""), new FakeCtx(), default);

        Assert.False(r.IsError);
        Assert.DoesNotContain("secret", r.Text);        //the script body's text is gone
        Assert.DoesNotContain("color:red", r.Text);     //the style body's text is gone
        Assert.DoesNotContain("<", r.Text);             //no tag survives the strip
        Assert.Contains("Hello", r.Text);
        Assert.Contains("[the docs](https://x.example/docs)", r.Text);
    }

    [Fact]
    public async Task Fetch_truncates_at_64_kb_with_notice()
    {
        var big = "<html><body>" + new string('x', 70_000) + "</body></html>";
        var handler = new ScriptedHandler { Responder = _ => Html(big) };
        var (_, fetch, _, _) = Load(handler);

        var r = await fetch.ExecuteAsync(Args("""{"url":"https://big.example/"}"""), new FakeCtx(), default);

        const int cap = 64 * 1024;
        const string notice = "\n[truncated at 64 KB]";
        Assert.EndsWith(notice, r.Text);
        Assert.Equal(cap + notice.Length, r.Text.Length);
    }

    [Fact]
    public async Task Fetch_decodes_entities_and_collapses_blank_lines()
    {
        const string page = "<html><body><p>a &amp; b</p>\n\n\n\n<p>c</p></body></html>";
        var handler = new ScriptedHandler { Responder = _ => Html(page) };
        var (_, fetch, _, _) = Load(handler);

        var r = await fetch.ExecuteAsync(Args("""{"url":"https://e.example/"}"""), new FakeCtx(), default);

        Assert.Contains("a & b", r.Text);
        Assert.DoesNotContain("&amp;", r.Text);
        Assert.DoesNotContain("\n\n\n", r.Text);        //three or more newlines collapse to two
    }

    //both tools are shell-class, permission-gated

    [Fact]
    public void Both_tools_are_committed_shell_class_not_read_class()
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        var (_, _, _, committed) = Load(handler);

        Assert.Contains(committed, c => c.Name == "web_search" && !c.ReadClass);
        Assert.Contains(committed, c => c.Name == "web_fetch" && !c.ReadClass);
    }

    [Theory]
    [InlineData("web_search")]
    [InlineData("web_fetch")]
    public async Task Each_tool_triggers_a_permission_prompt(string toolName)
    {
        var handler = new ScriptedHandler { Responder = _ => Html(Fixture) };
        Load(handler);   //both tools register through the same real load path, with no readClass

        var store = PermissionStore.Load(_home, out var warning);
        Assert.Null(warning);
        var prompter = new FakePermissionPrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(store, prompter, autoYes: false);

        await gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", toolName, "{}")));

        Assert.Single(prompter.Requests);   //the prompt happens, shell-class never short-circuits
    }
}
