using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the name the provider answered under is drawn and recorded, and the request, the resume and the environment keep the chosen id
[Collection("e2e")]
public class ServedNameTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-served-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-served-cwd-").FullName;
    private readonly string _origCwd = Environment.CurrentDirectory;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly StringWriter _err = new();
    private readonly StringWriter _out = new();

    public ServedNameTests()
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
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    //every chunk names the model the way a provider does, the first one is what counts
    private static FakeResponse Answer(string text, string? model) => new(Frames: new[]
    {
        "data: {" + Named(model) + "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\n",
        "data: {" + Named(model) + "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n",
    });

    private static string Named(string? model) => model is null ? "" : "\"model\":" + JsonSerializer.Serialize(model) + ",";

    private static ChatRequest Ask(string model) => new(model, [new ChatMessage("user", "hi")]);

    private static async Task<StreamEvent.Finished> Drain(OpenAiCompatClient client, string model)
    {
        StreamEvent.Finished? finished = null;
        await foreach (var e in client.StreamAsync(Ask(model), CancellationToken.None))
            if (e is StreamEvent.Finished f) finished = f;
        return finished!;
    }

    [Fact]
    public async Task The_first_answers_model_name_is_raised_once_and_the_body_keeps_the_chosen_id()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Answer("one", "c1-build"));
        server.Enqueue(Answer("two", "c1-build"));
        var raised = new List<(string, string)>();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", new EndpointConfig(server.BaseUrl)) { ReadsServedName = true, OnServed = (r, s) => raised.Add((r, s)) };

        Assert.Equal("c1-build", (await Drain(client, "c1")).ServedModel);
        Assert.Equal("c1-build", (await Drain(client, "c1")).ServedModel);

        Assert.Equal([("c1", "c1-build"), ("c1", "c1-build")], raised);
        Assert.All(server.RequestBodies, b => Assert.Equal("c1", b.GetProperty("model").GetString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("c1-\u001b[31m")]
    public async Task An_empty_absent_or_unprintable_model_field_changes_nothing(string? model)
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Answer("one", model));
        var raised = 0;
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", new EndpointConfig(server.BaseUrl)) { ReadsServedName = true, OnServed = (_, _) => raised++ };

        Assert.Null((await Drain(client, "c1")).ServedModel);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_server_gatto_runs_is_never_read_for_a_served_name()
    {
        //a llama-server started with no alias answers under its file name, which is not the model id the request names
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Answer("one", "C:/weights/model-Q4_K_M.gguf"));
        var raised = 0;
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl)) { OnServed = (_, _) => raised++ };

        Assert.Null((await Drain(client, "qwen")).ServedModel);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task An_answer_under_the_asked_name_is_raised_and_never_recorded()
    {
        //the screen hears it so a rename is taken back, the record keeps only a name that differs
        await using var server = new FakeOpenAiServer();
        server.Enqueue(Answer("one", "c1"));
        var raised = new List<(string, string)>();
        var client = new OpenAiCompatClient(new HttpClient(), "hosted", new EndpointConfig(server.BaseUrl)) { ReadsServedName = true, OnServed = (r, s) => raised.Add((r, s)) };

        Assert.Null((await Drain(client, "c1")).ServedModel);
        Assert.Equal([("c1", "c1")], raised);
    }

    private void Hosted(FakeOpenAiServer server)
    {
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", "hosted.csx"),
            "Gatto.Endpoint(\"hosted\", \"" + server.BaseUrl + "\", models: new[] { \"c1\", \"c2\" });");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{}},\"default_endpoint\":\"hosted\"}");
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
    }

    private IReadOnlyList<JsonElement> Assistants()
    {
        var session = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal));
        return [.. File.ReadAllLines(session).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(r => r.TryGetProperty("role", out var role) && role.GetString() == "assistant")];
    }

    private static string? ServedOf(JsonElement record) =>
        record.TryGetProperty("served_model", out var s) ? s.GetString() : null;

    [Fact]
    public async Task The_assistant_record_carries_served_model_only_when_it_differs_and_each_keeps_its_own()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Answer("one", "c1-build"));
        server.Enqueue(Answer("two", "c1-next"));
        server.Enqueue(Answer("three", "c1"));
        Console.SetIn(new StringReader("hi\nagain\nonce more\n/quit\n"));

        Assert.Equal(0, await GattoApp.RunAsync(Array.Empty<string>()));

        Assert.Equal(new string?[] { "c1-build", "c1-next", null }, Assistants().Select(ServedOf).ToArray());
        //the banner is the launch's echo and stands as printed, in whichever glyph set the run draws
        var banner = _out.ToString().Split('\n').First(l => l.Contains("generalist", StringComparison.Ordinal));
        Assert.Contains(" c1 ", banner);
        Assert.DoesNotContain("c1-", banner);
        Assert.All(server.RequestBodies.Where(b => b.TryGetProperty("messages", out _)), b => Assert.Equal("c1", b.GetProperty("model").GetString()));
    }

    [Fact]
    public async Task Continue_resumes_on_the_chosen_id_and_the_environment_keeps_it()
    {
        await using var server = new FakeOpenAiServer();
        Hosted(server);
        server.Enqueue(Answer("one", "c1-build"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "-p", "first" }));
        Assert.Equal("c1", Environment.GetEnvironmentVariable("GATTO_MODEL"));

        server.Enqueue(Answer("two", "c1-build"));
        Assert.Equal(0, await GattoApp.RunAsync(new[] { "--continue", "-p", "second" }));

        Assert.Equal("c1", server.LastRequestBody!.Value.GetProperty("model").GetString());
        Assert.Equal("c1", Environment.GetEnvironmentVariable("GATTO_MODEL"));
    }

    private static Gatto.Repl.Repl Repl(string model)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), model);
        return new Gatto.Repl.Repl(loop, new Conversation("sys"), "generalist", model, null, client, null, 65_536, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"),
            switchModel: (id, _) => new Gatto.Repl.ModelSwitchResult(true, id, null, null, null, null));
    }

    [Fact]
    public async Task A_model_switch_shows_the_chosen_name_until_the_new_models_first_answer()
    {
        var repl = Repl("c1");
        repl.SetServedName("c1", "c1-build");
        Console.SetIn(new StringReader("/model c2\n/quit\n"));

        await repl.RunAsync(CancellationToken.None);

        Assert.Equal("c2", repl.ShownModelForTest);
        repl.SetServedName("c2", "c2-build");
        Assert.Equal("c2-build", repl.ShownModelForTest);
    }

    [Fact]
    public void An_answer_under_the_chosen_name_takes_the_rename_back()
    {
        var repl = Repl("c1");
        repl.SetServedName("c1", "c1-build");

        repl.SetServedName("c1", "c1");

        Assert.Equal("c1", repl.ShownModelForTest);
    }

    [Fact]
    public void The_footer_follows_the_latest_name_served_for_the_model_in_use()
    {
        var repl = Repl("c1");
        Assert.Equal("c1", repl.ShownModelForTest);

        repl.SetServedName("c1", "c1-build");
        Assert.Equal("c1-build", repl.ShownModelForTest);

        repl.SetServedName("c1", "c1-next");
        Assert.Equal("c1-next", repl.ShownModelForTest);
    }

    [Fact]
    public void A_childs_served_name_never_reaches_the_parents_footer()
    {
        //a run_agent child on another model shares the client, and its answer names its own model
        var repl = Repl("c1");
        repl.SetServedName("c1", "c1-build");

        repl.SetServedName("c2", "c2-build");

        Assert.Equal("c1-build", repl.ShownModelForTest);
    }
}
