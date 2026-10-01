using System.Net;
using Gatto.Cli;

namespace Gatto.Tests;

//gatto status: the endpoint, model and role defaults, the serve state and the session count for this folder. no test opens a network
public class StatusTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-status-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-status-cwd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));   //the health probe answers as if there is no serve.json
    }

    [Fact]
    public async Task NoConfig_SurfacesTheLoadFailureAndDoesNotThrow()
    {
        var output = new StringWriter();

        var exit = await Status.RunAsync(_home, _cwd, output, new HttpClient(new FakeHandler()), CancellationToken.None);

        Assert.Equal(0, exit);
        var text = output.ToString();
        //the command banner already names status, so this screen must not name it twice. a missing config is reported rather than thrown
        Assert.DoesNotContain("gatto — status", text);
        Assert.Contains("gatto.json:", text);   //the load failure shows on the screen
        Assert.Contains("sessions for this project: 0", text);
    }

    [Fact]
    public async Task A_default_endpoint_outside_gatto_json_says_an_extension_may_contribute_it()
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"local":{}},"default_endpoint":"acme"}
            """);
        var output = new StringWriter();

        await Status.RunAsync(_home, _cwd, output, new HttpClient(new FakeHandler()), CancellationToken.None);

        Assert.Contains("endpoint: acme (not in gatto.json, an extension may contribute it)", output.ToString());
    }

    [Fact]
    public async Task WithConfig_ShowsEndpointModelAndNotServing()
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local","default_model":"test-model"}
            """);
        var output = new StringWriter();

        var exit = await Status.RunAsync(_home, _cwd, output, new HttpClient(new FakeHandler()), CancellationToken.None);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("endpoint: local (http://127.0.0.1:1235)", text);
        Assert.Contains("default_model: test-model", text);
        Assert.Contains("role: generalist", text);
        Assert.Contains("not serving", text);   //with no serve.json the serve state reads as not serving
        Assert.Contains("sessions for this project: 0", text);
    }
}
