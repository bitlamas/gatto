using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class OpenAiCompatUsageTests
{
    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static ChatRequest Req() => new("m", new[] { new ChatMessage("user", "hi") });

    private static async Task Drain(IChatClient c)
    {
        await foreach (var _ in c.StreamAsync(Req())) { }
    }

    [Fact]
    public async Task OnUsage_gets_the_whole_usage_object_once_per_request()
    {
        //a provider's own keys such as limits and cost reach the caller untouched, and a usage-only chunk counts
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}", finish: "stop"),
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":418,\"completion_tokens\":2,\"cost\":0.01,\"remaining_limits\":{\"max\":18},\"applied_limit_category\":\"max\"}}\n\n",
            "data: [DONE]\n\n",
        }));
        var seen = new List<JsonElement>();
        var client = new OpenAiCompatClient(new HttpClient(), "acme", new EndpointConfig(s.BaseUrl)) { OnUsage = u => seen.Add(u) };

        await Drain(client);

        var u = Assert.Single(seen);
        Assert.Equal(18, u.GetProperty("remaining_limits").GetProperty("max").GetInt32());
        Assert.Equal("max", u.GetProperty("applied_limit_category").GetString());
        Assert.Equal(0.01m, u.GetProperty("cost").GetDecimal());
    }

    //the cached part of the prompt rides in prompt_tokens_details, and a usage without it reads as nothing said
    [Theory]
    [InlineData(",\"prompt_tokens_details\":{\"cached_tokens\":300}", 300)]
    [InlineData("", null)]
    public async Task The_usage_carries_the_cached_prompt_tokens(string details, int? expected)
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}", finish: "stop"),
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":418,\"completion_tokens\":2" + details + "}}\n\n",
            "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "acme", new EndpointConfig(s.BaseUrl));
        Usage? usage = null;

        await foreach (var ev in client.StreamAsync(Req())) if (ev is StreamEvent.Finished f) usage = f.Usage;

        Assert.Equal(418, usage!.PromptTokens);
        Assert.Equal(expected, usage.CachedTokens);
    }

    [Fact]
    public async Task OnUsage_takes_the_last_usage_when_a_server_repeats_it()
    {
        //a server that sends usage on two chunks would double a summed cost, so only the last one is handed on
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"o\"},\"finish_reason\":null}],\"usage\":{\"cost\":0.01}}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"k\"},\"finish_reason\":\"stop\"}],\"usage\":{\"cost\":0.02}}\n\n",
            "data: [DONE]\n\n",
        }));
        var seen = new List<JsonElement>();
        var client = new OpenAiCompatClient(new HttpClient(), "x", new EndpointConfig(s.BaseUrl)) { OnUsage = u => seen.Add(u) };

        await Drain(client);

        Assert.Equal(0.02m, Assert.Single(seen).GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task OnUsage_is_silent_when_the_stream_carries_no_usage()
    {
        //no usage means nothing was reported, so the hook is not called with an empty stand-in
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var calls = 0;
        var client = new OpenAiCompatClient(new HttpClient(), "x", new EndpointConfig(s.BaseUrl)) { OnUsage = _ => calls++ };

        await Drain(client);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_throwing_OnUsage_never_ends_the_turn()
    {
        //the hook feeds the footer, so its failure must not cost the reply
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1}}\n\n",
            "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "x", new EndpointConfig(s.BaseUrl))
        {
            OnUsage = _ => throw new InvalidOperationException("footer broke"),
        };

        var events = new List<StreamEvent>();
        await foreach (var e in client.StreamAsync(Req())) events.Add(e);

        Assert.IsType<StreamEvent.Finished>(events[^1]);
    }
}
