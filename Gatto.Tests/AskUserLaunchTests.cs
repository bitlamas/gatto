using System.Text.Json;
using Gatto.Cli;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//ask_user is offered exactly where the launch built a prompter, read off the wire of a real launch in the three faces a test can reach
[Collection("e2e")]
public class AskUserLaunchTests : IDisposable
{
    private const string PolicyLine = "When a choice is the user's, ask with ask_user instead of choosing.";
    private const string NoPrompter = "interactive input unavailable in non-interactive mode";

    //a user's script that asks whatever the host says, so the prompter behind Gatto.Ui shows in a face that offers no ask_user
    private const string AsksAnyway = """
        Gatto.Register("asks_anyway", "asks the user", "{\"type\":\"object\"}", async (args, ctx, ct) =>
        {
            var answers = await Gatto.Ui.AskAsync(new[] { new AskQuestion("Which db?", "DB", new[] { "sqlite", "postgres" }, false) }, ct);
            return new ToolResult(string.Join(",", answers[0].Selected));
        });
        """;

    private readonly string _home = Directory.CreateTempSubdirectory("gatto-asklaunch-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-asklaunch-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;

    public AskUserLaunchTests()
    {
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{\"model\":\"test-model\"}");
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            "{\"files\":[{\"path\":\"m.gguf\",\"active\":true}],\"port\":1235,\"context\":8192,\"auto_serve\":false}");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        //a loaded extension dll stays locked for the process, so cleanup must never fail a green test
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static string[] CallRound(string name, object args) =>
    [
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new object[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new object[]
                        {
                            new { index = 0, id = "c1", type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        }) + "\n\n",
        Chunk("{}", finish: "tool_calls"),
        "data: [DONE]\n\n",
    ];

    private static readonly string[] TextRound = [Chunk("{\"content\":\"ok\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n"];

    private static readonly object OneQuestion = new
    {
        questions = new[] { new { question = "Which db?", header = "DB", options = new[] { "sqlite", "postgres" } } },
    };

    private void Arm(FakeOpenAiServer server, bool asksAnyway)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model"}""");
        if (asksAnyway)
        {
            Directory.CreateDirectory(Path.Combine(_home, "extensions"));
            File.WriteAllText(Path.Combine(_home, "extensions", "zz_asks_anyway.csx"), AsksAnyway);
        }
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    private readonly StringWriter _out = new(), _err = new();

    //the launch's own words beside a wrong exit code, so a failure names its cause
    private void AssertExitZero(int exit) => Assert.True(exit == 0, $"exit {exit}\nstderr:\n{_err}\nstdout:\n{_out}");

    private static string SystemOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;

    private static List<string> ToolsOf(JsonElement body) =>
        [.. body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];

    private static string ToolResultOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool")
            .GetProperty("content").GetString()!;

    //a -p run has nobody to answer, so its requests carry neither the tool nor the line, and a script that asks anyway gets the same error as before
    [Fact]
    public async Task A_p_RUN_OFFERS_NO_ASK_USER_AND_A_SCRIPT_THAT_ASKS_GETS_THE_ERROR()
    {
        await using var server = new FakeOpenAiServer();
        Arm(server, asksAnyway: true);
        server.Enqueue(new FakeResponse(Frames: CallRound("asks_anyway", new { })));
        server.Enqueue(new FakeResponse(Frames: TextRound));

        var exit = await GattoApp.RunAsync(["--yes", "-p", "ask me something"]);

        AssertExitZero(exit);   //the turn completes, the loop never hangs on a missing prompter
        var first = server.RequestBodies[0];
        Assert.DoesNotContain("ask_user", ToolsOf(first));
        Assert.Contains("asks_anyway", ToolsOf(first));   //the script loaded, so the absence above is the guard and not a failed load
        Assert.DoesNotContain(PolicyLine, SystemOf(first), StringComparison.Ordinal);
        Assert.Equal(NoPrompter, ToolResultOf(server.LastRequestBody!.Value));
    }

    //the plain loop with stdin redirected has no prompter either, the same as -p
    [Fact]
    public async Task A_PLAIN_LOOP_ON_REDIRECTED_INPUT_OFFERS_NO_ASK_USER()
    {
        await using var server = new FakeOpenAiServer();
        Arm(server, asksAnyway: true);
        server.Enqueue(new FakeResponse(Frames: CallRound("asks_anyway", new { })));
        server.Enqueue(new FakeResponse(Frames: TextRound));
        Console.SetIn(new StringReader("ask me something\n/quit\n"));

        var exit = await GattoApp.RunAsync(["--yes"]);

        AssertExitZero(exit);
        var first = server.RequestBodies[0];
        Assert.DoesNotContain("ask_user", ToolsOf(first));
        Assert.Contains("asks_anyway", ToolsOf(first));
        Assert.DoesNotContain(PolicyLine, SystemOf(first), StringComparison.Ordinal);
        Assert.Equal(NoPrompter, ToolResultOf(server.LastRequestBody!.Value));
    }

    //with stdin attached the plain loop offers both, and the shipped tool reaches the prompter the launch built
    [Fact]
    public async Task A_PLAIN_LOOP_ON_ATTACHED_INPUT_OFFERS_ASK_USER_AND_ITS_ANSWER_ARRIVES()
    {
        await using var server = new FakeOpenAiServer();
        Arm(server, asksAnyway: false);
        server.Enqueue(new FakeResponse(Frames: CallRound("ask_user", OneQuestion)));
        server.Enqueue(new FakeResponse(Frames: TextRound));
        Console.SetIn(new StringReader("ask me something\n1\n/quit\n"));

        int exit;
        using (GattoApp.AttachedInputScope())
            exit = await GattoApp.RunAsync([]);

        AssertExitZero(exit);
        var first = server.RequestBodies[0];
        Assert.Contains("ask_user", ToolsOf(first));
        Assert.Contains(PolicyLine, SystemOf(first), StringComparison.Ordinal);
        Assert.Equal("""[{"header":"DB","selected":["sqlite"]}]""", ToolResultOf(server.LastRequestBody!.Value));
    }
}
