using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Client;
using Gatto.Core.Home;

namespace Gatto.Tests;

//every server here is a stub, so these check what gatto concludes from each response shape
public class ProveItTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static OpenAiCompatClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { Timeout = Timeout.InfiniteTimeSpan },
            "local", new EndpointConfig(BaseUrl: "http://127.0.0.1:1235"));

    //the prove under test, with a model name, since the request carries what it is given
    private static Task<ProveOutcome> Run(OpenAiCompatClient client, CancellationToken ct) => ProveIt.RunAsync(client, "test-model", ct);

    private static HttpResponseMessage Sse(params string[] frames)
    {
        var body = string.Concat(frames.Select(f => $"data: {f}\n\n")) + "data: [DONE]\n\n";
        var res = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8) };
        res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return res;
    }

    private const string DeltaOk = """{"choices":[{"delta":{"content":"OK"}}]}""";
    private const string DeltaStop = """{"choices":[{"delta":{},"finish_reason":"stop"}]}""";

    [Fact]
    public async Task A_streaming_server_proves_the_config_and_measures_first_token()
    {
        var r = await Run(Client(_ => Sse(DeltaOk, DeltaStop)), CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        //require the first-token time above zero, since an unassigned TimeSpan is zero and a greater-or-equal assert would pass with the measurement deleted
        Assert.True(r.FirstToken > TimeSpan.Zero, "first-token time was never measured");
    }

    //a stream that closes with no content did stream, so the outcome says it streamed empty and not that it never streamed
    [Fact]
    public async Task A_stream_that_closes_with_no_content_is_told_apart_from_no_stream()
    {
        var empty = await Run(Client(_ => Sse(DeltaStop)), CancellationToken.None);
        Assert.False(empty.Ok);
        Assert.True(empty.StreamedEmpty);

        var whole = await Run(Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[]}""", System.Text.Encoding.UTF8, "application/json"),
        }), CancellationToken.None);
        Assert.False(whole.StreamedEmpty);
    }

    [Fact]
    public async Task A_server_that_answers_WITHOUT_streaming_fails_and_says_streaming()
    {
        //the failure detail must name streaming, since a parse error reads as gatto being broken rather than this server
        var whole = """{"choices":[{"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""";
        var r = await Run(Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(whole, System.Text.Encoding.UTF8, "application/json"),
        }), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("stream", r.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", r.Detail);
        Assert.DoesNotContain("JSON", r.Detail);
    }

    [Fact]
    public async Task A_404_on_completions_is_a_named_failure_not_a_throw()
    {
        var r = await Run(
            Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Detail));
    }

    [Fact]
    public async Task A_transport_death_is_reported_with_the_clients_actionable_sentence()
    {
        //the client turns a dead connection into an actionable sentence, so this check reports that one
        var r = await Run(
            Client(_ => throw new HttpRequestException("connection reset")), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("server down", r.Detail);
    }

    [Fact]
    public async Task Cancellation_propagates_to_the_caller()
    {
        //the caller owns the deadline, so cancellation must reach it and never collapse into Ok=false
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(Client(_ => Sse(DeltaOk, DeltaStop)), cts.Token));
    }
}
