using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Gatto.Tests.Fakes;

public sealed record FakeResponse(
    int Status = 200,
    string[]? Frames = null,
    int? DropAfterFrames = null,
    string? Body = null);

public sealed class FakeOpenAiServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentQueue<FakeResponse> _queue = new();
    private readonly Task _serveTask;
    private volatile bool _stopping;

    public string BaseUrl { get; private set; } = "";
    public JsonElement? LastRequestBody { get; private set; }

    private readonly ConcurrentQueue<JsonElement> _bodies = new();
    //every accepted body in arrival order, the prefix-stability guard compares the system message across requests
    public IReadOnlyList<JsonElement> RequestBodies => _bodies.ToArray();

    //the received body verbatim, before parsing, so a byte check compares what actually arrived
    public string? LastRequestRaw { get; private set; }

    //the Authorization header of the last accepted request, null when the client sends none (a model with no api_key sends no header)
    public string? LastAuthorization { get; private set; }

    private int _requestCount;
    //accepted requests only, so a test can assert zero extra model calls (a GET /props probe is routed separately and never counted here)
    public int RequestCount => Volatile.Read(ref _requestCount);

    private volatile bool _propsCalled;
    //true as soon as a GET /props probe arrives, so a test can assert a one-shot -p run never probes
    public bool PropsCalled => _propsCalled;

    //probe responses route before the queue, so a probe never consumes a queued chat response, and the default 404 keeps the probe unknown
    public FakeResponse? PropsResponse { get; set; }

    //llama-server's counting routes, answered before the queue and kept apart from the chat bodies: null leaves a route at 404
    public int? InputTokens { get; set; }
    public Func<string, int>? TokenizeCount { get; set; }
    public string? InputTokensRaw { get; private set; }
    public List<string> Tokenized { get; } = new();

    public FakeOpenAiServer()
    {
        HttpListener? started = null;
        Exception? last = null;
        for (var attempt = 0; attempt < 3 && started is null; attempt++)
        {
            var port = FreeTcpPort();
            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { candidate.Start(); started = candidate; BaseUrl = $"http://127.0.0.1:{port}"; }
            catch (Exception ex) { last = ex; }   //a failed Start disposes its own listener, so dropping it is enough
        }
        _listener = started ?? throw new InvalidOperationException("FakeOpenAiServer: no port after 3 attempts", last);
        _serveTask = Task.Run(ServeAsync);
    }

    public void Enqueue(FakeResponse r) => _queue.Enqueue(r);

    private static int FreeTcpPort() => FreePort.Next();

    private async Task ServeAsync()
    {
        while (!_stopping)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            //route a GET /props before the queue and before the body read (a GET has no body), so it consumes no queued chat response
            if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url?.AbsolutePath == "/props")
            {
                _propsCalled = true;
                LastAuthorization = ctx.Request.Headers["Authorization"];
                var props = PropsResponse ?? new FakeResponse(Status: 404, Body: "{\"error\":\"fake server: no /props configured\"}");
                try
                {
                    ctx.Response.StatusCode = props.Status;
                    ctx.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(props.Body ?? "{}");
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { ctx.Response.Abort(); }
                continue;
            }

            var path = ctx.Request.Url?.AbsolutePath;
            if (ctx.Request.HttpMethod == "POST" && path is "/v1/chat/completions/input_tokens" or "/tokenize")
            {
                string counted;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) counted = await sr.ReadToEndAsync();
                string? answer = null;
                if (path == "/tokenize" && TokenizeCount is { } count)
                {
                    var content = JsonDocument.Parse(counted).RootElement.GetProperty("content").GetString() ?? "";
                    lock (Tokenized) Tokenized.Add(content);
                    answer = "{\"tokens\":[" + string.Join(",", Enumerable.Repeat("1", count(content))) + "]}";
                }
                else if (path != "/tokenize" && InputTokens is int n)
                {
                    InputTokensRaw = counted;
                    answer = $"{{\"input_tokens\":{n},\"object\":\"response.input_tokens\"}}";
                }
                try
                {
                    ctx.Response.StatusCode = answer is null ? 404 : 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer ?? "{\"error\":\"fake server: route off\"}"));
                    ctx.Response.Close();
                }
                catch { ctx.Response.Abort(); }
                continue;
            }

            Interlocked.Increment(ref _requestCount);
            LastAuthorization = ctx.Request.Headers["Authorization"];

            try
            {
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    var body = await sr.ReadToEndAsync();
                    if (body.Length > 0)
                    {
                        LastRequestRaw = body;    //capture the raw body before parsing, so byte checks see what arrived.
                        LastRequestBody = JsonDocument.Parse(body).RootElement.Clone();
                        _bodies.Enqueue(LastRequestBody.Value);
                    }
                }
            }
            catch
            {
                try
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes("{\"error\":\"fake server: unparseable request body\"}");
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { ctx.Response.Abort(); }   //when the client is already gone, drop the response and keep serving.
                continue;
            }

            if (!_queue.TryDequeue(out var r))
                r = new FakeResponse(Status: 500, Body: "{\"error\":\"fake server: nothing enqueued\"}");

            ctx.Response.StatusCode = r.Status;
            try
            {
                if (r.Status == 200 && r.Frames is not null)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    var written = 0;
                    var aborted = false;
                    foreach (var frame in r.Frames)
                    {
                        if (r.DropAfterFrames is int n && written >= n)
                        {
                            ctx.Response.Abort();
                            aborted = true;
                            break;
                        }
                        var bytes = Encoding.UTF8.GetBytes(frame);
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                        await ctx.Response.OutputStream.FlushAsync();
                        written++;
                    }
                    if (!aborted) ctx.Response.Close();
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(r.Body ?? "{}");
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            }
            catch { } //a departed client never stops the loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _listener.Close();
        try { await _serveTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}
