using Gatto.Cli;
using Gatto.Roles;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a launch that finds its own server loading must wait for it and must not start a second. the launch probe reads a loading server as no server
public sealed class LaunchLoadingWaitTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-launchwait-").FullName;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_home, true); } catch (Exception) { } //the temp folder cleanup is best effort, so a delete failure is ignored.
    }

    //this fake matches the pid in serve.json, stays alive, and is named llama-server, so the record reads as ours
    private sealed class LiveProc : ServeManager.IServeProcess
    {
        public int Pid => 30660;
        public bool HasExited => false;
        public string ProcessName => "llama-server";
        public void Kill() => throw new Xunit.Sdk.XunitException("nothing may be stopped by the wait");
    }

    private ServeManager Manager(int? servedPort)
    {
        File.WriteAllText(Path.Combine(_home, "llama-server.exe"), "");
        if (servedPort is { } port)
            File.WriteAllText(Path.Combine(_home, "serve.json"),
                $$"""{"pid":30660,"model":"loading-model","port":{{port}},"started":"2026-09-13T00:00:00Z"}""");

        var proc = new LiveProc();
        return new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => throw new Xunit.Sdk.XunitException("nothing may be spawned by the wait"),
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(5), pollTimeout: TimeSpan.FromMilliseconds(30));
    }

    //health answers on the third try, so exactly 3 requests prove the wait polled and stopped. a refused-start count of 0 proves no second start was asked for.
    [Fact]
    public async Task A_SERVER_OF_OURS_STILL_LOADING_IS_WAITED_FOR_AND_NOT_STARTED_AGAIN()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 503, Body: "{}"));   //a 503 from /health means the model is still loading
        server.Enqueue(new FakeResponse(Status: 503, Body: "{}"));
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));
        var manager = Manager(port);
        var lines = new List<string>();

        var readiness = await AutoServeAsk.AwaitOursLoadingAsync(
            manager, port, TimeSpan.FromSeconds(10), Row(lines), _ => "~23.8 GB", CancellationToken.None);

        Assert.Equal(ServerReadiness.Ready, readiness);
        Assert.Equal(3, server.RequestCount);
        Assert.Equal(0, manager.RefusedStarts);
        //the one row, naming the model this home recorded and the size read from its id
        Assert.Contains(lines, l => l.Contains("loading loading-model…", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_SERVER_OF_OURS_ON_ANOTHER_PORT_IS_NOT_WAITED_FOR()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        var manager = Manager(servedPort: port + 1);   //the recorded port differs from the launch port, so the record is compared and no request may be sent.

        var readiness = await AutoServeAsk.AwaitOursLoadingAsync(manager, port, TimeSpan.FromSeconds(10),
            Silent(), null, CancellationToken.None);

        Assert.Null(readiness);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task NO_SERVER_OF_OURS_IS_NOT_WAITED_FOR()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        var manager = Manager(servedPort: null);

        var readiness = await AutoServeAsk.AwaitOursLoadingAsync(manager, port, TimeSpan.FromSeconds(10),
            Silent(), null, CancellationToken.None);

        Assert.Null(readiness);
        Assert.Equal(0, server.RequestCount);
    }

    //a row over a recording sink with no theme, so what it draws is the plain text a reader would see
    private static LoadingLine Row(List<string> drawn) =>
        new(rich: true, live: drawn.Add, note: drawn.Add, GlyphSet.Unicode, theme: null);

    //a row that must never be drawn (nothing is being waited for)
    private static LoadingLine Silent() =>
        new(rich: true, live: _ => throw new Xunit.Sdk.XunitException("no row when nothing is waited for"),
            note: _ => throw new Xunit.Sdk.XunitException("no row when nothing is waited for"),
            GlyphSet.Unicode, theme: null);
}
