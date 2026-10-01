using System.Text.Json;
using Gatto.Cli;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//each launch reads its endpoint's own saved choice, through the real launch with a fake server behind it
[Collection("e2e")]
public class EndpointDefaultsLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-epd-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-epd-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;
    private readonly StringWriter _err = new();

    public EndpointDefaultsLaunchTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_home, "gatto.json"), json);

    private void WriteRole(string name, string json)
    {
        var dir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.json"), json);
    }

    private static FakeResponse Completion(string text) => new(Frames: new[]
    {
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}},\"finish_reason\":null}}]}}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static string SentModel(FakeOpenAiServer server) =>
        server.LastRequestBody!.Value.GetProperty("model").GetString()!;

    private string Listed(FakeOpenAiServer server, string models, string defaults) =>
        "{\"endpoints\":{\"cloudy\":{\"base_url\":\"" + server.BaseUrl + "\",\"models\":" + models + "}}," +
        "\"default_endpoint\":\"cloudy\",\"defaults\":" + defaults + "}";

    [Fact]
    public async Task A_configured_listed_endpoint_launches_on_its_saved_model()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(Listed(server, "[\"m1\",\"m2\"]", "{\"cloudy\":{\"model\":\"m2\"}}"));
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Equal("m2", SentModel(server));
    }

    [Fact]
    public async Task A_stale_saved_model_warns_once_and_takes_the_first()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(Listed(server, "[\"m1\"]", "{\"cloudy\":{\"model\":\"gone\"}}"));
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Equal("m1", SentModel(server));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(_err.ToString(), "'gone'"));
    }

    [Fact]
    public async Task A_recorded_model_wins_over_the_saved_one_on_continue()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(Listed(server, "[\"m1\",\"m2\"]", "{\"cloudy\":{\"model\":\"m2\"}}"));
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("first"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-m", "m1", "-p", "first question" }));

        server.Enqueue(Completion("second"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--continue", "-p", "second question" }));
        Assert.Equal("m1", SentModel(server));
    }

    [Fact]
    public async Task A_default_endpoint_nobody_contributes_refuses_with_exit_2()
    {
        WriteConfig("{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"acme\"}");
        WriteRole("generalist", "{}");

        Assert.Equal(2, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Contains("default_endpoint 'acme' is not a configured endpoint and no extension contributes it", _err.ToString());
    }

    [Fact]
    public async Task A_default_endpoint_an_extension_contributes_launches_on_its_saved_model()
    {
        await using var server = new FakeOpenAiServer();
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"),
            "Gatto.Endpoint(\"hosted\", \"" + server.BaseUrl + "\", models: new[] { \"h1\", \"h2\" });");
        WriteConfig("{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\",\"defaults\":{\"hosted\":{\"model\":\"h2\"}}}");
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Equal("h2", SentModel(server));
    }

    [Fact]
    public async Task A_local_launch_does_not_read_another_endpoints_legacy_default()
    {
        WriteConfig("{\"endpoints\":{\"local\":{},\"r\":{\"base_url\":\"https://r.test\"}},\"default_endpoint\":\"r\",\"default_model\":\"remote-model\"}");
        WriteRole("generalist", "{}");

        Assert.Equal(2, await GattoApp.RunAsync(new[] { "-e", "local", "-p", "hi" }));
        Assert.Contains("has no model for local endpoint", _err.ToString());
    }

    private string WithEffort(FakeOpenAiServer server, string effort) =>
        "{\"endpoints\":{\"cloudy\":{\"base_url\":\"" + server.BaseUrl + "\",\"models\":[\"m1\",\"m2\"]," +
        "\"thinking\":{\"none\":null,\"low\":{\"reasoning_effort\":\"low\"},\"high\":{\"reasoning_effort\":\"high\"}}}}," +
        "\"default_endpoint\":\"cloudy\",\"defaults\":{\"cloudy\":{\"effort\":" + effort + "}}}";

    private static string? SentEffort(JsonElement body) =>
        body.TryGetProperty("reasoning_effort", out var e) ? e.GetString() : null;

    [Fact]
    public async Task A_saved_cloud_effort_is_the_launch_effort()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(WithEffort(server, "{\"m1\":\"high\"}"));
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Equal("high", SentEffort(server.LastRequestBody!.Value));
    }

    [Fact]
    public async Task A_saved_effort_that_is_no_level_refuses_and_names_the_key()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(WithEffort(server, "{\"m1\":\"huge\"}"));
        WriteRole("generalist", "{}");

        Assert.Equal(2, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Contains("defaults.cloudy.effort.m1", _err.ToString());
    }

    [Fact]
    public async Task A_model_switch_takes_the_new_models_saved_effort()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(WithEffort(server, "{\"m1\":\"low\",\"m2\":\"high\"}"));
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));
        Console.SetIn(new StringReader("/model m2\nhi\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));
        var body = server.LastRequestBody!.Value;
        Assert.Equal("m2", body.GetProperty("model").GetString());
        Assert.Equal("high", SentEffort(body));
    }
}
