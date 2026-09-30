using System.Text.Json;
using Gatto.Cli;
using Gatto.Roles;
using Gatto.Tests.Census;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//these drive the real launch and read the system message off the wire, and a redirected stream takes the plain loop
[Collection("e2e")]
public class PolicyLineLaunchTests : IDisposable
{
    private const string Line = "stop and ask when a choice is the user's";

    private readonly string _home = Directory.CreateTempSubdirectory("gatto-policy-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-policy-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;

    public PolicyLineLaunchTests()
    {
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{\"model\":\"test-model\"}");
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            "{\"files\":[{\"path\":\"m.gguf\",\"active\":true}],\"port\":1235,\"context\":8192}");

        //this agent file makes the sub-agent seam reachable from a real launch.
        var agentsDir = Path.Combine(_home, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "helper.md"), """
            ---
            description: helps with things
            tools: read_file
            max_turns: 3
            ---
            You are helper.
            """);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        TryDelete(_home);
        TryDelete(_cwd);
    }

    private static void TryDelete(string path)
    {
        //a real launch loads the extension dll and locks it for the process, so cleanup must never fail a green test.
        try { Directory.Delete(path, true); } catch { }
    }

    private void WriteExtension(string name, string body)
    {
        var dir = Path.Combine(_home, "extensions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), body);
    }

    private void WriteConfig(FakeOpenAiServer server) =>
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$$"""{"endpoints":{"local":{"base_url":"{{{server.BaseUrl}}}"}},"default_endpoint":"local","default_model":"test-m"}""");

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static void EnqueueOneAnswer(FakeOpenAiServer server) =>
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

    private static string SystemOf(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;

    [Fact]
    public async Task The_one_shot_p_launch_puts_the_line_in_the_first_request_system_message()
    {
        await using var server = new FakeOpenAiServer();
        WriteExtension("zz_policy.csx", $"Gatto.Policy(\"{Line}\");");
        WriteConfig(server);
        EnqueueOneAnswer(server);
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());

        var exit = await GattoApp.RunAsync(new[] { "-p", "hello" });

        Assert.Equal(0, exit);
        //the request count is asserted, since with one request the first and last body are the same
        Assert.Equal(1, server.RequestCount);
        Assert.Contains(Line, SystemOf(server.RequestBodies[0]));
    }

    //this covers the second call site, since a test only covers the site it drives

    [Fact]
    public async Task The_repl_launch_puts_the_line_in_the_first_request_system_message()
    {
        await using var server = new FakeOpenAiServer();
        WriteExtension("zz_policy.csx", $"Gatto.Policy(\"{Line}\");");
        WriteConfig(server);
        EnqueueOneAnswer(server);
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());
        Console.SetIn(new StringReader("hello\n/quit\n"));

        var exit = await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Equal(1, server.RequestCount);
        Assert.Contains(Line, SystemOf(server.RequestBodies[0]));
    }

    [Fact]
    public async Task A_broken_extension_contributes_no_line_and_the_launch_survives()
    {
        await using var server = new FakeOpenAiServer();
        WriteExtension("zz_broken.csx", """
            Gatto.Policy("never reaches the prompt");
            throw new System.Exception("boom");
            """);
        WriteConfig(server);
        EnqueueOneAnswer(server);
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());

        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);                        //a broken extension must not fail the launch.
        Assert.DoesNotContain("never reaches the prompt", SystemOf(server.RequestBodies[0]));
    }

    //no test extension is written here, since a string in a file proves nothing about the prompt
    [Fact]
    public async Task The_shipped_ask_user_arms_its_own_policy_line_with_no_help_from_the_test()
    {
        await using var server = new FakeOpenAiServer();
        WriteConfig(server);
        EnqueueOneAnswer(server);
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());

        var exit = await GattoApp.RunAsync(new[] { "-p", "hi" });

        Assert.Equal(0, exit);
        Assert.Contains("stop and ask with ask_user", SystemOf(server.RequestBodies[0]));
    }

    private static string[] RunAgentToolCallRound() => new[]
    {
        Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"t1\",\"function\":{\"name\":\"run_agent\",\"arguments\":\"{\\\"agent\\\":\\\"helper\\\",\\\"task\\\":\\\"go\\\"}\"}}]}"),
        Chunk("{}", "tool_calls"), "data: [DONE]\n\n",
    };

    private static string[] TextRound(string text) => new[]
    {
        Chunk($"{{\"content\":\"{text}\"}}"), Chunk("{}", "stop"), "data: [DONE]\n\n",
    };

    //the other test hands the nudge a literal, so only a real launch catches a change in how GattoApp builds that argument
    [Fact]
    public async Task The_real_launch_wiring_keeps_the_line_out_of_the_subagent_request()
    {
        await using var server = new FakeOpenAiServer();
        WriteExtension("zz_policy.csx", $"Gatto.Policy(\"{Line}\");");
        WriteConfig(server);
        server.Enqueue(new FakeResponse(Frames: RunAgentToolCallRound()));   //the first round asks the parent to call run_agent
        server.Enqueue(new FakeResponse(Frames: TextRound("done")));          //the second round is the sub-agent's own answer.
        server.Enqueue(new FakeResponse(Frames: TextRound("ok")));            //the third round is the parent again, after the result.
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());
        Console.SetIn(new StringReader("use run_agent to check something\n/quit\n"));

        var exit = await GattoApp.RunAsync(new[] { "--yes" });

        Assert.Equal(0, exit);
        Assert.Equal(3, server.RequestCount);
        Assert.Contains(Line, SystemOf(server.RequestBodies[0]));        //the parent's first request must include the policy line.
        Assert.DoesNotContain(Line, SystemOf(server.RequestBodies[1]));  //the sub-agent request must not include it.
        Assert.Contains(Line, SystemOf(server.RequestBodies[2]));        //the parent keeps the line after the sub-agent returns.
    }

    [Fact]
    public async Task The_block_survives_a_new_and_is_byte_identical_across_the_session()
    {
        await using var server = new FakeOpenAiServer();
        WriteExtension("zz_policy.csx", $"Gatto.Policy(\"{Line}\");");
        WriteConfig(server);
        EnqueueOneAnswer(server);
        EnqueueOneAnswer(server);
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(new StringWriter());
        Console.SetIn(new StringReader("hi\n/new\nhi again\n/quit\n"));

        await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(2, server.RequestCount);
        var systems = server.RequestBodies.Select(SystemOf).ToList();
        Assert.All(systems, s => Assert.Contains(Line, s));
        //the prefix must stay byte-identical, since a moved or respaced block keeps every contains green and re-prefills the whole prompt
        Assert.Equal(systems[0], systems[1]);
    }

    //when the /new re-read throws, the closure serves its last composed system verbatim, so keep the policy block in that local
    [Fact]
    public async Task A_failed_new_rewalk_serves_the_launch_prefix_with_its_policy_block()
    {
        await using var server = new FakeOpenAiServer();
        WriteExtension("zz_policy.csx", $"Gatto.Policy(\"{Line}\");");
        WriteConfig(server);
        EnqueueOneAnswer(server);
        EnqueueOneAnswer(server);
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        //write the bad file after the launch reads its config and before /new, or the launch fails and the degrade path never runs
        Console.SetOut(new StringWriter());
        Console.SetIn(new ScriptedInput(
            new[] { "hi", "/new", "hi again", "/quit" },
            before: "/new",
            act: () => File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), "{ this is not json")));

        await GattoApp.RunAsync(Array.Empty<string>());

        Assert.Equal(2, server.RequestCount);
        var systems = server.RequestBodies.Select(SystemOf).ToList();
        Assert.Contains(Line, systems[1]);          //the degraded prefix still includes the policy block.
        Assert.Equal(systems[0], systems[1]);       //the served prefix is identical byte for byte.
    }

    //the action runs just before its line reaches the reader, the only moment a reader-driven REPL can change state between turns
    private sealed class ScriptedInput(string[] lines, string before, Action act) : TextReader
    {
        private int _i;

        public override string? ReadLine()
        {
            if (_i >= lines.Length) return null;
            var line = lines[_i++];
            if (line == before) act();
            return line;
        }
    }

    [Fact]
    public void The_launch_resolves_the_role_exactly_twice_never_a_third_time()
    {
        //this counts source, since the recompose exposes no observable. two per launch is the ceiling, since each resolution re-reads the context files
        var code = SourceTree.CodeOnly(
            File.ReadAllText(Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs")));

        Assert.Equal(2, Occurrences(code, ResolveMarker));
    }

    [Fact]
    public void The_resolution_census_can_see_a_third_one()
    {
        //a third resolution goes in as code, since a marker that stopped matching would report two forever
        var planted = SourceTree.CodeOnly(
            File.ReadAllText(Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"))
            //write the added call out in full, since one built from the constant only proves the counter finds a copy of its own text
            + "\nclass PlantedThirdResolution { RoleResolution M() => ResolveRole(args.Role); }\n");

        Assert.Equal(3, Occurrences(planted, ResolveMarker));
    }

    //match the marker by its argument, so a differently written assignment still counts and the /role path stays out
    private const string ResolveMarker = "ResolveRole(args.Role)";

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    //two locals read the other composition fields by value, so a policy line must leave them alone
    [Fact]
    public void A_policy_line_changes_the_system_text_and_nothing_else_the_launch_already_read()
    {
        var role = new RoleFile("r", "p", null, new[] { "grounding" }, true, null, ThinkingLevel.Medium, null);

        var without = RoleComposition.Compose(role, null, Array.Empty<(string, string)>(), null, cwd: @"C:\p");
        var with = RoleComposition.Compose(role, null, Array.Empty<(string, string)>(), null, cwd: @"C:\p",
            policyLines: new[] { ("ask_user", Line) });

        Assert.NotEqual(without.SystemText, with.SystemText);          //the policy block must change the system text.
        Assert.Equal(without.Gates, with.Gates);                       //the gates field must not change with the block.
        Assert.Equal(without.Checkpoints, with.Checkpoints);           //the checkpoints field must not change either.
        Assert.Equal(without.Thinking, with.Thinking);
        Assert.Equal(without.RepairArmed, with.RepairArmed);
    }
}
