using System.Net;
using System.Text.Json;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Gatto.Extensions;

namespace Gatto.Tests;

//warm Roslyn once, and keep every real-compile test in this class so xunit runs them one at a time
public sealed class RoslynWarmupFixture
{
    public RoslynWarmupFixture()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-roslyn-warmup-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "warm.csx"), "var x = 1 + 1;");
            var api = ExtensionHostTests.NewApi(out _, out _);
            ExtensionHost.LoadAll(dir, api, _ => { });
        }
        catch { } //a warmup failure is tolerated and never fails the fixture.
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

//join the e2e collection, so the process-global compile counter stays put while this class measures its delta
[Collection("e2e")]
public sealed class ExtensionHostTests : IClassFixture<RoslynWarmupFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-ext-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    //build the api the way the app does, one real CitationLedger shared by the recorder and the api. fetch runs in-process, so no test hits the network
    internal static GattoApi NewApi(out CitationLedger ledger, out GuardedFetch fetch)
    {
        var fakeHandler = new FetchHandler();
        var (api, l, f) = ExtensionHost.BuildApi(
            home: "C:\\home", cwd: "C:\\cwd",
            ledgerPath: () => null,
            prompter: () => null,
            log: _ => { },
            handler: fakeHandler,
            resolver: _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        ledger = l;
        fetch = f;
        return api;
    }

    private GattoApi NewApi() => NewApi(out _, out _);

    private sealed class FetchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("hello from the web"),
            });
    }

    private void WriteFile(string name, string body) => File.WriteAllText(Path.Combine(_dir, name), body);

    //commit into a registry that already holds a built-in, so the tool-name collision case is reachable.
    private void Register(IReadOnlyList<LoadedExtension> loaded, out ToolRegistry tools, out HookBus hooks, out List<string> diags)
    {
        tools = new ToolRegistry();
        tools.Register(new ShellTool());   //register a built-in so a name collision can happen.
        hooks = new HookBus();
        diags = new List<string>();
        var localDiags = diags;
        ExtensionHost.Commit(loaded, tools, hooks, m => localDiags.Add(m));
    }

    [Fact]
    public async Task MinimalTool_Registers_Callable_ThrownErrorCarriesExactMessage()
    {
        WriteFile("greet.csx", """
            Gatto.Register("greet", "greets", "{\"type\":\"object\"}",
                (args, ctx, ct) => throw new Exception("boom from greet"));
            """);
        var api = NewApi();
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, api, diags.Add);

        Assert.Single(loaded);
        Assert.Equal("greet", loaded[0].Name);
        var tool = loaded[0].Registrations.Tools.Single().Tool;
        Assert.Equal("greet", tool.Name);

        //the tool throws inside ExecuteAsync, and the exact message must reach the loop's error result
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new FakeCtx(), CancellationToken.None));
        Assert.Equal("boom from greet", ex.Message);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task A_script_contributes_an_endpoint_with_headers_and_a_quota_reader()
    {
        WriteFile("hosted.csx", """
            Gatto.Endpoint("hosted", "https://cloud.example.test/api", context: 4096,
                thinking: "{\"none\":null,\"high\":{\"reasoning_effort\":\"high\"}}",
                headers: ct => Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string> { ["Authorization"] = "Bearer t", ["x-account"] = "a" }),
                quota: usage => usage.TryGetProperty("left", out var n) ? new QuotaReading(n.GetInt64(), "big") : null);
            """);
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, NewApi(), diags.Add);

        Assert.Empty(diags);
        var (name, config) = Assert.Single(Assert.Single(loaded).Registrations.Endpoints);
        Assert.Equal("hosted", name);
        Assert.Equal("a", (await config.Headers!(CancellationToken.None))["x-account"]);
        Assert.Equal(new Gatto.Core.Client.QuotaReading(7, "big"), config.Quota!(JsonDocument.Parse("{\"left\":7}").RootElement));
    }

    private sealed class FakeCtx : IToolContext
    {
        public string Cwd => "C:\\cwd";
        public string HomePath => "C:\\home";
        public IUserPrompter? Prompter => null;
    }

    [Fact]
    public void SyntaxError_ReportsFileLine_OtherExtensionsStillLoad()
    {
        WriteFile("broken.csx", "this is not valid c#$$$");
        WriteFile("ok.csx", """Gatto.Register("okay", "ok", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");
        var api = NewApi();
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, api, diags.Add);

        Assert.Single(loaded);
        Assert.Equal("okay", loaded[0].Registrations.Tools.Single().Tool.Name);
        Assert.Contains(diags, d => d.Contains("broken.csx:") && System.Text.RegularExpressions.Regex.IsMatch(d, @"broken\.csx:\d+"));
    }

    [Fact]
    public void GrantVettedReadClass_CollisionSkippedVettedExt_NeverBlessesUnvettedTool()
    {
        //both files register shared_tool, so only aaa commits, and vetting zzz must grant nothing
        const string body = """
            Gatto.Register("shared_tool", "shared", "{\"type\":\"object\"}",
                (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")),
                readClass: true);
            """;
        WriteFile("aaa.csx", body);
        WriteFile("zzz.csx", body);
        var api = NewApi();
        var loaded = ExtensionHost.LoadAll(_dir, api, _ => { });
        Assert.Equal(2, loaded.Count);

        var tools = new ToolRegistry();
        var diags = new List<string>();
        var (committed, _) = ExtensionHost.Commit(loaded, tools, new HookBus(), diags.Add);

        Assert.Single(committed);                       //only one extension commits, zzz loses the name collision
        Assert.Equal("aaa", committed[0].Extension.Name);
        Assert.Contains(diags, d => d.Contains("zzz") && d.Contains("collides"));

        var granted = new List<string>();
        ExtensionHost.GrantVettedReadClass(committed, isVetted: src => src.Name == "zzz", granted.Add);
        Assert.Empty(granted);                          //vetting zzz grants nothing, its tool never committed

        ExtensionHost.GrantVettedReadClass(committed, isVetted: src => src.Name == "aaa", granted.Add);
        Assert.Equal("shared_tool", Assert.Single(granted));   //the control case: vetting the owner grants that tool.
    }

    [Fact]
    public void FolderExtension_LoadsSiblingHelper()
    {
        var folder = Path.Combine(_dir, "kit");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "helper.csx"), "string Greeting() => \"hi\";");
        File.WriteAllText(Path.Combine(folder, "main.csx"), """
            #load "helper.csx"
            Gatto.Register("kit_tool", Greeting(), "{\"type\":\"object\"}",
                (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));
            """);
        var api = NewApi();
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, api, diags.Add);

        Assert.Single(loaded);
        Assert.Equal("kit_tool", loaded[0].Registrations.Tools.Single().Tool.Name);
    }

    [Fact]
    public void FolderExtension_LoadEscapingRoot_Fails()
    {
        //the escape target sits outside the extensions dir, so only the folder ext tries it, and the confined resolver must refuse the #load
        var outside = Path.Combine(Path.GetTempPath(), "gatto-evil-" + Guid.NewGuid().ToString("N") + ".csx");
        File.WriteAllText(outside, "// outside the folder");
        try
        {
            var folder = Path.Combine(_dir, "kit");
            Directory.CreateDirectory(folder);
            var outsideEsc = outside.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(folder, "main.csx"), $$"""
                #load "{{outsideEsc}}"
                Gatto.Register("kit_tool", "x", "{\"type\":\"object\"}",
                    (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));
                """);
            var api = NewApi();
            var diags = new List<string>();

            var loaded = ExtensionHost.LoadAll(_dir, api, diags.Add);

            Assert.Empty(loaded);
            Assert.NotEmpty(diags);
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void MidScriptThrow_RegistersNothing_SiblingUnaffected()
    {
        WriteFile("halfway.csx", """
            Gatto.Register("half", "h", "{\"type\":\"object\"}",
                (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));
            throw new System.Exception("die mid-script");
            """);
        WriteFile("good.csx", """Gatto.Register("good_tool", "g", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");
        var api = NewApi();
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, api, diags.Add);

        Assert.Single(loaded);
        Assert.Equal("good_tool", loaded[0].Registrations.Tools.Single().Tool.Name);
        var failurePath = Path.Combine(_dir, "halfway.csx");
        Assert.Contains(diags, d => d.Contains("die mid-script") && d.Contains(failurePath));
    }

    [Fact]
    public void NameCollisionWithBuiltin_SkipsThatExtension_OthersCommit()
    {
        WriteFile("clash.csx", """Gatto.Register("shell", "clash", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");
        WriteFile("fine.csx", """Gatto.Register("fine_tool", "f", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");
        var api = NewApi();

        var loaded = ExtensionHost.LoadAll(_dir, api, _ => { });
        Register(loaded, out var tools, out _, out var diags);

        Assert.IsType<ShellTool>(tools.Get("shell"));   //the built-in keeps the name, and the tool from clash never replaces it
        Assert.NotNull(tools.Get("fine_tool"));
        Assert.Contains(diags, d => d.Contains("extension 'clash'") && d.Contains("tool 'shell'"));
    }

    [Fact]
    public void UnknownHookEvent_IsALoadFailure()
    {
        WriteFile("badhook.csx", """Gatto.On("frobnicate", p => System.Threading.Tasks.Task.CompletedTask);""");
        var api = NewApi();
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, api, diags.Add);

        Assert.Empty(loaded);
        Assert.Contains(diags, d => d.Contains("frobnicate"));
    }

    [Fact]
    public async Task CommittedHook_FiresOnMessageEnd()
    {
        var marker = Path.Combine(_dir, "fired.txt");
        var markerJson = marker.Replace("\\", "\\\\");
        WriteFile("hook.csx", $$"""
            Gatto.On("message_end", p => { System.IO.File.WriteAllText("{{markerJson}}", "yes"); return System.Threading.Tasks.Task.CompletedTask; });
            """);
        var api = NewApi();

        var loaded = ExtensionHost.LoadAll(_dir, api, _ => { });
        var tools = new ToolRegistry();
        var hooks = new HookBus();
        ExtensionHost.Commit(loaded, tools, hooks, _ => { });

        await hooks.EmitMessageEndAsync(new HookPayload(AssistantText: "done"));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task FetchThroughGattoHttp_RecordsToLedger()
    {
        var api = NewApi(out var ledger, out _);

        var result = await api.Http.FetchAsync("https://example.com/", CancellationToken.None);

        Assert.Equal("hello from the web", result.Text);
        Assert.Single(ledger.Entries);
        Assert.Equal("https://example.com/", ledger.Entries[0].Ref);
    }

    [Fact]
    public void Cache_HitSkipsRecompile_EditAndApiBumpForceRecompile()
    {
        WriteFile("cached.csx", """Gatto.Register("cached_tool", "c", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");

        var start = ExtensionHost.CompileCount;

        var loaded1 = ExtensionHost.LoadAll(_dir, NewApi(), _ => { });
        Assert.Single(loaded1);
        Assert.Equal(start + 1, ExtensionHost.CompileCount);
        var cacheDir = Path.Combine(_dir, ".cache");
        Assert.True(Directory.Exists(cacheDir));
        Assert.Single(Directory.GetFiles(cacheDir, "*.dll"));

        var loaded2 = ExtensionHost.LoadAll(_dir, NewApi(), _ => { });
        Assert.Single(loaded2);
        Assert.Equal("cached_tool", loaded2[0].Registrations.Tools.Single().Tool.Name);
        Assert.Equal(start + 1, ExtensionHost.CompileCount);

        WriteFile("cached.csx", """Gatto.Register("cached_tool", "c2", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");
        ExtensionHost.LoadAll(_dir, NewApi(), _ => { });
        Assert.Equal(start + 2, ExtensionHost.CompileCount);

        var saved = ExtensionHost.HostApiVersionOverride;
        try
        {
            ExtensionHost.HostApiVersionOverride = "999";
            ExtensionHost.LoadAll(_dir, NewApi(), _ => { });
            Assert.Equal(start + 3, ExtensionHost.CompileCount);
        }
        finally { ExtensionHost.HostApiVersionOverride = saved; }
    }

    //assert the quoted extension name, a dropped name would still pass

    [Fact]
    public void Policy_stages_the_trimmed_line()
    {
        var api = NewApi();
        api.BeginExtension("ask_user");
        api.Policy("  when a choice is the user's, stop and ask  ");
        Assert.Equal("when a choice is the user's, stop and ask", api.TakeStaged().Policy);
    }

    [Fact]
    public void No_policy_call_stages_no_line()
    {
        var api = NewApi();
        api.BeginExtension("quiet");
        Assert.Null(api.TakeStaged().Policy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Policy_rejects_an_empty_line(string line)
    {
        var api = NewApi();
        api.BeginExtension("probe");
        var ex = Assert.Throws<InvalidOperationException>(() => api.Policy(line));
        Assert.Contains("'probe'", ex.Message);
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("two\r\nlines")]
    [InlineData("tab\there")]
    public void Policy_rejects_anything_that_is_not_one_line(string line)
    {
        var api = NewApi();
        api.BeginExtension("probe");
        var ex = Assert.Throws<InvalidOperationException>(() => api.Policy(line));
        Assert.Contains("'probe'", ex.Message);
    }

    [Fact]
    public void Policy_rejects_a_line_over_two_hundred_characters()
    {
        var api = NewApi();
        api.BeginExtension("at_the_boundary");
        api.Policy(new string('a', 200));                       //200 characters is exactly the limit and must be accepted
        Assert.Equal(200, api.TakeStaged().Policy!.Length);

        api.BeginExtension("over_it");
        var ex = Assert.Throws<InvalidOperationException>(() => api.Policy(new string('a', 201)));
        Assert.Contains("'over_it'", ex.Message);
        Assert.Contains("201", ex.Message);                     //the error states how far over the limit it went.
    }

    [Fact]
    public void A_second_policy_call_throws_and_the_first_line_is_the_one_staged()
    {
        var api = NewApi();
        api.BeginExtension("probe");
        api.Policy("first");
        Assert.Throws<InvalidOperationException>(() => api.Policy("second"));
        Assert.Equal("first", api.TakeStaged().Policy);         //a rejected second call must not replace the first line, which stays staged
    }

    [Fact]
    public void BeginExtension_clears_a_policy_the_previous_extension_staged()
    {
        var api = NewApi();
        api.BeginExtension("x");
        api.Policy("x's line");
        api.BeginExtension("y");
        Assert.Null(api.TakeStaged().Policy);
    }

    [Fact]
    public void TakeStaged_clears_the_policy_so_the_next_load_starts_empty()
    {
        var api = NewApi();
        api.BeginExtension("x");
        api.Policy("x's line");
        Assert.Equal("x's line", api.TakeStaged().Policy);
        Assert.Null(api.TakeStaged().Policy);
    }

    //the host api version is asserted in one test only, a second assertion could drift from it

    //the structural error must outrank the content error, so a blank second call is reported as a second call
    [Fact]
    public void A_second_policy_call_is_reported_as_a_second_call_even_when_it_is_also_empty()
    {
        var api = NewApi();
        api.BeginExtension("probe");
        api.Policy("first");
        var ex = Assert.Throws<InvalidOperationException>(() => api.Policy("   "));
        Assert.Contains("second policy line", ex.Message);
        Assert.DoesNotContain("must not be empty", ex.Message);
    }

    //test each claim at the site that owns it, so the ordering claim about Commit takes a hand-built list

    [Fact]
    public void A_tool_less_extension_commits_and_keeps_its_policy_line()
    {
        WriteFile("skillish.csx", """Gatto.Policy("always restate the task before starting it");""");
        var loaded = ExtensionHost.LoadAll(_dir, NewApi(), _ => { });

        var (tools, extensions) = ExtensionHost.Commit(loaded, new ToolRegistry(), new HookBus(), _ => { });

        Assert.Empty(tools);
        Assert.Single(extensions);
        Assert.Equal("always restate the task before starting it", extensions[0].Registrations.Policy);
    }

    [Fact]
    public void A_collision_skipped_extension_contributes_no_line()
    {
        WriteFile("clasher.csx", """
            Gatto.Policy("never fires");
            Gatto.Register("shell", "clash", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));
            """);
        var loaded = ExtensionHost.LoadAll(_dir, NewApi(), _ => { });
        Assert.Single(loaded);                      //the script loads fine. the collision skip happens only at commit.
        Assert.Equal("never fires", loaded[0].Registrations.Policy);

        var registry = new ToolRegistry();
        registry.Register(new ShellTool());         //register a built-in so the extension collides on its name.
        var (_, extensions) = ExtensionHost.Commit(loaded, registry, new HookBus(), _ => { });

        Assert.Empty(extensions);                   //a skipped extension must leave no policy line.
    }

    //a script that throws must contribute nothing, including a policy line staged before the throw
    [Fact]
    public void A_script_that_throws_after_Policy_contributes_no_line()
    {
        WriteFile("doomed.csx", """
            Gatto.Policy("never fires");
            throw new System.Exception("die after the line");
            """);
        var diags = new List<string>();

        var loaded = ExtensionHost.LoadAll(_dir, NewApi(), diags.Add);
        Assert.Empty(loaded);                       //the load failed, so nothing remains to commit.
        Assert.Contains(diags, d => d.Contains("die after the line"));

        var (_, extensions) = ExtensionHost.Commit(loaded, new ToolRegistry(), new HookBus(), _ => { });
        Assert.Empty(extensions);
    }

    [Fact]
    public void Commit_returns_committed_extensions_in_the_order_it_was_given()
    {
        var loaded = new[] { Staged("zeta", "z line"), Staged("alpha", "a line") };

        var (_, extensions) = ExtensionHost.Commit(loaded, new ToolRegistry(), new HookBus(), _ => { });

        Assert.Equal(new[] { "zeta", "alpha" }, extensions.Select(e => e.Name).ToArray());
    }

    //hand-build the stage only for the ordering test, a builder that skips the loader would be a second way to make an extension
    private static LoadedExtension Staged(string name, string policy) =>
        new(name,
            new ExtensionSource(name, name + ".csx", new[] { name + ".csx" }),
            new StagedRegistrations(
                Array.Empty<(ITool, bool)>(),
                Array.Empty<(string, Func<HookPayload, Task>)>(),
                policy));
}
