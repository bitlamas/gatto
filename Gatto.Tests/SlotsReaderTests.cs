using System.Net;
using System.Text;
using Gatto.Core.Client;

namespace Gatto.Tests;

//the /slots reader that turns llama-server's prompt counters into a phase-keyed purr row, tested through a stubbed HTTP handler so nothing goes live

file sealed class SlotsStub(HttpStatusCode code, string body) : HttpMessageHandler
{
    public Uri? LastUri { get; private set; }

    //the Authorization header the reader sent, null when it sent none
    public string? LastAuthorization { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        LastUri = req.RequestUri;
        LastAuthorization = req.Headers.Authorization?.ToString();
        return Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

file sealed class SlotsThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        throw new HttpRequestException("connection refused");
}

public class SlotsReaderTests
{
    //captured from a live prefill: the two counters only ever advance together, one n_batch at a time. don't invent this fixture
    private const string PrefillSlots = """
        [{"id":0,"is_processing":true,"n_prompt_tokens":20480,"n_prompt_tokens_processed":20480,
          "n_prompt_tokens_cache":0,"next_token":[{"n_decoded":202}]}]
        """;

    //parsing in HttpSlotsReader

    [Fact]
    public async Task ProcessingSlot_ParsesPromptCountersAndDecoded()
    {
        var stub = new SlotsStub(HttpStatusCode.OK, PrefillSlots);
        using var http = new HttpClient(stub);
        var p = await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default);
        Assert.NotNull(p);
        Assert.Equal(20480, p!.Value.PromptProcessed);   //the processed count is the truthful numerator and the only progress signal here
        Assert.True(p.Value.Processing);
        Assert.EndsWith("/slots", stub.LastUri!.AbsolutePath, StringComparison.Ordinal);
    }

    //the api key goes on /slots too, through the same middleware as /props

    [Fact]
    public async Task ApiKey_IsSentAsBearer()
    {
        //without the header the prefill progress row goes silent the moment a model sets api_key
        var stub = new SlotsStub(HttpStatusCode.OK, PrefillSlots);
        using var http = new HttpClient(stub);

        var p = await new HttpSlotsReader(http, "http://127.0.0.1:1235", "s3cr3t-value").ReadAsync(default);

        Assert.NotNull(p);
        Assert.Equal("Bearer s3cr3t-value", stub.LastAuthorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutApiKey_NoAuthorizationHeaderAtAll(string? apiKey)
    {
        var stub = new SlotsStub(HttpStatusCode.OK, PrefillSlots);
        using var http = new HttpClient(stub);

        await new HttpSlotsReader(http, "http://127.0.0.1:1235", apiKey).ReadAsync(default);

        Assert.Null(stub.LastAuthorization);
    }

    [Fact]
    public void The_two_prompt_counters_move_together_so_neither_is_a_denominator()
    {
        //the two counters were equal in every live poll, so don't build a percentage on processed < total
        var p = new SlotProgress(PromptTotal: 20480, PromptProcessed: 20480, Decoded: 202, Processing: true);
        Assert.Equal(p.PromptTotal, p.PromptProcessed);
    }

    [Fact]
    public async Task Decoded_ComesFromNextTokenZero()
    {
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.OK, """
            [{"is_processing":true,"n_prompt_tokens":101197,"n_prompt_tokens_processed":101197,
              "next_token":[{"n_decoded":97}]}]
            """));
        var p = await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default);
        Assert.Equal(97, p!.Value.Decoded);
    }

    [Fact]
    public async Task NothingProcessing_ReturnsNull()
    {
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.OK,
            """[{"id":0,"is_processing":false,"n_prompt_tokens":0,"n_prompt_tokens_processed":0}]"""));
        Assert.Null(await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default));
    }

    [Fact]
    public async Task EmptyArray_ReturnsNull()
    {
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.OK, "[]"));
        Assert.Null(await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default));
    }

    [Fact]
    public async Task MultipleProcessing_PicksLargestPrompt()
    {
        //with several slots processing, the reader takes the biggest prompt, and a wrong pick mis-attributes only cosmetically
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.OK, """
            [{"id":0,"is_processing":true,"n_prompt_tokens":2000,"n_prompt_tokens_processed":2000,"next_token":[{"n_decoded":5}]},
             {"id":1,"is_processing":true,"n_prompt_tokens":101197,"n_prompt_tokens_processed":34407,"next_token":[{"n_decoded":0}]}]
            """));
        var p = await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default);
        Assert.Equal(101197, p!.Value.PromptTotal);
    }

    [Fact]
    public async Task Non200_ReturnsNull()
    {
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.ServiceUnavailable, "busy"));
        Assert.Null(await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default));
    }

    [Fact]
    public async Task MalformedJson_ReturnsNull()
    {
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.OK, "{not json"));
        Assert.Null(await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default));
    }

    [Fact]
    public async Task RootNotAnArray_ReturnsNull()
    {
        using var http = new HttpClient(new SlotsStub(HttpStatusCode.OK, """{"error":"slots disabled"}"""));
        Assert.Null(await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default));
    }

    [Fact]
    public async Task ConnectionRefused_ReturnsNullNeverThrows()
    {
        using var http = new HttpClient(new SlotsThrowingHandler());
        Assert.Null(await new HttpSlotsReader(http, "http://127.0.0.1:1235").ReadAsync(default));
    }

    [Fact]
    public async Task TrailingSlashBaseUrl_StillHitsSlots()
    {
        var stub = new SlotsStub(HttpStatusCode.OK, PrefillSlots);
        using var http = new HttpClient(stub);
        await new HttpSlotsReader(http, "http://127.0.0.1:1235/").ReadAsync(default);
        Assert.Equal("/slots", stub.LastUri!.AbsolutePath);   //a trailing slash on the base url must not double into //slots
    }
}
