using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a refused request is retried once on the extension's word, never after a byte of the stream, and a dead refresh takes the login in place
[Collection("e2e")]
public class RetrySeamTests
{
    private static FakeResponse Text(string text) => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static FakeResponse Refused(int status, string error = "update your client") =>
        new(Status: status, Body: "{\"error\":\"" + error + "\"}");

    private static int ChatRequests(FakeOpenAiServer server) => server.RequestBodies.Count(b => b.TryGetProperty("messages", out _));

    private static ChatRequest Ask() => new("m", [new ChatMessage("user", "hi")]);

    private static async Task<string> Text(OpenAiCompatClient client)
    {
        var text = "";
        await foreach (var e in client.StreamAsync(Ask(), CancellationToken.None))
            if (e is StreamEvent.TextDelta d) text += d.Text;
        return text;
    }

    //headers that count, so the resend's Authorization shows the callback ran again
    private static (EndpointConfig Endpoint, Func<int> Calls) Counted(FakeOpenAiServer server, Func<int, string, CancellationToken, Task<bool>>? retry)
    {
        var calls = 0;
        var ep = new EndpointConfig(server.BaseUrl,
            Headers: _ => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["Authorization"] = "Bearer t" + Interlocked.Increment(ref calls) }),
            Retry: retry);
        return (ep, () => calls);
    }

    [Fact]
    public async Task A_426_then_200_is_one_retry_and_the_answer()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(426));
        server.Enqueue(Text("ok"));
        var seen = new List<(int, string)>();
        var (ep, _) = Counted(server, (status, body, _) => { seen.Add((status, body)); return Task.FromResult(true); });
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        Assert.Equal("ok", await Text(client));

        Assert.Equal([(426, "{\"error\":\"update your client\"}")], seen);
        Assert.Equal(2, ChatRequests(server));
        Assert.Equal("Bearer t2", server.LastAuthorization);
    }

    [Fact]
    public async Task A_426_twice_is_the_providers_sentence_once()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(426));
        server.Enqueue(Refused(426, "still old"));
        var retries = 0;
        var (ep, _) = Counted(server, (_, _, _) => { retries++; return Task.FromResult(true); });
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        var ex = await Assert.ThrowsAsync<GattoHttpStatusException>(() => Text(client));

        Assert.Equal(1, retries);
        Assert.Equal(2, ChatRequests(server));
        Assert.Equal("endpoint hosted returned 426: still old", ex.Message);
    }

    [Fact]
    public async Task A_retry_that_throws_fails_the_turn_with_its_message()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(426));
        var (ep, _) = Counted(server, (_, _, _) => throw new InvalidOperationException("the version page did not answer"));
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));

        Assert.Equal("the version page did not answer", ex.Message);
        Assert.Equal(1, ChatRequests(server));
    }

    [Fact]
    public async Task A_retry_that_answers_false_fails_with_the_providers_sentence()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(403, "not allowed"));
        var (ep, _) = Counted(server, (_, _, _) => Task.FromResult(false));
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        var ex = await Assert.ThrowsAsync<GattoHttpStatusException>(() => Text(client));

        Assert.Equal("endpoint hosted returned 403: not allowed", ex.Message);
        Assert.Equal(1, ChatRequests(server));
    }

    [Fact]
    public async Task A_retry_that_never_returns_fails_in_time_with_the_endpoint_named()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(426));
        using var never = new ManualResetEventSlim();
        var (ep, _) = Counted(server, (_, _, _) => { never.Wait(CancellationToken.None); return Task.FromResult(true); });
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);
        var saved = EndpointAuth.HeaderBound;
        try
        {
            EndpointAuth.HeaderBound = TimeSpan.FromMilliseconds(300);
            var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Text(client));
            Assert.Equal("endpoint hosted: its extension did not answer the retry in time", ex.Message);
        }
        finally
        {
            EndpointAuth.HeaderBound = saved;
            never.Set();
        }
    }

    [Fact]
    public async Task No_retry_after_the_first_streamed_byte()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Text("partial") with { DropAfterFrames = 1 });
        var retries = 0;
        var (ep, _) = Counted(server, (_, _, _) => { retries++; return Task.FromResult(true); });
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        await foreach (var _ in client.StreamAsync(Ask(), CancellationToken.None)) { }

        Assert.Equal(0, retries);
        Assert.Equal(1, ChatRequests(server));
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task A_dead_refresh_runs_the_login_in_place_and_sends_once_more(bool loggedIn, int requests)
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(401, "token expired"));
        server.Enqueue(Text("ok"));
        var logins = 0;
        var (ep, _) = Counted(server, (_, _, _) => Task.FromResult(false));
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep)
        {
            LoginInPlace = _ => { logins++; return Task.FromResult(loggedIn); },
        };

        if (loggedIn) Assert.Equal("ok", await Text(client));
        else Assert.Equal("endpoint hosted returned 401: token expired", (await Assert.ThrowsAsync<GattoHttpStatusException>(() => Text(client))).Message);

        Assert.Equal(1, logins);
        Assert.Equal(requests, ChatRequests(server));
    }

    [Fact]
    public async Task A_401_with_no_retry_seam_still_takes_the_login_in_place()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(401, "token expired"));
        server.Enqueue(Refused(401, "still expired"));
        var logins = 0;
        var (ep, _) = Counted(server, null);
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep)
        {
            LoginInPlace = _ => { logins++; return Task.FromResult(true); },
        };

        var ex = await Assert.ThrowsAsync<GattoHttpStatusException>(() => Text(client));

        Assert.Equal(1, logins);
        Assert.Equal(2, ChatRequests(server));
        Assert.Equal("endpoint hosted returned 401: still expired", ex.Message);
    }

    [Theory]
    [InlineData(403, 1)]
    [InlineData(400, 0)]
    [InlineData(500, 0)]
    public async Task A_403_and_a_400_differ(int status, int retries)
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Refused(status));
        server.Enqueue(Text("ok"));
        var seen = 0;
        var (ep, _) = Counted(server, (_, _, _) => { seen++; return Task.FromResult(true); });
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", ep);

        if (retries == 1) Assert.Equal("ok", await Text(client));
        else await Assert.ThrowsAsync<GattoHttpStatusException>(() => Text(client));

        Assert.Equal(retries, seen);
    }
}
