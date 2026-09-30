using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Home;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//resuming by id with --continue goes back to the folder the session was saved in, so the tools and the saved file read that folder
[Collection("e2e")]
public sealed class ResumeFolderTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-folder-home-").FullName;
    private readonly string _a = Directory.CreateTempSubdirectory("gatto-folder-a-").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("gatto-folder-b-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public ResumeFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{\"model\":\"test-model\"}");
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"), "{\"files\":[{\"path\":\"m.gguf\",\"active\":true}],\"port\":1235,\"context\":8192}");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        foreach (var d in new[] { _home, _a, _b }) try { Directory.Delete(d, true); } catch { }
    }

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private void Config(FakeOpenAiServer server)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model"}""");
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
    }

    private static void Answer(FakeOpenAiServer server) =>
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"answer\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

    private static async Task<string> Run(params string[] args)
    {
        var err = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(err);
        Assert.Equal(0, await GattoApp.RunAsync(args));
        return err.ToString();
    }

    private string SavedInA(FakeOpenAiServer server)
    {
        Config(server);
        Environment.CurrentDirectory = _a;
        Answer(server);
        Run("-p", "first").GetAwaiter().GetResult();
        return Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_continue_by_id_from_another_folder_resumes_in_the_sessions_folder()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_a, "only-in-a.txt"), "MARKER ONLY IN A 3f9c\n");
        var id = SavedInA(server);
        Environment.CurrentDirectory = _b;
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            "data: " + JsonSerializer.Serialize(new { choices = new object[] { new { index = 0, delta = new { tool_calls = new object[] { new { index = 0, id = "c1", type = "function", function = new { name = "read_file", arguments = "{\"path\":\"only-in-a.txt\"}" } } } }, finish_reason = (string?)null } } }) + "\n\n",
            Chunk("{}", finish: "tool_calls"), "data: [DONE]\n\n",
        }));
        Answer(server);
        await Run("-p", "where", "--continue", id);
        var tool = server.RequestBodies[^1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("MARKER ONLY IN A 3f9c", tool.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(_a, Environment.CurrentDirectory);
        var newest = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).Last();
        Assert.Equal(_a, SessionStore.CwdOf(newest));
    }

    [Fact]
    public async Task A_continue_by_id_whose_folder_is_gone_stays_and_resets()
    {
        await using var server = new FakeOpenAiServer();
        var id = SavedInA(server);
        Environment.CurrentDirectory = _b;
        Directory.Delete(_a, true);
        Answer(server);
        var err = await Run("-p", "again", "--continue", id);
        Assert.Equal(_b, Environment.CurrentDirectory);
        Assert.Contains($"the session's folder {_a} no longer exists: staying in {_b}, the conversation is sent again in full", err, StringComparison.Ordinal);
        var system = server.RequestBodies[^1].GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains($"Working directory: {_b}.", system, StringComparison.Ordinal);
    }

    //an older record holds no folder, so the line says that and names no path
    [Fact]
    public async Task A_continue_by_id_of_a_record_with_no_folder_stays_and_says_so()
    {
        await using var server = new FakeOpenAiServer();
        Config(server);
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-session-7d209d82.jsonl"), Path.Combine(_home, "sessions", "nofolder-0001.jsonl"));
        Environment.CurrentDirectory = _b;
        Answer(server);
        var err = await Run("-p", "again", "--continue", "nofolder-0001");
        Assert.Equal(_b, Environment.CurrentDirectory);
        Assert.Contains($"this session does not record its folder: staying in {_b}, the conversation is sent again in full", err, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer exists", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_continue_moves_nothing()
    {
        await using var server = new FakeOpenAiServer();
        SavedInA(server);
        Answer(server);
        await Run("-p", "again", "--continue");
        Assert.Equal(_a, Environment.CurrentDirectory);
    }
}
