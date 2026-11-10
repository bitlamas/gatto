using System.Text.Json;
using Gatto.Cli;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the live model list through the real launch, an extension named hosted that declares one model and reads two others live
[Collection("e2e")]
public class CatalogueLaunchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-cat-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-cat-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextReader _origIn = Console.In;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public CatalogueLaunchTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        Environment.CurrentDirectory = _cwd;
        Console.SetOut(_out);
        Console.SetError(_err);
        Console.SetIn(new StringReader("/quit\n"));
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\"}");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Console.SetIn(_origIn);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private const string Map =
        "{\\\"none\\\":null,\\\"low\\\":{\\\"reasoning_effort\\\":\\\"low\\\"},\\\"medium\\\":{\\\"reasoning_effort\\\":\\\"medium\\\"},\\\"high\\\":{\\\"reasoning_effort\\\":\\\"high\\\"},\\\"xhigh\\\":{\\\"reasoning_effort\\\":\\\"xhigh\\\"}}";

    //c1 offers two levels, names one the map lacks and one core does not know, and two windows. c2 is bare
    private const string Live =
        "Task.FromResult(new ModelCatalogue(new[] { " +
        "new ModelEntry(\"c1\", Efforts: new[] { \"low\", \"high\", \"ultra\", \"max\" }, DefaultEffort: \"high\", Windows: new[] { 256000, 500000 }, DefaultWindow: 256000), " +
        "new ModelEntry(\"c2\", DefaultWindow: 0) }))";

    private void Hosted(FakeOpenAiServer server, string catalogue = Live, string declared = "\"h1\"", string extra = "")
    {
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"),
            "Gatto.Endpoint(\"hosted\", \"" + server.BaseUrl + "\", context: 4096, thinking: \"" + Map + "\"" +
            (declared.Length > 0 ? ", models: new[] { " + declared + " }" : "") +
            (catalogue.Length > 0 ? ", catalogue: ct => " + catalogue : "") + ");\n" + extra);
    }

    private static FakeResponse Text(string text) => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static IReadOnlyList<JsonElement> Chats(FakeOpenAiServer server) =>
        [.. server.RequestBodies.Where(b => b.TryGetProperty("messages", out _))];

    private static string Sent(FakeOpenAiServer server, string property) =>
        Chats(server)[^1].TryGetProperty(property, out var v) ? v.GetString()! : "";

    //an answer that reports its prompt count, so the loop has the measured figure its auto-compaction acts on
    private static FakeResponse Counted(string text, int promptTokens) => new(Frames: new[]
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":" + promptTokens + ",\"completion_tokens\":2}}\n\n",
        "data: [DONE]\n\n",
    });

    [Fact]
    public async Task A_model_switch_moves_the_window_the_loop_cuts_against_and_the_name_is_the_chosen_one()
    {
        //c1 holds 256000 and c2 names none, so it takes the endpoint's 4096, and the second turn is cut against that window
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Counted("ok", 5000));
        server.Enqueue(Text("ok"));
        Console.SetIn(new StringReader(new string('w', 14000) + "\n/model c2\nagain\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("context estimate exceeds the window", _out.ToString() + _err.ToString());
        Assert.Equal("c2", Sent(server, "model"));
    }

    [Fact]
    public async Task The_live_list_is_what_the_launch_resolves_and_model_lists()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Text("ok"));
        Console.SetIn(new StringReader("/model\n/model c2\nhi\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal("c2", Sent(server, "model"));
        var o = _out.ToString();
        Assert.Contains("c1", o);
        Assert.DoesNotContain("h1", o);
    }

    [Fact]
    public async Task A_stale_saved_default_warns_once_with_the_d_words()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.json"), """{"defaults":{"hosted":{"model":"gone"}}}""");
        server.Enqueue(Text("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Equal("c1", Sent(server, "model"));
        var line = "the saved model 'gone' is no longer offered by endpoint 'hosted'; using 'c1', pick one with /model and d to make it the default";
        Assert.Equal(1, _err.ToString().Split(line).Length - 1);
    }

    [Theory]
    [InlineData("throw new InvalidOperationException(\"the list route answered 426\")", "the list route answered 426")]
    [InlineData("Task.FromResult(new ModelCatalogue(new ModelEntry[0]))", "it returned no models")]
    [InlineData("Task.FromResult(new ModelCatalogue(new[] { new ModelEntry(\"c1\"), new ModelEntry(\"c1\") }))", "it listed a model twice")]
    [InlineData("Task.FromResult(new ModelCatalogue(new[] { new ModelEntry(\"c1\\u0007\") }))", "it returned a model name gatto cannot show")]
    public async Task A_catalogue_that_throws_or_is_empty_keeps_the_declared_list_and_warns(string catalogue, string reason)
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: catalogue);
        server.Enqueue(Text("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Equal("h1", Sent(server, "model"));
        Assert.Contains($"hosted: the model list could not be read ({reason}); using the list the extension declares", _err.ToString());
    }

    [Fact]
    public async Task No_catalogue_and_no_declared_list_refuses_with_exit_2()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, catalogue: "throw new InvalidOperationException(\"no route\")", declared: "");

        Assert.Equal(2, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Contains("endpoint hosted: no model list could be read and the extension declares none (no route)", _err.ToString());
        Assert.Empty(Chats(server));
    }

    [Fact]
    public async Task A_p_run_reads_the_catalogue_with_no_live_row()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Text("ok"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));

        Assert.Equal("c1", Sent(server, "model"));
        Assert.DoesNotContain("reading the model list", _out.ToString() + _err.ToString());
    }

    [Fact]
    public async Task An_interactive_launch_says_the_read_before_the_session()
    {
        //this host cannot repaint, so the row is committed once, and it comes before the banner
        await using var server = new FakeOpenAiServer();
        Hosted(server);

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        var read = o.IndexOf("hosted: reading the model list", StringComparison.Ordinal);
        Assert.True(read >= 0 && read < o.IndexOf("generalist", StringComparison.Ordinal), o);
    }

    [Fact]
    public async Task A_role_switch_onto_another_endpoint_at_the_same_url_drops_the_catalogue()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server, extra: "Gatto.Endpoint(\"acme\", \"" + server.BaseUrl + "\", models: new[] { \"a1\" });\n");
        File.WriteAllText(Path.Combine(_home, "roles", "writer.json"), "{\"endpoint\":\"acme\"}");
        server.Enqueue(Text("ok"));
        Console.SetIn(new StringReader("/role writer\n/model c2\nhi\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Contains("endpoint acme has no model 'c2'. Models: a1", _out.ToString());
        Assert.Equal("a1", Sent(server, "model"));
    }

    [Fact]
    public async Task Effort_offers_the_entrys_levels_and_refuses_one_outside_with_the_list()
    {
        //ultra is no level core knows and max is none the map sends, so the offer is the two the entry and the map share
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Text("ok"));
        Console.SetIn(new StringReader("/effort medium\n/effort xhigh\n/effort low\nhi\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        Assert.Contains("effort medium is not offered by c1: low, high", o);
        Assert.Contains("effort xhigh is not offered by c1: low, high", o);
        Assert.Equal("low", Sent(server, "reasoning_effort"));
    }

    [Fact]
    public async Task The_entrys_default_effort_is_the_first_turns_effort_and_a_saved_level_wins()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Text("ok"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Equal("high", Sent(server, "reasoning_effort"));

        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.json"), """{"defaults":{"hosted":{"effort":{"c1":"low"}}}}""");
        server.Enqueue(Text("ok"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "hi" }));
        Assert.Equal("low", Sent(server, "reasoning_effort"));
    }

    [Fact]
    public async Task The_entrys_default_window_is_the_budget_and_context_draws_the_offered_sizes()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        Console.SetIn(new StringReader("/context\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var o = _out.ToString();
        Assert.Contains("of 256,000 tokens", o);
        Assert.True(o.Contains("window 256000 (500000 offered)"), o);
    }

    [Fact]
    public async Task A_window_of_zero_falls_back_to_the_endpoints_context()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        Console.SetIn(new StringReader("/context\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-m", "c2" }));

        var o = _out.ToString();
        Assert.Contains("of 4,096 tokens", o);
        Assert.DoesNotContain("offered", o);
    }

    [Fact]
    public async Task The_catalogue_is_absent_from_the_prefix_and_the_body()
    {
        //two turns, and neither request names another listed model, a level name the entry offers or a window size
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Text("one"));
        server.Enqueue(Text("two"));
        Console.SetIn(new StringReader("hi\nagain\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        var chats = Chats(server);
        Assert.Equal(2, chats.Count);
        foreach (var body in chats)
        {
            var raw = body.GetRawText();
            Assert.DoesNotContain("c2", raw);
            Assert.DoesNotContain("256000", raw);
            Assert.DoesNotContain("500000", raw);
            Assert.DoesNotContain("ultra", raw);
            Assert.Equal("c1", body.GetProperty("model").GetString());
        }
        var first = chats[0].GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(first, chats[1].GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
