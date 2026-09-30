using System.Text.Json;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Gatto.Extensions;

namespace Gatto.Tests;

public class GattoApiTests
{
    //build a GattoApi with inert delegates, each test overrides only what it probes
    private static GattoApi MakeApi(
        Func<string, CancellationToken, Task<FetchResult>>? fetch = null,
        Action<string, string?, bool>? record = null,
        Func<IUserPrompter?>? prompter = null,
        Action<string>? log = null)
        => new GattoApi(
            home: @"C:\home",
            cwd: @"C:\cwd",
            fetch: (url, _, ct) => (fetch ?? ((_, _) => Task.FromResult(new FetchResult("u", null, "", false))))(url, ct),
            recordFetch: record ?? ((_, _, _) => { }),
            prompter: prompter ?? (() => null),
            log: log ?? (_ => { }));

    private sealed class FakeTool : ITool
    {
        public string Name { get; init; } = "fake_tool";
        public string Description { get; init; } = "a fake tool";
        public JsonElement ParametersSchema { get; init; } =
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct) =>
            Task.FromResult(new ToolResult("ok"));
    }

    private sealed class FakePrompter : IUserPrompter
    {
        public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AskAnswer>>(new[] { new AskAnswer("h", new[] { "yes" }) });
    }

    [Fact]
    public void HostApiVersion_IsFour() => Assert.Equal("4", GattoApi.HostApiVersion);

    [Fact]
    public void Config_section_reaches_scripts_and_defaults_to_null()
    {
        var home = Directory.CreateTempSubdirectory("gatto-api-cfg-").FullName;
        try
        {
            JsonElement search = JsonDocument.Parse("""{"providers":["ddg"]}""").RootElement.Clone();
            var (api, _, _) = ExtensionHost.BuildApi(
                home, Path.GetTempPath(), () => null, () => null, _ => { },
                configSection: n => n == "search" ? search : (JsonElement?)null);
            Assert.Equal("""{"providers":["ddg"]}""", api.Config.Section("search")!.Value.GetRawText());
            Assert.Null(api.Config.Section("nope"));

            var (bare, _, _) = ExtensionHost.BuildApi(home, Path.GetTempPath(), () => null, () => null, _ => { });
            Assert.Null(bare.Config.Section("search"));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void HomeAndCwd_AreExposed()
    {
        var api = MakeApi();
        Assert.Equal(@"C:\home", api.Home);
        Assert.Equal(@"C:\cwd", api.Cwd);
    }

    [Theory]
    [InlineData("Bad")]     //uppercase letters are not allowed in a tool name.
    [InlineData("has-dash")]
    [InlineData("has space")]
    [InlineData("")]
    public void Register_RejectsBadName(string name)
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.Register(name, "desc", """{"type":"object"}""", (_, _, _) => Task.FromResult(new ToolResult("x"))));
        Assert.Equal($"tool name '{name}' must match [a-z0-9_]+", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_RejectsEmptyDescription(string description)
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.Register("good_name", description, """{"type":"object"}""", (_, _, _) => Task.FromResult(new ToolResult("x"))));
        Assert.Equal("tool 'good_name' description must not be empty", ex.Message);
    }

    [Fact]
    public void Register_RejectsUnparseableSchema()
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.Register("good_name", "desc", "{ not json", (_, _, _) => Task.FromResult(new ToolResult("x"))));
        Assert.Equal("tool 'good_name' parametersSchema must be a JSON object", ex.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    public void Register_RejectsNonObjectSchema(string schema)
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.Register("good_name", "desc", schema, (_, _, _) => Task.FromResult(new ToolResult("x"))));
        Assert.Equal("tool 'good_name' parametersSchema must be a JSON object", ex.Message);
    }

    [Fact]
    public void Register_RejectsNullExecute()
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.Register("good_name", "desc", """{"type":"object"}""", null!));
        Assert.Equal("tool 'good_name' execute delegate must not be null", ex.Message);
    }

    [Fact]
    public async Task Register_Convenience_RoundTripsExecuteThroughDelegateTool()
    {
        var api = MakeApi();
        api.Register("echo", "echoes", """{"type":"object","properties":{"m":{"type":"string"}}}""",
            (args, _, _) =>
            {
                var m = args.GetProperty("m").GetString();
                return Task.FromResult(new ToolResult($"echo:{m}"));
            });

        var staged = api.TakeStaged();
        var (tool, readClass) = Assert.Single(staged.Tools);
        Assert.Equal("echo", tool.Name);
        Assert.Equal("echoes", tool.Description);
        Assert.Equal(JsonValueKind.Object, tool.ParametersSchema.ValueKind);
        Assert.False(readClass);

        var args = JsonDocument.Parse("""{"m":"hi"}""").RootElement;
        var result = await tool.ExecuteAsync(args, new FakeCtx(), CancellationToken.None);
        Assert.Equal("echo:hi", result.Text);
    }

    [Fact]
    public void Register_Convenience_SchemaSurvivesDocumentDisposal()
    {
        //the stored schema is a detached Clone, so it must still read after the parse document is disposed
        var api = MakeApi();
        api.Register("t", "d", """{"type":"object","properties":{"x":{"type":"integer"}}}""",
            (_, _, _) => Task.FromResult(new ToolResult("x")));
        GC.Collect();
        var tool = api.TakeStaged().Tools[0].Tool;
        Assert.True(tool.ParametersSchema.GetProperty("properties").TryGetProperty("x", out _));
    }

    [Fact]
    public void Register_CarriesReadClassFlag()
    {
        var api = MakeApi();
        api.Register("reader", "reads", """{"type":"object"}""",
            (_, _, _) => Task.FromResult(new ToolResult("x")), readClass: true);
        Assert.True(api.TakeStaged().Tools[0].ReadClass);
    }

    [Fact]
    public void Register_DirectTool_IsStaged()
    {
        var api = MakeApi();
        var tool = new FakeTool();
        api.Register(tool, readClass: true);
        var staged = api.TakeStaged();
        var entry = Assert.Single(staged.Tools);
        Assert.Same(tool, entry.Tool);
        Assert.True(entry.ReadClass);
    }

    [Fact]
    public void Register_DirectTool_RejectsBadName()
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.Register(new FakeTool { Name = "Bad Name" }));
        Assert.Equal("tool name 'Bad Name' must match [a-z0-9_]+", ex.Message);
    }

    [Theory]
    [InlineData("tool_call")]
    [InlineData("tool_result")]
    [InlineData("message_end")]
    [InlineData("session_summary")]
    public void On_StagesValidEvents(string evt)
    {
        var api = MakeApi();
        Func<HookPayload, Task> handler = _ => Task.CompletedTask;
        api.On(evt, handler);
        var staged = api.TakeStaged();
        var hook = Assert.Single(staged.Hooks);
        Assert.Equal(evt, hook.Evt);
        Assert.Same(handler, hook.Handler);
    }

    [Theory]
    [InlineData("tool_start")]
    [InlineData("ToolCall")]
    [InlineData("")]
    public void On_RejectsUnknownEvent(string evt)
    {
        var api = MakeApi();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            api.On(evt, _ => Task.CompletedTask));
        Assert.Equal($"unknown event '{evt}' — valid: tool_call, tool_result, message_end, session_summary", ex.Message);
    }

    [Fact]
    public void TakeStaged_ClearsTheStage()
    {
        var api = MakeApi();
        api.Register(new FakeTool());
        api.On("tool_call", _ => Task.CompletedTask);
        Assert.Single(api.TakeStaged().Tools);

        var second = api.TakeStaged();
        Assert.Empty(second.Tools);
        Assert.Empty(second.Hooks);
    }

    [Fact]
    public void BeginExtension_ReArmsAFreshStage()
    {
        var api = MakeApi();
        api.Register(new FakeTool());
        api.On("tool_call", _ => Task.CompletedTask);

        api.BeginExtension("ext-b");

        var staged = api.TakeStaged();
        Assert.Empty(staged.Tools);
        Assert.Empty(staged.Hooks);
        Assert.Equal("ext-b", api.CurrentExtension);
    }

    [Fact]
    public async Task Ui_AskAsync_NullPrompter_ThrowsExactMessage()
    {
        var api = MakeApi(prompter: () => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.Ui.AskAsync(new[] { new AskQuestion("q", "h", new[] { "a", "b" }, false) }, CancellationToken.None));
        //assert the exact text, so it changes only with the message AskAsync throws
        Assert.Equal("interactive input unavailable in non-interactive mode", ex.Message);
    }

    [Fact]
    public async Task Ui_AskAsync_DelegatesToPrompter()
    {
        var api = MakeApi(prompter: () => new FakePrompter());
        var answers = await api.Ui.AskAsync(
            new[] { new AskQuestion("q", "h", new[] { "a", "b" }, false) }, CancellationToken.None);
        var a = Assert.Single(answers);
        Assert.Equal("h", a.Header);
    }

    //assert the fake prompter was invoked and its answer came back, a non-null check misses AskAsync no longer delegating
    [Fact]
    public async Task Ui_AskAsync_PrompterPresent_IsActuallyInvoked_NotJustNonNull()
    {
        var fake = new TrackingPrompter();
        var api = MakeApi(prompter: () => fake);
        var questions = new[] { new AskQuestion("q", "h", new[] { "a", "b" }, false) };

        var answers = await api.Ui.AskAsync(questions, CancellationToken.None);

        Assert.True(fake.WasInvoked, "a present prompter must be reached — the non-interactive fix must not swallow the interactive path");
        Assert.Same(questions, fake.Seen);
        var a = Assert.Single(answers);
        Assert.Equal("h", a.Header);
        Assert.Equal(new[] { "yes" }, a.Selected);
    }

    private sealed class TrackingPrompter : IUserPrompter
    {
        public bool WasInvoked { get; private set; }
        public IReadOnlyList<AskQuestion>? Seen { get; private set; }

        public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
        {
            WasInvoked = true;
            Seen = questions;
            return Task.FromResult<IReadOnlyList<AskAnswer>>(new[] { new AskAnswer("h", new[] { "yes" }) });
        }
    }

    [Fact]
    public async Task Http_FetchAsync_DelegatesToInjectedFetch()
    {
        string? seen = null;
        var api = MakeApi(fetch: (url, _) =>
        {
            seen = url;
            return Task.FromResult(new FetchResult(url, "text/html", "body", true));
        });
        var result = await api.Http.FetchAsync("http://example.com", CancellationToken.None);
        Assert.Equal("http://example.com", seen);
        Assert.Equal("body", result.Text);
        Assert.True(result.FromCache);
    }

    [Fact]
    public void Ledger_RecordFetch_DelegatesToInjectedRecorder()
    {
        (string url, string? content, bool searchOnly)? seen = null;
        var api = MakeApi(record: (u, c, s) => seen = (u, c, s));
        api.Ledger.RecordFetch("http://x", "content", searchOnly: true);
        Assert.Equal(("http://x", "content", true), seen);
    }

    [Fact]
    public void Log_WritesToInjectedSink()
    {
        var lines = new List<string>();
        var api = MakeApi(log: lines.Add);
        api.Log("hello");
        Assert.Equal(new[] { "hello" }, lines);
    }

    private sealed class FakeCtx : IToolContext
    {
        public string Cwd => @"C:\cwd";
        public string HomePath => @"C:\home";
        public IUserPrompter? Prompter => null;
    }
}
