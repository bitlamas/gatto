using System.Text.Json;
using Gatto.Cli;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Cli;

//the prefix the check reads is the one a real launch sends, so the oracle is a launch against a fake server and never a second composer
[Collection("e2e")]
public class SessionPrefixTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-prefix-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-prefix-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public SessionPrefixTests()
    {
        //two roles, so run_agent is offered and its description lists them, and one model on the local endpoint
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{\"model\":\"test-model\"}");
        File.WriteAllText(Path.Combine(rolesDir, "reviewer.json"), "{\"model\":\"test-model\",\"description\":\"reads a diff\"}");
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
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    //one real -p launch in the fixture home, and the first request it sent
    private async Task<JsonElement> LaunchedRequest(FakeOpenAiServer server)
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model"}""");
        server.Enqueue(new FakeResponse(Frames: [Chunk("{\"content\":\"ok\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n"]));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(["-p", "hello"]));
        return Assert.Single(server.RequestBodies);
    }

    //every file and folder under the home with its size and write time, so a write anywhere shows, an empty new folder included
    private IReadOnlyList<string> Tree() =>
        [.. Directory.EnumerateFileSystemEntries(_home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => File.Exists(f)
                ? $"{f} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f).Ticks}"
                : $"{f}{Path.DirectorySeparatorChar} {Directory.GetLastWriteTimeUtc(f).Ticks}")];

    //the system text and every tool of the launch's request, in order, name, description and schema, from the launch's own steps
    [Fact]
    public async Task THE_PREFIX_IS_THE_SYSTEM_TEXT_AND_TOOLS_A_LAUNCH_IN_THIS_HOME_SENDS()
    {
        await using var server = new FakeOpenAiServer();
        var sent = await LaunchedRequest(server);

        var prefix = GattoApp.ComposeSessionPrefix(_home, "local", "test-model");

        Assert.NotNull(prefix);
        //the one allowed difference is the launch's session line, which a prefix that belongs to no session leaves out
        var launched = sent.GetProperty("messages")[0].GetProperty("content").GetString()!;
        var cut = launched.LastIndexOf("\n\nThis is session ", StringComparison.Ordinal);
        Assert.True(cut > 0, "the launch's prompt names no session");
        Assert.Equal(launched[..cut], prefix!.SystemText);
        Assert.DoesNotContain("This is session", prefix.SystemText, StringComparison.Ordinal);
        var tools = sent.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).ToList();
        Assert.Equal(tools.Select(t => t.GetProperty("name").GetString()), prefix.Tools.Select(t => t.Name));
        Assert.Equal(tools.Select(t => t.GetProperty("description").GetString()), prefix.Tools.Select(t => t.Description));
        Assert.Equal(tools.Select(t => t.GetProperty("parameters").GetRawText()), prefix.Tools.Select(t => t.ParametersSchema.GetRawText()));
        //the roles reach the prefix through run_agent's description, so a role file in the home is in it
        Assert.Contains(prefix.Tools, t => t.Name == "run_agent" && t.Description.Contains("reviewer: reads a diff", StringComparison.Ordinal));
    }

    //a project's own context file is in the launch's prefix and not in the check's, and composing writes nothing to the home
    [Fact]
    public async Task THE_PREFIX_LEAVES_OUT_THE_PROJECT_AND_WRITES_NOTHING_TO_THE_HOME()
    {
        const string marker = "PROJECT-CONTEXT-MARKER-7F3A";
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), $"# notes\n{marker}\n");
        await using var server = new FakeOpenAiServer();
        var sent = await LaunchedRequest(server);
        Assert.Contains(marker, sent.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);

        var before = Tree();
        var prefix = GattoApp.ComposeSessionPrefix(_home, "local", "test-model");

        Assert.NotNull(prefix);
        Assert.DoesNotContain(marker, prefix!.SystemText, StringComparison.Ordinal);
        Assert.Equal(before, Tree());
    }

    //a home whose launch would refuse has no prefix, and the check it feeds goes on without one
    [Fact]
    public void A_HOME_THAT_WOULD_NOT_LAUNCH_HAS_NO_PREFIX()
    {
        Environment.CurrentDirectory = _cwd;
        Assert.Null(GattoApp.ComposeSessionPrefix(_home, "local", "no-such-model"));
    }
}
