using System.Text;
using System.Text.Json;
using Gatto.Cli;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//these tests change Console, the current directory and GATTO_HOME, so they must never run in parallel
[CollectionDefinition("e2e", DisableParallelization = true)]
public sealed class E2ECollection { }

[Collection("e2e")]
public class EndToEndTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-e2e-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-e2e-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public EndToEndTests()
    {
        //seed a role and a model profile, a local endpoint needs both (the port is a placeholder, each test sets its own base url)
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
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        //deleting the home dir can throw (a loaded extension dll stays locked), and cleanup must never fail a test that passed
        TryDeleteDir(_home);
        TryDeleteDir(_cwd);
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, true); } catch { } //a locked cache dll may survive deletion, and that is tolerated.
    }

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    //build the sse frame with JsonSerializer, hand-escaped interpolation breaks down once the arguments nest
    private static string ToolCallChunk(string id, string name, object args) =>
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
                            new { index = 0, id, function = new { name, arguments = JsonSerializer.Serialize(args) } },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        }) + "\n\n";

    [Fact]
    public async Task Unconfigured_home_with_dash_p_exits_2_with_guidance_and_never_prompts()
    {
        //an unconfigured home with -p exits 2 with guidance instead of prompting, so this test seeds its own home
        var home = Directory.CreateTempSubdirectory("gatto-unconf-").FullName;
        Gatto.Core.Home.GattoHome.EnsureInitialized(home);
        var err = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("GATTO_HOME", home);
            Environment.CurrentDirectory = _cwd;
            Console.SetError(err);
            var exit = await GattoApp.RunAsync(new[] { "-p", "hello" });
            Assert.Equal(2, exit);
            Assert.Contains("default_model", err.ToString());   //the guidance must name the default_model setting as the fix
        }
        finally
        {
            Console.SetError(_origErr);
            Environment.SetEnvironmentVariable("GATTO_HOME", null);
            TryDeleteDir(home);
        }
    }

    //a missing home gets the config error and is never created, and the guidance must say run gatto setup
    [Fact]
    public async Task A_NEVER_SET_UP_HOME_WITH_DASH_P_SAYS_RUN_SETUP_AND_SCAFFOLDS_NOTHING()
    {
        var parent = Directory.CreateTempSubdirectory("gatto-nohome-").FullName;
        var home = Path.Combine(parent, ".gatto");
        var err = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("GATTO_HOME", home);
            Environment.CurrentDirectory = _cwd;
            Console.SetError(err);

            var exit = await GattoApp.RunAsync(new[] { "-p", "hello" });

            Assert.Equal(2, exit);
            Assert.Contains("gatto setup", err.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(home));
        }
        finally
        {
            Console.SetError(_origErr);
            Environment.SetEnvironmentVariable("GATTO_HOME", null);
            TryDeleteDir(parent);
        }
    }

    [Fact]
    public async Task One_shot_prompt_streams_answer_and_exits_zero()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"the answer is 42\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "what is the answer?" });

        Assert.Equal(0, exit);
        Assert.Contains("the answer is 42", stdout.ToString());
        Assert.Single(Directory.GetFiles(Path.Combine(_home, "sessions")));
    }

    [Fact]
    public async Task One_shot_with_tool_roundtrip_works_end_to_end()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        File.WriteAllText(Path.Combine(_cwd, "hello.txt"), "file content here");
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"c1\",\"function\":{{\"name\":\"read_file\",\"arguments\":\"{{\\\"path\\\":\\\"hello.txt\\\"}}\"}}}}]}},\"finish_reason\":null}}]}}\n\n",
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"the file says: file content here\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "read hello.txt" });

        Assert.Equal(0, exit);
        var body = server.LastRequestBody!.Value;                 //the body is the second request, sent after the tool result.
        var toolMsg = body.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("file content here", toolMsg.GetProperty("content").GetString());
        Assert.Contains("the file says", stdout.ToString());
    }

    [Fact]
    public async Task Continue_flag_reloads_prior_turn_into_the_next_request()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"first answer\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));
        var stdout1 = new StringWriter();
        Console.SetOut(stdout1);
        var exit1 = await GattoApp.RunAsync(new[] { "-p", "first question" });
        Assert.Equal(0, exit1);

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"second answer\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));
        var stdout2 = new StringWriter();
        Console.SetOut(stdout2);
        var exit2 = await GattoApp.RunAsync(new[] { "-p", "second question", "--continue" });

        Assert.Equal(0, exit2);
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        var firstUserIdx = messages.FindIndex(m =>
            m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == "first question");
        var firstAssistantIdx = messages.FindIndex(m =>
            m.GetProperty("role").GetString() == "assistant" && m.GetProperty("content").GetString() == "first answer");
        var secondUserIdx = messages.FindIndex(m =>
            m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == "second question");

        Assert.True(firstUserIdx >= 0, "first turn's user message missing from reloaded session");
        Assert.True(firstAssistantIdx >= 0, "first turn's assistant answer missing from reloaded session");
        Assert.True(secondUserIdx >= 0, "second turn's user message missing");
        Assert.True(firstUserIdx < firstAssistantIdx && firstAssistantIdx < secondUserIdx,
            "first turn's user+assistant messages must precede the second turn's user message");
    }

    [Fact]
    public async Task ContinueFlag_PrintsResumedSessionPath_ToStderr()
    {
        //a resumed run prints the resumed session's raw path on stderr (the json of -p only covers the file written at exit)
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"first answer\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n",
        }));
        var stdout1 = new StringWriter();
        Console.SetOut(stdout1);
        var exit1 = await GattoApp.RunAsync(new[] { "-p", "first question" });
        Assert.Equal(0, exit1);
        var firstSessionFile = Directory.GetFiles(Path.Combine(_home, "sessions")).Single();

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"second answer\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n",
        }));
        var stdout2 = new StringWriter();
        var stderr2 = new StringWriter();
        Console.SetOut(stdout2);
        Console.SetError(stderr2);
        var exit2 = await GattoApp.RunAsync(new[] { "-p", "second question", "--continue" });

        Assert.Equal(0, exit2);
        Assert.Contains($"gatto: session {Path.GetFullPath(firstSessionFile)}", stderr2.ToString());
        //under -p the stdout is a byte contract: the model's text and one closing newline, nothing else. the notice and the stop-reason object go to stderr
        Assert.Equal("second answer" + Environment.NewLine, stdout2.ToString());
        Assert.Contains("\"outcome\"", stderr2.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continue_after_compact_carries_the_summary_into_the_recomposed_system()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        //the seed session is compacted. the --continue run recomposes the system fresh, so the stored summary block must be appended again or the model loses its history
        var store = new Gatto.Core.Home.SessionStore(_home, _cwd);
        var seed = new Gatto.Core.Loop.Conversation(
            "BASE SYSTEM\n\n" + Gatto.Core.Loop.Compactor.BuildContext("THE OLD SUMMARY LINE", "old.jsonl", null));
        seed.AddUser("post-compact question");
        seed.AddAssistant("post-compact answer");
        store.Save(seed);

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n",
        }));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "new question", "--continue" });

        Assert.Equal(0, exit);
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        var system = messages.First(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
        Assert.Contains("THE OLD SUMMARY LINE", system, StringComparison.Ordinal);        //the compacted summary must survive into the new request.
        Assert.Contains("Previous session summary", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continue_after_compact_slices_the_LAST_marker_not_an_earlier_lookalike()
    {
        //the real block is always last, so slice from the last marker (an earlier match would pull its tail into the slice)
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        var store = new Gatto.Core.Home.SessionStore(_home, _cwd);
        var fakeEarlier = Gatto.Core.Loop.Compactor.ContextMarker + "FAKE)\n\nLOOKALIKE FROM A GATTO.MD\n\n";
        var seed = new Gatto.Core.Loop.Conversation(
            "BASE\n\n" + fakeEarlier + Gatto.Core.Loop.Compactor.BuildContext("REAL SUMMARY LINE", "real.jsonl", null));
        seed.AddUser("q");
        seed.AddAssistant("a");
        store.Save(seed);

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n",
        }));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "new", "--continue" });

        Assert.Equal(0, exit);
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        var system = messages.First(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
        Assert.Contains("REAL SUMMARY LINE", system, StringComparison.Ordinal);          //the real summary block must survive into the system text.
        Assert.DoesNotContain("LOOKALIKE FROM A GATTO.MD", system, StringComparison.Ordinal);   //the earlier fake marker's tail must not be swallowed into the system.
    }

    [Fact]
    public async Task ContinueWithId_ResumesASpecificSession_NotJustTheLatest()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        //the id form of --continue reaches past the latest session, which the bare flag would pick
        var store = new Gatto.Core.Home.SessionStore(_home, _cwd);
        var older = new Gatto.Core.Loop.Conversation("sys");
        older.AddUser("older question");
        older.AddAssistant("older answer");
        store.Save(older);
        var olderId = Path.GetFileNameWithoutExtension(store.CurrentPath!);

        store.StartNew();
        var newer = new Gatto.Core.Loop.Conversation("sys");
        newer.AddUser("newer question");
        newer.AddAssistant("newer answer");
        store.Save(newer);

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}"), Chunk("{}", finish: "stop"), "data: [DONE]\n\n",
        }));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "new question", "--continue", olderId });

        Assert.Equal(0, exit);
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(messages, m =>
            m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == "older question");
        Assert.DoesNotContain(messages, m =>
            m.GetProperty("role").GetString() == "user"
            && m.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            && c.GetString() == "newer question");
    }

    [Fact]
    public async Task ContinueWithUnknownId_ExitsTwoWithAFriendlyMessageNamingTheId()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","default_model":"test-m"}""");

        var stderr = new StringWriter();
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi", "--continue", "does-not-exist" });

        Assert.Equal(2, exit);
        Assert.Contains("does-not-exist", stderr.ToString());
    }

    [Fact]
    public async Task ContinueWithAmbiguousId_ExitsTwoNamingTheCount()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","default_model":"test-m"}""");

        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, "dupe111.jsonl"), "");
        File.WriteAllText(Path.Combine(sessionsDir, "dupe222.jsonl"), "");

        var stderr = new StringWriter();
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi", "--continue", "dupe" });

        Assert.Equal(2, exit);
        Assert.Contains("2", stderr.ToString());
    }

    [Fact]
    public async Task Dead_server_one_shot_exits_one_with_hint()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","default_model":"test-m"}""");

        var stderr = new StringWriter();
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(1, exit);
        Assert.Contains("gatto serve start", stderr.ToString());
        //a connection failure emits no outcome line, and the caller reads that absence as gatto failing
        Assert.DoesNotContain("\"outcome\"", stderr.ToString());
    }

    [Fact]
    public async Task OneShot_EmitsStructuredStopReasonJson_OnStderr_AfterSessionSave()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"the answer is 42\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "what is the answer?" });

        Assert.Equal(0, exit);
        var sessionFile = Directory.GetFiles(Path.Combine(_home, "sessions")).Single();

        var errLines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var jsonLine = errLines.Last().Trim();
        using var doc = JsonDocument.Parse(jsonLine);
        Assert.Equal("completed", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("rounds").GetInt32());
        Assert.Equal(Path.GetFullPath(sessionFile), doc.RootElement.GetProperty("session_path").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("last_error").ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());

        //stdout stays byte-pure for piping, the outcome json goes to stderr
        var output = stdout.ToString();
        Assert.Contains("the answer is 42", output);
        Assert.DoesNotContain("outcome", output);
    }

    [Fact]
    public async Task OneShot_FinishReasonLength_EmitsTruncatedLength_ButStillExitsZero()
    {
        //a length-cut answer still completes and exits zero, only the outcome string says truncated_length
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"half an answ\"}"),
            Chunk("{}", finish: "length"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "give me a long answer" });

        Assert.Equal(0, exit);
        var errLines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var jsonLine = errLines.Last().Trim();
        using var doc = JsonDocument.Parse(jsonLine);
        Assert.Equal("truncated_length", doc.RootElement.GetProperty("outcome").GetString());
    }

    //piped one-shot output stays byte-pure: no escape bytes, raw markdown, and the [tool] name row
    [Fact]
    public async Task OneShot_PipedOutput_HasZeroEscapeBytes()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"## Head\\n**bold** `code`\\n\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.DoesNotContain('\x1b', output);
        Assert.Contains("## Head", output);
        Assert.Contains("**bold** `code`", output);
    }

    [Fact]
    public async Task OneShot_ToolLine_KeepsV1Format()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"c1\",\"function\":{{\"name\":\"shell\",\"arguments\":\"{{\\\"command\\\":\\\"Write-Output hi\\\"}}\"}}}}]}},\"finish_reason\":null}}]}}\n\n",
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"done\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "run a shell command" });

        Assert.Equal(0, exit);
        var errOutput = stderr.ToString();
        Assert.Contains("[tool] shell", errOutput);
        Assert.DoesNotContain('\x1b', errOutput);
    }

    //with -p the tool fails fast instead of blocking on stdin, so the turn ends and the exit code stays zero

    [Fact]
    public async Task OneShot_AskUser_NoPrompter_FailsFastWithExactMessage_NeverBlocks()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallChunk("c1", "ask_user", new
            {
                questions = new[]
                {
                    new { question = "Which db?", header = "DB", options = new[] { "sqlite", "postgres" }, multi_select = false },
                },
            }),
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"acknowledged\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "ask me something" });   //no --yes and no answer supplied, so the non-interactive route runs

        Assert.Equal(0, exit);   //the turn completes with exit zero even with no prompter, the loop never hangs
        var body = server.LastRequestBody!.Value;                 //the second request includes the tool result.
        var toolMsg = body.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("interactive input unavailable in non-interactive mode", toolMsg.GetProperty("content").GetString());
    }

    [Fact]
    public async Task OneShot_MutatingToolWithoutYes_DeniesByDefault_NeverBlocks()
    {
        await using var server = new FakeOpenAiServer();
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallChunk("c1", "shell", new { command = "echo hi" }),
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"acknowledged\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "run a shell command" });   //no --yes given, so nothing grants the permission

        Assert.Equal(0, exit);   //the missing permission denies by default, and the turn completes instead of hanging.
        var body = server.LastRequestBody!.Value;
        var toolMsg = body.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal(
            "blocked: no interactive terminal to grant permission (use --yes or pre-grant in .gatto\\permissions.json)",
            toolMsg.GetProperty("content").GetString());
    }
}
