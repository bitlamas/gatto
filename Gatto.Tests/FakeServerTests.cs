using System.Net;
using System.Net.Sockets;
using System.Text;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class FakeServerTests
{
    [Fact]
    public async Task Serves_scripted_sse_and_captures_request_body()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { "data: hello\n\n", "data: [DONE]\n\n" }));

        using var http = new HttpClient();
        var resp = await http.PostAsync($"{server.BaseUrl}/v1/chat/completions",
            new StringContent("{\"model\":\"m\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("data: hello", body);
        Assert.Equal("m", server.LastRequestBody!.Value.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Drop_after_frames_aborts_connection()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { "data: a\n\n", "data: b\n\n" }, DropAfterFrames: 1));

        using var http = new HttpClient();
        //put PostAsync inside the assertion, the abort throws from the call itself
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var resp = await http.PostAsync($"{server.BaseUrl}/v1/chat/completions",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            var s = await resp.Content.ReadAsStringAsync();
            if (!s.Contains("data: b")) throw new IOException("stream ended early");
        });
    }

    [Fact]
    public async Task Non_200_returns_status_and_body()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Status: 401, Body: "{\"error\":\"bad key\"}"));

        using var http = new HttpClient();
        var resp = await http.PostAsync($"{server.BaseUrl}/v1/chat/completions",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Survives_client_abort_mid_request_body()
    {
        await using var server = new FakeOpenAiServer();
        var uri = new Uri(server.BaseUrl);

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(uri.Host, uri.Port);
            tcp.LingerState = new LingerOption(true, 0); //close the socket with an RST instead of a clean FIN
            using var stream = tcp.GetStream();
            var request = Encoding.ASCII.GetBytes(
                "POST /v1/chat/completions HTTP/1.1\r\n" +
                $"Host: {uri.Host}:{uri.Port}\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: 100\r\n" +
                "\r\n" +
                "{\"partial\":true"); //the body is far shorter than the promised 100 bytes.
            await stream.WriteAsync(request);
            await stream.FlushAsync();
        } //the socket resets here, before the body ends.

        server.Enqueue(new FakeResponse(Body: "{\"ok\":true}"));

        //the fake serves on the thread pool, so a busy pool delays its answer, and 30 s is a ceiling the test never waits out
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var resp = await http.PostAsync($"{server.BaseUrl}/v1/chat/completions",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Malformed_body_gets_400_then_serving_continues()
    {
        await using var server = new FakeOpenAiServer();
        using var http = new HttpClient();

        var badResp = await http.PostAsync($"{server.BaseUrl}/v1/chat/completions",
            new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

        server.Enqueue(new FakeResponse(Body: "{\"ok\":true}"));
        var goodResp = await http.PostAsync($"{server.BaseUrl}/v1/chat/completions",
            new StringContent("{\"model\":\"m\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, goodResp.StatusCode);
    }
}
