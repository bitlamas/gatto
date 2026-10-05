using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//llama-server's two counting routes as /context calls them: the whole request's tokens, and one part's text
public class ContextCountClientTests
{
    private static OpenAiCompatClient Client(FakeOpenAiServer server) =>
        new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, "local", new EndpointConfig(server.BaseUrl));

    private static ChatRequest Request() => new("m",
        [new ChatMessage("system", "sys"), new ChatMessage("user", "hello")],
        [new ToolSpec("read_file", "reads a file", JsonDocument.Parse("{\"type\":\"object\"}").RootElement)],
        BodyOverrides: JsonDocument.Parse("{\"chat_template_kwargs\":{\"enable_thinking\":true}}").RootElement);

    //the count must describe the prompt the server renders for this request, so the body is the one the chat call sends
    [Fact]
    public async Task Counting_the_input_tokens_sends_the_chat_requests_own_body()
    {
        await using var server = new FakeOpenAiServer { InputTokens = 4321 };
        server.Enqueue(new FakeResponse(Frames: ["data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n", "data: [DONE]\n\n"]));
        var client = Client(server);
        await foreach (var _ in client.StreamAsync(Request())) { }

        var counted = await client.CountInputTokensAsync(Request(), CancellationToken.None);

        Assert.Equal(4321, counted);
        Assert.Equal(server.LastRequestRaw, server.InputTokensRaw);
    }

    [Fact]
    public async Task Tokenizing_a_part_counts_its_text_as_text()
    {
        await using var server = new FakeOpenAiServer { TokenizeCount = s => s.Length };

        var counted = await Client(server).TokenizeCountAsync("<|im_start|>abc", CancellationToken.None);

        Assert.Equal(15, counted);
        Assert.Equal("<|im_start|>abc", Assert.Single(server.Tokenized));
    }

    //a server without the routes answers 404, which is a null count and never an exception
    [Fact]
    public async Task A_server_without_the_routes_gives_no_count()
    {
        await using var server = new FakeOpenAiServer();
        var client = Client(server);

        Assert.Null(await client.CountInputTokensAsync(Request(), CancellationToken.None));
        Assert.Null(await client.TokenizeCountAsync("abc", CancellationToken.None));
    }
}
