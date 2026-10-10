using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the context probe gives the server 2 seconds, so these run apart from the parallel classes, which can use up the 2 seconds
[Collection("e2e")]
public class OpenAiCompatPropsTests
{
    [Fact]
    public async Task Props_NCtx_IsReturned()
    {
        //a stub for GET /props goes in PropsResponse, Enqueue serves chat-completion bodies
        await using var server = new FakeOpenAiServer();
        server.PropsResponse = new FakeResponse(Body: """{"default_generation_settings":{"n_ctx":32768}}""");
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl))
            { ProbeDeadline = TimeSpan.FromSeconds(30) };   //the assertion reads the answer, so the suite's load must not turn it into a race
        Assert.Equal(32768, await client.TryGetContextLengthAsync());
    }

    [Fact]
    public async Task Props_Malformed_IsNull()
    {
        await using var server = new FakeOpenAiServer();
        server.PropsResponse = new FakeResponse(Body: """{"nope":true}""");
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        Assert.Null(await client.TryGetContextLengthAsync());
    }

    [Fact]
    public async Task Props_ServerDown_IsNull()
    {
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig("http://127.0.0.1:1"));
        Assert.Null(await client.TryGetContextLengthAsync());
    }
}
