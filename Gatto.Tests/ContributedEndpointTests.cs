using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Xunit;

namespace Gatto.Tests;

//the header bound is one static, so the class joins the e2e collection and no other test sees the shortened value
[Collection("e2e")]
public sealed class ContributedEndpointTests
{
    private static IReadOnlyDictionary<string, string> Map(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static LoadedExtension Ext(string name, Action<GattoApi> stage)
    {
        var api = ExtensionHostTests.NewApi(out _, out _);
        api.BeginExtension(name);
        stage(api);
        return new LoadedExtension(name, new ExtensionSource(name, name + ".csx", new[] { name + ".csx" }), api.TakeStaged());
    }

    private static void Tool(GattoApi api, string name) =>
        api.Register(name, "a tool", "{\"type\":\"object\"}", (_, _, _) => Task.FromResult(new ToolResult("ok")));

    [Theory]
    [InlineData("https://cloud.example.test/api", true)]
    [InlineData("https://hosted.example.test/api", true)]
    [InlineData("http://example.test:8080", true)]
    [InlineData("http://127.0.0.1:8081", false)]
    [InlineData("http://localhost:1234", false)]
    [InlineData("http://172.16.4.2:8080", false)]
    [InlineData("http://10.0.0.5:8080", false)]
    [InlineData("http://[::1]:8080", false)]
    public void A_server_on_the_users_machine_or_lan_is_not_cloud(string baseUrl, bool cloud) =>
        Assert.Equal(cloud, CloudEndpoint.Is(new EndpointConfig(baseUrl)));

    [Fact]
    public void The_local_endpoint_with_no_base_url_is_not_cloud() =>
        Assert.False(CloudEndpoint.Is(new EndpointConfig(null)));

    [Fact]
    public async Task The_callback_headers_reach_the_request_and_replace_the_key_rule()
    {
        var ep = new EndpointConfig("https://example.test", ApiKey: "from-config",
            Headers: _ => Task.FromResult(Map(("Authorization", "Bearer from-callback"), ("x-account", "u1"))));
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions");
        await EndpointAuth.ApplyAsync(msg, ep, "acme", CancellationToken.None);
        Assert.Equal("Bearer from-callback", msg.Headers.Authorization!.ToString());
        Assert.Equal("u1", msg.Headers.GetValues("x-account").Single());
    }

    [Fact]
    public async Task With_no_callback_the_key_rule_stands()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions");
        await EndpointAuth.ApplyAsync(msg, new EndpointConfig("https://example.test", ApiKey: "k"), "x", CancellationToken.None);
        Assert.Equal("Bearer k", msg.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_throwing_callback_fails_the_request_with_its_message_and_adds_no_header()
    {
        var ep = new EndpointConfig("https://example.test", ApiKey: "from-config",
            Headers: _ => throw new InvalidOperationException("paste a new acme token"));
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions");
        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => EndpointAuth.ApplyAsync(msg, ep, "acme", CancellationToken.None));
        Assert.Equal("paste a new acme token", ex.Message);
        Assert.Null(msg.Headers.Authorization);
    }

    [Fact]
    public async Task A_callback_that_blocks_and_ignores_its_token_fails_at_the_bound()
    {
        var saved = EndpointAuth.HeaderBound;
        using var release = new ManualResetEventSlim(false);
        try
        {
            EndpointAuth.HeaderBound = TimeSpan.FromMilliseconds(200);
            var ep = new EndpointConfig("https://example.test", Headers: _ => { release.Wait(CancellationToken.None); return Task.FromResult(Map()); });
            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions");
            var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => EndpointAuth.ApplyAsync(msg, ep, "acme", CancellationToken.None));
            Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            EndpointAuth.HeaderBound = saved;
            release.Set();
        }
    }

    [Fact]
    public async Task A_cancelled_request_leaves_the_callback_as_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var ep = new EndpointConfig("https://example.test",
            Headers: async ct => { await Task.Delay(Timeout.Infinite, ct); return Map(); });
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions");
        var pending = EndpointAuth.ApplyAsync(msg, ep, "acme", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void The_endpoint_text_never_holds_what_the_callback_returns()
    {
        var ep = new EndpointConfig("https://example.test", Headers: _ => Task.FromResult(Map(("Authorization", "Bearer secret-value"))));
        Assert.DoesNotContain("secret-value", ep.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_extension_stages_an_endpoint_with_its_thinking_map()
    {
        var ext = Ext("acme", api => api.Endpoint("acme", "https://cloud.example.test/api", context: 131072,
            thinking: "{\"none\":null,\"high\":{\"reasoning_effort\":\"high\"}}"));
        var (name, config) = Assert.Single(ext.Registrations.Endpoints);
        Assert.Equal("acme", name);
        Assert.Equal(131072, config.Context);
        Assert.Null(config.Thinking!["none"]);
        Assert.Equal("high", config.Thinking["high"]!.Value.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData("Acme", "https://example.test")]
    [InlineData("acme", "https://example.test/api/v1")]
    [InlineData("acme", "https://example.test/api/v1/")]
    [InlineData("acme", "http://example.test")]
    [InlineData("acme", "not a url")]
    public void A_bad_name_or_base_url_fails_the_load(string name, string baseUrl)
    {
        var api = ExtensionHostTests.NewApi(out _, out _);
        api.BeginExtension("x");
        Assert.Throws<InvalidOperationException>(() => api.Endpoint(name, baseUrl));
    }

    [Fact]
    public void An_extension_whose_tool_collides_with_a_builtin_loses_its_endpoint_too()
    {
        var diag = new List<string>();
        var bad = Ext("bad", api => { Tool(api, "read_file"); api.Endpoint("acme", "https://example.test"); });
        var good = Ext("good", api => api.Endpoint("other", "https://example.test"));
        var kept = ExtensionHost.Survivors(new[] { bad, good }, new[] { "read_file" }, Array.Empty<string>(), diag.Add);
        Assert.Equal("good", Assert.Single(kept).Name);
        Assert.Contains("bad", Assert.Single(diag), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("hosted")]
    public void An_endpoint_named_like_local_or_a_configured_one_skips_its_extension(string name)
    {
        var diag = new List<string>();
        var ext = Ext("x", api => api.Endpoint(name, "https://example.test"));
        Assert.Empty(ExtensionHost.Survivors(new[] { ext }, Array.Empty<string>(), new[] { "hosted" }, diag.Add));
        Assert.Contains(name, Assert.Single(diag), StringComparison.Ordinal);
    }

    [Fact]
    public void The_second_extension_to_contribute_a_name_is_the_one_skipped()
    {
        var a = Ext("a", api => api.Endpoint("acme", "https://example.test"));
        var b = Ext("b", api => api.Endpoint("acme", "https://example.test"));
        var kept = ExtensionHost.Survivors(new[] { a, b }, Array.Empty<string>(), Array.Empty<string>(), _ => { });
        Assert.Equal("a", Assert.Single(kept).Name);
    }

    private static SessionBaseline Baseline(string? endpoint) =>
        new(1, "generalist", "big-model", "all", new ThinkingMark("high", null), new List<ToolMark>(),
            new BaselineSources(null, null, new List<ContextFileMark>(), new List<PolicyMark>(), "r", "m"), endpoint);

    [Fact]
    public void The_system_record_carries_the_endpoint_and_reads_it_back()
    {
        var line = SessionStore.ChatJson(new ChatMessage("system", "sys"), @"C:\proj", Baseline("acme"));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("acme", SessionStore.ParseBaseline(doc.RootElement)!.Endpoint);
    }

    [Fact]
    public void A_record_from_before_the_field_still_parses_and_holds_no_endpoint()
    {
        var line = SessionStore.ChatJson(new ChatMessage("system", "sys"), @"C:\proj", Baseline(null));
        Assert.DoesNotContain("\"endpoint\"", line, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(line);
        var parsed = SessionStore.ParseBaseline(doc.RootElement);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Endpoint);
    }

    [Fact]
    public void The_notice_opens_with_the_mark_and_the_model() =>
        Assert.StartsWith("@ big-model is a cloud model: ", CloudEndpoint.Notice("@", "big-model"), StringComparison.Ordinal);

    [Fact]
    public void An_endpoint_keeps_its_model_names_in_order()
    {
        var ext = Ext("acme", api => api.Endpoint("acme", "https://cloud.example.test/api", models: new[] { "small", "big" }));
        Assert.Equal(new[] { "small", "big" }, Assert.Single(ext.Registrations.Endpoints).Config.Models);
    }

    [Theory]
    [InlineData()]
    [InlineData("a", "a")]
    [InlineData("a", " ")]
    public void A_model_list_that_is_empty_or_repeats_or_holds_a_blank_fails_the_load(params string[] models)
    {
        var api = ExtensionHostTests.NewApi(out _, out _);
        api.BeginExtension("x");
        Assert.Throws<InvalidOperationException>(() => api.Endpoint("acme", "https://cloud.example.test/api", models: models));
    }
}
