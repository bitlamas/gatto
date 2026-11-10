using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;
using Trace = Gatto.Core.Trace;

namespace Gatto.Tests;

//an endpoint with a login is asked before a request leaves, so nothing is sent without a bearer, and a session logs in in place where -p names the login
[Collection("e2e")]
public class LoginFirstTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-login-first-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private static FakeResponse Text(string text) => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static int ChatRequests(FakeOpenAiServer server) => server.RequestBodies.Count(b => b.TryGetProperty("messages", out _));

    private static async Task<string> Text(OpenAiCompatClient client, CancellationToken ct = default)
    {
        var text = "";
        await foreach (var e in client.StreamAsync(new ChatRequest("m", [new ChatMessage("user", "hi")]), ct))
            if (e is StreamEvent.TextDelta d) text += d.Text;
        return text;
    }

    //a store of one token, which the login fills and the headers callback reads, as an extension over its secrets does
    private sealed class Store
    {
        public string? Token;
        public int Logins;
        public int HeaderCalls;
        public int Checks;
    }

    private static EndpointConfig Endpoint(FakeOpenAiServer server, Store store,
        Func<IReadOnlyDictionary<string, string>>? headers = null, Func<int, string, CancellationToken, Task<bool>>? retry = null,
        Func<bool>? loggedIn = null) =>
        new(server.BaseUrl,
            Headers: _ =>
            {
                store.HeaderCalls++;
                return Task.FromResult(headers?.Invoke() ?? (store.Token is { } t
                    ? new Dictionary<string, string> { ["Authorization"] = "Bearer " + t }
                    : throw new InvalidOperationException("not logged in")));
            },
            Login: (_, _) => Task.FromResult("someone"),
            LoggedIn: loggedIn ?? (() => { store.Checks++; return store.Token is not null; }),
            Retry: retry);

    //the session's login in place, which stores a fresh token as the wiring's login would
    private static Func<CancellationToken, Task<bool>> InPlace(Store store, bool answer = true) => _ =>
    {
        store.Logins++;
        if (answer) store.Token = "fresh" + store.Logins;
        return Task.FromResult(answer);
    };

    [Fact]
    public async Task A_logged_out_endpoint_logs_in_before_the_request_and_sends_once()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Text("ok"));
        var store = new Store();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store)) { LoginInPlace = InPlace(store) };

        Assert.Equal("ok", await Text(client));

        Assert.Equal(1, store.Logins);
        Assert.Equal(1, ChatRequests(server));
        Assert.Equal("Bearer fresh1", server.LastAuthorization);
    }

    [Fact]
    public async Task A_logged_out_endpoint_with_no_login_in_place_sends_nothing_and_says_the_login_line()
    {
        await using var server = new FakeOpenAiServer();
        var store = new Store();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store));

        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));

        Assert.Equal("endpoint hosted: not logged in; run gatto -e hosted and log in once", ex.Message);
        Assert.Equal(0, server.RequestCount);
        Assert.Equal(0, store.HeaderCalls);
    }

    [Fact]
    public async Task A_login_in_place_that_answers_false_sends_nothing()
    {
        await using var server = new FakeOpenAiServer();
        var store = new Store();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store)) { LoginInPlace = InPlace(store, answer: false) };

        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));

        Assert.Equal("endpoint hosted: not logged in", ex.Message);
        Assert.Equal(1, store.Logins);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_headers_callback_that_fails_and_logs_out_runs_the_login_and_is_asked_again()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Text("ok"));
        var store = new Store { Token = "dead" };
        //the first call finds the refresh dead, drops the token and throws, as an extension does on invalid_grant
        IReadOnlyDictionary<string, string> Headers()
        {
            if (store.Token == "dead")
            {
                store.Token = null;
                throw new InvalidOperationException("the login expired");
            }
            return new Dictionary<string, string> { ["Authorization"] = "Bearer " + store.Token };
        }
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store, Headers)) { LoginInPlace = InPlace(store) };

        Assert.Equal("ok", await Text(client));

        Assert.Equal(2, store.HeaderCalls);
        Assert.Equal(1, store.Logins);
        Assert.Equal(1, ChatRequests(server));
        Assert.Equal("Bearer fresh1", server.LastAuthorization);
    }

    [Fact]
    public async Task A_headers_callback_that_fails_while_logged_in_fails_the_turn_with_its_message()
    {
        await using var server = new FakeOpenAiServer();
        var store = new Store { Token = "t" };
        var client = new OpenAiCompatClient(new HttpClient(), "hosted",
            Endpoint(server, store, () => throw new InvalidOperationException("the provider did not answer the refresh")))
        { LoginInPlace = InPlace(store) };

        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));

        Assert.Equal("the provider did not answer the refresh", ex.Message);
        Assert.Equal(0, store.Logins);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_loggedIn_that_throws_fails_the_turn_with_its_message_and_runs_no_login()
    {
        await using var server = new FakeOpenAiServer();
        var store = new Store();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted",
            Endpoint(server, store, loggedIn: () => throw new InvalidOperationException("the secrets file does not unseal")))
        { LoginInPlace = InPlace(store) };

        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));

        Assert.Equal("the secrets file does not unseal", ex.Message);
        Assert.Equal(0, store.Logins);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_loggedIn_that_never_returns_fails_the_turn_in_time_and_runs_no_login()
    {
        await using var server = new FakeOpenAiServer();
        var store = new Store();
        using var never = new ManualResetEventSlim();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted",
            Endpoint(server, store, loggedIn: () => { never.Wait(CancellationToken.None); return true; }))
        { LoginInPlace = InPlace(store) };
        var saved = EndpointAuth.HeaderBound;
        try
        {
            EndpointAuth.HeaderBound = TimeSpan.FromMilliseconds(300);
            var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));
            Assert.Equal("endpoint hosted: its extension did not answer in time", ex.Message);
        }
        finally
        {
            EndpointAuth.HeaderBound = saved;
            never.Set();
        }

        Assert.Equal(0, store.Logins);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_bearer_refused_right_after_a_login_in_place_is_one_login_and_the_providers_sentence()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Status: 401, Body: "{\"error\":\"no subscription\"}"));
        var store = new Store();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted",
            Endpoint(server, store, retry: (_, _, _) => Task.FromResult(false)))
        { LoginInPlace = InPlace(store) };

        var ex = await Assert.ThrowsAsync<GattoHttpStatusException>(() => Text(client));

        Assert.Equal("endpoint hosted returned 401: no subscription", ex.Message);
        Assert.Equal(1, store.Logins);
        Assert.Equal(1, ChatRequests(server));
    }

    [Fact]
    public async Task A_login_before_the_send_marks_the_request_span()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Text("one"));
        server.Enqueue(Text("two"));
        var file = Path.Combine(_dir, "s.trace.jsonl");
        using var trace = new Trace(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => file, () => "s", "b", 1);
        var store = new Store();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store), trace: trace) { LoginInPlace = InPlace(store) };

        await Text(client);
        await Text(client);
        trace.Flush();

        var requests = File.ReadAllLines(file).Skip(1).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(s => s.GetProperty("s").GetString() == "request").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, requests[0].GetProperty("logged_in_first").GetInt32());
        Assert.False(requests[1].TryGetProperty("logged_in_first", out _));
    }

    [Fact]
    public async Task A_login_after_a_headers_callback_that_logged_out_marks_the_span_and_so_does_one_whose_send_failed()
    {
        //the first request meets a dead refresh in its headers, the second logs in and finds no server, and both spans name the login
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Text("one"));
        var file = Path.Combine(_dir, "s.trace.jsonl");
        using var trace = new Trace(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => file, () => "s", "b", 1);
        var store = new Store { Token = "dead" };
        IReadOnlyDictionary<string, string> Headers()
        {
            if (store.Token == "dead")
            {
                store.Token = null;
                throw new InvalidOperationException("the login expired");
            }
            return new Dictionary<string, string> { ["Authorization"] = "Bearer " + store.Token };
        }
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store, Headers), trace: trace) { LoginInPlace = InPlace(store) };
        var closed = new OpenAiCompatClient(new HttpClient(), "hosted", new EndpointConfig("http://127.0.0.1:1",
            Login: (_, _) => Task.FromResult("someone"), LoggedIn: () => false), trace: trace) { LoginInPlace = _ => Task.FromResult(true) };

        await Text(client);
        await Assert.ThrowsAsync<GattoConnectionException>(() => Text(closed));
        trace.Flush();

        var requests = File.ReadAllLines(file).Skip(1).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(s => s.GetProperty("s").GetString() == "request").ToList();
        Assert.Equal([1, 1], requests.Select(r => r.GetProperty("logged_in_first").GetInt32()));
    }

    [Fact]
    public async Task An_endpoint_without_a_login_is_never_asked()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Text("ok"));
        var checks = 0;
        var ep = new EndpointConfig(server.BaseUrl, LoggedIn: () => { checks++; return false; });
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        Assert.Equal("ok", await Text(client));

        Assert.Equal(0, checks);
        Assert.Equal(1, ChatRequests(server));
    }

    [Fact]
    public async Task A_cancel_during_the_login_in_place_ends_the_turn_as_cancelled()
    {
        await using var server = new FakeOpenAiServer();
        var store = new Store();
        using var cts = new CancellationTokenSource();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", Endpoint(server, store))
        {
            LoginInPlace = async ct =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Text(client, cts.Token));

        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_refused_bearer_still_takes_the_401_door()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Status: 401, Body: "{\"error\":\"token revoked\"}"));
        server.Enqueue(Text("ok"));
        var store = new Store { Token = "revoked" };
        var client = new OpenAiCompatClient(new HttpClient(), "hosted",
            Endpoint(server, store, retry: (_, _, _) => Task.FromResult(false)))
        { LoginInPlace = InPlace(store) };

        Assert.Equal("ok", await Text(client));

        Assert.Equal(1, store.Logins);
        Assert.Equal(2, ChatRequests(server));
        Assert.Equal("Bearer fresh1", server.LastAuthorization);
    }
}
