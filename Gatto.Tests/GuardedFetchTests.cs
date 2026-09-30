using Gatto.Core.Web;
using System.Net;
using System.Text;

namespace Gatto.Tests;

public class GuardedFetchTests
{

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        public int Calls { get; private set; }
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Responder(request));
        }
    }

    //hands out length bytes, and sets DrainedToEnd only when a read arrives past the end
    private sealed class CountingStream(long length) : Stream
    {
        private long _pos;
        public bool DrainedToEnd { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= length) { DrainedToEnd = true; return 0; }
            int n = (int)Math.Min(count, length - _pos);
            _pos += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static Func<string, Task<IPAddress[]>> Resolver(Dictionary<string, string>? map = null) =>
        host => Task.FromResult(new[]
        {
            IPAddress.Parse(map is not null && map.TryGetValue(host, out var ip) ? ip : "8.8.8.8"),
        });

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static HttpResponseMessage Ok(string body, string contentType = "text/plain")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        return r;
    }

    [Fact]
    public async Task Redirect_ToLiteralPrivateIp_BlockedOnSecondHop()
    {
        var handler = new ScriptedHandler { Responder = _ => Redirect("http://127.0.0.1/") };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("blocked", ex.Message);
        Assert.Equal(1, handler.Calls); //the redirect target is checked before the second request, so only one hop reaches the network.
    }

    [Fact]
    public async Task Redirect_ToHostResolvingPrivate_Blocked()
    {
        var handler = new ScriptedHandler { Responder = _ => Redirect("http://internal.example/") };
        var resolver = Resolver(new() { ["public.example"] = "8.8.8.8", ["internal.example"] = "10.0.0.1" });
        var fetch = new GuardedFetch(handler: handler, resolver: resolver);

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("10.0.0.1", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Redirect_ExceedingMax_Throws()
    {
        var handler = new ScriptedHandler();
        handler.Responder = _ => Redirect($"http://public.example/{handler.Calls}");
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("too many redirects", ex.Message);
    }

    [Fact]
    public async Task Body_OverCap_ThrowsAndStreamNotDrained()
    {
        var stream = new CountingStream(2 * 1024 * 1024 + 1);
        var handler = new ScriptedHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) },
        };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("2 MB cap", ex.Message);
        Assert.False(stream.DrainedToEnd, "stream must be aborted mid-read, not fully drained");
    }

    [Fact]
    public async Task Success_PopulatesResult_AndRecordsOnce()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("hello") };
        var records = new List<(string url, string? content, bool search)>();
        var fetch = new GuardedFetch(
            recorder: (u, c, s) => records.Add((u, c, s)),
            handler: handler,
            resolver: Resolver());

        var result = await fetch.FetchAsync("http://public.example/", CancellationToken.None);

        Assert.Equal("hello", result.Text);
        Assert.Contains("public.example", result.FinalUrl);
        Assert.Contains("text/plain", result.ContentType);
        Assert.False(result.FromCache);
        var rec = Assert.Single(records);
        Assert.Equal("hello", rec.content);
        Assert.False(rec.search);
    }

    [Fact]
    public async Task SecondFetch_SameUrl_HitsCache_RecordsAgain()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("hello") };
        var records = new List<(string url, string? content, bool search)>();
        var fetch = new GuardedFetch(
            recorder: (u, c, s) => records.Add((u, c, s)),
            handler: handler,
            resolver: Resolver());

        await fetch.FetchAsync("http://public.example/", CancellationToken.None);
        var second = await fetch.FetchAsync("http://public.example/", CancellationToken.None);

        Assert.True(second.FromCache);
        Assert.Equal(1, handler.Calls);   //the cached answer means no second request leaves the process.
        Assert.Equal(2, records.Count);   //the recorder must fire on a cache hit too, so every fetch reaches the log.
    }

    [Fact]
    public async Task Cache_EvictsOldest_AfterCapacity()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("body") };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        for (int i = 0; i < 51; i++)
            await fetch.FetchAsync($"http://public.example/{i}", CancellationToken.None);

        Assert.Equal(51, handler.Calls);

        //the cache holds 50 entries, so it drops the first url and that re-fetch must reach the handler.
        await fetch.FetchAsync("http://public.example/0", CancellationToken.None);
        Assert.Equal(52, handler.Calls);

        //the newest url stays cached, so its re-fetch must not reach the handler.
        var still = await fetch.FetchAsync("http://public.example/50", CancellationToken.None);
        Assert.True(still.FromCache);
        Assert.Equal(52, handler.Calls);
    }

    [Fact]
    public async Task SendAsync_ThrowsHttpRequestException_TranslatesToGattoFetchException()
    {
        var handler = new ScriptedHandler
        {
            Responder = _ => throw new HttpRequestException("Connection refused"),
        };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("fetch failed", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public async Task Resolver_ThrowsSocketException_TranslatesToGattoFetchException()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("hello") };
        Func<string, Task<IPAddress[]>> resolver = _ =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        var fetch = new GuardedFetch(handler: handler, resolver: resolver);

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("blocked", ex.Message);
        Assert.Equal(0, handler.Calls); //a failed lookup must stop the fetch before any request goes out.
    }

    [Fact]
    public async Task Redirect_RecordsRequestedUrl_AsContentlessAlias()
    {
        //the model cites the url it asked for, so the recorder logs that url too, as a contentless alias
        var handler = new ScriptedHandler();
        handler.Responder = req => req.RequestUri!.AbsoluteUri.Contains("final")
            ? Ok("landed") : Redirect("http://public.example/final");
        var records = new List<(string url, string? content, bool search)>();
        var fetch = new GuardedFetch(
            recorder: (u, c, s) => records.Add((u, c, s)),
            handler: handler,
            resolver: Resolver());

        await fetch.FetchAsync("http://public.example/start", CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.Equal(("http://public.example/final", "landed", false), records[0]);   //the record for the final url holds the content.
        Assert.Equal(("http://public.example/start", null, false), records[1]);       //the alias row for the requested url holds no content.
    }

    [Fact]
    public async Task CacheHit_AfterRedirect_RecordsAliasAgain()
    {
        var handler = new ScriptedHandler();
        handler.Responder = req => req.RequestUri!.AbsoluteUri.Contains("final")
            ? Ok("landed") : Redirect("http://public.example/final");
        var records = new List<(string url, string? content, bool search)>();
        var fetch = new GuardedFetch(
            recorder: (u, c, s) => records.Add((u, c, s)),
            handler: handler,
            resolver: Resolver());

        await fetch.FetchAsync("http://public.example/start", CancellationToken.None);
        await fetch.FetchAsync("http://public.example/start", CancellationToken.None);

        Assert.Equal(4, records.Count);   //a cached hit records the final url and the alias again, so four records total.
        Assert.Equal("http://public.example/start", records[3].url);
        Assert.Null(records[3].content);
    }

    [Theory]
    [InlineData("127.0.0.1")]     //a literal address needs no lookup, and the resolver throws if the code calls it.
    [InlineData("[::1]")]         //a bracketed ipv6 address arrives in the DnsEndPoint shape
    public async Task ResolveValidated_BlockedLiteral_Throws(string host)
    {
        Func<string, Task<IPAddress[]>> resolver = _ => throw new InvalidOperationException("must not resolve a literal");
        await Assert.ThrowsAsync<GattoFetchException>(() => GuardedFetch.ResolveValidatedAsync(resolver, host));
    }

    [Fact]
    public async Task ResolveValidated_RebindToPrivate_Throws()
    {
        //loopback where the name earlier answered a public address, the rebind the resolution check must stop
        var resolver = Resolver(new() { ["rebind.example"] = "127.0.0.1" });
        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => GuardedFetch.ResolveValidatedAsync(resolver, "rebind.example"));
        Assert.Contains("127.0.0.1", ex.Message);
    }

    [Fact]
    public async Task ResolveValidated_PublicHost_ReturnsAddresses()
    {
        var addresses = await GuardedFetch.ResolveValidatedAsync(Resolver(), "public.example");
        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(addresses));
    }

    [Fact]
    public async Task NonSuccessStatus_Throws()
    {
        var handler = new ScriptedHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
        };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var ex = await Assert.ThrowsAsync<GattoFetchException>(
            () => fetch.FetchAsync("http://public.example/", CancellationToken.None));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Post_sends_body_and_headers()
    {
        HttpRequestMessage? seen = null;
        string? seenBody = null;
        var handler = new ScriptedHandler();
        handler.Responder = req =>
        {
            seen = req;
            seenBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Ok("resp");
        };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var opts = new FetchOptions(
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer k123" },
            PostJson: """{"query":"x"}""");
        var res = await fetch.FetchAsync("http://api.example.com/search", opts, CancellationToken.None);

        Assert.Equal("resp", res.Text);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("""{"query":"x"}""", seenBody);
        Assert.Equal("Bearer k123", seen.Headers.GetValues("Authorization").Single());
        Assert.Equal("application/json", seen.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Post_is_never_cached_and_never_seeds_the_get_cache()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("r") };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());
        var opts = new FetchOptions(PostJson: """{"a":1}""");

        var r1 = await fetch.FetchAsync("http://api.example.com/s", opts, CancellationToken.None);
        var r2 = await fetch.FetchAsync("http://api.example.com/s", opts, CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.False(r2.FromCache);

        await fetch.FetchAsync("http://api.example.com/s", CancellationToken.None);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Post_redirect_is_an_error()
    {
        var handler = new ScriptedHandler { Responder = _ => Redirect("http://api.example.com/elsewhere") };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var ex = await Assert.ThrowsAsync<GattoFetchException>(() =>
            fetch.FetchAsync("http://api.example.com/s", new FetchOptions(PostJson: "{}"), CancellationToken.None));
        Assert.Contains("redirect answering POST", ex.Message);
    }

    [Fact]
    public async Task Get_headers_ride_without_forcing_post()
    {
        HttpRequestMessage? seen = null;
        var handler = new ScriptedHandler();
        handler.Responder = req => { seen = req; return Ok("x"); };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        await fetch.FetchAsync("http://api.example.com/",
            new FetchOptions(Headers: new Dictionary<string, string> { ["X-Probe"] = "1" }), CancellationToken.None);

        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal("1", seen.Headers.GetValues("X-Probe").Single());
    }

    [Fact]
    public async Task Allowed_origin_fetches_and_redirect_escape_is_still_blocked()
    {
        var handler = new ScriptedHandler();
        handler.Responder = req => req.RequestUri!.AbsolutePath == "/hop"
            ? Redirect("http://10.0.0.99:80/")
            : Ok("searx");
        var fetch = new GuardedFetch(
            handler: handler,
            resolver: _ => Task.FromResult(new[] { IPAddress.Parse("10.0.0.50") }),
            allowedOrigins: new[] { "http://10.0.0.50:8888" });

        var ok = await fetch.FetchAsync("http://10.0.0.50:8888/search?q=x", CancellationToken.None);
        Assert.Equal("searx", ok.Text);

        var ex = await Assert.ThrowsAsync<GattoFetchException>(() =>
            fetch.FetchAsync("http://10.0.0.50:8888/hop", CancellationToken.None));
        Assert.Contains("blocked", ex.Message);
    }

    [Fact]
    public async Task Allowed_origin_by_hostname_resolving_private_is_admitted()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("searx") };
        var fetch = new GuardedFetch(
            handler: handler,
            resolver: _ => Task.FromResult(new[] { IPAddress.Parse("10.0.0.50") }),
            allowedOrigins: new[] { "http://pi.local:8888" });

        var ok = await fetch.FetchAsync("http://pi.local:8888/search?q=x", CancellationToken.None);
        Assert.Equal("searx", ok.Text);

        //the port is not on the allowlist, so the private address stays blocked.
        await Assert.ThrowsAsync<GattoFetchException>(() =>
            fetch.FetchAsync("http://pi.local:9999/", CancellationToken.None));
    }

    [Fact]
    public async Task No_allowlist_means_todays_behavior()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("x") };
        var fetch = new GuardedFetch(
            handler: handler,
            resolver: _ => Task.FromResult(new[] { IPAddress.Parse("10.0.0.50") }));
        await Assert.ThrowsAsync<GattoFetchException>(() =>
            fetch.FetchAsync("http://10.0.0.50:8888/", CancellationToken.None));
    }

    [Fact]
    public async Task Get_with_null_options_still_caches()
    {
        var handler = new ScriptedHandler { Responder = _ => Ok("hello") };
        var fetch = new GuardedFetch(handler: handler, resolver: Resolver());

        var r1 = await fetch.FetchAsync("http://ok.example.com/", null, CancellationToken.None);
        var r2 = await fetch.FetchAsync("http://ok.example.com/", null, CancellationToken.None);
        Assert.Equal("hello", r1.Text);
        Assert.True(r2.FromCache);
        Assert.Equal(1, handler.Calls);
    }
}
