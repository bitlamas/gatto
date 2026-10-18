using Gatto.Cli;
using Gatto.Core.Loop.Permissions;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the real launch on the plain face, which a redirected console takes. it pins the GattoApp half the Repl facts cannot reach
[Collection("e2e")]
public class LaunchWarningsLaunchTests : IDisposable
{
    private const string Banner = "(/quit to exit, /new to reset, Ctrl+C aborts a turn)";

    private readonly string _home = Directory.CreateTempSubdirectory("gatto-lw-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-lw-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;

    public LaunchWarningsLaunchTests()
    {
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{\"model\":\"test-model\"}");
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            "{\"files\":[{\"path\":\"m.gguf\",\"active\":true}],\"port\":1235,\"context\":8192}");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        //a real launch loads the extension dll and locks it for the process, so cleanup must never fail a green test
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private async Task<(string Out, string Err)> LaunchPlain(FakeOpenAiServer server, string stdin = "/quit\n", string configExtra = "")
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"{{{configExtra}}}}""");
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Console.SetIn(new StringReader(stdin));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));
        return (stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task The_wild_notice_prints_once_after_the_banner()
    {
        await using var server = new FakeOpenAiServer();
        PermissionStore.Load(_home, _cwd, out _).SetWild(true, persist: true);

        var (stdout, _) = await LaunchPlain(server);

        //the glyphs follow the host, so the row is matched by its words and the marker and dot are left to the glyph set
        const string Notice = "wild mode: ON for this project, stored in your gatto home";
        var at = stdout.IndexOf(Notice, StringComparison.Ordinal);
        Assert.True(at > stdout.IndexOf(Banner, StringComparison.Ordinal) && stdout.IndexOf(Banner, StringComparison.Ordinal) >= 0, stdout);
        Assert.Equal(at, stdout.LastIndexOf(Notice, StringComparison.Ordinal));
        Assert.Matches(@"(?m)^\S wild mode: ON for this project, stored in your gatto home \S /safe to turn off\r?$", stdout);
    }

    [Fact]
    public async Task A_HOME_WIDE_WILD_LAUNCHES_WILD_AND_NEVER_SAYS_THE_NEXT_LAUNCH_IS_WILD_AGAIN()
    {
        await using var server = new FakeOpenAiServer();

        var (stdout, _) = await LaunchPlain(server, "/wild never\n/quit\n", ",\"wild\":true");

        Assert.Matches(@"(?m)^\S wild mode: ON, gatto.json sets ""wild"": true \S /safe to turn off\r?$", stdout);
        Assert.Contains("wild mode off for this session, gatto.json sets \"wild\": true, so it is on again next launch", stdout, StringComparison.Ordinal);
    }

    //the off notice under a project veto names the file and nothing to turn off, since wild is already off
    [Fact]
    public async Task THE_VETO_NOTICE_CARRIES_NO_WAY_OFF()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), "{\"wild\": false}");

        var (stdout, _) = await LaunchPlain(server, configExtra: ",\"wild\":true");

        Assert.Matches(@"(?m)^\S wild mode: off, .* sets ""wild"": false for this project\r?$", stdout);
        Assert.DoesNotContain("/safe to turn off", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WILD_NEVER_ON_A_GRANTED_FOLDER_SAYS_THE_GRANT_IS_REMOVED()
    {
        await using var server = new FakeOpenAiServer();
        PermissionStore.Load(_home, _cwd, out _).SetWild(true, persist: true);

        var (stdout, _) = await LaunchPlain(server, "/wild never\n/quit\n");

        Assert.Contains("persistence removed from your gatto home", stdout, StringComparison.Ordinal);
        Assert.False(PermissionStore.Load(_home, _cwd, out _).Wild);
    }

    //auto_serve false vetoes the start, and the launch still names the model and the command instead of opening silent
    [Fact]
    public async Task A_DECLINED_AUTO_SERVE_WITH_NOTHING_SERVING_SAYS_SO_AT_LAUNCH()
    {
        var server = new FakeOpenAiServer();
        await server.DisposeAsync();
        File.WriteAllText(Path.Combine(_home, "models", "test-model", "profile.json"),
            "{\"files\":[{\"path\":\"m.gguf\",\"active\":true}],\"port\":1235,\"context\":8192,\"auto_serve\":false}");

        var (stdout, stderr) = await LaunchPlain(server);

        Assert.Contains("nothing is serving test-model. gatto serve start test-model when you want it.", stdout + stderr, StringComparison.Ordinal);
    }

    //a server that answers /props is up, so there is nothing to hint
    [Fact]
    public async Task A_DECLINED_AUTO_SERVE_WITH_A_SERVER_ANSWERING_SAYS_NOTHING()
    {
        await using var server = new FakeOpenAiServer();
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "m.gguf"}""");
        File.WriteAllText(Path.Combine(_home, "models", "test-model", "profile.json"),
            "{\"files\":[{\"path\":\"m.gguf\",\"active\":true}],\"port\":1235,\"context\":8192,\"auto_serve\":false}");

        var (stdout, stderr) = await LaunchPlain(server);

        Assert.DoesNotContain("nothing is serving", stdout + stderr, StringComparison.Ordinal);
    }

    //stderr already shows the line on the plain face, so a second copy after the banner would only repeat it
    [Fact]
    public async Task A_sink_warning_stays_on_stderr_alone_on_the_plain_face()
    {
        await using var server = new FakeOpenAiServer();
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", "zz_broken.csx"), "throw new System.Exception(\"boom\");");

        var (stdout, stderr) = await LaunchPlain(server);

        Assert.Matches(@"! extension: .*zz_broken\.csx", stderr);
        Assert.DoesNotContain("zz_broken.csx", stdout, StringComparison.Ordinal);
    }
}
