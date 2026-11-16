using System.Text.Json;
using Gatto.Cli;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the seat with no role is a record built in code, and gatto default and /role default reach it with the composition an empty role file gave
[Collection("e2e")]
public sealed class DefaultSeatLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-default-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-default-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;

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

    private static FakeResponse Answer() => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private void Home(FakeOpenAiServer server, string? extraConfig = null)
    {
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"), """{"files":[{"path":"m.gguf","active":true}],"port":1235,"context":8192,"auto_serve":false}""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model","memory":{"enabled":false}{{{extraConfig ?? ""}}}}""");
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
    }

    private static string SystemOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;

    //the session line names the session, so two launches are compared without it
    private static string WithoutSessionLine(string system) =>
        system[..system.LastIndexOf("\n\nThis is session ", StringComparison.Ordinal)];

    private static string ToolsOf(JsonElement body) => body.GetProperty("tools").GetRawText();

    private static async Task OneShot(FakeOpenAiServer server, params string[] args)
    {
        server.Enqueue(Answer());
        var stderr = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(stderr);
        Assert.True(await GattoApp.RunAsync(args) == 0, stderr.ToString());
    }

    [Fact]
    public async Task GATTO_DEFAULT_COMPOSES_WHAT_AN_EMPTY_ROLE_FILE_COMPOSES()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        File.WriteAllText(Path.Combine(_home, "roles", "empty.json"), "{}");

        await OneShot(server, "empty", "-p", "hi");
        var fromFile = server.LastRequestBody!.Value;
        await OneShot(server, "default", "-p", "hi");
        var builtIn = server.LastRequestBody!.Value;

        Assert.Equal(WithoutSessionLine(SystemOf(fromFile)), WithoutSessionLine(SystemOf(builtIn)));
        Assert.Equal(ToolsOf(fromFile), ToolsOf(builtIn));
    }

    [Fact]
    public async Task ROLE_DEFAULT_IN_A_SESSION_COMPOSES_WHAT_AN_EMPTY_ROLE_FILE_COMPOSES()
    {
        await using var server = new FakeOpenAiServer();
        Home(server, ""","default_role":"empty","glyphs":"unicode" """);
        File.WriteAllText(Path.Combine(_home, "roles", "empty.json"), "{}");
        server.Enqueue(Answer());
        server.Enqueue(Answer());
        Console.SetIn(new StringReader("one\n/role default\ntwo\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("role: default", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, server.RequestBodies.Count);
        Assert.Equal(SystemOf(server.RequestBodies[0]), SystemOf(server.RequestBodies[1]));
        Assert.Equal(ToolsOf(server.RequestBodies[0]), ToolsOf(server.RequestBodies[1]));
    }

    [Fact]
    public async Task GATTO_DEFAULT_RUNS_IN_A_HOME_WITH_NO_ROLES_FOLDER()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        Directory.Delete(Path.Combine(_home, "roles"), true);

        await OneShot(server, "default", "-p", "hi");

        Assert.Contains("This is session ", SystemOf(server.LastRequestBody!.Value), StringComparison.Ordinal);
    }

    //the shipped files land at the first launch, so the folder is removed after one and the bare launch must still run
    [Fact]
    public async Task A_BARE_LAUNCH_RUNS_IN_A_HOME_WITH_NO_ROLES_FOLDER_AND_RECORDS_THE_SEAT_AS_DEFAULT()
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        await OneShot(server, "-p", "warm");
        Directory.Delete(Path.Combine(_home, "roles"), true);
        foreach (var f in Directory.GetFiles(_home, "*.jsonl", SearchOption.AllDirectories)) File.Delete(f);

        await OneShot(server, "-p", "hi");

        var session = Directory.GetFiles(_home, "*.jsonl", SearchOption.AllDirectories).Single(p => !p.EndsWith(".ledger.jsonl", StringComparison.Ordinal));
        var assistant = File.ReadLines(session).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .First(r => r.TryGetProperty("role", out var role) && role.GetString() == "assistant");
        Assert.Equal("default", assistant.GetProperty("gatto_role").GetString());
    }

    //a file named default is not loaded, the launch says so once in the casing on disk, and help lists default once
    [Theory]
    [InlineData("default.json")]
    [InlineData("Default.json")]
    public async Task A_ROLE_FILE_NAMED_DEFAULT_IS_NOT_LOADED_AND_THE_LAUNCH_SAYS_SO_ONCE(string file)
    {
        await using var server = new FakeOpenAiServer();
        Home(server);
        File.WriteAllText(Path.Combine(_home, "roles", file), """{"append":"the shadow marker"}""");

        server.Enqueue(Answer());
        var stderr = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        var row = $"role 'default' is gatto with no role, so roles\\{file} is not loaded";
        Assert.Single(stderr.ToString().Split('\n'), l => l.Contains(row, StringComparison.Ordinal));
        Assert.DoesNotContain("the shadow marker", SystemOf(server.LastRequestBody!.Value), StringComparison.Ordinal);
        Assert.DoesNotContain("default | default", GattoApp.HelpFor(_home), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> PlainLoop(string script, params string[] args)
    {
        Console.SetIn(new StringReader(script));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(args));
        return stdout.ToString();
    }

    private static string BannerOf(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("gatto " + Gatto.Terminal.GlyphSet.Unicode.Dot + " ", StringComparison.Ordinal));

    //the starting seat goes unnamed on the banner, and /role with no argument still names it, the user asked
    [Fact]
    public async Task THE_NO_ROLE_SEAT_IS_UNNAMED_ON_THE_BANNER_AND_ROLE_NAMES_IT()
    {
        await using var server = new FakeOpenAiServer();
        Home(server, ""","glyphs":"unicode" """);

        var output = await PlainLoop("/role\n/role raw\n/role\n/role default\n/quit\n");

        Assert.StartsWith("gatto " + Gatto.Terminal.GlyphSet.Unicode.Dot + " test-model ", BannerOf(output), StringComparison.Ordinal);
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r').TrimStart('>', ' ')).Where(l => l.StartsWith("role: ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "role: default", "role: raw", "role: raw", "role: default" }, lines);
    }

    //with a default_role the bare launch starts in it unnamed, and gatto default is a seat the user picked, so it is named
    [Fact]
    public async Task UNDER_A_DEFAULT_ROLE_THE_BARE_BANNER_NAMES_NONE_AND_GATTO_DEFAULT_SHOWS_DEFAULT()
    {
        await using var server = new FakeOpenAiServer();
        Home(server, ""","glyphs":"unicode","default_role":"reviewer" """);
        File.WriteAllText(Path.Combine(_home, "roles", "reviewer.json"), "{}");

        var bare = BannerOf(await PlainLoop("/quit\n"));
        var picked = BannerOf(await PlainLoop("/quit\n", "default"));

        Assert.StartsWith("gatto " + Gatto.Terminal.GlyphSet.Unicode.Dot + " test-model ", bare, StringComparison.Ordinal);
        Assert.StartsWith("gatto " + Gatto.Terminal.GlyphSet.Unicode.Dot + " default " + Gatto.Terminal.GlyphSet.Unicode.Dot + " test-model ", picked, StringComparison.Ordinal);
    }

    //a user's own file named generalist is a role like any other, so the banner names it
    [Fact]
    public async Task A_USERS_OWN_GENERALIST_ROLE_IS_NAMED_ON_THE_BANNER()
    {
        await using var server = new FakeOpenAiServer();
        Home(server, ""","glyphs":"unicode" """);
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");

        var banner = BannerOf(await PlainLoop("/quit\n", "generalist"));

        Assert.StartsWith("gatto " + Gatto.Terminal.GlyphSet.Unicode.Dot + " generalist " + Gatto.Terminal.GlyphSet.Unicode.Dot + " ", banner, StringComparison.Ordinal);
    }
}
