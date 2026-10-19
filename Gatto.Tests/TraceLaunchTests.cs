using System.Text.Json;
using Gatto.Cli;
using Gatto.Core;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a traced -p run end to end: the sidecar beside the session, the launch before the turn, the resume mark, and every field a number or a closed name
[Collection("e2e")]
public sealed class TraceLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-trace-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-trace-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    //the names a span may carry as a string value, and the role and model the run uses
    private static readonly HashSet<string> Closed =
    [
        "turn", "shape", "compact", "request", "tool", "tool.gate", "tool.exec", "shell.drain", "tool.result", "hook",
        "session.write", "resume", "launch",
        "read_file", "Completed", "Truncated", "Cancelled", "Error", "Proactive", "Overflow", "Manual",
        "message_end", "session_summary", "gatto", "generalist", "test-model",
    ];

    public TraceLaunchTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{\"model\":\"test-model\"}");
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

    private static string ReadCall() =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new object[]
            {
                new
                {
                    index = 0,
                    delta = new { tool_calls = new object[] { new { index = 0, id = "c1", function = new { name = "read_file", arguments = "{\"path\":\"hello.txt\"}" } } } },
                    finish_reason = (string?)null,
                },
            },
        }) + "\n\n";

    private static List<JsonElement> Lines(string file) =>
        File.ReadAllLines(file).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static string Newest(string dir, string glob) =>
        Directory.GetFiles(dir, glob).Where(f => glob != "*.jsonl" || !SessionFiles.IsSidecar(f)).OrderBy(f => f, StringComparer.Ordinal).Last();

    [Fact]
    public async Task A_traced_run_and_its_resume_write_beside_their_sessions()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-model"}""");
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        File.WriteAllText(Path.Combine(_cwd, "hello.txt"), "file content 5e2a");
        server.Enqueue(new FakeResponse(Frames: new[] { ReadCall(), Chunk("{}", finish: "tool_calls"), "data: [DONE]\n\n" }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"read it\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        server.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"again\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        var sessions = Path.Combine(_home, "sessions");

        using (GattoApp.TraceScope())
        {
            Assert.Equal(0, await GattoApp.RunAsync(["-p", "read hello.txt", "--yes"]));
            var first = Newest(sessions, "*.jsonl");
            var sidecar = SessionFiles.Sidecar(first, SessionFiles.Trace);
            var lines = Lines(sidecar);
            var header = lines[0];
            var system = Lines(first).First(l => l.TryGetProperty("role", out var r) && r.GetString() == "system");
            Assert.Equal(system.GetProperty("session_id").GetString(), header.GetProperty("session").GetString());
            var spans = lines.Skip(1).ToList();

            var launch = spans.Single(s => s.GetProperty("s").GetString() == "launch");
            foreach (var key in new[] { "home_us", "shipped_us", "context_files_us", "memory_us", "probe_us" })
                Assert.True(launch.GetProperty(key).GetInt64() >= 0, key);
            var turn = spans.Single(s => s.GetProperty("s").GetString() == "turn");
            //the headline number runs from launch to the turn, so the launch span ends before the turn begins
            Assert.True(launch.GetProperty("t").GetInt64() + launch.GetProperty("d").GetInt64() <= turn.GetProperty("t").GetInt64());
            Assert.Equal(0, launch.GetProperty("t").GetInt64());
            Assert.Equal("generalist", turn.GetProperty("role").GetString());
            Assert.Equal(2, spans.Count(s => s.GetProperty("s").GetString() == "request"));
            Assert.Contains(spans, s => s.GetProperty("s").GetString() == "session.write");
            Assert.Equal("read_file", spans.Single(s => s.GetProperty("s").GetString() == "tool").GetProperty("name").GetString());

            //no text the model wrote and no file content, only numbers and the closed names
            foreach (var span in spans)
                foreach (var field in span.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Number) continue;
                    Assert.Equal(JsonValueKind.String, field.Value.ValueKind);
                    Assert.Contains(field.Value.GetString()!, Closed);
                }
            Assert.DoesNotContain("5e2a", File.ReadAllText(sidecar));

            Assert.Equal(0, await GattoApp.RunAsync(["-p", "again", "--continue"]));
            var second = Newest(sessions, "*.jsonl");
            Assert.NotEqual(first, second);
            var resumed = Lines(SessionFiles.Sidecar(second, SessionFiles.Trace)).Skip(1).ToList();
            var resume = resumed.Single(s => s.GetProperty("s").GetString() == "resume");
            Assert.True(resume.GetProperty("records").GetInt32() >= 4);
            Assert.True(resume.GetProperty("est_tokens").GetInt32() > 0);
            var request = resumed.Single(s => s.GetProperty("s").GetString() == "request");
            Assert.Equal(1, request.GetProperty("after_resume").GetInt32());
        }
    }
}
