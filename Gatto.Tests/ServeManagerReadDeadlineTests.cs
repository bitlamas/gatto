using System.Reflection;
using Gatto.Roles;

namespace Gatto.Tests;

//the manager's client carries no timeout, so each health read holds a deadline of its own
public class ServeManagerReadDeadlineTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-serve-deadline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    //a handler that holds every request until its token fires, as a server that never answers would
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable, the delay ends only by its token");
        }
    }

    [Fact]
    public async Task A_HEALTH_READ_THAT_NEVER_ANSWERS_ENDS_AT_ITS_OWN_DEADLINE()
    {
        using var http = new HttpClient(new SilentHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"), spawn: null, lookup: _ => null,
            http, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));

        //an untimed client over a silent server, so only the read's own deadline can end this read
        var read = manager.AwaitReadyAsync(41999, TimeSpan.Zero, null, CancellationToken.None);
        var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(ReferenceEquals(read, first), "the health read never ended, so nothing bounds it but the client");
        Assert.Equal(ServerReadiness.StillLoading, await read);
    }

    [Fact]
    public void THE_DEFAULT_CLIENT_CARRIES_NO_TIMEOUT()
    {
        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"));
        var field = typeof(ServeManager).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var client = Assert.IsType<HttpClient>(field!.GetValue(manager));

        //a timeout on the client is a budget beneath every caller's token, and a linked token cannot lengthen it
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
