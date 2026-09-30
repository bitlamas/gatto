using Gatto.Cli;
using Gatto.Core.Client;

namespace Gatto.Tests;

//budget and user-visible line must come out of one call, a separate computation could drift from what happened
public class ConnectContextTests
{
    [Fact]
    public void Live_wins_when_it_is_SMALLER_than_the_configured_value()
    {
        var r = ConnectContext.Resolve(configured: 262144, liveNCtx: 131072);

        Assert.Equal(131072, r.Budget);
        Assert.Equal("server reports 131072. Using it (endpoint config says 262144)", r.Line);
    }

    [Fact]
    public void Live_wins_when_it_is_LARGER_than_the_configured_value()
    {
        //this case rules out min(), which would return 131072 here. the live report must win
        var r = ConnectContext.Resolve(configured: 131072, liveNCtx: 262144);

        Assert.Equal(262144, r.Budget);
        Assert.Equal("server reports 262144. Using it (endpoint config says 131072)", r.Line);
    }

    [Fact]
    public void Agreement_is_silent()
    {
        var r = ConnectContext.Resolve(configured: 32768, liveNCtx: 32768);

        Assert.Equal(32768, r.Budget);
        Assert.Null(r.Line);
    }

    [Fact]
    public void No_answer_falls_back_to_the_configured_value_and_says_so()
    {
        var r = ConnectContext.Resolve(configured: 32768, liveNCtx: null);

        Assert.Equal(32768, r.Budget);
        Assert.Equal("server didn't report a context. Using your configured 32768", r.Line);
    }

    [Fact]
    public void Neither_side_answers_leaves_a_null_budget_and_NO_line()
    {
        //when nothing answers, ContextWarning already states that, a second sentence would say the same fact twice
        var r = ConnectContext.Resolve(configured: null, liveNCtx: null);

        Assert.Null(r.Budget);
        Assert.Null(r.Line);
        Assert.NotNull(ContextWarning.Compose(r.Budget));
    }

    [Fact]
    public void Live_with_nothing_configured_says_where_the_number_came_from_and_contradicts_nothing()
    {
        //with nothing configured there is no value to contrast, the parenthetical would name a key the user never wrote
        var r = ConnectContext.Resolve(configured: null, liveNCtx: 131072);

        Assert.Equal(131072, r.Budget);
        Assert.Equal("server reports 131072. Using it", r.Line);
        Assert.DoesNotContain("config says", r.Line);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonsense_context_from_the_server_is_NOT_an_answer(int reported)
    {
        //treat only a positive number as an answer, the same predicate ContextWarning uses. a server reporting 0 must not become a zero-token budget
        var r = ConnectContext.Resolve(configured: 32768, liveNCtx: reported);

        Assert.Equal(32768, r.Budget);
        Assert.Equal("server didn't report a context. Using your configured 32768", r.Line);
    }

    [Fact]
    public void A_nonsense_CONFIGURED_value_is_not_an_answer_either()
    {
        //symmetry with the server side is not decoration, a configured 0 would otherwise resolve to a zero budget that reads as managed
        var r = ConnectContext.Resolve(configured: 0, liveNCtx: null);

        Assert.Null(r.Budget);
        Assert.Null(r.Line);
        Assert.NotNull(ContextWarning.Compose(r.Budget));
    }

    [Fact]
    public void The_line_never_states_a_number_the_budget_did_not_take()
    {
        //sweep every state instead of one case, a line that exists must name the budget actually resolved
        (int? cfg, int? live)[] table =
        [
            (262144, 131072), (131072, 262144), (32768, 32768),
            (32768, null), (null, null), (null, 131072), (32768, 0),
        ];

        foreach (var (cfg, live) in table)
        {
            var r = ConnectContext.Resolve(cfg, live);
            if (r.Line is null) continue;
            Assert.Contains(r.Budget!.Value.ToString(), r.Line);
        }
    }

    //drive the real parser from a /props body, a unit test on Resolve alone stays green while the producer starves it
    [Fact]
    public async Task A_PATHLESS_SERVERS_CONTEXT_WINS_and_the_line_is_TRUE()
    {
        var http = new HttpClient(new PropsHandler(
            """{"default_generation_settings":{"n_ctx":55555}}"""));

        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1234", default);
        var r = ConnectContext.Resolve(configured: 262144, liveNCtx: loaded?.NCtx);

        Assert.Equal(55555, r.Budget);
        Assert.Contains("server reports 55555", r.Line!, StringComparison.Ordinal);
        Assert.Contains("262144", r.Line!, StringComparison.Ordinal);   //the line must also name the configured value it overrode.

        //the false sentence must be gone once the live report wins.
        Assert.DoesNotContain("didn't report a context", r.Line!, StringComparison.Ordinal);
    }

    //a server that really reports no window must still fall back and say so
    [Fact]
    public async Task A_SERVER_THAT_REALLY_SAYS_NOTHING_still_falls_back_truthfully()
    {
        var http = new HttpClient(new PropsHandler("""{"model_path": "C:\m.gguf"}"""));

        var loaded = await ServeProbe.ProbeAsync(http, "http://127.0.0.1:1234", default);
        var r = ConnectContext.Resolve(configured: 32768, liveNCtx: loaded?.NCtx);

        Assert.Equal(32768, r.Budget);
        Assert.Contains("didn't report a context", r.Line!, StringComparison.Ordinal);
    }

    private sealed class PropsHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
