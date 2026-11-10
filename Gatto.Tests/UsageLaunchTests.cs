using Gatto.Cli;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the usage reader through the real launch, an extension named hosted contributing the endpoint and counting its calls in a file
[Collection("e2e")]
public class UsageLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-usage-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-usage-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public UsageLaunchTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        CloudEndpoint.IsForTest = null;
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private string Marks => Path.Combine(_home, "marks.txt");

    private int Reads => File.Exists(Marks) ? File.ReadAllText(Marks).Length : 0;

    //the reader answers the weekly row with its call count as the percent, so a drawn row says which read it came from
    private void Hosted(FakeOpenAiServer server, bool usage = true)
    {
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        var marks = Marks.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"),
            "var calls = 0;\n" +
            "Gatto.Endpoint(\"hosted\", \"" + server.BaseUrl + "\", models: new[] { \"h1\" }" +
            (usage
                ? ", usage: ct => { var n = Interlocked.Increment(ref calls); System.IO.File.AppendAllText(\"" + marks + "\", \"u\"); " +
                  "return Task.FromResult<IReadOnlyList<Allowance>>(new[] { new Allowance(\"week\", AllowanceShape.Percent, UsedPercent: n) }); }"
                : "") +
            ");");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\"}");
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
    }

    private static FakeResponse Text(string text) => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static FakeResponse Call() => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"no_such_tool\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static async Task Settle(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50);
    }

    [Fact]
    public async Task Usage_re_reads_first_then_draws_and_a_turn_of_two_requests_reads_once()
    {
        //one read at launch, one after the turn whose tool call made it two requests, one at the command, and the command draws the third
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Call());
        server.Enqueue(Text("done"));
        Console.SetIn(new StringReader("hi\n/usage\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal(2, server.RequestBodies.Count(b => b.TryGetProperty("messages", out _)));
        await Settle(() => Reads >= 3);
        Assert.Equal(3, Reads);
        Assert.Contains("week 3% used", _out.ToString());
        Assert.Contains("read just now", _out.ToString());
    }

    [Fact]
    public async Task A_p_run_never_calls_the_usage_reader()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Text("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        await Task.Delay(200);
        Assert.Equal(0, Reads);
    }

    [Fact]
    public async Task Usage_on_an_endpoint_with_no_reader_answers_its_sentence()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, usage: false);
        Console.SetIn(new StringReader("/usage\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("endpoint hosted reports no allowance", _out.ToString());
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Usage_on_the_local_endpoint_answers_its_sentence_and_sends_nothing()
    {
        //the plain face of a session with no allowance seam, as a local launch builds it
        var client = new FakeChatClient();
        var loop = new Gatto.Core.Loop.AgentLoop(client, new Gatto.Core.Tools.ToolRegistry(), new Gatto.Core.Loop.HookBus(),
            new TestToolContext(_cwd), "m");
        var repl = new Gatto.Repl.Repl(loop, new Gatto.Core.Session.Conversation("sys"), "generalist", "m", null, client, null, 65_536, _cwd,
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"),
            noAllowanceLine: Gatto.Repl.Repl.LocalAllowance);
        Console.SetIn(new StringReader(string.Join(Environment.NewLine, "/usage", "/quit", "")));

        await repl.RunAsync(CancellationToken.None);

        Assert.Contains("local models have no allowance", _out.ToString());
        Assert.Empty(client.Requests);
    }
}
