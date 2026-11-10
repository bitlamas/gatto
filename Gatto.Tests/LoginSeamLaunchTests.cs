using Gatto.Cli;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the login seam through the real launch, an extension named hosted whose secret is a file the test reads and whose every call leaves a mark
[Collection("e2e")]
public class LoginSeamLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-login-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-login-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly List<string> _opened = [];

    public LoginSeamLaunchTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(_out);
        Console.SetError(_err);
        Console.SetIn(new StringReader("/quit\n"));
        BrowserOpen.OpenOverride.Value = _opened.Add;
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        BrowserOpen.OpenOverride.Value = null;
        GattoApp.LoginCancelOverride.Value = null;
        CloudEndpoint.IsForTest = null;
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private string Esc(string path) => path.Replace("\\", "\\\\");

    private string Secret(string endpoint) => Path.Combine(_home, endpoint + ".secret");

    private string Marks => Path.Combine(_home, "marks.txt");

    private string MarksRead => File.Exists(Marks) ? File.ReadAllText(Marks) : "";

    //one endpoint whose login writes its secret and answers the label, unless the test hands another body for the login
    private string Endpoint(FakeOpenAiServer server, string name, string? loginBody = null, string catalogue = "") =>
        "Gatto.Endpoint(\"" + name + "\", \"" + server.BaseUrl + "\", models: new[] { \"h1\" }," +
        " login: async (screen, ct) => { System.IO.File.AppendAllText(\"" + Esc(Marks) + "\", \"L" + name + " \"); " +
        (loginBody ?? "screen.Say(\"open https://login.example.test/device and enter ABCD-1234\"); screen.Open(\"https://login.example.test/device\"); " +
            "System.IO.File.WriteAllText(\"" + Esc(Secret(name)) + "\", \"t\"); return \"someone@example.test\";") + " }," +
        " loggedIn: () => System.IO.File.Exists(\"" + Esc(Secret(name)) + "\")," +
        " logout: () => System.IO.File.Delete(\"" + Esc(Secret(name)) + "\")" + catalogue + ");\n";

    private void Hosted(FakeOpenAiServer server, string? loginBody = null, string catalogue = "", string defaultEndpoint = "hosted")
    {
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"), Endpoint(server, "hosted", loginBody, catalogue));
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"" + defaultEndpoint + "\"}");
    }

    private static FakeResponse Text(string text) => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    [Fact]
    public async Task A_launch_on_a_logged_out_endpoint_runs_the_login_and_prints_its_rows()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        var code = o.IndexOf("hosted: open https://login.example.test/device and enter ABCD-1234", StringComparison.Ordinal);
        var label = o.IndexOf("hosted: logged in as someone@example.test", StringComparison.Ordinal);
        Assert.True(code >= 0 && label > code, o);
        Assert.Equal(["https://login.example.test/device"], _opened);
        Assert.True(File.Exists(Secret("hosted")));
    }

    [Fact]
    public async Task A_p_run_on_a_logged_out_endpoint_refuses_with_exit_2_and_the_line()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);

        Assert.Equal(2, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Contains("endpoint hosted: not logged in; run gatto -e hosted and log in once", _err.ToString());
        Assert.Equal("", MarksRead);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_logged_in_endpoint_runs_no_login()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        File.WriteAllText(Secret("hosted"), "t");
        server.Enqueue(Text("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Equal("", MarksRead);
    }

    [Fact]
    public async Task A_login_that_throws_refuses_with_its_message()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, loginBody: "throw new InvalidOperationException(\"the device code expired\");");

        Assert.Equal(2, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("hosted: the device code expired", _out.ToString());
    }

    [Fact]
    public async Task An_empty_label_refuses_naming_the_extension()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, loginBody: "return \" \";");

        Assert.Equal(2, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("endpoint hosted: its extension returned no account label", _out.ToString());
    }

    [Fact]
    public async Task Esc_ends_the_wait_whatever_the_script_does()
    {
        //a login that blocks its thread and ignores the token, ended by the launch's own cancel as Esc ends it
        await using var server = new FakeOpenAiServer();
        Hosted(server, loginBody: "System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite); return \"never\";");
        using var cancel = new CancellationTokenSource();
        GattoApp.LoginCancelOverride.Value = cancel;
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));

        var run = GattoApp.RunAsync(Array.Empty<string>());
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));

        Assert.Equal(2, await run);
        Assert.Contains("hosted: login cancelled", _out.ToString());
    }

    [Fact]
    public async Task A_bare_prefix_run_neither_logs_in_nor_reads_the_catalogue()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: ", catalogue: ct => { System.IO.File.AppendAllText(\"" + Esc(Marks) + "\", \"C \"); " +
            "return Task.FromResult(new ModelCatalogue(new[] { new ModelEntry(\"h1\") })); }");

        var prefix = GattoApp.ComposeSessionPrefix(_home, "hosted", null);

        Assert.NotNull(prefix);
        Assert.Equal("", MarksRead);
        Assert.False(File.Exists(Secret("hosted")));
    }

    [Fact]
    public async Task The_login_rows_print_before_the_cloud_notice()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        CloudEndpoint.IsForTest = ep => ep.BaseUrl == server.BaseUrl;

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        var label = o.IndexOf("hosted: logged in as", StringComparison.Ordinal);
        var notice = o.IndexOf("is a cloud model", StringComparison.Ordinal);
        Assert.True(label >= 0 && notice > label, o);
    }

    [Fact]
    public async Task The_endpoint_name_the_login_uses_is_the_one_the_role_resolves()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"), Endpoint(server, "hosted") + Endpoint(server, "acme"));
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\"}");
        File.WriteAllText(Path.Combine(_home, "roles", "writer.json"), "{\"endpoint\":\"acme\"}");

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "writer" }));

        Assert.Equal("Lacme ", MarksRead);
    }

    [Fact]
    public async Task A_broken_picked_role_falls_back_and_the_login_follows_the_fallback()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"), Endpoint(server, "hosted") + Endpoint(server, "acme"));
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\"}");
        File.WriteAllText(Path.Combine(_home, "roles", "writer.json"), "{\"endpoint\":\"acme\",");

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "writer" }));

        Assert.Equal("Lhosted ", MarksRead);
    }

    [Fact]
    public async Task Login_and_logout_in_a_session_run_the_handshake_again_and_forget_the_secret()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        File.WriteAllText(Secret("hosted"), "t");
        Console.SetIn(new StringReader("/login\n/logout\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        Assert.Equal("Lhosted ", MarksRead);
        Assert.Contains("logged in as someone@example.test", o);
        Assert.Contains("logged out of hosted", o);
        Assert.False(File.Exists(Secret("hosted")));
    }

    private string Dead => Path.Combine(_home, "dead.flag");

    //a list read that finds the flag drops the secret and throws, as an extension does on a refresh the provider refused, and lists h2 first otherwise
    private string DroppingCatalogue(bool once = true) =>
        ", catalogue: ct => { System.IO.File.AppendAllText(\"" + Esc(Marks) + "\", \"C \"); " +
        "if (System.IO.File.Exists(\"" + Esc(Dead) + "\")) { " + (once ? "System.IO.File.Delete(\"" + Esc(Dead) + "\"); " : "") +
        "System.IO.File.Delete(\"" + Esc(Secret("hosted")) + "\"); throw new InvalidOperationException(\"the login expired\"); } " +
        "return Task.FromResult(new ModelCatalogue(new[] { new ModelEntry(\"h2\"), new ModelEntry(\"h1\") })); }";

    private static string Model(FakeOpenAiServer server) =>
        server.RequestBodies.Last(b => b.TryGetProperty("messages", out _)).GetProperty("model").GetString()!;

    [Fact]
    public async Task A_read_that_logs_the_endpoint_out_runs_the_login_and_reads_again()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: DroppingCatalogue());
        File.WriteAllText(Secret("hosted"), "t");
        File.WriteAllText(Dead, "");
        server.Enqueue(Text("ok"));
        Console.SetIn(new StringReader("hi\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        Assert.Equal("C Lhosted C ", MarksRead);
        var first = o.IndexOf("hosted: reading the model list", StringComparison.Ordinal);
        var label = o.IndexOf("hosted: logged in as someone@example.test", StringComparison.Ordinal);
        var second = o.IndexOf("hosted: reading the model list", label + 1, StringComparison.Ordinal);
        Assert.True(first >= 0 && label > first && second > label, o);
        Assert.DoesNotContain("could not be read", o + _err);
        Assert.Equal("h2", Model(server));
    }

    [Fact]
    public async Task A_launch_that_already_logged_in_runs_no_second_login_when_the_read_logs_out()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: DroppingCatalogue());
        File.WriteAllText(Dead, "");

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal("Lhosted C ", MarksRead);
        Assert.Contains("hosted: the model list could not be read (the login expired); using the list the extension declares", _out.ToString() + _err);
    }

    [Fact]
    public async Task A_p_run_whose_read_logs_out_refuses_with_the_login_line()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: DroppingCatalogue());
        File.WriteAllText(Secret("hosted"), "t");
        File.WriteAllText(Dead, "");

        Assert.Equal(2, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Contains("endpoint hosted: not logged in; run gatto -e hosted and log in once", _err.ToString());
        Assert.Equal("C ", MarksRead);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_read_that_fails_while_still_logged_in_keeps_the_declared_list_and_runs_no_login()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: ", catalogue: ct => { System.IO.File.AppendAllText(\"" + Esc(Marks) + "\", \"C \"); " +
            "throw new InvalidOperationException(\"the list did not answer\"); }");
        File.WriteAllText(Secret("hosted"), "t");

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal("C ", MarksRead);
        Assert.Contains("hosted: the model list could not be read (the list did not answer); using the list the extension declares", _out.ToString() + _err);
    }

    [Fact]
    public async Task A_second_logged_out_read_falls_back_and_does_not_loop()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: DroppingCatalogue(once: false));
        File.WriteAllText(Secret("hosted"), "t");
        File.WriteAllText(Dead, "");

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal("C Lhosted C ", MarksRead);
        Assert.Contains("hosted: the model list could not be read (the login expired); using the list the extension declares", _out.ToString() + _err);
    }

    [Fact]
    public async Task Login_on_an_endpoint_without_the_seam_answers_its_sentence()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"),
            "Gatto.Endpoint(\"hosted\", \"" + server.BaseUrl + "\", models: new[] { \"h1\" });");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\"}");
        Console.SetIn(new StringReader("/login\n/logout\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        Assert.Equal(2, o.Split("endpoint hosted has no login").Length - 1);
        Assert.Equal(0, server.RequestCount);
    }
}
