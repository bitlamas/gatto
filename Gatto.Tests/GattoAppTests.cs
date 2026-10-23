using Gatto.Cli.Update;
using Gatto.Roles.Models;
using Gatto.Roles.Serve;
using Gatto.Tests.Setup;
using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop.Permissions;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//this class changes process globals, so it shares the e2e collection and must never run in parallel with another test
[Collection("e2e")]
public class GattoAppTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-app-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-app-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        Environment.SetEnvironmentVariable("GATTO_DEBUG", null);
        Environment.SetEnvironmentVariable("GATTO_ENDPOINT", null);
        Environment.SetEnvironmentVariable("GATTO_MODEL", null);
        Environment.SetEnvironmentVariable("GATTO_EFFORT", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    //naming sessions as a file makes EnsureInitialized throw, so the error reaches the top-level backstop. a home that is a file fails config resolution first
    private static async Task<string> CaptureBackstopStderr()
    {
        var home = Directory.CreateTempSubdirectory("gatto-backstop-").FullName;
        File.WriteAllText(Path.Combine(home, "gatto.json"), """
            { "endpoints": { "local": { "base_url": "http://127.0.0.1:1235" } },
              "default_endpoint": "local" }
            """);
        File.WriteAllText(Path.Combine(home, "sessions"), "not a directory");

        Environment.SetEnvironmentVariable("GATTO_HOME", home);
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });
            Assert.Equal(1, exit);
            return stderr.ToString();
        }
        finally
        {
            try { Directory.Delete(home, true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task Backstop_WithoutGattoDebug_PrintsMessageOnly()
    {
        var output = await CaptureBackstopStderr();
        Assert.DoesNotContain("   at ", output);
    }

    [Fact]
    public async Task Backstop_GattoDebug_PrintsStackTrace()
    {
        Environment.SetEnvironmentVariable("GATTO_DEBUG", "1");
        try { Assert.Contains("   at ", await CaptureBackstopStderr()); }
        finally { Environment.SetEnvironmentVariable("GATTO_DEBUG", null); }
    }

    private void UseHome()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_home, "gatto.json"), json);

    //the extra text is a raw json fragment pasted into the profile object, so this helper supplies the comma
    private void WriteModel(string id, int port, string? profileExtra = null)
    {
        var dir = Path.Combine(_home, "models", id);
        Directory.CreateDirectory(dir);
        var extra = profileExtra is null ? "" : "," + profileExtra;
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{"files": [{ "path": "m.gguf", "active": true }],"port":{{port}},"context":8192{{extra}}}""");
    }

    private void WriteRole(string name, string json)
    {
        var dir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.json"), json);
    }

    private static FakeResponse Completion(string text) => new(Frames: new[]
    {
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}},\"finish_reason\":null}}]}}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    //the real thinking-map shape, three on-levels and no high entry. every test below shares this literal so the shape cannot drift between them
    private const string Qwen38ThinkingMap =
        "\"thinking\":{" +
        "\"none\":{\"chat_template_kwargs\":{\"enable_thinking\":false}}," +
        "\"low\":{\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"low\"}}," +
        "\"medium\":{\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"medium\"}}," +
        "\"xhigh\":{\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"xhigh\"}}" +
        "}";

    //the real template names both switches, so a sniff that stops at enable_thinking answers Toggle for a genuine multi-level map
    private const string BothSwitchesTemplate = "enable_thinking ... reasoning_effort";

    //a role carries no sampling: the one it still holds is dropped on the path that used to send it, and the thinking body still arrives
    [Fact]
    public async Task Launch_RoleSamplingIsNotSent_TheThinkingBodyStillIs()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235,
            profileExtra: "\"thinking\":{\"medium\":{\"chat_template_kwargs\":{\"enable_thinking\":true}}}");
        WriteRole("generalist", "{\"model\":\"test-model\",\"sampling\":{\"temperature\":0.3}}");
        server.Enqueue(Completion("ok"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        var body = server.LastRequestBody!.Value;
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.True(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    private static readonly string[] SeatSix = { "read_file", "write_file", "edit_file", "glob", "grep", "shell" };

    private async Task<FakeOpenAiServer> OneShotWithGeneralist(string roleJson)
    {
        var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", roleJson);
        server.Enqueue(Completion("ok"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        return server;
    }

    [Fact]
    public async Task A_SEAT_WITH_AN_EMPTY_TOOLS_ARRAY_ARMS_NONE_OF_THE_SIX()
    {
        await using var server = await OneShotWithGeneralist("""{"model":"test-model","tools":[]}""");

        var names = ToolNamesOf(server);
        Assert.Empty(names.Intersect(SeatSix));
        Assert.Contains("run_agent", names);
    }

    [Fact]
    public async Task A_SEAT_WITH_THREE_TOOLS_ARMS_THOSE_THREE()
    {
        await using var server = await OneShotWithGeneralist("""{"model":"test-model","tools":["read_file","glob","grep"]}""");

        Assert.Equal(new[] { "glob", "grep", "read_file" }, ToolNamesOf(server).Intersect(SeatSix).Order());
    }

    //the plain /tools rows open with two spaces and the tool's name, so the names are read off the rows and never off a description
    private static IReadOnlyList<string> ListedTools(string section) =>
        section.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))
            .Select(l => l.TrimStart().Split(' ')[0]).ToList();

    private async Task<string> SwitchInThePlainLoop(string script)
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 65501);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("narrow", "{\"model\":\"test-model\",\"tools\":[\"grep\"]}");

        Console.SetIn(new StringReader(script));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port, so the probes give up at once
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));
        return stdout.ToString();
    }

    [Fact]
    public async Task A_ROLE_SWITCH_REARMS_THE_SEATS_TOOLS()
    {
        var output = await SwitchInThePlainLoop("/tools\n/role narrow\n/tools\n/quit\n");

        var cut = output.IndexOf("role: narrow", StringComparison.Ordinal);
        Assert.True(cut > 0, output);
        Assert.Contains("shell", ListedTools(output[..cut]));
        var after = ListedTools(output[cut..]);
        Assert.Contains("grep", after);
        Assert.Empty(after.Intersect(new[] { "shell", "read_file", "write_file", "edit_file", "glob" }));
    }

    [Fact]
    public async Task A_ROLE_SWITCH_BACK_REARMS_THE_SIX()
    {
        var output = await SwitchInThePlainLoop("/role narrow\n/role generalist\n/tools\n/quit\n");

        var cut = output.IndexOf("role: generalist", StringComparison.Ordinal);
        Assert.True(cut > 0, output);
        Assert.Equal(SeatSix.Order(), ListedTools(output[cut..]).Intersect(SeatSix).Order());
    }

    //the listing cannot hold the order, so the two requests' manifests are compared as text
    [Fact]
    public async Task A_ROLE_SWITCH_BACK_RESTORES_THE_LAUNCH_MANIFEST_BYTE_FOR_BYTE()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("narrow", "{\"model\":\"test-model\",\"tools\":[\"grep\"]}");
        server.Enqueue(Completion("one"));
        server.Enqueue(Completion("two"));
        Console.SetIn(new StringReader("first\n/role narrow\n/role generalist\nsecond\n/quit\n"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var bodies = server.RequestBodies;
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0].GetProperty("tools").GetRawText(), bodies[1].GetProperty("tools").GetRawText());
    }

    [Fact]
    public async Task A_COMMITTED_SWITCH_LEAVES_EVERY_OTHER_TOOL_IN_PLACE()
    {
        var output = await SwitchInThePlainLoop("/tools\n/role narrow\n/tools\n/quit\n");

        var cut = output.IndexOf("role: narrow", StringComparison.Ordinal);
        Assert.True(cut > 0, output);
        var before = ListedTools(output[..cut]);
        var after = ListedTools(output[cut..]);
        foreach (var name in new[] { "run_agent", "ask_user", "recall_memory" })
        {
            Assert.Contains(name, before);
            Assert.Contains(name, after);
        }
    }

    [Fact]
    public async Task A_REFUSED_SWITCH_LEAVES_THE_SEATS_TOOLS_WHERE_THEY_WERE()
    {
        WriteModel("other-model", port: 65501);
        WriteRole("other", "{\"model\":\"other-model\",\"tools\":[\"grep\"]}");

        var output = await SwitchInThePlainLoop("/role other\n/tools\n/quit\n");

        Assert.Contains("uses model 'other-model'", output, StringComparison.Ordinal);
        Assert.Equal(SeatSix.Order(), ListedTools(output).Intersect(SeatSix).Order());
    }

    //a project cannot arm its own tools when someone opens it. the same script in the home does load, so the refusal is not a script that never compiled
    [Fact]
    public async Task Extensions_in_the_project_folder_are_not_loaded()
    {
        const string probe = "Gatto.Register(\"probe\", \"a project tool\", \"{\\\"type\\\":\\\"object\\\"}\", async (args, ctx, ct) => new ToolResult(\"armed\"));\n";
        foreach (var dir in new[] { Path.Combine(_cwd, "extensions"), Path.Combine(_cwd, ".gatto", "extensions") })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "probe.csx"), probe);
        }

        var fromProject = ListedTools(await SwitchInThePlainLoop("/tools\n/quit\n"));
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", "probe.csx"), probe);
        var fromHome = ListedTools(await SwitchInThePlainLoop("/tools\n/quit\n"));

        Assert.DoesNotContain("probe", fromProject);
        Assert.Contains("probe", fromHome);
    }

    [Fact]
    public async Task THE_LAUNCH_OPENS_WITH_THE_ROLES_FIRST_MESSAGE_IN_THE_PLAIN_LOOP()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", """{"model":"test-model","first_message":"Hello."}""");

        Console.SetIn(new StringReader("/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var output = stdout.ToString();
        var banner = output.IndexOf("gatto " + Gatto.Terminal.GlyphSet.Unicode.Dot + " generalist", StringComparison.Ordinal);
        Assert.True(banner >= 0, output);
        Assert.Contains("Hello.", output[banner..], StringComparison.Ordinal);
        Assert.Empty(server.RequestBodies);
    }

    private static IReadOnlyList<(string Role, string? Content)> MessagesOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString()!,
                m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null))
            .ToList();

    [Fact]
    public async Task A_ONE_SHOT_CARRIES_THE_FIRST_MESSAGE_AS_HISTORY()
    {
        await using var server = await OneShotWithGeneralist("""{"model":"test-model","first_message":"Hello."}""");

        var messages = MessagesOf(server.LastRequestBody!.Value);
        Assert.Equal(["system", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("Hello.", messages[1].Content);
    }

    [Fact]
    public async Task A_RESUMED_SESSION_OPENS_NO_FIRST_MESSAGE()
    {
        await using var server = await OneShotWithGeneralist("""{"model":"test-model","first_message":"Hello."}""");
        server.Enqueue(Completion("again ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "again", "--continue" }));

        var assistants = MessagesOf(server.LastRequestBody!.Value)
            .Select((m, i) => (m.Role, m.Content, Index: i)).Where(m => m.Role == "assistant" && m.Content == "Hello.").ToList();
        Assert.Single(assistants);
        Assert.Equal(1, assistants[0].Index);
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private async Task<string> PlainLoopWithOpening(string generalistJson, string script, bool withServer = false)
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", generalistJson);
        WriteRole("sera", """{"model":"test-model","first_message":"I am Sera."}""");
        if (withServer) server.Enqueue(Completion("ok"));
        Console.SetIn(new StringReader(script));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));
        return stdout.ToString();
    }

    //the plain /new seeds, stamps and paints, so the opening is on screen below the line and every assistant record names the role
    [Fact]
    public async Task THE_PLAIN_LOOP_OPENS_AGAIN_AFTER_NEW_AND_AFTER_A_ROLE_SWITCH()
    {
        var output = await PlainLoopWithOpening("""{"model":"test-model","first_message":"Hello."}""", "/new\nhi\n/quit\n", withServer: true);

        var line = output.IndexOf(Gatto.Repl.Repl.NewConversationLine, StringComparison.Ordinal);
        Assert.True(line >= 0, output);
        Assert.Contains("Hello.", output[line..], StringComparison.Ordinal);

        var assistants = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl")
            .Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal))
            .SelectMany(File.ReadLines)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("role").GetString() == "assistant").ToList();
        Assert.True(assistants.Count >= 2, "the opening and the reply");
        Assert.All(assistants, e => Assert.Equal("generalist", e.TryGetProperty("gatto_role", out var r) ? r.GetString() : null));
    }

    [Fact]
    public async Task THE_LAUNCH_SWITCH_HANDS_THE_ROLES_FIRST_MESSAGE_TO_THE_REPL()
    {
        var output = await PlainLoopWithOpening("""{"model":"test-model"}""", "/role sera\n/new\n/quit\n");

        var switched = output.IndexOf("role: sera", StringComparison.Ordinal);
        Assert.True(switched >= 0, output);
        Assert.Equal(2, CountOf(output[switched..], "I am Sera."));
    }

    //sampling belongs to the model, so the profile's values go into the request, not only into a spawned server's argv
    [Fact]
    public async Task Launch_ProfileSampling_ReachesTheRequestBody()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235, profileExtra: "\"sampling\":{\"temperature\":0.6}");
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal(0.6, server.LastRequestBody!.Value.GetProperty("temperature").GetDouble(), 3);
    }

    //a local endpoint with no base_url talks to the port serve starts the model on, which is how a fresh home reads
    [Fact]
    public async Task Launch_LocalWithoutBaseUrl_ReachesTheModelsOwnPort()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: new Uri(server.BaseUrl).Port);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.NotNull(server.LastRequestBody);
    }

    [Theory]
    [InlineData(false, true, true, false, "wild mode: ON, gatto.json sets \"wild\": true")]
    [InlineData(true, false, true, false, "wild mode: ON for this project, stored in your gatto home")]
    [InlineData(true, true, true, false, "wild mode: ON for this project, stored in your gatto home")]
    [InlineData(false, true, false, true, "wild mode: off, F sets \"wild\": false for this project")]
    [InlineData(false, false, false, false, null)]
    public void THE_WILD_NOTICE_NAMES_THE_SOURCE_THAT_TURNED_IT_ON(bool grant, bool fromHome, bool on, bool forbidden, string? expected)
    {
        var permissions = PermissionStore.Load(_home, _cwd, out _);
        if (grant) permissions.SetWild(true, persist: true);
        var wild = new WildState { On = on, ForbiddenBy = forbidden ? "F" : null };
        Assert.Equal(expected, GattoApp.WildNotice(permissions, wild, fromHome));
    }

    [Fact]
    public async Task A_BARE_LAUNCH_RUNS_THE_DEFAULT_ROLE_OF_GATTO_JSON()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_role":"coder"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("coder", "{\"model\":\"test-model\",\"append\":\"the coder marker\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("the coder marker", server.LastRequestBody!.Value.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_TYPED_ROLE_BEATS_THE_DEFAULT_ROLE_OF_GATTO_JSON()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_role":"coder"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("coder", "{\"model\":\"test-model\",\"append\":\"the coder marker\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "generalist", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.DoesNotContain("the coder marker", server.LastRequestBody!.Value.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_DEFAULT_ROLE_WITH_NO_FILE_EXITS_2_AND_LISTS_THE_ROLES()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_role":"ghost"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");

        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(2, exit);
        Assert.Contains("ghost", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("generalist", output.ToString(), StringComparison.Ordinal);
    }

    //an explicit base_url is the user's choice of a proxy or another machine, so the model's port never replaces it
    [Fact]
    public async Task Launch_ExplicitLocalBaseUrl_WinsOverTheModelsPort()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.NotNull(server.LastRequestBody);
    }

    //a model folder applies on any endpoint: its append and sampling reach the request, while its port never replaces the endpoint's base url
    [Fact]
    public async Task Launch_ModelFolderOnANonLocalEndpoint_SuppliesItsModelParts_AndTheEndpointStillServes()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"},"vega":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1, profileExtra: "\"auto_serve\":true,\"sampling\":{\"temperature\":0.6}");
        File.WriteAllText(Path.Combine(_home, "models", "test-model", "system-append.md"), "VEGA-APPEND");
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "vega", "-m", "test-model", "-p", "hi" });

        Assert.Equal(0, exit);
        var body = server.LastRequestBody!.Value;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(0.6, body.GetProperty("temperature").GetDouble(), 3);
        Assert.Contains("VEGA-APPEND", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    //a cloud id is no folder name, so it finds no folder even when one could be read at that path, and goes out verbatim
    [Fact]
    public async Task Launch_ACloudIdOnANonLocalEndpoint_FindsNoFolder_AndIsSentAsTyped()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"vega":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"vega"}""");
        WriteModel(Path.Combine("openai", "gpt-4.1"), port: 1, profileExtra: "\"sampling\":{\"temperature\":0.6}");
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-m", "openai/gpt-4.1", "-p", "hi" });

        Assert.Equal(0, exit);
        var body = server.LastRequestBody!.Value;
        Assert.Equal("openai/gpt-4.1", body.GetProperty("model").GetString());
        Assert.False(body.TryGetProperty("temperature", out _));
    }

    //only the endpoint named local is gatto's to start, probe and size from a folder, which is what keeps auto_serve and the port away from a remote server
    [Fact]
    public void ServingFor_LeavesAFolderOnANonLocalEndpoint_ToTheEndpoint()
    {
        UseHome();
        WriteModel("test-model", port: 1, profileExtra: "\"auto_serve\":true");
        var model = Gatto.Roles.Models.Model.Load(Path.Combine(_home, "models"), "test-model");
        var endpoint = new Gatto.Core.Client.EndpointConfig("http://127.0.0.1:9", Context: 32768);

        Assert.Equal((false, (int?)32768), ServeCommand.ServingFor(servedHere: false, model, endpoint, "http://127.0.0.1:9"));
        Assert.Equal((true, (int?)8192), ServeCommand.ServingFor(servedHere: true, model, endpoint, "http://127.0.0.1:9"));
        Assert.Equal((false, (int?)32768), ServeCommand.ServingFor(servedHere: false, null, endpoint, "http://127.0.0.1:9"));
    }

    //the child must dispatch before GattoConfig.Load, which exits 2 on a bad config. a silent child reads as no vulkan loader, so the machine is priced CPU-only
    [Fact]
    public async Task THE_PROBE_CHILD_ANSWERS_BEFORE_THE_CONFIG_IS_EVEN_READ()
    {
        UseHome();
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{ this is not json");

        //the harness writer is a different sink than Console.Out, so a mutation that writes to the console instead of ctx.Out fails here
        var pen = new StringWriter();
        Console.SetOut(new StringWriter());
        var ctx = CommandContext.Production() with
        {
            Out = pen,
            ProbeMachine = () => "visible=17179869184\n",
        };

        var exit = await GattoApp.RunAsync(new[] { Gatto.Core.Hardware.HardwareProbe.ChildWord }, ctx);

        Assert.Equal(0, exit);
        Assert.Contains("visible=17179869184", pen.ToString(), StringComparison.Ordinal);
    }

    //an update sweep deletes files before the dispatch, so this leftover zip must survive it. the path comes from UpdateDownload.Folder, where the sweep looks
    [Fact]
    public async Task THE_PROBE_CHILD_OUTRUNS_THE_UPDATE_SWEEP()
    {
        UseHome();
        var leftover = Path.Combine(_home, UpdateDownload.Folder, "gatto-0.0.1-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "a downloaded update this run must not touch");

        //the file must exist where the sweep looks, or this test cannot fail
        Assert.True(File.Exists(leftover));
        Assert.Equal(Path.Combine(_home, UpdateDownload.Folder), UpdateDownload.Dir(_home));

        Console.SetOut(new StringWriter());
        var ctx = CommandContext.Production() with
        {
            Out = new StringWriter(),
            ProbeMachine = () => "visible=1\n",
        };

        Assert.Equal(0, await GattoApp.RunAsync(new[] { Gatto.Core.Hardware.HardwareProbe.ChildWord }, ctx));
        Assert.True(File.Exists(leftover), "the probe child let the update sweep run before it answered");
    }

    //the child's word is internal, so Help must never name it. the same test asserts a real command name, or empty text passes the negative for the wrong reason
    [Fact]
    public void HELP_DOES_NOT_NAME_THE_PROBE_CHILD()
    {
        Assert.DoesNotContain(Gatto.Core.Hardware.HardwareProbe.ChildWord, GattoApp.Help, StringComparison.Ordinal);
        Assert.Contains("doctor", GattoApp.Help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_ModelApiKey_RequestCarriesBearerHeader()
    {
        //drive the whole seam, from api_key in profile.json to the header the fake server receives
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235, profileExtra: "\"api_key\":\"s3cr3t-value\"");
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("Bearer s3cr3t-value", server.LastAuthorization);
    }

    [Fact]
    public async Task Launch_ModelWithoutApiKey_SendsNoAuthorizationHeader()
    {
        //the key is opt-in, so a model without one sends no Authorization header at all
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Null(server.LastAuthorization);
    }

    [Fact]
    public async Task Launch_LocalWithoutBaseUrl_DerivesPortFromModel()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;   //this port is the one the client must take from the model's profile
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");   //the local endpoint is written with no base_url, so the port has to come from the model
        WriteModel("test-model", port: port);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("routed"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.NotNull(server.LastRequestBody);           //the fake server got a request, so the derived address was reachable.
        Assert.Contains("routed", stdout.ToString());
    }

    [Fact]
    public async Task Launch_UnknownRole_ExitsTwoAndListsAvailableRoles()
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");

        var stderr = new StringWriter();
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "frobnicate", "-p", "hi" });

        Assert.Equal(2, exit);
        var msg = stderr.ToString();
        Assert.Contains("frobnicate", msg);
        Assert.Contains("Available roles:", msg);   //the refusal names the roles under an Available roles heading instead of printing the generic help
        Assert.Contains("generalist", msg);         //the shipped role is written when the home initializes, so the listing must name it.
    }

    [Fact]
    public async Task Launch_LocalRoleWithModelKey_IsNoLongerAConfigRefusal()
    {
        //a model key is valid in a local role, so this asserts only that the config refusal is gone. no server runs here, so a successful launch is not claimed
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1);
        WriteRole("generalist", "{\"model\":\"test-model\"}");

        var stderr = new StringWriter();
        Console.SetError(stderr);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        var msg = stderr.ToString();
        Assert.NotEqual(2, exit);                      //exit 2 is the config and usage code, so this run must not end with it
        Assert.DoesNotContain("must be absent", msg);
        Assert.DoesNotContain("has no model for local endpoint", msg);
    }

    //the effective model is -m, then the role file, then default_model, resolved before RoleFile.Validate. a role with no model key can still launch

    [Fact]
    public async Task Launch_BareRoleWithDefaultModel_LaunchesUsingConfigModel()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"cfg-model"}""");
        WriteModel("cfg-model", port: 1235);
        WriteRole("generalist", "{}");   //the role file is shipped-style and holds no model key
        server.Enqueue(Completion("via-default"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("via-default", stdout.ToString());
    }

    [Fact]
    public async Task Launch_DashM_WinsOverDefaultModel()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        //default_model names a model that is not on disk, so a wrong order makes Model.Load throw and the exit becomes 2
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"nonexistent-model"}""");
        WriteModel("cli-model", port: 1235);
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("via-cli"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-m", "cli-model", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("via-cli", stdout.ToString());
    }

    [Fact]
    public async Task Launch_RoleModel_WinsOverDefaultModel()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        //default_model names a model that is not on disk, so the role's model must win or the load throws and the exit becomes 2
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"nonexistent-model"}""");
        WriteModel("role-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"role-model\"}");
        server.Enqueue(Completion("via-role"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("via-role", stdout.ToString());
    }

    [Fact]
    public async Task Launch_DashM_WinsOverRoleModelAndDefaultModel()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        //only the -m model exists on disk, so this row pins the whole precedence at once
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"nonexistent-default"}""");
        WriteModel("cli-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"nonexistent-role-model\"}");
        server.Enqueue(Completion("via-cli-full"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-m", "cli-model", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("via-cli-full", stdout.ToString());
    }

    [Fact]
    public async Task Launch_BareRoleNoModelNoDefaultModel_ExitsTwoNamingThreeSources()
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");
        WriteRole("generalist", "{}");   //no model comes from the role, from -m, or from the endpoint's saved model

        var stderr = new StringWriter();
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(2, exit);
        var msg = stderr.ToString();
        Assert.Contains("-m", msg);
        Assert.Contains("model", msg);
        Assert.Contains("defaults.local.model", msg);
        Assert.Contains("doctor", msg);
    }

    [Fact]
    public async Task Launch_CloudBareRoleWithASavedModel_LaunchesUsingIt()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"},"cloudy":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","defaults":{"cloudy":{"model":"cloud-model-x"} } }
            """);
        WriteRole("generalist", "{\"endpoint\":\"cloudy\"}");   //the role has no model key, so the model falls back to the endpoint's saved one
        server.Enqueue(Completion("via-cloud-default"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("via-cloud-default", stdout.ToString());
    }

    [Fact]
    public async Task HELP_NAMES_NO_AUTO_FLAG()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-h" });

        Assert.Equal(0, exit);
        Assert.DoesNotContain("--auto", stdout.ToString());
        Assert.Contains("--yes", stdout.ToString());
    }

    //the help text is the first thing a stranger reads, so it can't use an internal word such as harness. a real line is checked too, so an empty text can't pass
    [Fact]
    public async Task Help_SPEAKS_THE_AUDIENCES_LANGUAGE_and_never_says_harness()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-h" });
        var help = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.DoesNotContain("harness", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a terminal assistant that runs AI models on your own computer", help);
    }

    //the roles line reads the home, so the default the config names comes first and marked, and a role not on disk is never listed
    [Fact]
    public void HelpText_MARKS_THE_CONFIGURED_DEFAULT_and_lists_the_roles_on_disk()
    {
        var text = GattoApp.HelpText(new[] { "coder", "generalist", "oracle" }, "coder");
        Assert.Contains("roles:    coder (default) | generalist | oracle", text);
        Assert.DoesNotContain("{roles}", text);
        Assert.Contains("roles:    generalist (default) | coder | oracle | raw | <any ~\\.gatto\\roles\\*.json>", GattoApp.HelpText(Array.Empty<string>(), "generalist"));
    }

    //the connect flow must leave a home whose next launch resolves a model. writing an endpoint alone would leave the setup screen's promise false
    [Fact]
    public async Task ConnectWalk_EndsWithAGattoThatResolves_AndSendsTheConfirmedModel()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        GattoHome.EnsureInitialized(_home);

        var flow = new Gatto.Cli.Setup.SetupFlow(
            new ConnectOnlyProbes(server.BaseUrl, ["served-model"], nctx: 8192));
        flow.StartPastOpening();
        flow.Answer(Gatto.Cli.Setup.SetupFlow.ForkConnect);
        flow.Answer(Gatto.Cli.Setup.SetupFlow.Yes);

        Assert.True(flow.NeedsWritesApplied);
        Assert.Null(Gatto.Cli.Setup.WriteSetApply.Apply(_home, flow.Writes, out var written));
        Assert.Null(written);                       //this path writes no model, so written is null and the resume must take that
        flow.ResumeAfterWrites(written);

        //check the promise the setup screen made, that a plain gatto run works
        server.Enqueue(Completion("hi-back"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);                      //exit 2 is the config throw, and this path must not produce it
        Assert.Contains("hi-back", stdout.ToString());
        //the request must name the confirmed model. the literal model placeholder is ignored by some servers and rejected by others
        Assert.Equal("served-model", server.LastRequestBody!.Value.GetProperty("model").GetString());
    }

    //this fake covers only the connect path. a probe the path never reaches throws, and one it passes on the way answers null or a default
    private sealed class ConnectOnlyProbes(string baseUrl, IReadOnlyList<string> models, int nctx)
        : Gatto.Cli.Setup.ISetupProbes
    {
        public Gatto.Core.Hardware.HardwareSnapshot? Hardware() => null;

        //the names are display-only, so a default value is the honest answer.
        public Gatto.Cli.Setup.HardwareNames HardwareNames() => default;
        public Gatto.Core.Acquire.ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) =>
            new(baseUrl, models, nctx);
        public string? GattoServingOn(int port) => null;

        public Gatto.Core.Acquire.ConnectProbe? ProbeAt(string baseUrl) => null;
        public string? ExistingEndpointFor(string url) => null;
        public string ConfigPath() => "gatto.json";
        public Gatto.Core.Acquire.ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) =>
            new(true, "ok", TimeSpan.FromMilliseconds(1));

        //the welcome screen asks whether the engine is already here, so this answers. it reads config and File.Exists only, no scan and no process
        public string? LlamaServerPath() => null;
        public bool HasResolvableModel() => throw new NotSupportedException();
        public Gatto.Cli.Setup.ScanResult Scan(string? extraRoot) => throw new NotSupportedException();
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public Gatto.Cli.Setup.MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public Gatto.Cli.Setup.TypedIdOutcome EvaluateTypedId(string repoId) => throw new NotSupportedException();
        public Gatto.Cli.Setup.AuditionCheck RunAudition(string modelId, string? repoId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        //a connect run reaches this before taking the choice, so this answers instead of throwing. gatto cannot price the machine, so it names no build
        public LlamaAsset? ChooseLlamaAsset() => null;
        public Gatto.Core.Acquire.ProbeResult VerifyLlamaServer(string exePath) => throw new NotSupportedException();
        public int ContextFor(string ggufPath) => throw new NotSupportedException();
        public string? ExistingModelFor(string ggufPath) => throw new NotSupportedException();
        public string? Colliding { get; init; }
        public (Gatto.Roles.Models.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
            Colliding is { Length: > 0 }
                ? (Gatto.Roles.Models.IdClash.DifferentModel, Colliding)
                : (Gatto.Roles.Models.IdClash.Free, null);
        //this fake models a machine where gatto is installed, so this flow runs after install. the path is fixed, so the screen shows a chosen state
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.Install.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.Install.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        //the update answer is already given here, so no run raises that question.
        public bool? UpdateConsent() => UpdateAnswered;
        public bool? UpdateAnswered { get; init; } = false;
    }


    [Fact]
    public async Task ABareGattoOnAFreshMachine_DoesNotOfferThroughAPipe_AndFailsTheWayItAlwaysDid()
    {
        //a piped or scripted run must never stop on a question nobody can answer. the offer makes the same redirected-terminal check as setup
        var fresh = Path.Combine(_home, "never-created");
        Environment.SetEnvironmentVariable("GATTO_HOME", fresh);
        Environment.CurrentDirectory = _cwd;
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        Console.SetError(stderr);
        Console.SetOut(stdout);

        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(2, exit);
        Assert.DoesNotContain("isn't set up on this machine yet", stdout.ToString() + stderr.ToString(),
            StringComparison.Ordinal);
    }

    //a launch that dies on a config error must leave no home behind. nothing before a successful config load may create it, so setup is still offered
    [Fact]
    public async Task A_LAUNCH_THAT_FAILS_LEAVES_NO_HOME_BEHIND()
    {
        var fresh = Path.Combine(_home, "fresh-home");
        Environment.SetEnvironmentVariable("GATTO_HOME", fresh);
        Environment.CurrentDirectory = _cwd;
        Console.SetError(new StringWriter());
        Console.SetOut(new StringWriter());

        Assert.False(Directory.Exists(fresh));
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(fresh),
            "a launch that dies with a config error must not scaffold on the way out, since a home "
            + "left behind stops gatto offering setup on the next launch");
    }

    [Fact]
    public async Task Launch_ConnectEndpoint_ProbesOnDashP_LiveContextWins_AndTheLineNeverReachesThePrefix()
    {
        //the same run must prove the line was emitted before it proves the line is absent from the prefix. a negative about a line that never existed proves nothing.
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        //name the endpoint something other than local, which takes the model branch. the stored window is smaller, so a min defect shows
        WriteConfig($$$"""
            {"endpoints":{"cloudy":{"base_url":"{{{server.BaseUrl}}}","context":32768}},
             "default_endpoint":"cloudy","default_model":"served-model"}
            """);
        WriteRole("generalist", "{}");
        //the body needs model_path, since the probe returns null without it, and the window sits under default_generation_settings
        server.PropsResponse = new FakeResponse(
            Body: """{"model_path": "theirs.gguf","default_generation_settings":{"n_ctx":131072}}""");
        server.Enqueue(Completion("hello"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        //pin the probe on the one-shot path, where it can be seen rather than argued.
        Assert.True(server.PropsCalled, "-p must probe a connect endpoint too");
        //the live reading wins over a smaller stored value, which is the direction min() gets wrong
        var notices = stderr.ToString();
        Assert.Contains("server reports 131072. Using it (endpoint config says 32768)", notices);
        //the notice is display-only, so none of it may appear in the request body.
        var body = server.LastRequestBody!.Value.GetRawText();
        Assert.DoesNotContain("server reports 131072", body, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint config says", body, StringComparison.Ordinal);
        //stdout must stay pipeable, so the notice goes to stderr and only the completion to stdout.
        Assert.Contains("hello", stdout.ToString());
        Assert.DoesNotContain("server reports", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_EndpointThinkingMapWithInvalidLevelName_ExitsTwoNamingTheEndpoint()
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235","thinking":{"hgih":{}}}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");

        var stderr = new StringWriter();
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(2, exit);
        var msg = stderr.ToString();
        Assert.Contains("local", msg);
        Assert.Contains("hgih", msg);
    }

    [Fact]
    public async Task Launch_RoleArgDifferentCasing_ReportsCanonicalOnDiskName()
    {
        //the lookup ignores case, so the banner must report the name on disk. the banner prints before any turn, so no server exchange is needed
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 65501);
        WriteRole("coder", "{\"model\":\"test-model\"}");

        Console.SetIn(new StringReader("/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(new[] { "CODER" });

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains($"gatto {Gatto.Terminal.GlyphSet.Unicode.Dot} coder {Gatto.Terminal.GlyphSet.Unicode.Dot}", output);
        Assert.DoesNotContain("CODER", output);
    }

    //these drive the real path with a scripted Console.In, so the gates arm at launch. the gate-level tests build a harness and can't see that

    private static string ToolCallFrame(string id, string name, string argsJson) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new object[] { new
            {
                index = 0,
                delta = new { tool_calls = new object[] { new { index = 0, id, function = new { name, arguments = argsJson } } } },
                finish_reason = (string?)null,
            } },
        }) + "\n\n";

    private static string FinishFrame(string reason) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new object[] { new { index = 0, delta = new { }, finish_reason = reason } },
        }) + "\n\n";

    private static FakeResponse ToolCallThenStop(string id, string name, string argsJson) => new(Frames: new[]
    {
        ToolCallFrame(id, name, argsJson),
        FinishFrame("tool_calls"),
        "data: [DONE]\n\n",
    });

    //on a server with llama-server's counting routes /context is exact: the total is the server's and the template takes what the parts leave
    [Fact]
    public async Task Context_on_a_server_that_counts_is_exact_and_names_the_template_markup()
    {
        await using var server = new FakeOpenAiServer { InputTokens = 9_000, TokenizeCount = s => s.Length / 4 };
        UseHome();
        //the assertions read the unicode dot, so the set is pinned rather than left to the host's terminal
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");

        Console.SetIn(new StringReader("/context\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("context · 9,000 of 8,192 tokens", output);
        Assert.Contains("counted by the server's tokenizer · before the first request", output);
        Assert.Contains("template markup", output);
        //the count is of the request the first turn will send: the composed system message and every tool
        using var counted = JsonDocument.Parse(server.InputTokensRaw!);
        Assert.Equal("system", counted.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.True(counted.RootElement.GetProperty("tools").GetArrayLength() > 0);
        Assert.Contains(counted.RootElement.GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("function").GetProperty("name").GetString() == "shell");
    }

    //under -p --yes a model once rewrote gatto.json and gatto would not start, now the write is refused and the file stays
    [Fact]
    public async Task Under_p_yes_a_write_to_gattos_own_config_is_refused_and_the_file_is_unchanged()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        var config = Path.Combine(_home, "gatto.json");
        var before = File.ReadAllText(config);
        server.Enqueue(ToolCallThenStop("c1", "write_file",
            JsonSerializer.Serialize(new { path = config, content = "{\"compact_model\":\"x\"}" })));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        await GattoApp.RunAsync(new[] { "--yes", "-p", "set compact_model" });

        Assert.Equal(before, File.ReadAllText(config));
        var toolMsg = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains(Gatto.Core.Loop.Permissions.PermissionGate.HomeWrite, toolMsg.GetProperty("content").GetString());
    }

    //a role file written by an older gatto may still name the grounding gate, and it must add no tool and no text to a result
    [Fact]
    public async Task A_role_naming_the_retired_grounding_gate_adds_no_tool_and_no_reminder()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("oracle", "{\"model\":\"test-model\",\"gates\":[\"grounding\"]}");

        server.Enqueue(ToolCallThenStop("c1", "write_file", "{\"path\":\"a.txt\",\"content\":\"x\"}"));
        server.Enqueue(Completion("done"));

        Console.SetIn(new StringReader("write the file\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "oracle", "--yes" });

        Assert.Equal(0, exit);
        var body = server.LastRequestBody!.Value;
        var toolMsg = body.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.DoesNotContain("task_restate", toolMsg.GetProperty("content").GetString());
        var names = body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Contains("write_file", names);   //the list is the real one, so the absence below is not an empty read
        Assert.DoesNotContain("task_restate", names);
    }

    //the citation gate follows the role: unarmed under a role without it, armed by a /role to one that names it
    [Fact]
    public async Task A_role_switch_to_a_role_naming_the_citation_gate_arms_it()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("researcher", "{\"model\":\"test-model\",\"gates\":[\"citations\"]}");

        server.Enqueue(ToolCallThenStop("c1", "write_file", "{\"path\":\"a.txt\",\"content\":\"see https://made.up.test/page\"}"));
        server.Enqueue(Completion("done"));
        server.Enqueue(ToolCallThenStop("c2", "write_file", "{\"path\":\"b.txt\",\"content\":\"see https://made.up.test/page\"}"));
        server.Enqueue(Completion("done"));

        Console.SetIn(new StringReader("write one\n/role researcher\nwrite two\n/quit\n"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes" }));

        var toolMsgs = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "tool")
            .Select(m => m.GetProperty("content").GetString()!).ToList();
        Assert.Equal(2, toolMsgs.Count);
        Assert.DoesNotContain("[citation gate]", toolMsgs[0]);
        Assert.Contains("[citation gate] FABRICATED", toolMsgs[1]);
    }

    [Fact]
    public async Task RoleSwitch_LocalModelChange_SameBaseUrl_IsRefused_NamingBothModelsAndRestart()
    {
        //an explicit base_url makes both roles one address, so the address guard passes. the server still serves the launch model, so the switch must be refused
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local"}""");
        WriteModel("model-a", port: 1);
        WriteModel("model-b", port: 1);
        WriteRole("generalist", "{\"model\":\"model-a\"}");
        WriteRole("coder", "{\"model\":\"model-b\"}");

        Console.SetIn(new StringReader("/role coder\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("uses model 'model-b'", output);      //the message must name the model the new role wanted.
        Assert.Contains("serving 'model-a'", output);        //the message must also name the model the server serves
        Assert.Contains("Restart gatto to switch models", output);
        Assert.DoesNotContain("role: coder", output);     //the absent line proves the role never switched.
    }

    [Fact]
    public async Task RoleSwitch_LocalSameModelDifferentRole_StillSucceeds()
    {
        //the guard looks at model identity rather than role identity. a switch that keeps the same weights must stay allowed
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local"}""");
        WriteModel("model-a", port: 1);
        WriteRole("generalist", "{\"model\":\"model-a\"}");
        WriteRole("coder", "{\"model\":\"model-a\"}");

        Console.SetIn(new StringReader("/role coder\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("role: coder", output);
        Assert.DoesNotContain("restart gatto", output);
    }

    [Fact]
    public async Task RoleSwitch_LocalToCloudRole_SameBaseUrl_IsRefusedWithoutCrashing()
    {
        //a cloud endpoint can repeat the local address and pass the guard. the model guard then fires with no model, and the refusal must be a message
        UseHome();
        WriteConfig("""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1"},"cloudy":{"base_url":"http://127.0.0.1:1"}},
             "default_endpoint":"local"}
            """);
        WriteModel("model-a", port: 1);
        WriteRole("generalist", "{\"model\":\"model-a\"}");
        WriteRole("clouder", "{\"endpoint\":\"cloudy\",\"model\":\"model-x\"}");

        Console.SetIn(new StringReader("/role clouder\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);   //exit 0 means the REPL ran to /quit, so no null-reference error hit the backstop, which would exit 1
        var output = stdout.ToString();
        Assert.Contains("targets endpoint 'cloudy'", output);
        Assert.Contains("serving local model 'model-a'", output);
        Assert.Contains("Restart gatto to switch endpoints", output);
        Assert.DoesNotContain("role: clouder", output);   //the absent line proves the role never switched.
    }

    [Fact]
    public async Task RoleSwitch_Cloud_ThenCompact_SummarizesWithTheSwitchedModel()
    {
        //a cloud role may change the model, so the compactor must read the model at compact time. an id captured at construction would be stale
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"},"cloudy":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteRole("generalist", "{\"endpoint\":\"cloudy\",\"model\":\"model-a\"}");   //this role holds the model the session launches with.
        WriteRole("other", "{\"endpoint\":\"cloudy\",\"model\":\"model-b\"}");        //this role holds the model /role switches to
        server.Enqueue(Completion("SUMMARY"));                                        //this completion answers the summarization turn that /compact sends

        Console.SetIn(new StringReader("/role other\n/compact\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        //the summarization request must name the model /role switched to
        Assert.Equal("model-b", server.LastRequestBody!.Value.GetProperty("model").GetString());
    }

    //the default endpoint is local and unreachable, so a request at the fake server proves -e moved the session to the named endpoint
    private void WriteCloudConfig(string serverUrl) =>
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"},"cloudy":{"base_url":"{{{serverUrl}}}"},"other":{"base_url":"http://127.0.0.1:2"}},"default_endpoint":"local"}""");

    [Fact]
    public async Task THE_ENDPOINT_FLAG_SENDS_THE_REQUEST_TO_THE_NAMED_ENDPOINT()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteCloudConfig(server.BaseUrl);
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "cloudy", "-m", "model-x", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("model-x", server.LastRequestBody!.Value.GetProperty("model").GetString());
    }

    //an unknown -e name is a usage error, exit 2 raised before any request leaves gatto, and the message lists the endpoints
    [Fact]
    public async Task AN_UNKNOWN_ENDPOINT_NAME_IS_REFUSED_NAMING_THE_ENDPOINTS()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteCloudConfig(server.BaseUrl);
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        var stderr = new StringWriter();
        Console.SetError(stderr);
        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "nope", "-m", "model-x", "-p", "hi" });

        Assert.Equal(2, exit);
        Assert.Contains("-e names endpoint 'nope', which is not defined in gatto.json. Endpoints: cloudy, local, other", stderr.ToString());
        Assert.Null(server.LastRequestBody);
    }

    //a shell child reads GATTO_ENDPOINT to learn which endpoint its session runs on, so the value must be the resolved endpoint name
    [Fact]
    public async Task AN_ENDPOINT_LAUNCH_EXPORTS_THE_ENDPOINT_NAME()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteCloudConfig(server.BaseUrl);
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));
        Environment.SetEnvironmentVariable("GATTO_ENDPOINT", null);

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "cloudy", "-m", "model-x", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("cloudy", Environment.GetEnvironmentVariable("GATTO_ENDPOINT"));
    }

    //the default endpoint here is the unreachable cloud one, so exit 0 also proves -e local is accepted and beats default_endpoint
    [Fact]
    public async Task A_LOCAL_LAUNCH_EXPORTS_LOCAL_AS_THE_ENDPOINT_NAME()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"},"cloudy":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"cloudy"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));
        Environment.SetEnvironmentVariable("GATTO_ENDPOINT", "cloudy");

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "local", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("local", Environment.GetEnvironmentVariable("GATTO_ENDPOINT"));
    }

    //a driver reads GATTO_MODEL to learn which model wrote a session, so on a non-local endpoint it must be the id the request carries
    [Fact]
    public async Task A_NON_LOCAL_LAUNCH_EXPORTS_THE_MODEL_STRING_THE_REQUEST_CARRIES()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteCloudConfig(server.BaseUrl);
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));
        Environment.SetEnvironmentVariable("GATTO_MODEL", "wrong-model");

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "cloudy", "-m", "vendor/model-x:free", "-p", "hi" });

        Assert.Equal(0, exit);
        var sent = server.LastRequestBody!.Value.GetProperty("model").GetString();
        Assert.Equal("vendor/model-x:free", sent);
        Assert.Equal(sent, Environment.GetEnvironmentVariable("GATTO_MODEL"));
    }

    //a local launch with no thinking map still exports the composed level name, so a map-less and a mapped launch share one shape
    [Fact]
    public async Task A_LOCAL_LAUNCH_EXPORTS_THE_PROFILE_ID_AND_THE_COMPOSED_LEVEL()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));
        Environment.SetEnvironmentVariable("GATTO_MODEL", "wrong-model");
        Environment.SetEnvironmentVariable("GATTO_EFFORT", "high");

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("test-model", Environment.GetEnvironmentVariable("GATTO_MODEL"));
        Assert.Equal("medium", Environment.GetEnvironmentVariable("GATTO_EFFORT"));
    }

    //the role asks for high while the pre-set GATTO_EFFORT is none, so only a composed level can produce this value
    [Fact]
    public async Task A_LAUNCH_EXPORTS_THE_COMPOSED_EFFORT_LEVEL_NAME()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$$$$"""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1"},
              "cloudy":{"base_url":"{{{{{{server.BaseUrl}}}}}}","thinking":{
                "none":{"reasoning":{"effort":"none"}},
                "low":{"reasoning":{"effort":"low"}},
                "high":{"reasoning":{"effort":"high"}}}}},
             "default_endpoint":"local"}
            """);
        WriteRole("generalist", "{\"thinking\":\"high\"}");
        server.Enqueue(Completion("ok"));
        Environment.SetEnvironmentVariable("GATTO_EFFORT", "none");

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "cloudy", "-m", "model-x", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("high", server.LastRequestBody!.Value.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("high", Environment.GetEnvironmentVariable("GATTO_EFFORT"));
    }

    //the role asks for high and the flag for low, so only the flag can put low on the wire and in the exported level
    [Fact]
    public async Task THE_EFFORT_FLAG_WINS_OVER_THE_ROLE_LEVEL_IN_A_ONE_SHOT_RUN()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$$$$"""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1"},
              "cloudy":{"base_url":"{{{{{{server.BaseUrl}}}}}}","thinking":{
                "none":{"reasoning":{"effort":"none"}},
                "low":{"reasoning":{"effort":"low"}},
                "high":{"reasoning":{"effort":"high"}}}}},
             "default_endpoint":"local"}
            """);
        WriteRole("generalist", "{\"thinking\":\"high\"}");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var exit = await GattoApp.RunAsync(new[] { "-e", "cloudy", "-m", "model-x", "--effort", "low", "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Equal("low", server.LastRequestBody!.Value.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("low", Environment.GetEnvironmentVariable("GATTO_EFFORT"));
    }

    //a role with no endpoint of its own must keep the -e endpoint. resolving it to the default local endpoint would refuse every /role switch in that session
    [Fact]
    public async Task UNDER_THE_ENDPOINT_FLAG_A_ROLE_WITH_NO_ENDPOINT_SWITCHES_AND_STAYS_ON_THAT_ENDPOINT()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteCloudConfig(server.BaseUrl);
        WriteRole("generalist", "{}");
        WriteRole("coder", "{}");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("/role coder\nhi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(new[] { "-e", "cloudy", "-m", "model-x" });

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("role: coder", output);
        Assert.DoesNotContain("Restart gatto", output);
        Assert.Equal("model-x", server.LastRequestBody!.Value.GetProperty("model").GetString());   //the turn after the switch reached the named endpoint.
    }

    //a cloud endpoint that lists no models: the fake server stands in for it through the seam, cleared after
    private async Task<(string Output, FakeOpenAiServer Server)> OnCloud(string script, Action? arrange = null)
    {
        var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"cloudy":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"cloudy","default_model":"served-model"}
            """);
        WriteRole("generalist", "{}");
        arrange?.Invoke();
        server.Enqueue(Completion("ok"));
        Console.SetIn(new StringReader(script + "/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        CloudEndpoint.IsForTest = ep => ep.BaseUrl == server.BaseUrl;
        try { Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>())); }
        finally { CloudEndpoint.IsForTest = null; }
        return (stdout.ToString(), server);
    }

    [Fact]
    public async Task Model_switch_on_a_cloud_endpoint_with_no_list_names_the_typed_model()
    {
        var (output, server) = await OnCloud("/model foo\nhi\n");
        await using var _ = server;

        Assert.Equal("foo", server.LastRequestBody!.Value.GetProperty("model").GetString());
        Assert.DoesNotContain("own model settings only apply", output, StringComparison.Ordinal);
        Assert.DoesNotContain("serve", output.Replace("served-model", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Model_switch_on_a_cloud_endpoint_takes_the_parts_of_a_folder_of_that_name()
    {
        var (_, server) = await OnCloud("/model foo\nhi\n", () =>
        {
            WriteModel("foo", port: 1);
            File.WriteAllText(Path.Combine(_home, "models", "foo", "system-append.md"), "FOO-APPEND");
        });
        await using var _ = server;

        var body = server.LastRequestBody!.Value;
        Assert.Equal("foo", body.GetProperty("model").GetString());
        Assert.Contains("FOO-APPEND", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Bare_model_on_a_cloud_endpoint_with_no_list_says_how_to_switch()
    {
        var (output, server) = await OnCloud("/model\n");
        await using var _ = server;

        Assert.Contains(Gatto.Cli.GattoApp.CloudSwitchHint("cloudy"), output);
    }

    //an endpoint gatto does not serve and that lists no models refuses /model with the unmanaged sentence, and the next request names the model it had
    [Fact]
    public async Task Model_switch_on_an_unlisted_unserved_endpoint_is_refused_and_the_model_stays()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"cloudy":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"cloudy","default_model":"served-model"}
            """);
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("/model other-model\nhi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("gatto's own model settings only apply to servers it starts", stdout.ToString());
        Assert.Equal("served-model", server.LastRequestBody!.Value.GetProperty("model").GetString());
    }

    //the command /model must update the value ResolveRole consults. a stale launch value makes the local-model guard refuse every role, including the active one

    [Fact]
    public async Task ModelThenRole_BareRoleWithNoOwnModel_Succeeds_AndLandsOnTheNewModel()
    {
        //neither role declares a model, so both use the session's effective model. a stale value would refuse a role that pins nothing
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","default_model":"qwen-model"}""");
        WriteModel("qwen-model", port: 1);
        WriteModel("gemma-model", port: 1);
        WriteRole("generalist", "{}");
        WriteRole("coder", "{}");

        Console.SetIn(new StringReader("/model gemma-model\n/role coder\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: gemma-model", output);
        Assert.Contains("role: coder", output);
        Assert.DoesNotContain("restart gatto", output);     //no restart notice may appear when the switch is legal.
    }

    [Fact]
    public async Task RoleSwitch_TwoRolesWithDifferentExplicitModels_NoModelInvolved_IsStillRefused()
    {
        //the guard must refuse two roles that pin different models of their own, when /model was never used
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local"}""");
        WriteModel("qwen-model", port: 1);
        WriteModel("gemma-model", port: 1);
        WriteRole("generalist", "{\"model\":\"qwen-model\"}");
        WriteRole("pinned-role", "{\"model\":\"gemma-model\"}");

        Console.SetIn(new StringReader("/role pinned-role\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("uses model 'gemma-model'", output);
        Assert.Contains("serving 'qwen-model'", output);
        Assert.Contains("Restart gatto to switch models", output);
        Assert.DoesNotContain("role: pinned-role", output);   //the absent line proves the role never switched.
    }

    [Fact]
    public async Task ModelThenRole_SessionOverrideBeatsARolesOwnModelPin_SameAsLaunchDashMAlwaysDid()
    {
        //the session model outranks a role's own pin, the same as -m. the command /model keeps that precedence, so the switch succeeds onto the session model
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local","default_model":"qwen-model"}""");
        WriteModel("qwen-model", port: 1);
        WriteModel("gemma-model", port: 1);
        WriteRole("generalist", "{}");
        WriteRole("pinned-role", "{\"model\":\"qwen-model\"}");   //this role pins a model of its own, which the session model overrides.

        Console.SetIn(new StringReader("/model gemma-model\n/role pinned-role\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: gemma-model", output);
        Assert.Contains("role: pinned-role", output);   //the role switched, and the model shown is the session model rather than the role's pin.
        Assert.DoesNotContain("restart gatto", output);
    }

    [Fact]
    public async Task ModelThenRole_AfterLaunchWithDashM_SessionModelWinsOverLaunchDashM()
    {
        //the launch -m must not keep winning in ResolveRole once /model moves away. the order stays -m, then the role's model, then default_model
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1"}},"default_endpoint":"local"}""");
        WriteModel("qwen3.6-27b", port: 1);
        WriteModel("gemma-4-26b", port: 1);
        WriteRole("generalist", "{}");
        WriteRole("coder", "{}");

        Console.SetIn(new StringReader("/model gemma-4-26b\n/role coder\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(new[] { "-m", "qwen3.6-27b" });

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: gemma-4-26b", output);
        Assert.Contains("role: coder", output);
        Assert.DoesNotContain("restart gatto", output);
    }

    //each switch moves state the next one reads, so a switch that kept a stale copy would send the old model's parts or map
    [Fact]
    public async Task A_MODEL_SWITCH_THEN_A_ROLE_SWITCH_THEN_EFFORT_READ_THE_SAME_STATE()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"listed":{"base_url":"{{{server.BaseUrl}}}","models":["alpha-model","beta-model"]}},
             "default_endpoint":"listed","default_model":"alpha-model"}
            """);
        WriteModel("alpha-model", port: 65533, profileExtra: Qwen38ThinkingMap);
        File.WriteAllText(Path.Combine(_home, "models", "alpha-model", "system-append.md"), "ALPHA-APPEND");
        WriteModel("beta-model", port: 65533, profileExtra:
            "\"thinking\":{\"low\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"beta-low\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"beta-high\"}}}");
        File.WriteAllText(Path.Combine(_home, "models", "beta-model", "system-append.md"), "BETA-APPEND");
        WriteRole("generalist", "{}");
        WriteRole("coder", "{\"append\":\"CODER-APPEND\",\"thinking\":\"high\"}");
        for (var i = 0; i < 4; i++) server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("hi\n/model beta-model\nhi\n/role coder\nhi\n/effort low\nhi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var sent = server.RequestBodies.Where(b => b.TryGetProperty("messages", out _)).ToList();
        Assert.Equal(4, sent.Count);
        string Model(int i) => sent[i].GetProperty("model").GetString()!;
        string System(int i) => sent[i].GetProperty("messages")[0].GetProperty("content").GetString()!;
        string Effort(int i) => sent[i].GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString()!;

        //the launch: alpha's parts and alpha's map at the default level
        Assert.Equal("alpha-model", Model(0));
        Assert.Contains("ALPHA-APPEND", System(0));
        Assert.Equal("medium", Effort(0));

        //the model switch: beta's parts replace alpha's, and the default level lands on the nearest one beta's map declares
        Assert.Equal("beta-model", Model(1));
        Assert.Contains("BETA-APPEND", System(1));
        Assert.DoesNotContain("ALPHA-APPEND", System(1));
        Assert.Equal("beta-low", Effort(1));

        //the role switch keeps the session's model, adds the role's text, and takes the role's level through beta's map
        Assert.Equal("beta-model", Model(2));
        Assert.Contains("BETA-APPEND", System(2));
        Assert.Contains("CODER-APPEND", System(2));
        Assert.Equal("beta-high", Effort(2));

        //the effort switch moves the level alone, through the map the model switch armed
        Assert.Equal("beta-model", Model(3));
        Assert.Equal(System(2), System(3));
        Assert.Equal("beta-low", Effort(3));

        var output = stdout.ToString();
        Assert.Contains("model: beta-model", output);
        Assert.Contains("role: coder", output);
        Assert.Contains("effort: low", output);
    }

    //a model switch must reassign the chip from the new thinking map. a stale chip advertises a level that is never sent, and bare /effort shows it

    [Fact]
    public async Task ModelSwitch_ToModelWithNoThinkingMap_HidesTheEffortChip()
    {
        UseHome();
        //the port is dead on purpose, so the capability sniff fails. that isolates the model's map from any live server answer.
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65533"}},"default_endpoint":"local","default_model":"thinky-model"}""");
        //the launch model needs three on-levels (a single on-level map is a binary toggle and would change what /effort reports)
        WriteModel("thinky-model", port: 65533,
            profileExtra: "\"thinking\":{\"low\":{},\"medium\":{\"chat_template_kwargs\":{\"enable_thinking\":true}},\"high\":{}}");
        WriteModel("plain-model", port: 65533);   //this profile declares no thinking map.
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/model plain-model\n/effort\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: plain-model", output);
        //no map plus a capability of none leaves nothing to offer, so the answer is the outright refusal. a levels sniff with no map gets the display-only line instead.
        Assert.Contains("this model doesn't support changing the reasoning mode.", output);
    }

    [Fact]
    public async Task ModelSwitch_ToModelWithAThinkingMap_ShowsTheCorrectEffortChip()
    {
        UseHome();
        //the dead port makes the sniff report none, so /effort reads the model's own map instead of a live toggle switch
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65533"}},"default_endpoint":"local","default_model":"plain-model"}""");
        WriteModel("plain-model", port: 65533);   //this profile declares no thinking map.
        WriteModel("thinky-model", port: 65533,
            profileExtra: "\"thinking\":{\"medium\":{\"chat_template_kwargs\":{\"enable_thinking\":true}}}");
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/model thinky-model\n/effort\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: thinky-model", output);
        Assert.Contains("effort: medium", output);
        Assert.DoesNotContain("not configured", output);
    }

    //an option where the prompt belongs ends the run as a usage error, with the line that says how to pass such a prompt
    [Fact]
    public async Task A_prompt_flag_followed_by_an_option_exits_2()
    {
        UseHome();
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Console.SetOut(new StringWriter());

        var exit = await GattoApp.RunAsync(["-p", "--yes", "list the files"]);

        Assert.Equal(2, exit);
        Assert.Contains("-p needs the prompt right after it", stderr.ToString());
    }

    //an endpoint that lists its models switches by name, and the new model's folder brings its own map, so /effort offers what a launch on that model offers
    [Fact]
    public async Task ModelSwitch_on_a_listed_endpoint_recomputes_the_thinking_capability_from_the_new_map()
    {
        async Task<string> EffortAfter(string launchModel, string script)
        {
            await using var server = new FakeOpenAiServer();
            UseHome();
            WriteConfig($$$"""
                {"endpoints":{"listed":{"base_url":"{{{server.BaseUrl}}}","models":["plain-model","thinky-model"]}},
                 "default_endpoint":"listed","default_model":"{{{launchModel}}}"}
                """);
            WriteModel("plain-model", port: 65533);
            WriteModel("thinky-model", port: 65533,
                profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}");
            WriteRole("generalist", "{}");
            Console.SetIn(new StringReader(script + "/effort\n/quit\n"));
            var stdout = new StringWriter();
            Console.SetOut(stdout);
            Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));
            var lines = stdout.ToString().Split('\n');
            return lines.Last(l => l.Contains("effort", StringComparison.Ordinal) || l.Contains("reasoning", StringComparison.Ordinal)).Trim();
        }

        var launched = await EffortAfter("thinky-model", "");
        var switched = await EffortAfter("plain-model", "/model thinky-model\n");

        Assert.Equal(launched, switched);
    }

    [Fact]
    public async Task ModelSwitch_FromBinaryToMultiLevel_RecomputesTheReasoningCapability()
    {
        UseHome();
        //switching from a binary model to a multi-level one must recompute the capability. otherwise the toggle stays and the old map body is sent
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65533"}},"default_endpoint":"local","default_model":"binary-model"}""");
        WriteModel("binary-model", port: 65533,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}");
        WriteModel("levels-model", port: 65533,
            profileExtra: "\"thinking\":{\"low\":{},\"medium\":{},\"high\":{}}");
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/model levels-model\n/effort medium\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: levels-model", output);
        Assert.Contains("effort: medium", output);          //the named level proves the model now behaves as a levels model.
    }

    [Fact]
    public async Task BinaryReasoningMap_IsDrivenByTheEffortToggle_NotLevels()
    {
        UseHome();
        //a map with one on-level is a toggle, and the map decides that. typing its level name turns reasoning on and the display says on
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65533"}},"default_endpoint":"local","default_model":"mistral-ish","think":"on"}""");
        WriteModel("mistral-ish", port: 65533,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}");
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/effort none\n/effort high\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("effort: none", output);          //the command /effort none switches reasoning off in toggle mode
        Assert.Contains("effort: on", output);             //the command /effort high still reports the bare word on in toggle mode
        Assert.DoesNotContain("effort: high", output);     //the level name never appears, so the model is not read as a levels model.
    }

    [Fact]
    public async Task Launch_MultiLevelMapWithBothSwitchesTemplate_SendsTheRolesLevel_NotTheTemplatesDefault()
    {
        //a template naming both switches makes the sniff answer Toggle, so the map must win. this row drives launch, the switch site has its own test
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("qwen38-model", port: port, profileExtra: Qwen38ThinkingMap);
        WriteRole("generalist", "{\"model\":\"qwen38-model\"}");   //with no thinking key the role takes the default level, medium
        server.PropsResponse = new FakeResponse(Body:
            $$"""{"model_path":"m.gguf","chat_template":"{{BothSwitchesTemplate}}"}""");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("hi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var kwargs = server.LastRequestBody!.Value.GetProperty("chat_template_kwargs");
        Assert.Equal("medium", kwargs.GetProperty("reasoning_effort").GetString());
        Assert.True(kwargs.GetProperty("enable_thinking").GetBoolean());
    }

    //one theory covers four levels over one fixture, so the chip can't drift. the none row asserts an absent key, its body has a different shape
    [Theory]
    [InlineData("xhigh", "xhigh", "xhigh", true, null)]
    [InlineData("max", "xhigh", "xhigh", true, "requested max, the nearest level this model declares")]
    [InlineData("none", "none", null, false, null)]
    [InlineData("high", "medium", "medium", true, "requested high, the nearest level this model declares")]
    public async Task Effort_AgainstQwen38Map_SendsAndReportsTheLandedLevel(
        string requested, string landedChip, string? reasoningEffort, bool enableThinking, string? movedText)
    {
        //the effort state, the printed line, and the footer chip must all read the level actually sent. otherwise the line names a level the map never sent
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("qwen38-model", port: port, profileExtra: Qwen38ThinkingMap);
        WriteRole("generalist", "{\"model\":\"qwen38-model\"}");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader($"/effort {requested}\n/effort\nhi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains($"effort: {landedChip}", output);
        Assert.Contains($"effort: {landedChip}, usage:", output);   //this line is the chip's own readback, so it cannot disagree with the level sent.
        if (movedText is not null) Assert.Contains(movedText, output);

        var kwargs = server.LastRequestBody!.Value.GetProperty("chat_template_kwargs");
        Assert.Equal(enableThinking, kwargs.GetProperty("enable_thinking").GetBoolean());
        if (reasoningEffort is null)
            //the none body holds the thinking flag alone, so assert the absence of reasoning_effort. a value check would pass even if a stray key went out beside the flag
            Assert.False(kwargs.TryGetProperty("reasoning_effort", out _),
                "the none entry must carry no reasoning_effort key at all");
        else
            Assert.Equal(reasoningEffort, kwargs.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Effort_NoEntryAtOrBelowTheRequest_HidesTheChipInsteadOfClaimingIt()
    {
        //a map with only higher levels has nothing to send at or below the request. the line says so and the chip hides, rather than naming a level nothing sends.
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65533"}},"default_endpoint":"local"}""");
        WriteModel("high-only-model", port: 65533,
            profileExtra: "\"thinking\":{\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}");
        WriteRole("generalist", "{\"model\":\"high-only-model\"}");

        Console.SetIn(new StringReader("/effort low\n/effort\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("no thinking map entry at or below this level", output);
        Assert.Contains("effort: not configured, no thinking map", output);
    }

    private JsonElement ReadProfile(string id) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "models", id, "profile.json"))).RootElement;

    [Fact]
    public async Task Effort_TypedLevel_PersistsAsTheModelsDefault()
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65534"}},"default_endpoint":"local"}""");
        WriteModel("qwen38-model", port: 65534, profileExtra: Qwen38ThinkingMap);
        WriteRole("generalist", "{\"model\":\"qwen38-model\"}");

        Console.SetIn(new StringReader("/effort xhigh\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("saved as qwen38-model's default", stdout.ToString());
        Assert.Equal("xhigh", ReadProfile("qwen38-model").GetProperty("default_effort").GetString());
    }

    [Fact]
    public async Task Effort_PersistsTheLandedLevel_NotTheRawRequest()
    {
        //the map has no high entry, so /effort high ends at medium. saving the raw request would let a later session report a level the map cannot send
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65535"}},"default_endpoint":"local"}""");
        WriteModel("qwen38-model", port: 65535, profileExtra: Qwen38ThinkingMap);
        WriteRole("generalist", "{\"model\":\"qwen38-model\"}");

        Console.SetIn(new StringReader("/effort high\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Equal("medium", ReadProfile("qwen38-model").GetProperty("default_effort").GetString());
    }

    [Fact]
    public async Task Effort_PersistedDefault_AppliesOnTheNextSessionWithNoExplicitEffortCall()
    {
        //a role with no thinking key takes the model's persisted default before the medium fallback. no /effort call happens, so this row covers the whole flow
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("qwen38-model", port: port, profileExtra: Qwen38ThinkingMap + ",\"default_effort\":\"xhigh\"");
        WriteRole("generalist", "{\"model\":\"qwen38-model\"}");   //the role sets no thinking key, so the model's default governs.
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("hi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var kwargs = server.LastRequestBody!.Value.GetProperty("chat_template_kwargs");
        Assert.Equal("xhigh", kwargs.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Effort_OnACloudEndpoint_TypedLevelIsSavedForThatModel()
    {
        //a non-local endpoint has no model profile, so a typed /effort is saved in the endpoint's entry under the model
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        //two on-levels plus none make this a genuine multi-level map, so the capability reads Levels rather than the binary toggle
        WriteConfig(
            "{\"endpoints\":{\"cloudy\":{\"base_url\":\"" + server.BaseUrl + "\"," +
            "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"enable_thinking\":false}}," +
            "\"low\":{\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"low\"}}," +
            "\"medium\":{\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"medium\"}}}}}," +
            "\"default_endpoint\":\"cloudy\",\"default_model\":\"served-model\"}");
        WriteRole("generalist", "{}");
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "theirs.gguf"}""");

        Console.SetIn(new StringReader("/effort medium\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("effort: medium, saved as served-model's default", output);
        var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement
            .GetProperty("defaults").GetProperty("cloudy").GetProperty("effort").GetProperty("served-model").GetString();
        Assert.Equal("medium", saved);
    }

    [Fact]
    public async Task Effort_BinaryToggle_TypedOn_PersistsTheModelsOwnOnLevel()
    {
        UseHome();
        //the capability is computed above role resolution, so a test must select the model with default_model
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65502"}},"default_endpoint":"local","default_model":"mistral-ish"}""");
        WriteModel("mistral-ish", port: 65502,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}");
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/effort on\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        //the word on persists as the map's own on-level. a later launch reads back a real level
        Assert.Contains("saved as mistral-ish's default", stdout.ToString());
        Assert.Equal("high", ReadProfile("mistral-ish").GetProperty("default_effort").GetString());
    }

    [Fact]
    public async Task Effort_BinaryToggle_TypedNone_PersistsNone()
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65503"}},"default_endpoint":"local","default_model":"mistral-ish"}""");
        WriteModel("mistral-ish", port: 65503,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}," +
                          "\"default_effort\":\"high\"");
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/effort none\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("saved as mistral-ish's default", stdout.ToString());
        Assert.Equal("none", ReadProfile("mistral-ish").GetProperty("default_effort").GetString());
    }

    [Fact]
    public async Task Effort_BinaryToggle_PersistedOnDefault_AppliesAtTheNextLaunch()
    {
        //a role with no think override takes the model's persisted default. no /effort is typed here, yet the request uses the persisted on-level's body
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"mistral-ish"}""");
        WriteModel("mistral-ish", port: port,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}," +
                          "\"default_effort\":\"high\"");
        WriteRole("generalist", "{}");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("hi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var kwargs = server.LastRequestBody!.Value.GetProperty("chat_template_kwargs");
        Assert.Equal("high", kwargs.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ModelSwitch_ToADifferentBinaryModel_UsesItsOwnPersistedDefault_NotTheOldModelsSessionState()
    {
        //model-a has no persisted default and launches on, model-b defaults to off. a switch must read the new model's default rather than the old state
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65504"}},"default_endpoint":"local","default_model":"model-a"}""");
        WriteModel("model-a", port: 65504,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"high\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"high\"}}}");
        WriteModel("model-b", port: 65504,
            profileExtra: "\"thinking\":{\"none\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"none\"}},\"medium\":{\"chat_template_kwargs\":{\"reasoning_effort\":\"medium\"}}}," +
                          "\"default_effort\":\"none\"");
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/model model-b\n/effort\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("model: model-b", output);
        Assert.Contains("thinking off", output);   //this shows model-b's own off default rather than the thinking state model-a launched with
    }

    //a template that names only enable_thinking is how the sniff reads a toggle when the model has no map
    private const string ToggleOnlyTemplate = "enable_thinking, no reasoning_effort mentioned";

    [Fact]
    public async Task Effort_BinaryToggle_MapLess_TypedOn_PersistsTheMaxSentinel()
    {
        //with no map the capability comes from the template sniff alone. the toggle works every session, so its default must be persistable too.
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"qwen36-model"}""");
        WriteModel("qwen36-model", port: port);   //this model profile has no thinking map.
        WriteRole("generalist", "{}");
        server.PropsResponse = new FakeResponse(Body:
            $$"""{"model_path":"m.gguf","chat_template":"{{ToggleOnlyTemplate}}"}""");

        Console.SetIn(new StringReader("/effort on\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("saved as qwen36-model's default", stdout.ToString());
        Assert.Equal("max", ReadProfile("qwen36-model").GetProperty("default_effort").GetString());
    }

    [Fact]
    public async Task Effort_BinaryToggle_MapLess_PersistedMaxDefault_AppliesAtTheNextLaunch()
    {
        //the max sentinel passes a check that only asks whether the value is none. a fresh launch sends the flag from the persisted default alone
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"qwen36-model"}""");
        WriteModel("qwen36-model", port: port, profileExtra: "\"default_effort\":\"max\"");
        WriteRole("generalist", "{}");
        server.PropsResponse = new FakeResponse(Body:
            $$"""{"model_path":"m.gguf","chat_template":"{{ToggleOnlyTemplate}}"}""");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("hi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var kwargs = server.LastRequestBody!.Value.GetProperty("chat_template_kwargs");
        Assert.True(kwargs.GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task Effort_TypedLevel_OnANoneCapabilityModel_Refuses()
    {
        //the sniff fails to none and no map exists, so there is nothing to act on. a typed level must refuse the way bare /effort does instead of pretending to apply
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:65505"}},"default_endpoint":"local","default_model":"plain-model"}""");
        WriteModel("plain-model", port: 65505);   //this model profile has no thinking map.
        WriteRole("generalist", "{}");

        Console.SetIn(new StringReader("/effort high\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromMilliseconds(100));   //nothing listens on this port and windows retries a refused connect for 2 s, so the probes give up at once
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("this model doesn't support changing the reasoning mode.", stdout.ToString());
    }

    //a template that always emits a think tag and names neither switch is how the sniff reads always-on
    private const string AlwaysOnTemplate = "<think> with no switch mentioned at all";

    [Fact]
    public async Task Effort_OnAnAlwaysOnModel_Refuses()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"always-thinks"}""");
        WriteModel("always-thinks", port: port);   //the profile sets no thinking map, so the template sniff decides.
        WriteRole("generalist", "{}");
        server.PropsResponse = new FakeResponse(Body:
            $$"""{"model_path":"m.gguf","chat_template":"{{AlwaysOnTemplate}}"}""");

        Console.SetIn(new StringReader("/effort\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("this model doesn't support changing the reasoning mode.", stdout.ToString());
    }

    [Fact]
    public async Task ModelSwitch_FromNoMapToMultiLevel_ArrivesAsLevels_WithTheEntryInTheRequest()
    {
        //a switch into a multi-level map must recompute the capability. otherwise the toggle path overwrites the fresh map body next turn
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"plain-model"}""");
        WriteModel("plain-model", port: port);   //this model profile has no thinking map.
        WriteModel("qwen38-model", port: port, profileExtra: Qwen38ThinkingMap);
        WriteRole("generalist", "{}");
        server.PropsResponse = new FakeResponse(Body:
            $$"""{"model_path":"m.gguf","chat_template":"{{BothSwitchesTemplate}}"}""");
        server.Enqueue(Completion("ok"));

        Console.SetIn(new StringReader("/model qwen38-model\nhi\n/quit\n"));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("model: qwen38-model", stdout.ToString());
        var kwargs = server.LastRequestBody!.Value.GetProperty("chat_template_kwargs");
        Assert.Equal("medium", kwargs.GetProperty("reasoning_effort").GetString());
        Assert.True(kwargs.GetProperty("enable_thinking").GetBoolean());
    }

    //the one-shot run that writes a file, the mutating call that a grant or wild mode decides
    private async Task<(int Exit, string Stdout, string Stderr, string ToolText)> RunWriteOneShot(FakeOpenAiServer server)
    {
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(ToolCallThenStop("c1", "write_file", "{\"path\":\"wild-out.txt\",\"content\":\"x\"}"));
        server.Enqueue(Completion("all done"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "write the file" });
        var messages = server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        var toolText = messages.Single(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
        return (exit, stdout.ToString(), stderr.ToString(), toolText);
    }

    //a repository that ships its own permissions file with wild on gets nothing from it: the write is refused with no terminal to ask
    [Fact]
    public async Task Launch_ProjectWildTrue_IsIgnored_TheWriteIsRefused()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        Directory.CreateDirectory(Path.Combine(_cwd, ".gatto"));
        File.WriteAllText(Path.Combine(_cwd, ".gatto", "permissions.json"),
            """{"shell_prefixes":["git"],"write_dirs":["C:\\"],"wild":true}""");

        var (exit, stdout, stderr, toolText) = await RunWriteOneShot(server);

        Assert.Equal(0, exit);
        Assert.StartsWith("blocked: no interactive terminal", toolText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_cwd, "wild-out.txt")));
        Assert.DoesNotContain("wild mode: ON", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("wild mode", stdout, StringComparison.Ordinal);
    }

    //a .gatto.json that forbids wild mode wins over wild set in the home: the write is refused and the launch says which file forbids it
    [Fact]
    public async Task Launch_HomeWildTrue_ForbiddenByTheProject_TheWriteIsRefused_AndTheNoticeNamesTheFile()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        PermissionStore.Load(_home, _cwd, out _).SetWild(true, persist: true);
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"wild":false}""");

        var (exit, _, stderr, toolText) = await RunWriteOneShot(server);

        Assert.Equal(0, exit);
        Assert.StartsWith("blocked: no interactive terminal", toolText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_cwd, "wild-out.txt")));
        Assert.Contains($"wild mode: off, {Path.Combine(_cwd, ".gatto.json")} sets \"wild\": false for this project", stderr, StringComparison.Ordinal);
    }

    //wild mode set for this folder in the home runs the write unprompted, and the notice on stderr names where it is stored
    [Fact]
    public async Task Launch_HomeWildTrue_OneShotRunsMutatingToolUnprompted_NoticeOnStderr_StdoutPure()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        PermissionStore.Load(_home, _cwd, out _).SetWild(true, persist: true);

        var (exit, stdout, stderr, toolText) = await RunWriteOneShot(server);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("permission", toolText, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(_cwd, "wild-out.txt")));
        Assert.Contains("wild mode: ON for this project, stored in your gatto home", stderr, StringComparison.Ordinal);
        Assert.Contains("all done", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("wild mode", stdout, StringComparison.Ordinal);
    }

    //a full launch covers wiring the unit tests cannot see. it checks the index read, the consent switch, and a model's own index_budget

    //seed project memory with one file per fact, named fact-0.md onward, so the composed order by filename matches the order given
    private void WriteMemoryFacts(params string[] facts)
    {
        var dir = Path.Combine(_cwd, ".gatto", "memory");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < facts.Length; i++)
            File.WriteAllText(Path.Combine(dir, $"fact-{i}.md"), facts[i] + "\n");
    }

    private static string SystemTextOf(FakeOpenAiServer server) =>
        server.LastRequestBody!.Value.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;

    private static IReadOnlyList<string> ToolNamesOf(FakeOpenAiServer server) =>
        server.LastRequestBody!.Value.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()!)
            .ToList();

    private async Task<FakeOpenAiServer> LaunchWithMemoryAsync(string configExtra = "", string? profileExtra = null)
    {
        var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"{{{configExtra}}}}""");
        WriteModel("test-model", port: 1235, profileExtra: profileExtra);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("ok"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        return server;
    }

    [Fact]
    public async Task Launch_MemoryIndex_RidesThePrefixWithTheWriteNudgeAndTheTool()
    {
        WriteMemoryFacts("- PS 5.1 has no && operator");
        await using var server = await LaunchWithMemoryAsync();

        var system = SystemTextOf(server);
        //the block names its directory absolutely, a relative path sends the model astray. the assertion matches the exact chrome, so the line can't go missing
        Assert.Contains(
            "## Memory (project notes gatto keeps between sessions):\n"
            + $"[stored in {Gatto.Core.Memory.MemoryDir.DirFor(_cwd)} — one file per fact]\n"
            + "[notes were true when written — verify a path, flag, or version before relying on one]\n"
            + "- PS 5.1 has no && operator",
            system, StringComparison.Ordinal);
        Assert.Contains(RoleComposition.MemoryNudge, system, StringComparison.Ordinal);
        Assert.Contains("memory_write", ToolNamesOf(server));
        //the -p one-shot composes the same prompt as the REPL (recall of project notes helps a one-shot, and the write path is turn-scoped)
    }

    [Fact]
    public async Task Launch_NoMemoryIndex_StillNudges_ButComposesNoBlock()
    {
        //with no fact on disk yet the nudge must stay armed, so the first fact can ever be written.
        await using var server = await LaunchWithMemoryAsync();

        var system = SystemTextOf(server);
        Assert.DoesNotContain("## Memory", system, StringComparison.Ordinal);
        Assert.Contains(RoleComposition.MemoryNudge, system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_MemoryDisabled_NoBlock_NoNudge_NoTool()
    {
        WriteMemoryFacts("- a fact that must not be read");
        await using var server = await LaunchWithMemoryAsync(configExtra: ""","memory":{"enabled":false}""");

        var system = SystemTextOf(server);
        Assert.DoesNotContain("## Memory", system, StringComparison.Ordinal);
        Assert.DoesNotContain("a fact that must not be read", system, StringComparison.Ordinal);
        Assert.DoesNotContain(RoleComposition.MemoryNudge, system, StringComparison.Ordinal);
        Assert.DoesNotContain("memory_write", ToolNamesOf(server));
    }

    [Fact]
    public async Task Launch_ModelIndexBudget_OverridesTheGlobalOne_AndTheBlockCarriesTheTruncationNote()
    {
        //each fact is ~11 chars, so the model's 3-token budget of 12 chars admits exactly the first line.
        WriteMemoryFacts("- aaaaaaaa", "- bbbbbbbb", "- cccccccc");
        await using var server = await LaunchWithMemoryAsync(
            configExtra: ""","memory":{"index_budget":1000}""",
            profileExtra: "\"memory\":{\"index_budget\":3}");

        var system = SystemTextOf(server);
        Assert.Contains("- aaaaaaaa", system, StringComparison.Ordinal);
        Assert.DoesNotContain("- bbbbbbbb", system, StringComparison.Ordinal);
        Assert.Contains("[index truncated at budget — 2 facts not shown]", system, StringComparison.Ordinal);
    }

    //these run the real launch, a replica stays green if the real load moves out. no --yes is passed, so a permission regression fails loudly
    private void ArrangeMemoryReplLaunch(FakeOpenAiServer server)
    {
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(ToolCallThenStop("c1", "memory_write",
            "{\"slug\":\"the-new-fact\",\"content\":\"- the new fact\",\"mode\":\"write\"}"));
        server.Enqueue(Completion("banked"));
        server.Enqueue(Completion("second turn"));
    }

    [Fact]
    public async Task MemoryWrite_ThenSlashNew_TheFreshPrefixCarriesTheNewFact()
    {
        await using var server = new FakeOpenAiServer();
        ArrangeMemoryReplLaunch(server);

        Console.SetIn(new StringReader("remember this\n/new\nagain\n/quit\n"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("- the new fact",
            File.ReadAllText(Path.Combine(_cwd, ".gatto", "memory", "the-new-fact.md")), StringComparison.Ordinal);
        //the fact appears in the prefix of the second turn, since /new re-read it from disk
        Assert.Contains("- the new fact", SystemTextOf(server), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryWrite_WithoutCrossingABoundary_LeavesThePrefixUnmoved()
    {
        //a mid-session memory_write must never reach the prompt, since a re-prefill has to be user-chosen. this control runs the same session without /new
        await using var server = new FakeOpenAiServer();
        ArrangeMemoryReplLaunch(server);

        Console.SetIn(new StringReader("remember this\nagain\n/quit\n"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("- the new fact",
            File.ReadAllText(Path.Combine(_cwd, ".gatto", "memory", "the-new-fact.md")), StringComparison.Ordinal);
        Assert.DoesNotContain("- the new fact", SystemTextOf(server), StringComparison.Ordinal);
    }

    //a malformed .gatto.json must not end the session, editing it mid-session is a workflow. these tests drive the real closures

    //scripted stdin runs onTrigger just before handing back the trigger line. it is the only way to make a file appear mid-session when all input is a fixed script
    private sealed class TriggeredInput(string[] lines, string trigger, Action onTrigger) : TextReader
    {
        private readonly Queue<string> _lines = new(lines);
        private bool _fired;

        public override string? ReadLine()
        {
            if (_lines.Count == 0) return null;
            var next = _lines.Dequeue();
            if (!_fired && next == trigger) { _fired = true; onTrigger(); }
            return next;
        }
    }

    private const string MarkerAlpha = "LAUNCH-CONTEXT-ALPHA";
    private const string MarkerBeta = "EDITED-CONTEXT-BETA";
    private const string MalformedProjectFile = "{\"context_files\": {\"compat\": tru";

    //this launch scaffolding seeds GATTO.md with a marker that shows in the composed prefix. a test can then assert which prefix went out
    private void ArrangeContextFileLaunch(FakeOpenAiServer server, params string[] modelIds)
    {
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        var port = new Uri(server.BaseUrl).Port;
        foreach (var id in modelIds) WriteModel(id, port: port);
        WriteRole("generalist", $$"""{"model":"{{modelIds[0]}}"}""");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), MarkerAlpha);
    }

    //rewrite GATTO.md and drop a malformed .gatto.json beside it. the rewrite is the discriminator, so the old marker proves the last-good prefix
    private void BreakProjectFileMidSession()
    {
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), MarkerBeta);
        File.WriteAllText(Path.Combine(_cwd, ProjectFileConfig.FileName), MalformedProjectFile);
    }

    [Fact]
    public async Task MalformedProjectFileMidSession_SlashNew_WarnsAndKeepsTheLastGoodPrefix()
    {
        await using var server = new FakeOpenAiServer();
        ArrangeContextFileLaunch(server, "test-model");
        server.Enqueue(Completion("first"));
        server.Enqueue(Completion("second"));

        var stderr = new StringWriter();
        Console.SetError(stderr);
        Console.SetOut(new StringWriter());
        Console.SetIn(new TriggeredInput(
            new[] { "hello", "/new", "again", "/quit" }, "/new", BreakProjectFileMidSession));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal(2, server.RequestCount);
        var warned = stderr.ToString();
        Assert.Contains("context files not re-read", warned, StringComparison.Ordinal);
        Assert.Contains("is not valid JSON", warned, StringComparison.Ordinal);   //the warning must include the parse message itself.
        Assert.Contains(ProjectFileConfig.FileName, warned, StringComparison.Ordinal);

        //the degraded prefix is the last one composed in full, so a mid-recompose never picks up the edited GATTO.md
        var system = SystemTextOf(server);
        Assert.Contains(MarkerAlpha, system, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerBeta, system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedProjectFileMidSession_SlashModel_FailsTheSwitch_ArmsNothing()
    {
        await using var server = new FakeOpenAiServer();
        ArrangeContextFileLaunch(server, "model-a", "model-b");
        server.Enqueue(Completion("first"));
        server.Enqueue(Completion("second"));

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(new StringWriter());
        Console.SetIn(new TriggeredInput(
            new[] { "hello", "/model model-b", "again", "/quit" }, "/model model-b", BreakProjectFileMidSession));

        var configPath = Path.Combine(_home, "gatto.json");
        var configBefore = File.ReadAllText(configPath);

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal(2, server.RequestCount);
        Assert.Contains("is not valid JSON", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(configBefore, File.ReadAllText(configPath));
        //a failed switch arms nothing, so the next turn still runs the launch prefix.
        var system = SystemTextOf(server);
        Assert.Contains(MarkerAlpha, system, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerBeta, system, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryTruncationWarning_IsTheSpecSentence()
    {
        //one source feeds the launch line and every reload-boundary warning. the sentence counts facts and names the directory to prune, since INDEX.md no longer exists
        Assert.Equal(@"memory index over budget: 4 facts not shown; prune .gatto\memory\",
            GattoApp.MemoryTruncationWarning(4));
    }

    [Fact]
    public void MemoryTruncationWarning_NamesNoRetiredFile()
    {
        //no user-facing memory text may name INDEX.md, since that file does not exist
        Assert.DoesNotContain("INDEX.md", GattoApp.MemoryTruncationWarning(1), StringComparison.OrdinalIgnoreCase);
    }

    //these tests drive ModelSwitch.Switch directly, its closure sits behind a launch. each model gets a distinct model_path for MatchesLoaded

    private const string ModelSwitchBaseUrl = "http://127.0.0.1:1235";

    private static string ModelPathFor(string modelId) => modelId + ".gguf";

    private static readonly Dictionary<string, int> KnownContexts = new()
    {
        ["qwen3.6-35b"] = 8192,
        ["gemma-4-26b"] = 131072,
    };

    private void WriteModelModel(string id, int port, int? context = null)
    {
        var dir = Path.Combine(_home, "models", id);
        Directory.CreateDirectory(dir);
        var ctx = context ?? (KnownContexts.TryGetValue(id, out var c) ? c : 8192);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{"files": [{ "path": "{{ModelPathFor(id)}}", "active": true }],"port":{{port}},"context":{{ctx}}}""");
    }

    //build a home whose generalist role and default model point to the first id. every id gets a profile with a distinct model_path
    private string HomeWithModels(params string[] modelIds)
    {
        UseHome();
        WriteConfig(
            $$$"""{"endpoints":{"local":{"base_url":"{{{ModelSwitchBaseUrl}}}"}},"default_endpoint":"local","default_model":"{{{modelIds[0]}}}"}""");
        foreach (var id in modelIds) WriteModelModel(id, port: 1235);
        WriteRole("generalist", $$"""{"model":"{{modelIds[0]}}"}""");
        return _home;
    }

    //mirror the production order, decide first and probe after a successful switch. probeReports synthesizes what a real probe returns, with no HTTP
    private sealed record SwitchHarnessResult(
        bool Success, string ModelId, string? SystemText, int? ContextBudget,
        string? Message);

    private SwitchHarnessResult RunSwitchModel(string home, string requestedModel)
    {
        var config = GattoConfig.Load(home);
        var role = RoleFile.Load(Path.Combine(home, "roles"), "generalist");
        var modelsDir = Path.Combine(home, "models");
        var baseUrl = config.Endpoints["local"].BaseUrl;

        var outcome = ModelSwitch.Decide(
            modelsDir, home, requestedModel, role, Array.Empty<(string, string)>(), null, baseUrl);
        if (!outcome.Success || outcome.Model is null)
            return new SwitchHarnessResult(outcome.Success, outcome.ModelId, outcome.SystemText,
                outcome.ContextBudget, outcome.Message);

        //the harness goes straight from the decision to persisting, since it has no server to swap. persisting is a step of its own, after the decision
        if (ModelSwitch.Persist(home, "local", outcome.Model.Id) is { } problem)
            return new SwitchHarnessResult(false, outcome.ModelId, null, null, problem);

        return new SwitchHarnessResult(true, outcome.ModelId, outcome.SystemText,
            outcome.ContextBudget, outcome.Message);
    }

    [Fact]
    public void SwitchModel_UnknownModel_FailsWithoutChangingAnything()
    {
        var home = HomeWithModels("qwen3.6-35b", "gemma-4-26b");
        var before = File.ReadAllText(Path.Combine(home, "gatto.json"));

        var result = RunSwitchModel(home, "no-such-model");

        Assert.False(result.Success);
        Assert.Contains("no-such-model", result.Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(home, "gatto.json")));
    }

    [Fact]
    public void SwitchModel_PortMismatch_RefusesAndSaysWhy()
    {
        //the client cannot serve another port, since OpenAiCompatClient fixes its base url at construction
        var home = HomeWithModels("qwen3.6-35b");
        WriteModelModel("other-port", port: 8080);

        var result = RunSwitchModel(home, "other-port");

        Assert.False(result.Success);
        Assert.Contains("8080", result.Message);
        Assert.Contains("restart gatto", result.Message);
    }

    [Fact]
    public void SwitchModel_Success_PersistsDefaultModelAndReturnsNewBudget()
    {
        var home = HomeWithModels("qwen3.6-35b", "gemma-4-26b");   //the seeded gemma-4-26b profile sets a context of 131072

        var result = RunSwitchModel(home, "gemma-4-26b");

        Assert.True(result.Success);
        Assert.Equal("gemma-4-26b", result.ModelId);
        Assert.Equal(131072, result.ContextBudget);
        Assert.NotNull(result.SystemText);

        var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "gatto.json"))).RootElement;
        Assert.Equal("gemma-4-26b", cfg.GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
    }

    //a server that splits its context across slots gives each request less than the profile, and -p elides and compacts against that
    [Theory]
    [InlineData(true, 4096, "server reports 4096. Using it (the profile says 8192)")]
    [InlineData(true, 16384, null)]
    [InlineData(false, 4096, null)]
    public async Task OneShot_TakesTheSmallerWindowOfTheServerHoldingItsModel(bool ours, int nCtx, string? line)
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        var port = new Uri(server.BaseUrl).Port;
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: port);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        //the helper's profile names m.gguf bare, and the match reads a bare path against the process folder
        var held = ours ? Path.GetFullPath("m.gguf") : Path.Combine(_home, "elsewhere", "other.gguf");
        server.PropsResponse = new FakeResponse(Status: 200, Body: $$$"""
            {"model_path": {{{System.Text.Json.JsonSerializer.Serialize(held)}}},"default_generation_settings":{"n_ctx":{{{nCtx}}}}}
            """);
        server.Enqueue(Completion("response"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(new[] { "-p", "hello" });

        Assert.True(exit == 0, $"exit {exit}, stderr: {stderr}");
        Assert.True(server.PropsCalled, "-p must read the server's window");
        if (line is null) Assert.DoesNotContain("server reports", stderr.ToString(), StringComparison.Ordinal);
        else Assert.Contains(line, stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("server reports", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneShot_AnswersWithTheServerUnread()
    {
        //a server whose /props says nothing usable leaves the profile's window and the run goes on
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        var port = new Uri(server.BaseUrl).Port;
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: port);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.PropsResponse = new FakeResponse(Status: 200, Body: "{\"model\":\"test-model.gguf\"}");
        server.Enqueue(Completion("response"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Environment.SetEnvironmentVariable("GATTO_DEBUG", "1");
        try
        {
            var exit = await GattoApp.RunAsync(new[] { "-p", "hello" });
            if (exit != 0)
                Assert.Fail($"exit {exit}, stderr: {stderr}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GATTO_DEBUG", null);
        }
        Assert.DoesNotContain("server reports", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("response", stdout.ToString());
    }

    //give each model a file with its own name. the shared helper writes m.gguf for every model, so a served file would match both and the mismatch could never arise
    private void WriteModelWithOwnFile(string id, int port)
    {
        var dir = Path.Combine(_home, "models", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{"files": [{ "path": "{{id}}.gguf", "active": true }],"port":{{port}},"context":8192}""");
    }

    //when another model serves, gatto -m X must refuse with exit 1 and name it. the oracle is all three, exit 1, the sentence, and no chat request
    [Fact]
    public async Task DashM_WHILE_ANOTHER_MODEL_SERVES_REFUSES_AND_NEVER_ATTACHES()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","default_model":"model-y"}
            """);
        WriteRole("generalist", "{}");
        WriteModelWithOwnFile("model-x", port: 1);
        WriteModelWithOwnFile("model-y", port: 1);
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "model-y.gguf"}""");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetIn(new StringReader("/quit\n"));
        Console.SetOut(stdout);
        Console.SetError(stderr);

        var exit = await GattoApp.RunAsync(new[] { "-m", "model-x" });

        Assert.Equal(1, exit);
        Assert.Contains("model-y", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("gatto serve stop", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("without -m", stderr.ToString(), StringComparison.Ordinal);
        //the refusal returns before the REPL is built, so the probe is the only request the server ever sees.
        Assert.True(server.PropsCalled, "the probe must have run, or the refusal proves nothing");
        Assert.Equal(0, server.RequestCount);
    }

    //the control proves not every -m is refused: -m X while X itself serves must attach with exit 0 and no refusal
    [Fact]
    public async Task DashM_WHILE_THAT_SAME_MODEL_SERVES_ATTACHES()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","default_model":"model-y"}
            """);
        WriteRole("generalist", "{}");
        WriteModelWithOwnFile("model-x", port: 1);
        WriteModelWithOwnFile("model-y", port: 1);
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "model-x.gguf"}""");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetIn(new StringReader("/quit\n"));
        Console.SetOut(stdout);
        Console.SetError(stderr);

        var exit = await GattoApp.RunAsync(new[] { "-m", "model-x" });

        Assert.Equal(0, exit);
        //the assertion must use without -m, which only the refusal prints. the hint gatto serve stop also prints on an attaching launch, so it cannot stand for a refusal
        Assert.DoesNotContain("without -m", stderr.ToString(), StringComparison.Ordinal);
    }

    //a gatto with no -m has not claimed which model it wants, so the mismatch refusal must not reach it
    [Fact]
    public async Task NO_DASH_M_OPENS_ON_WHATEVER_IS_SERVING()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","default_model":"model-x"}
            """);
        WriteRole("generalist", "{}");
        WriteModelWithOwnFile("model-x", port: 1);
        WriteModelWithOwnFile("model-y", port: 1);
        //the server holds Y while the default is X, a mismatch nobody asked for by name.
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "model-y.gguf"}""");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetIn(new StringReader("/quit\n"));
        Console.SetOut(stdout);
        Console.SetError(stderr);

        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        //the assertion must use without -m, which only the refusal prints. the hint gatto serve stop also prints on an attaching launch, so it cannot stand for a refusal
        Assert.DoesNotContain("without -m", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains($"using model-y, served on :{new Uri(server.BaseUrl).Port}", stdout.ToString(), StringComparison.Ordinal);
    }

    //a profile written whole, so a test can give a model its own window, port or second quant
    private void WriteProfile(string id, string json)
    {
        var dir = Path.Combine(_home, "models", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"), json);
    }

    //a -p launch on the local endpoint whose server answers /props with the given body, so the request names the model the launch armed
    private async Task<(int Exit, string Stdout, string Stderr, FakeOpenAiServer Server)> OneShotAgainst(
        string? props, bool pinnedBaseUrl = true, params string[] extraArgs)
    {
        var server = new FakeOpenAiServer();
        UseHome();
        var port = new Uri(server.BaseUrl).Port;
        var local = pinnedBaseUrl ? $$$"""{"base_url":"{{{server.BaseUrl}}}"}""" : "{}";
        WriteConfig($$$"""{"endpoints":{"local":{{{local}}}},"default_endpoint":"local","default_model":"model-x"}""");
        WriteRole("generalist", "{}");
        //auto_serve off, so an unanswered probe can never start a process on this machine
        WriteProfile("model-x", $$$"""{"files":[{"path":"model-x.gguf","active":true}],"port":{{{port}}},"context":8192,"auto_serve":false}""");
        WriteProfile("model-y", $$$"""{"files":[{"path":"model-y.gguf","active":true},{"path":"model-y-q8.gguf"}],"port":{{{(pinnedBaseUrl ? port : 1)}}},"context":16384,"auto_serve":false}""");
        if (props is not null) server.PropsResponse = new FakeResponse(Body: props);
        server.Enqueue(Completion("answer"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync([.. extraArgs, "-p", "hi"]);
        return (exit, stdout.ToString(), stderr.ToString(), server);
    }

    private static string ModelOf(FakeOpenAiServer server) => server.LastRequestBody!.Value.GetProperty("model").GetString()!;

    //the server holds a shelf model other than the default, so the launch arms that one and its window, and says so on stderr under -p
    [Fact]
    public async Task A_LAUNCH_ARMS_THE_SHELF_MODEL_THE_SERVER_HOLDS()
    {
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var (exit, stdout, stderr, server) = await OneShotAgainst(
            """{"model_path":"model-y.gguf","default_generation_settings":{"n_ctx":4096}}""");
        await using var owned = server;

        Assert.Equal(0, exit);
        Assert.Equal("model-y", ModelOf(server));
        Assert.Contains($"using model-y, served on :{new Uri(server.BaseUrl).Port}", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("using model-y", stdout, StringComparison.Ordinal);
        //the window line reads the armed profile, so model-y's 16384 proves the window was built from the served model
        Assert.Contains("the profile says 16384", stderr, StringComparison.Ordinal);
        var session = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal));
        Assert.Equal("model-y", Gatto.Core.Session.SessionStore.LoadBaseline(session)!.Model);
    }

    //the same arm interactively, said once and inside the session, since the launch screen goes unread once a server is up
    [Fact]
    public async Task AN_INTERACTIVE_LAUNCH_ARMS_THE_SERVED_MODEL_AND_SAYS_ONE_LINE()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"model-x"}""");
        WriteRole("generalist", "{}");
        WriteModelWithOwnFile("model-x", port: 1);
        WriteModelWithOwnFile("model-y", port: 1);
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "model-y.gguf"}""");
        server.Enqueue(Completion("answer"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetIn(new StringReader("hello\n/quit\n"));
        Console.SetOut(stdout);
        Console.SetError(stderr);

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal("model-y", ModelOf(server));
        var text = stdout.ToString() + stderr.ToString();
        var line = Assert.Single(text.Split('\n'), l => l.Contains("using model-y", StringComparison.Ordinal));
        Assert.Contains($"using model-y, served on :{new Uri(server.BaseUrl).Port}", line, StringComparison.Ordinal);
        //the session's own opening line comes first, so the line is one the session says and not the launch screen
        Assert.True(stdout.ToString().IndexOf("(/quit to exit", StringComparison.Ordinal) is >= 0 and var opened
            && stdout.ToString().IndexOf("using model-y", StringComparison.Ordinal) > opened, "the line is not inside the session");
        Assert.DoesNotContain("the server is running", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reusing", text, StringComparison.Ordinal);
    }

    //the default being served is no news, so its line stays on the launch screen and the session says nothing about it
    [Fact]
    public async Task A_LAUNCH_ON_THE_SERVED_DEFAULT_SAYS_NOTHING_INSIDE_THE_SESSION()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"model-x"}""");
        WriteRole("generalist", "{}");
        WriteModelWithOwnFile("model-x", port: 1);
        WriteModelWithOwnFile("model-y", port: 1);
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "model-x.gguf"}""");
        var stdout = new StringWriter();
        Console.SetIn(new StringReader("/quit\n"));
        Console.SetOut(stdout);
        Console.SetError(new StringWriter());

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var text = stdout.ToString();
        var at = text.IndexOf($"using model-x, served on :{new Uri(server.BaseUrl).Port}", StringComparison.Ordinal);
        Assert.True(at >= 0, "the launch line is missing");
        Assert.True(at < text.IndexOf("(/quit to exit", StringComparison.Ordinal), "the default's line moved inside the session");
        Assert.Single(text.Split('\n'), l => l.Contains("using model-x", StringComparison.Ordinal));
    }

    //a quant is a setting, so a file of model-y's list that is not the active one still arms model-y
    [Fact]
    public async Task A_NON_ACTIVE_QUANT_CLAIMS_ITS_MODEL()
    {
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var (exit, _, stderr, server) = await OneShotAgainst("""{"model_path":"model-y-q8.gguf"}""");
        await using var owned = server;

        Assert.Equal(0, exit);
        Assert.Equal("model-y", ModelOf(server));
        Assert.Contains("using model-y, served on", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("not on the shelf", stderr, StringComparison.Ordinal);
    }

    //a file no profile claims has no profile to arm, so the default stays and the one line names the file
    [Fact]
    public async Task A_FILE_OFF_THE_SHELF_KEEPS_THE_DEFAULT()
    {
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var (exit, _, stderr, server) = await OneShotAgainst("""{"model_path":"stranger-Q4_K_M.gguf"}""");
        await using var owned = server;

        Assert.Equal(0, exit);
        Assert.Equal("model-x", ModelOf(server));
        Assert.Contains($"using model-x, the server on :{new Uri(server.BaseUrl).Port} holds stranger-Q4_K_M, not on the shelf", stderr, StringComparison.Ordinal);
    }

    //model-y declares another port and no base_url is pinned, so arming it would send every request where no server answered
    [Fact]
    public async Task A_SHELF_MODEL_ON_ANOTHER_PORT_KEEPS_THE_DEFAULT()
    {
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var (exit, _, stderr, server) = await OneShotAgainst("""{"model_path":"model-y.gguf"}""", pinnedBaseUrl: false);
        await using var owned = server;

        Assert.Equal(0, exit);
        Assert.Equal("model-x", ModelOf(server));
        Assert.Contains($"using model-x, the server on :{new Uri(server.BaseUrl).Port} holds model-y", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("not on the shelf", stderr, StringComparison.Ordinal);
    }

    //a server that names no file cannot be matched, so the line never says it serves the default
    [Fact]
    public async Task A_SERVER_THAT_NAMES_NO_FILE_KEEPS_THE_DEFAULT()
    {
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var (exit, _, stderr, server) = await OneShotAgainst("""{"default_generation_settings":{"n_ctx":8192}}""");
        await using var owned = server;

        Assert.Equal(0, exit);
        Assert.Equal("model-x", ModelOf(server));
        Assert.Contains($"using model-x, the server on :{new Uri(server.BaseUrl).Port} does not name its file", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("served on", stderr, StringComparison.Ordinal);
    }

    //an unanswered probe arms nothing and says nothing about a server
    [Fact]
    public async Task AN_UNANSWERED_PROBE_CHANGES_NOTHING()
    {
        var (exit, _, stderr, server) = await OneShotAgainst(props: null);
        await using var owned = server;

        Assert.Equal(0, exit);
        Assert.True(server.PropsCalled, "the probe must have run, or the silence proves nothing");
        Assert.Equal("model-x", ModelOf(server));
        Assert.DoesNotContain("using model-", stderr, StringComparison.Ordinal);
    }

    //a one-shot probes now, so -m X against a server holding Y refuses there as it does interactively
    [Fact]
    public async Task DashM_UNDER_P_WHILE_ANOTHER_MODEL_SERVES_REFUSES()
    {
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        var (exit, stdout, stderr, server) = await OneShotAgainst("""{"model_path":"model-y.gguf"}""", true, "-m", "model-x");
        await using var owned = server;

        Assert.Equal(1, exit);
        Assert.Contains(ServeNotice.Refusing("model-y.gguf"), stderr, StringComparison.Ordinal);
        Assert.Equal("", stdout);
        Assert.Equal(0, server.RequestCount);
    }

    //with the key on, a quit calls the stop, which deletes an unreadable record. with the key off the record stays, and no server means no stop
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_QUIT_STOPS_THE_SERVER_ONLY_WITH_STOP_SERVER_ON_EXIT(bool key, bool serverUp)
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        var flag = key ? "true" : "false";
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","default_model":"model-x","stop_server_on_exit":{{{flag}}}}
            """);
        WriteRole("generalist", "{}");
        //with no server up the fake answers /props with 404, and a declined auto serve keeps the launch from starting one
        if (serverUp)
        {
            WriteModelWithOwnFile("model-x", port: 1);
            server.PropsResponse = new FakeResponse(Body: """{"model_path": "model-x.gguf"}""");
        }
        else WriteModel("model-x", port: 1, profileExtra: "#auto_serve#: false".Replace('#', '"'));
        //an unreadable record is removed before any process lookup, so the stop reaches no real process on this machine.
        var record = Path.Combine(_home, "serve.json");
        File.WriteAllText(record, "{ not json");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetIn(new StringReader("/quit"));
        Console.SetOut(stdout);
        Console.SetError(stderr);

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal(!key, File.Exists(record));
        //the end of a session reaches stdout in one order, and stderr holds none of it
        Assert.Equal(key && serverUp, stdout.ToString().Contains("gatto won't close a server gatto didn't start", StringComparison.Ordinal));
        Assert.Equal(!key && serverUp, stdout.ToString().Contains("model still loaded", StringComparison.Ordinal));
        Assert.DoesNotContain("gatto won't close", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("model still loaded", stderr.ToString(), StringComparison.Ordinal);
    }

    //the resumed session's path is a line a person reads, so it goes to stdout. under -p it goes to stderr, beside the -p stdout contract
    [Fact]
    public async Task AN_INTERACTIVE_RESUME_NAMES_ITS_SESSION_ON_STDOUT()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","default_model":"model-x"}
            """);
        WriteRole("generalist", "{}");
        WriteModel("model-x", port: 1, profileExtra: "#auto_serve#: false".Replace('#', '"'));

        //a one-shot turn leaves the session the interactive launch then resumes
        server.Enqueue(Completion("first answer"));
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "first question" }));
        var session = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl")
            .Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetIn(new StringReader("/quit"));
        Console.SetOut(stdout);
        Console.SetError(stderr);

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--continue" }));

        var line = $"gatto: session {Path.GetFullPath(session)}";
        Assert.Contains(line, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(line, stderr.ToString(), StringComparison.Ordinal);
    }

    //a connect session never used the server this home records, so the key leaves that record alone.
    [Fact]
    public async Task A_CONNECT_SESSION_STOPS_NOTHING_WITH_STOP_SERVER_ON_EXIT()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"cloudy":{"base_url":"{{{server.BaseUrl}}}","context":32768}},
             "default_endpoint":"cloudy","default_model":"served-model","stop_server_on_exit":true}
            """);
        WriteRole("generalist", "{}");
        server.PropsResponse = new FakeResponse(Body: """{"model_path": "theirs.gguf"}""");
        var record = Path.Combine(_home, "serve.json");
        File.WriteAllText(record, "{ not json");
        Console.SetIn(new StringReader("/quit"));
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.True(File.Exists(record), "a connect session removed the record of the local server");
    }

    //the exit hint must price the whole shard set, model_path names one shard only. it runs the real launch, the defect was the number the site computes
    [Fact]
    public async Task THE_EXIT_HINT_PRICES_THE_WHOLE_SHARD_SET()
    {
        await using var server = new FakeOpenAiServer();
        using var probeRoom = GattoApp.ProbeDeadlineScope(TimeSpan.FromSeconds(30));   //the assertions read the probe's answer, so the suite's load must not turn them into a race
        UseHome();

        var weights = Directory.CreateDirectory(Path.Combine(_home, "weights")).FullName;
        string Shard(string name, long bytes)
        {
            var p = Path.Combine(weights, name);
            using var fs = File.Create(p);
            fs.SetLength(bytes);
            return p;
        }
        var one = Shard("m-00001-of-00003.gguf", 10_000_000);
        Shard("m-00002-of-00003.gguf", 100_000_000);
        Shard("m-00003-of-00003.gguf", 100_000_000);

        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"local","default_model":"model-x","stop_server_on_exit":false}
            """);
        WriteRole("generalist", "{}");
        WriteModelWithOwnFile("model-x", port: 1);
        server.PropsResponse = new FakeResponse(
            Body: $$"""{"model_path": "{{one.Replace("\\", "\\\\")}}"}""");

        var stdout = new StringWriter();
        Console.SetIn(new StringReader("/quit\n"));
        Console.SetOut(stdout);
        Console.SetError(new StringWriter());

        var exit = await GattoApp.RunAsync([]);
        var text = stdout.ToString();

        Assert.Equal(0, exit);
        //assert the hint first, so the size checks cannot pass vacuously.
        Assert.Contains("model still loaded (~0.2 GB)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<0.1 GB", text, StringComparison.Ordinal);
    }

    //every launch in the suite takes the run's own sandbox folders, so a test never reads the machine's NuGet folders or unplants a live session's label
    [Fact]
    public void NO_LAUNCH_UNDER_TEST_NAMES_THE_MACHINES_SANDBOX_FOLDERS()
    {
        var folders = Assert.IsType<(string NuGet, string HttpCache, string TempParent)>(GattoApp.SandboxFoldersDefault);
        foreach (var folder in new[] { folders.NuGet, folders.HttpCache, folders.TempParent })
            Assert.StartsWith(TestTempRoot.Root! + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
    }

    //a killed sandboxed session leaves its labels, so the next launch that opens a session removes them and says so once, in the session or on stderr under -p
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_LAUNCH_REMOVES_THE_LABELS_A_KILLED_SANDBOX_LEFT_AND_SAYS_SO_ONCE(bool oneShot)
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"model-x"}""");
        WriteRole("generalist", "{}");
        WriteModel("model-x", port: 1, profileExtra: "#auto_serve#: false".Replace('#', '"'));
        var folders = Directory.CreateTempSubdirectory("gatto-residue-").FullName;
        try
        {
            //the test's own NuGet and temp folders, so the launch never reads or unplants the machine's
            using var scope = GattoApp.SandboxFoldersScope(Path.Combine(folders, "nuget"), Path.Combine(folders, "cache"), Path.Combine(folders, "tmp"));
            Gatto.Core.Sandbox.SandboxGrants.Plant(_cwd, _home);
            var built = Path.Combine(_cwd, "built.exe");
            File.WriteAllText(built, "x");
            server.Enqueue(Completion("answer"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Console.SetIn(new StringReader("/quit\n"));
            Console.SetOut(stdout);
            Console.SetError(stderr);

            Assert.Equal(0, await GattoApp.RunAsync(oneShot ? ["-p", "hi"] : []));

            var said = (oneShot ? stderr : stdout).ToString();
            Assert.Single(said.Split('\n'), l => l.Contains(Gatto.Core.Sandbox.SandboxState.ResidueLine, StringComparison.Ordinal));
            Assert.False(Gatto.Core.Sandbox.SandboxGrants.Holds(_cwd));
            Assert.DoesNotContain(";;;LW)", Gatto.Core.Sandbox.SandboxGrants.Label(built), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(folders, true); } catch (IOException) { }
        }
    }

    //a scripted round in which the parent calls run_agent on one role
    private static FakeResponse RunAgentCall(string roleName)
    {
        var arguments = JsonSerializer.Serialize(JsonSerializer.Serialize(new { role = roleName, task = "go" }));
        return new(Frames: new[]
        {
            $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"t1\",\"function\":{{\"name\":\"run_agent\",\"arguments\":{arguments}}}}}]}},\"finish_reason\":null}}]}}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n",
            "data: [DONE]\n\n",
        });
    }

    private static string SystemOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;

    private static string LastToolResultOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;

    private static string RunAgentDescriptionOf(JsonElement body) =>
        body.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function"))
            .Single(f => f.GetProperty("name").GetString() == "run_agent").GetProperty("description").GetString()!;

    [Fact]
    public async Task A_ROLE_WANTING_ANOTHER_MODEL_IS_REFUSED_WITH_THE_CHILDS_SENTENCE()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteModel("other-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("other", "{\"model\":\"other-model\"}");
        server.Enqueue(RunAgentCall("other"));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "-p", "hi" }));

        Assert.Equal(2, server.RequestCount);   //the parent twice and no child
        Assert.Equal("role 'other' wants model 'other-model', this session serves 'test-model'",
            LastToolResultOf(server.RequestBodies[1]));
    }

    [Fact]
    public async Task A_ROLE_WANTING_ANOTHER_ENDPOINT_IS_REFUSED_WITH_THE_CHILDS_SENTENCE()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        //the role names no model and cloudy lists none the caller runs, so a model resolved before the endpoint check would be refused with the wrong sentence
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"},"cloudy":{"base_url":"http://127.0.0.1:9","models":["c-model"]}},
             "default_endpoint":"local"}
            """);
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("other", "{\"endpoint\":\"cloudy\"}");
        server.Enqueue(RunAgentCall("other"));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "-p", "hi" }));

        Assert.Equal(2, server.RequestCount);
        Assert.Equal("role 'other' wants endpoint 'cloudy', this session is on 'local'",
            LastToolResultOf(server.RequestBodies[1]));
    }

    //a local endpoint with no base_url gives each model its own port, so another model differs in URL too, and the sentence must name the model
    [Fact]
    public async Task ANOTHER_LOCAL_MODEL_ON_ITS_OWN_PORT_IS_REFUSED_WITH_THE_MODEL_SENTENCE()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: new Uri(server.BaseUrl).Port);
        WriteModel("other-model", port: 1);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("other", "{\"model\":\"other-model\"}");
        server.Enqueue(RunAgentCall("other"));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "-p", "hi" }));

        Assert.Equal(2, server.RequestCount);
        Assert.Equal("role 'other' wants model 'other-model', this session serves 'test-model'",
            LastToolResultOf(server.RequestBodies[1]));
    }

    //a local endpoint with no base_url reaches the model's port, so the early endpoint check must not compare its null against the session's URL
    [Fact]
    public async Task A_ROLE_NAMING_THE_SESSIONS_OWN_ENDPOINT_RUNS_WHEN_IT_HAS_NO_BASE_URL()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: new Uri(server.BaseUrl).Port);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("helper", "{\"endpoint\":\"local\"}");
        server.Enqueue(RunAgentCall("helper"));
        server.Enqueue(Completion("child done"));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "-p", "hi" }));

        Assert.Equal(3, server.RequestCount);
        Assert.Equal("child done", LastToolResultOf(server.RequestBodies[2]));
    }

    //a cloud endpoint serves every listed model on one URL, so a role naming another listed model runs on it instead of silently taking the caller's
    [Fact]
    public async Task A_CALLED_ROLES_MODEL_REACHES_THE_CHILD_REQUEST_ON_A_CLOUD_ENDPOINT()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"},"cloudy":{"base_url":"{{{server.BaseUrl}}}","models":["a-model","b-model"]}},
             "default_endpoint":"cloudy"}
            """);
        WriteModel("a-model", port: 1235);
        WriteModel("b-model", port: 1235);
        WriteRole("generalist", "{\"endpoint\":\"cloudy\",\"model\":\"a-model\"}");
        WriteRole("cheap", "{\"model\":\"b-model\"}");
        server.Enqueue(RunAgentCall("cheap"));
        server.Enqueue(Completion("child done"));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "-p", "hi" }));

        Assert.Equal(3, server.RequestCount);
        Assert.Equal("a-model", server.RequestBodies[0].GetProperty("model").GetString());
        Assert.Equal("b-model", server.RequestBodies[1].GetProperty("model").GetString());
        Assert.Contains("Model: b-model.", SystemOf(server.RequestBodies[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("a-model", SystemOf(server.RequestBodies[1]), StringComparison.Ordinal);
        Assert.Equal("a-model", server.RequestBodies[2].GetProperty("model").GetString());
    }

    //the map has no medium, so a child resolving b-model's own default lands on low, and only the session's level puts high on the wire
    [Fact]
    public async Task A_ROLE_ON_ANOTHER_MODEL_TAKES_THE_CALLERS_LEVEL()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$$$$"""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"},
              "cloudy":{"base_url":"{{{{{{server.BaseUrl}}}}}}","models":["a-model","b-model"],"thinking":{
                "none":{"reasoning":{"effort":"none"}},
                "low":{"reasoning":{"effort":"low"}},
                "high":{"reasoning":{"effort":"high"}}}}},
             "default_endpoint":"cloudy"}
            """);
        WriteModel("a-model", port: 1235);
        WriteModel("b-model", port: 1235);
        WriteRole("generalist", "{\"endpoint\":\"cloudy\",\"model\":\"a-model\"}");
        WriteRole("cheap", "{\"model\":\"b-model\"}");
        server.Enqueue(RunAgentCall("cheap"));
        server.Enqueue(Completion("child done"));
        server.Enqueue(Completion("done"));
        Console.SetIn(new SteppedReader(("/effort high", null), ("go", null), ("/quit", null)));
        Console.SetOut(new StringWriter());

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes" }));

        Assert.Equal(3, server.RequestCount);
        Assert.Equal("high", server.RequestBodies[0].GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("b-model", server.RequestBodies[1].GetProperty("model").GetString());
        Assert.Equal("high", server.RequestBodies[1].GetProperty("reasoning").GetProperty("effort").GetString());
    }

    //once the profile forgets the level, a resumed session holds it alone, so a child that read its body from disk would send another one
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"qwen38-model\"}")]
    public async Task A_CHILD_ON_THE_SESSIONS_OWN_MODEL_SENDS_THE_RESUMED_LEVEL(string helper)
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        UseHome();
        WriteConfig("""{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        WriteModel("qwen38-model", port: port, profileExtra: Qwen38ThinkingMap);
        WriteRole("generalist", "{\"model\":\"qwen38-model\"}");
        WriteRole("helper", helper);
        server.Enqueue(Completion("first"));
        Console.SetIn(new StringReader("/effort xhigh\nhi\n/quit\n"));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        WriteModel("qwen38-model", port: port, profileExtra: Qwen38ThinkingMap);   //the persisted default_effort goes, the session record keeps xhigh
        server.Enqueue(RunAgentCall("helper"));
        server.Enqueue(Completion("child done"));
        server.Enqueue(Completion("done"));
        Console.SetIn(new StringReader("go\n/quit\n"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "--continue" }));

        var bodies = server.RequestBodies;
        string? EffortOf(JsonElement body) =>
            body.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString();
        Assert.Equal(4, bodies.Count);
        Assert.Equal("xhigh", EffortOf(bodies[1]));   //the resumed parent
        Assert.Equal("xhigh", EffortOf(bodies[2]));   //the child
    }

    //the plain loop prints the banner cat as its first rows and a one-shot prints none, glyphs pinned since a host without WT_SESSION draws the ascii cat
    [Fact]
    public async Task THE_LAUNCH_HANDS_THE_ROLES_CAT_TO_THE_PLAIN_BANNER()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","glyphs":"unicode"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\",\"cat\":[\"  cat row one\",\"  cat row two\"]}");
        server.Enqueue(Completion("ok"));

        var oneShot = new StringWriter();
        Console.SetOut(oneShot);
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.DoesNotContain("cat row one", oneShot.ToString(), StringComparison.Ordinal);

        var plain = new StringWriter();
        Console.SetOut(plain);
        Console.SetIn(new StringReader("/quit\n"));
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));
        var rows = plain.ToString().Split('\n').Select(r => r.TrimEnd('\r')).ToArray();
        Assert.Equal(["  cat row one", "  cat row two"], rows[..2]);
    }

    [Fact]
    public async Task A_PROJECTS_AGENTS_FOLDER_IS_NOT_READ()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        var projectAgents = Path.Combine(_cwd, ".gatto", "agents");
        Directory.CreateDirectory(projectAgents);
        File.WriteAllText(Path.Combine(projectAgents, "x.md"), "---\ndescription: a project agent\n---\nYou are x.\n");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        var description = RunAgentDescriptionOf(server.RequestBodies[0]);
        Assert.Contains("- generalist", description, StringComparison.Ordinal);
        Assert.DoesNotContain("- x", description, StringComparison.Ordinal);
        Assert.DoesNotContain("x.md", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(".gatto", "agents"), stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ROLE_ADDED_MID_SESSION_APPEARS_AFTER_A_ROLE_SWITCH()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("one"));
        server.Enqueue(Completion("two"));
        server.Enqueue(Completion("three"));
        //the file lands before the second turn, so the second request shows the list is read at /role and not at every call
        Console.SetIn(new SteppedReader(
            ("first", null),
            ("second", () => WriteRole("added", "{\"description\":\"added later\"}")),
            ("/role generalist", null),
            ("third", null),
            ("/quit", null)));
        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal(3, server.RequestCount);
        Assert.DoesNotContain("- added", RunAgentDescriptionOf(server.RequestBodies[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("- added", RunAgentDescriptionOf(server.RequestBodies[1]), StringComparison.Ordinal);
        Assert.Contains("- added: added later", RunAgentDescriptionOf(server.RequestBodies[2]), StringComparison.Ordinal);
    }

    //the description rides every request, so a /role that is refused after it resolves must leave the list where it was
    [Fact]
    public async Task A_REFUSED_ROLE_SWITCH_LEAVES_THE_ROLE_LIST_AS_IT_WAS()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteModel("other-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("other", "{\"model\":\"other-model\"}");
        server.Enqueue(Completion("one"));
        server.Enqueue(Completion("two"));
        Console.SetIn(new SteppedReader(
            ("first", () => WriteRole("added", "{\"description\":\"added later\"}")),
            ("/role other", null),
            ("second", null),
            ("/quit", null)));
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("uses model 'other-model'", stdout.ToString(), StringComparison.Ordinal);   //the switch was refused
        Assert.Equal(2, server.RequestCount);
        Assert.DoesNotContain("- added", RunAgentDescriptionOf(server.RequestBodies[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_BROKEN_FILE_WRITTEN_MID_SESSION_WARNS_AT_THE_NEXT_ROLE_SWITCH()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        server.Enqueue(Completion("one"));
        Console.SetIn(new SteppedReader(
            ("/role generalist", () => WriteRole("broken", "{ \"turns\": 0 }")),
            ("first", null),
            ("/quit", null)));
        Console.SetOut(new StringWriter());
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var warning = Assert.Single(stderr.ToString().Split('\n'), l => l.Contains("broken", StringComparison.Ordinal));
        Assert.Contains("\"turns\"", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("broken", RunAgentDescriptionOf(server.RequestBodies[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_BROKEN_ROLE_FILE_IS_SKIPPED_WITH_A_WARNING()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("broken", "{ \"turns\": 0 }");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hello" }));

        var warning = Assert.Single(stderr.ToString().Split('\n'), l => l.Contains("broken", StringComparison.Ordinal));
        Assert.Contains("\"turns\"", warning, StringComparison.Ordinal);
        var description = RunAgentDescriptionOf(server.RequestBodies[0]);
        Assert.Contains("- generalist", description, StringComparison.Ordinal);
        Assert.DoesNotContain("broken", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "generalist")]
    [InlineData("coder", "coder")]
    public async Task A_BROKEN_LAUNCHED_ROLE_RUNS_THE_FALLBACK(string? defaultRole, string fallback)
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        var defaultKey = defaultRole is null ? "" : $",\"default_role\":\"{defaultRole}\"";
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"{{{defaultKey}}}}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\",\"append\":\"the generalist marker\"}");
        WriteRole("coder", "{\"model\":\"test-model\",\"append\":\"the coder marker\"}");
        WriteRole("broken", "{ \"turns\": 0 }");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "broken", "-p", "hello" }));

        var warning = Assert.Single(stderr.ToString().Split('\n'), l => l.Contains("broken", StringComparison.Ordinal));
        Assert.EndsWith($", running {fallback} instead", warning.TrimEnd('\r'), StringComparison.Ordinal);
        Assert.Contains($"the {fallback} marker", SystemOf(server.RequestBodies[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_BROKEN_DEFAULT_ROLE_RUNS_GENERALIST()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_role":"broken"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\",\"append\":\"the generalist marker\"}");
        WriteRole("broken", "{ \"turns\": 0 }");
        server.Enqueue(Completion("ok"));

        Console.SetOut(new StringWriter());
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hello" }));

        var warning = Assert.Single(stderr.ToString().Split('\n'), l => l.Contains("broken", StringComparison.Ordinal));
        Assert.EndsWith(", running generalist instead", warning.TrimEnd('\r'), StringComparison.Ordinal);
        Assert.Contains("the generalist marker", SystemOf(server.RequestBodies[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_BROKEN_FALLBACK_EXITS_2()
    {
        UseHome();
        WriteConfig("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{ \"turns\": \"ten\" }");
        WriteRole("broken", "{ \"turns\": 0 }");

        Console.SetOut(new StringWriter());
        var stderr = new StringWriter();
        Console.SetError(stderr);
        Assert.Equal(2, await GattoApp.RunAsync(new[] { "broken", "-p", "hello" }));

        var text = stderr.ToString();
        Assert.Contains("role 'broken' has a \"turns\" of 0", text, StringComparison.Ordinal);
        Assert.Contains("role 'generalist' has a \"turns\" of \"ten\"", text, StringComparison.Ordinal);
    }

    //the parent's request carries memory and the context files, a child carries no memory and the files only while its role allows them
    [Fact]
    public async Task THE_REAL_LAUNCH_CHILD_CARRIES_NO_MEMORY_AND_FOLLOWS_CONTEXT_FILES()
    {
        await using var server = new FakeOpenAiServer();
        UseHome();
        WriteConfig($$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local"}""");
        WriteModel("test-model", port: 1235);
        WriteRole("generalist", "{\"model\":\"test-model\"}");
        WriteRole("withfiles", "{\"append\":\"you read files\"}");
        WriteRole("nofiles", "{\"context_files\":false}");
        WriteMemoryFacts("- the memory marker");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "the context marker");
        server.Enqueue(RunAgentCall("withfiles"));
        server.Enqueue(Completion("child one"));
        server.Enqueue(RunAgentCall("nofiles"));
        server.Enqueue(Completion("child two"));
        server.Enqueue(Completion("done"));

        Console.SetOut(new StringWriter());
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--yes", "-p", "hi" }));

        Assert.Equal(5, server.RequestCount);
        var parent = SystemOf(server.RequestBodies[0]);
        var withFiles = SystemOf(server.RequestBodies[1]);
        var noFiles = SystemOf(server.RequestBodies[3]);
        Assert.Contains("the memory marker", parent, StringComparison.Ordinal);
        Assert.Contains(RoleComposition.MemoryNudge, parent, StringComparison.Ordinal);
        Assert.Contains("the context marker", parent, StringComparison.Ordinal);
        foreach (var child in new[] { withFiles, noFiles })
        {
            Assert.DoesNotContain("## Memory", child, StringComparison.Ordinal);
            Assert.DoesNotContain(RoleComposition.MemoryNudge, child, StringComparison.Ordinal);
        }
        Assert.Contains("you read files", withFiles, StringComparison.Ordinal);
        Assert.Contains("the context marker", withFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("the context marker", noFiles, StringComparison.Ordinal);
    }
}

//hands the REPL one line per read and runs an action before a chosen line, so a test can change the disk mid-session
file sealed class SteppedReader(params (string Line, Action? Before)[] lines) : TextReader
{
    private int _next;

    public override string? ReadLine()
    {
        if (_next >= lines.Length) return null;
        var (line, before) = lines[_next++];
        before?.Invoke();
        return line;
    }
}
