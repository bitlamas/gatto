using System.Net;
using System.Text;
using Gatto.Core.Client;

namespace Gatto.Tests;

file sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
{
    //the Authorization header the probe sent, null when it sent none
    public string? LastAuthorization { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        LastAuthorization = req.Headers.Authorization?.ToString();
        return Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

file sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        throw new HttpRequestException("connection refused");
}

public class ServeProbeTests
{
    private const string GoodProps = """
        {"default_generation_settings":{"params":{"temperature":1.0},"n_ctx":131072},
         "model_path":"C:\\models\\gemma-4-26B-A4B-it-Q6_K.gguf","total_slots":1}
        """;

    [Fact]
    public async Task WellFormedProps_ReturnsModelPathAndNCtx()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, GoodProps));
        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default);
        Assert.NotNull(loaded);
        Assert.Equal(@"C:\models\gemma-4-26B-A4B-it-Q6_K.gguf", loaded!.ModelPath);
        Assert.Equal(131072, loaded.NCtx);
    }

    [Fact]
    public async Task ApiKey_IsSentAsBearer()
    {
        //a keyed server demands the bearer token, without it the probe 401s and every field reads unknown
        var stub = new StubHandler(HttpStatusCode.OK, GoodProps);
        using var http = new HttpClient(stub);

        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default, "s3cr3t-value");

        Assert.NotNull(loaded);
        Assert.Equal("Bearer s3cr3t-value", stub.LastAuthorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutApiKey_NoAuthorizationHeaderAtAll(string? apiKey)
    {
        //the key is opt-in both ways, so absent, empty, and blank all mean send nothing.
        var stub = new StubHandler(HttpStatusCode.OK, GoodProps);
        using var http = new HttpClient(stub);

        await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default, apiKey);

        Assert.Null(stub.LastAuthorization);
    }

    [Fact]
    public async Task MalformedJson_ReturnsNull()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, "{not json"));
        Assert.Null(await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default));
    }

    //a missing model_path must not discard the answer, the connect path still needs the reported context
    [Fact]
    public async Task MissingModelPath_STILL_REPORTS_THE_CONTEXT_it_did_give()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            """{"default_generation_settings":{"n_ctx":55555}}"""));

        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.ModelPath);      //a null ModelPath means the server named no file
        Assert.Equal(55555, loaded.NCtx);    //the reported window is what the connect path needs.
    }

    [Fact]
    public async Task A_PROPS_THAT_SAYS_NOTHING_is_an_answer_that_says_nothing()
    {
        //a 200 with no fields is still an answer, so the result is not null and every field reads unknown
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, "{}"));

        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.ModelPath);
        Assert.Null(loaded.NCtx);
    }

    [Fact]
    public async Task A_BLANK_MODEL_PATH_IS_NULL_rather_than_an_empty_claim()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            """{"model_path":"   ","default_generation_settings":{"n_ctx":4096}}"""));

        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default);

        Assert.Null(loaded!.ModelPath);
        Assert.Equal(4096, loaded.NCtx);
    }

    [Fact]
    public async Task MissingNCtx_StillIdentifiesTheWeights_WindowIsUnknown()
    {
        //n_ctx is optional, model_path alone identifies the weights, and failing the probe would hide a real mismatch
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            """{"model_path":"C:\\m.gguf"}"""));
        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default);
        Assert.NotNull(loaded);
        Assert.Equal(@"C:\m.gguf", loaded!.ModelPath);
        Assert.Null(loaded.NCtx);
    }

    [Fact]
    public async Task Non200_ReturnsNull()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.NotFound, "nope"));
        Assert.Null(await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default));
    }

    [Fact]
    public async Task ConnectionRefused_ReturnsNullNeverThrows()
    {
        using var http = new HttpClient(new ThrowingHandler());
        Assert.Null(await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", default));
    }

    [Fact]
    public async Task TrailingSlashInBaseUrl_StillProbes()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, GoodProps));
        Assert.NotNull(await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235/", default));
    }
}
