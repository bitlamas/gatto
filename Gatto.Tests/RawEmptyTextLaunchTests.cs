using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Home;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a raw role with no source composes the empty text, and the session keeps its system record while the wire carries none
[Collection("e2e")]
public sealed class RawEmptyTextLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-rawempty-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-rawempty-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

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

    private static async Task<string> Run(FakeOpenAiServer server, params string[] args)
    {
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"answer\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        var stderr = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(args));
        return stderr.ToString();
    }

    private static List<(string Role, string? Content)> Sent(FakeOpenAiServer server) =>
        server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString()!, m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null))
            .ToList();

    [Fact]
    public async Task AN_EMPTY_RAW_TEXT_SENDS_NO_SYSTEM_TURN_AND_RESUMES_WITH_THE_SAME_SHAPE()
    {
        await using var server = new FakeOpenAiServer();
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{\"model\":\"test-model\",\"raw\":true}");
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"), """{"files":[{"path":"m.gguf","active":true}],"port":1235,"context":8192}""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model","memory":{"enabled":false}}""");
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;

        //the first launch writes the shipped extensions, and a deletion sticks, so the next launch has no policy line to compose
        await Run(server, "-p", "warm");
        foreach (var f in Directory.GetFiles(Path.Combine(_home, "extensions"), "*.csx")) File.Delete(f);
        foreach (var f in Directory.GetFiles(_home, "*.jsonl", SearchOption.AllDirectories)) File.Delete(f);

        await Run(server, "-p", "first");
        Assert.Equal(new[] { ("user", (string?)"first") }, Sent(server));

        var session = Directory.GetFiles(_home, "*.jsonl", SearchOption.AllDirectories).Single(p => !p.EndsWith(".ledger.jsonl", StringComparison.Ordinal));
        var system = File.ReadLines(session).Select(l => JsonDocument.Parse(l).RootElement)
            .First(r => r.TryGetProperty("role", out var role) && role.GetString() is "system" or "user" or "assistant" or "tool");
        Assert.Equal("system", system.GetProperty("role").GetString());
        Assert.Equal("", system.GetProperty("content").GetString());
        Assert.NotNull(SessionStore.LoadBaseline(session));

        var err = await Run(server, "-p", "second", "--continue");
        var sent = Sent(server);
        Assert.DoesNotContain(sent, m => m.Role == "system");
        Assert.Equal(("user", (string?)"first"), sent[0]);
        Assert.Equal("user", sent[^1].Role);
        Assert.DoesNotContain("predates", err, StringComparison.Ordinal);
    }
}
