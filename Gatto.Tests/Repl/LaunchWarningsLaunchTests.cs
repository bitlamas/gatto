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

    private async Task<(string Out, string Err)> LaunchPlain(FakeOpenAiServer server)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Console.SetIn(new StringReader("/quit\n"));

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
        Assert.Matches(@"(?m)^\S wild mode: ON for this project, stored in your gatto home \S /wild to turn off\r?$", stdout);
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
