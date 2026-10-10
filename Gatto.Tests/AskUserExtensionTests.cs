using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Gatto.Terminal;

namespace Gatto.Tests;

//each case loads ask_user through the real ExtensionHost from the shipped text, so validation and the vetted-hash check run as in a live session
[Collection("e2e")] //every case compiles for real and moves the process-wide ExtensionHost.CompileCount, which ExtensionHostTests asserts exactly, so this class must not run in parallel with it
public sealed class AskUserExtensionTests : IDisposable
{
    //the host keeps compiled assemblies locked for the process lifetime, so temp dirs can outlive disposal. this sweep removes stale siblings only, keeping fresh ones a parallel runner may own.
    static AskUserExtensionTests()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "gatto-askuser-*"))
                try { if (Directory.GetCreationTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true); }
                catch { } //a locked directory stays for a later run.
        }
        catch { } //a failed sweep must never fail the suite.
    }

    private readonly string _home = Directory.CreateTempSubdirectory("gatto-askuser-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { } //a failed delete is tolerated.
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private const string OneQuestion = """
        {"questions":[{"question":"Which db?","header":"DB","options":["sqlite","postgres"],"multi_select":false}]}
        """;

    private sealed class FakeCtx : IToolContext
    {
        public string Cwd => Path.GetTempPath();
        public string HomePath => Path.GetTempPath();
        //the extension asks through the host API ExtensionHost.BuildApi wires, so a null Prompter is safe here
        public IUserPrompter? Prompter => null;
    }

    private sealed class ScriptedPrompter(Func<IReadOnlyList<AskQuestion>, IReadOnlyList<AskAnswer>> answer) : IUserPrompter
    {
        public IReadOnlyList<AskQuestion>? Seen;
        public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> qs, CancellationToken ct)
        {
            Seen = qs;
            return Task.FromResult(answer(qs));
        }
    }

    private sealed class FakePermissionPrompter(params PermissionAnswer[] answers) : IPermissionPrompter
    {
        private readonly Queue<PermissionAnswer> _answers = new(answers);
        public List<PermissionRequest> Requests { get; } = new();
        public PermissionAnswer Ask(PermissionRequest request)
        {
            Requests.Add(request);
            return _answers.Count > 0 ? _answers.Dequeue() : PermissionAnswer.Deny;
        }
    }

    //writes text as the only extension, loads it through the real host, and says whether the production allow-list vetted the tool
    private (ITool Tool, bool Vetted) LoadAskUser(string text, IUserPrompter? prompter)
    {
        var extDir = Path.Combine(_home, "extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "ask_user.csx"), text);

        var (api, _, _) = ExtensionHost.BuildApi(
            _home, Path.GetTempPath(),
            ledgerPath: () => null,
            prompter: () => prompter,
            log: _ => { });

        var diags = new List<string>();
        var loaded = ExtensionHost.LoadAll(extDir, api, diags.Add);
        Assert.Empty(diags);
        var loadedExt = Assert.Single(loaded);

        var tools = new ToolRegistry();
        var hooks = new HookBus();
        ExtensionHost.Commit(loaded, tools, hooks, diags.Add);
        Assert.Empty(diags);

        var tool = tools.Get("ask_user");
        Assert.NotNull(tool);
        var vetted = ShippedExtensions.IsVetted(loadedExt.Source);
        return (tool!, vetted);
    }

    private string Canonical => ShippedExtensions.Files["extensions/ask_user.csx"];

    [Fact]
    public async Task Asks_and_returns_selection_json()
    {
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "sqlite" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        Assert.False(r.IsError);
        var parsed = JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal("DB", parsed[0].GetProperty("header").GetString());
        Assert.Equal("sqlite", parsed[0].GetProperty("selected")[0].GetString());
        Assert.Equal("Which db?", prompter.Seen![0].Question);
        Assert.Equal("DB: sqlite", r.Gloss);   //the gloss must name the header and the pick
    }

    //the gloss is the only record of the user's choice in the transcript, so keep it readable when questions pile up or go unanswered

    [Fact]
    public async Task Gloss_names_each_question_so_several_answers_stay_readable()
    {
        var prompter = new ScriptedPrompter(qs => new[]
        {
            new AskAnswer("DB", new[] { "sqlite" }),
            new AskAnswer("Cache", new[] { "redis", "none" }),
        });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args("""
            {"questions":[
              {"question":"Which db?","header":"DB","options":["sqlite","postgres"]},
              {"question":"Which cache?","header":"Cache","options":["redis","none"],"multi_select":true}]}
            """), new FakeCtx(), default);

        Assert.Equal("DB: sqlite · Cache: redis, none", r.Gloss);
    }

    [Fact]
    public async Task Gloss_says_skipped_for_a_question_the_user_never_answered()
    {
        //an unanswered question yields an empty selection, the gloss says skipped
        var prompter = new ScriptedPrompter(_ => new[] { new AskAnswer("DB", Array.Empty<string>()) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        Assert.Equal("DB: skipped", r.Gloss);
        //the word skipped is a display choice, it must not leak into the tool result the model reads
        var parsed = JsonDocument.Parse(r.Text).RootElement;
        Assert.Empty(parsed[0].GetProperty("selected").EnumerateArray());
    }

    [Fact]
    public async Task Gloss_caps_a_rambling_free_text_answer_but_keeps_the_result_verbatim()
    {
        //the gloss is one line in the transcript, a long free-text answer must not push other answers off it
        var essay = new string('x', 500);
        var prompter = new ScriptedPrompter(_ => new[] { new AskAnswer("DB", new[] { essay }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        Assert.True(r.Gloss!.Length <= 60, $"gloss is {r.Gloss.Length} chars: {r.Gloss}");
        Assert.EndsWith("…", r.Gloss, StringComparison.Ordinal);
        Assert.StartsWith("DB: xxx", r.Gloss, StringComparison.Ordinal);
        //the cap applies to the display only, the model receives the answer uncut
        Assert.Equal(essay, JsonDocument.Parse(r.Text).RootElement[0].GetProperty("selected")[0].GetString());
    }

    [Fact]
    public async Task Gloss_caps_by_DISPLAY_CELLS_not_utf16_units()
    {
        //the cap must count display cells, a CJK character takes two cells. four wide answers are used, one answer fits both rules and cannot tell them apart
        var wide = new string('漢', 200);
        var prompter = new ScriptedPrompter(_ => new[]
        {
            new AskAnswer("A", new[] { wide }), new AskAnswer("B", new[] { wide }),
            new AskAnswer("C", new[] { wide }), new AskAnswer("D", new[] { wide }),
        });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        Assert.True(UnicodeWidth.Of(r.Gloss!) <= 120, $"gloss is {UnicodeWidth.Of(r.Gloss!)} cells: {r.Gloss}");
    }

    [Fact]
    public async Task Gloss_cap_never_splits_a_surrogate_pair()
    {
        //a cut inside a surrogate pair produces a lone surrogate that encodes as U+FFFD. the leading x shifts the cut inside a pair, an even index would pass by luck
        var emoji = "x" + string.Concat(Enumerable.Repeat("😀", 60));
        var prompter = new ScriptedPrompter(_ => new[] { new AskAnswer("DB", new[] { emoji }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        var roundTripped = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(r.Gloss!));
        Assert.DoesNotContain('�', roundTripped);   //a lone surrogate would surface as U+FFFD after the round trip.
        Assert.Equal(r.Gloss, roundTripped);
    }

    [Fact]
    public async Task Gloss_caps_the_whole_row_when_every_question_answers_long()
    {
        var prompter = new ScriptedPrompter(_ => new[]
        {
            new AskAnswer(new string('H', 32), new[] { new string('a', 40) }),
            new AskAnswer(new string('I', 32), new[] { new string('b', 40) }),
            new AskAnswer(new string('J', 32), new[] { new string('c', 40) }),
            new AskAnswer(new string('K', 32), new[] { new string('d', 40) }),
        });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        Assert.True(r.Gloss!.Length <= 121, $"gloss is {r.Gloss.Length} chars");
        Assert.EndsWith("…", r.Gloss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_tty_throws_actionable_message()
    {
        var (tool, _) = LoadAskUser(Canonical, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default));
        //the message must match the exact wording a non-interactive caller sees.
        Assert.Equal("interactive input unavailable in non-interactive mode", ex.Message);
    }

    [Fact]
    public async Task Object_option_parses_into_AskOption_with_description_and_recommended()
    {
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "sqlite" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        await tool.ExecuteAsync(Args("""
            {"questions":[{"question":"Which db?","header":"DB","options":[
              {"label":"sqlite","description":"file-based, zero config","recommended":true},
              "postgres"],"multi_select":false}]}
            """), new FakeCtx(), default);

        var opts = prompter.Seen![0].Options;
        Assert.Equal("sqlite", opts[0].Label);
        Assert.Equal("file-based, zero config", opts[0].Description);
        Assert.True(opts[0].Recommended);
    }

    [Fact]
    public async Task String_option_still_parses_as_a_plain_AskOption_no_description_not_recommended()
    {
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "sqlite" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        await tool.ExecuteAsync(Args(OneQuestion), new FakeCtx(), default);

        var opts = prompter.Seen![0].Options;
        Assert.Equal("sqlite", opts[0].Label);
        Assert.Null(opts[0].Description);
        Assert.False(opts[0].Recommended);
        Assert.Equal("postgres", opts[1].Label);
    }

    [Fact]
    public async Task Mixed_array_of_string_and_object_options_all_reach_the_prompter()
    {
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "redis" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        await tool.ExecuteAsync(Args("""
            {"questions":[{"question":"Which cache?","header":"Cache","options":[
              "redis",
              {"label":"memcached","description":"simpler, no persistence"},
              {"label":"none","recommended":false}],"multi_select":false}]}
            """), new FakeCtx(), default);

        var opts = prompter.Seen![0].Options;
        Assert.Equal(3, opts.Count);
        Assert.Equal("redis", opts[0].Label);
        Assert.Null(opts[0].Description);
        Assert.Equal("memcached", opts[1].Label);
        Assert.Equal("simpler, no persistence", opts[1].Description);
        Assert.Equal("none", opts[2].Label);
        Assert.False(opts[2].Recommended);
    }

    [Fact]
    public async Task Object_option_without_label_throws_malformed_args_message()
    {
        var prompter = new ScriptedPrompter(_ => Array.Empty<AskAnswer>());
        var (tool, _) = LoadAskUser(Canonical, prompter);
        const string argsJson = """
            {"questions":[{"question":"q","header":"H","options":[{"description":"x"},"b"],"multi_select":false}]}
            """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => tool.ExecuteAsync(Args(argsJson), new FakeCtx(), default));
        Assert.Contains("no label", ex.Message);
        //the message must be clean, the loop seam reads it and a stack trace must never leak through
        Assert.DoesNotContain("at Gatto", ex.Message);
    }

    [Fact]
    public async Task Non_string_description_is_ignored_not_thrown()
    {
        //a wrong-typed field is treated as absent, matching the lenient handling of multi_select
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "sqlite" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        await tool.ExecuteAsync(Args("""
            {"questions":[{"question":"q","header":"H","options":[{"label":"sqlite","description":5},"postgres"],"multi_select":false}]}
            """), new FakeCtx(), default);

        Assert.Null(prompter.Seen![0].Options[0].Description);
    }

    [Fact]
    public async Task Non_boolean_recommended_is_treated_as_false_not_thrown()
    {
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "sqlite" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        await tool.ExecuteAsync(Args("""
            {"questions":[{"question":"q","header":"H","options":[{"label":"sqlite","recommended":"yes"},"postgres"],"multi_select":false}]}
            """), new FakeCtx(), default);

        Assert.False(prompter.Seen![0].Options[0].Recommended);
    }

    [Fact]
    public async Task Duplicate_label_across_a_string_and_an_object_option_throws()
    {
        var prompter = new ScriptedPrompter(_ => Array.Empty<AskAnswer>());
        var (tool, _) = LoadAskUser(Canonical, prompter);
        const string argsJson = """
            {"questions":[{"question":"q","header":"H","options":["a",{"label":"a","description":"same label"}],"multi_select":false}]}
            """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => tool.ExecuteAsync(Args(argsJson), new FakeCtx(), default));
        Assert.Contains("duplicate", ex.Message);
    }

    //the validation message text must stay byte-identical to the built-in's

    [Theory]
    [InlineData("""{"questions":[]}""", "1-4 questions")]
    [InlineData("""{"questions":[{"question":"q","header":"H","options":["only-one"],"multi_select":false}]}""", "2-4 options")]
    [InlineData("""{"questions":[{"question":"q","header":"H","options":["a","a"],"multi_select":false}]}""", "duplicate")]
    [InlineData("""{"questions":[{"question":"q","header":"H","options":["a",5],"multi_select":false}]}""", "non-string option")]
    [InlineData("""{"questions":[{"question":"q","header":"H","options":["a",null],"multi_select":false}]}""", "non-string option")]
    public async Task Validation_failures_throw(string argsJson, string expectInMessage)
    {
        var prompter = new ScriptedPrompter(_ => Array.Empty<AskAnswer>());
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => tool.ExecuteAsync(Args(argsJson), new FakeCtx(), default));
        Assert.Contains(expectInMessage, ex.Message);
    }

    [Fact]
    public async Task Empty_string_header_yields_empty_message_not_missing_param()
    {
        var prompter = new ScriptedPrompter(_ => Array.Empty<AskAnswer>());
        var (tool, _) = LoadAskUser(Canonical, prompter);
        const string argsJson = """
            {"questions":[{"question":"q","header":"","options":["a","b"],"multi_select":false}]}
            """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => tool.ExecuteAsync(Args(argsJson), new FakeCtx(), default));
        Assert.Contains("header must not be empty", ex.Message);
        Assert.DoesNotContain("missing required parameter", ex.Message);
    }

    [Fact]
    public async Task A_LONG_HEADER_reaches_the_panel_and_the_model_WHOLE()
    {
        //the panel flows and wraps a long header, so a rejection only cost a small model its turn
        const string header = "Database migration strategy for the users table";
        var prompter = new ScriptedPrompter(qs => new[] { new AskAnswer(qs[0].Header, new[] { "expand" }) });
        var (tool, _) = LoadAskUser(Canonical, prompter);

        var r = await tool.ExecuteAsync(Args($$"""
            {"questions":[{"question":"q","header":"{{header}}","options":["expand","contract"]}]}
            """), new FakeCtx(), default);

        Assert.False(r.IsError);
        Assert.Equal(header, prompter.Seen![0].Header);
        Assert.Equal(header, JsonDocument.Parse(r.Text).RootElement[0].GetProperty("header").GetString());
    }

    [Fact]
    public async Task Missing_header_yields_missing_required_parameter()
    {
        var prompter = new ScriptedPrompter(_ => Array.Empty<AskAnswer>());
        var (tool, _) = LoadAskUser(Canonical, prompter);
        const string argsJson = """
            {"questions":[{"question":"q","options":["a","b"],"multi_select":false}]}
            """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => tool.ExecuteAsync(Args(argsJson), new FakeCtx(), default));
        Assert.Equal("missing required parameter: header", ex.Message);
    }

    [Fact]
    public async Task Non_object_question_element_yields_missing_required_parameter_not_framework_exception()
    {
        //the JsonValueKind.Object check comes first, so a non-object question element yields a clean ArgumentException
        var prompter = new ScriptedPrompter(_ => Array.Empty<AskAnswer>());
        var (tool, _) = LoadAskUser(Canonical, prompter);
        const string argsJson = """{"questions":[5]}""";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => tool.ExecuteAsync(Args(argsJson), new FakeCtx(), default));
        Assert.Equal("missing required parameter: question", ex.Message);
    }

    //the canonical text sits on the vetted list, so it never prompts. one edited byte drops it off the list and it prompts like any extension

    [Fact]
    public async Task Vetted_canonical_grants_readclass_no_permission_prompt()
    {
        var (tool, vetted) = LoadAskUser(Canonical, null);
        Assert.True(vetted);   //the sanity check proves the seam about to be exercised applies.

        var store = PermissionStore.Load(_home, _home, out var warning);
        Assert.Null(warning);
        var prompter = new FakePermissionPrompter();
        var gate = new PermissionGate(store, prompter, autoYes: false);
        if (vetted) gate.AllowReadClass(tool.Name);   //this line mirrors the GattoApp wiring for a vetted tool

        await gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "ask_user", "{}")));

        Assert.Empty(prompter.Requests);   //the read-class allow-list short-circuits the gate, so the prompter is never asked.
    }

    [Fact]
    public async Task One_byte_edited_copy_is_not_vetted_and_gate_prompts()
    {
        var edited = Canonical + "// tampered\n";
        var (tool, vetted) = LoadAskUser(edited, null);
        Assert.False(vetted);   //a single edited byte drops the text off the shipped allow-list.

        var store = PermissionStore.Load(_home, _home, out var warning);
        Assert.Null(warning);
        var prompter = new FakePermissionPrompter(PermissionAnswer.Once);
        var gate = new PermissionGate(store, prompter, autoYes: false);
        if (vetted) gate.AllowReadClass(tool.Name);   //this line must not run, vetted is false for the edited text

        await gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", "ask_user", "{}")));

        Assert.Single(prompter.Requests);   //without vetting or a read-class entry, the gate must ask once.
    }
}
