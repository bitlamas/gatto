using System.Net;
using Gatto.Cli;
using Gatto.Core.Tools;
using Gatto.Terminal;
using Gatto.Tests.Support;

namespace Gatto.Tests;

//each forced failure renders its own line with a fix, and a green run exits 0. the context checks assert whole lines, so the tree sits on a subst drive
[Collection(SubstDriveCollection.Name)]
public class DoctorTests : IDisposable
{
    private readonly SubstRoot _root = SubstRoot.Mount();
    private readonly string _home;
    private readonly string _cwd;

    //both directories sit on the mount. home must too: a test ascends from inside home, and a temp home would bring real ancestors back in reach
    public DoctorTests()
    {
        _home = _root.Dir("home");
        _cwd = _root.Dir("cwd");
    }

    public void Dispose() => _root.Dispose();   //dispose unmounts the drive and deletes the tree behind it.

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Respond(request));
    }

    private static HttpClient HealthyClient() => new(new FakeHandler());

    private static HttpClient UnreachableClient() => new(new FakeHandler
    {
        Respond = _ => throw new HttpRequestException("connection refused"),
    });

    private const int Port = 1235;
    private string _modelPath = "";
    private string _llamaServerPath = "";

    private void WriteGreenHome()
    {
        _modelPath = Path.Combine(_home, "model.gguf");
        File.WriteAllText(_modelPath, "");
        _llamaServerPath = Path.Combine(_home, "llama-server.exe");
        File.WriteAllText(_llamaServerPath, "");

        var modelPathJson = _modelPath.Replace("\\", "\\\\");
        var llamaServerJson = _llamaServerPath.Replace("\\", "\\\\");

        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{Port}}" } },
              "default_endpoint": "local",
              "default_model": "test-model",
              "llama_server": "{{llamaServerJson}}",
              "glyphs": "unicode"
            }
            """);

        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{"files":[{"path":"{{modelPathJson}}","active":true}],"port":{{Port}},"context":8192}""");

        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), """{"model":"test-model"}""");

        File.WriteAllText(Path.Combine(_home, "GATTO.md"), "# GATTO.md\n");
        //the permissions.json file is absent on purpose, since that check reports skipped on a green home
    }

    [Fact]
    public async Task AllChecksGreen_ExitsZero_EveryLineIsCheckmark()
    {
        WriteGreenHome();
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        //doctor prints the 10 checks and nothing else, since the version belongs to the shared banner
        Assert.Equal(10, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("✓", l));
    }

    [Fact]
    public async Task Check1_ServerUnreachable_FailsWithServeStartFix()
    {
        WriteGreenHome();
        var doctor = NewDoctor(UnreachableClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "server reachable");
        Assert.StartsWith("✗", line);
        Assert.Contains("run: gatto serve start test-model", line);
    }

    //a handler that holds every request until its token fires, as a server that never answers would
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable, the delay ends only by its token");
        }
    }

    //an untimed client over a silent server, so only the check's own deadline can end the read
    [Fact]
    public async Task A_SILENT_SERVER_ENDS_AT_THE_CHECKS_OWN_DEADLINE()
    {
        WriteGreenHome();
        using var http = new HttpClient(new SilentHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var output = new StringWriter();

        var run = NewDoctor(http).RunAsync(_home, _cwd, output, CancellationToken.None);
        var first = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(ReferenceEquals(run, first), "the doctor never finished, so nothing bounds its read but the client");
        Assert.Equal(1, await run);
        Assert.Contains("unreachable", Line(output, "server reachable"));
    }

    //a timeout on the client is a budget beneath every check's token, and a linked token cannot lengthen it
    [Fact]
    public void THE_DOCTOR_CLIENT_CARRIES_NO_TIMEOUT()
    {
        using var http = GattoApp.DoctorClient(new FakeHandler());
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    //the resolved glyph set is used, since the harness is ascii without WT_SESSION. the unknown key fails the whole load, so the glyphs setting is never read
    [Fact]
    public async Task Check2_UnknownKeyInGattoJson_FailsWithEditFix()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local","bogus_key":true}
            """);
        var g = CommandBanner.GlyphsFor(_home);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "gatto.json:");
        Assert.StartsWith(g.Bad, line);
        Assert.Contains("unknown key", line);
        Assert.Contains("run: gatto doctor", line);
    }

    //the glyph set comes from the resolver, since a harness without WT_SESSION is an ascii host and no gatto.json here pins it
    [Fact]
    public async Task Check2_MissingGattoJson_IsAFailureNotASilentPass()
    {
        var g = CommandBanner.GlyphsFor(_home);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "gatto.json:");
        Assert.StartsWith(g.Bad, line);
        Assert.Contains("no gatto.json at", line);
    }

    [Fact]
    public async Task RegionsKeyPresent_IsSilentlyIgnored_AndStaysGreen()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{Port}}" } },
              "default_endpoint": "local",
              "default_model": "test-model",
              "llama_server": "{{_llamaServerPath.Replace("\\", "\\\\")}}",
              "regions": "off"
            }
            """);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);   //the regions key stays in the allowlist, so a config that carries it still loads
        Assert.DoesNotContain("regions:", output.ToString());
    }

    //the models line names the weights root, the folder that holds the weight files and the one the user moves
    [Fact]
    public async Task Check3_TheModelsLine_NamesTheWeightsRoot()
    {
        WriteGreenHome();
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        //assert the default root, since the fixture sets no root and a fresh machine has none either
        Assert.Contains(Path.Combine(_home, "weights"), Line(output, "models:"), StringComparison.Ordinal);
    }

    //a model under the old root must still be found after the root moves, so assert it instead of trusting the model_path claim
    [Fact]
    public async Task Check3_AModelUnderTheOldRoot_IsStillFoundAfterTheRootMoves()
    {
        WriteGreenHome();
        //insert the key after the opening brace, since trimming trailing braces leaves broken json that reads as an ignored key
        var path = Path.Combine(_home, "gatto.json");
        var config = File.ReadAllText(path);
        File.WriteAllText(path, string.Concat("{\"weights_root\":\"D:\\\\elsewhere\",",
                config.AsSpan(config.IndexOf('{') + 1)));

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        //assert on the models line rather than the exit, which sums every check and turns red for unrelated failures
        var line = Line(output, "models:");
        Assert.StartsWith("✓", line, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", line, StringComparison.Ordinal);
        Assert.Contains(@"D:\elsewhere", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check3_ModelModelFileMissing_FailsNamingTheModelAndPath()
    {
        WriteGreenHome();
        File.Delete(_modelPath);   //the profile still references the file after this delete.
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "models:");
        Assert.StartsWith("✗", line);
        Assert.Contains("'test-model'", line);
        Assert.Contains("model file not found", line);
        Assert.Contains("fix the listed model(s)", line);
    }

    [Fact]
    public async Task Check3_ModelProjectorMissing_FailsNamingITasTheProjectorNotTheModel()
    {
        //a missing projector file must fail the check, and its line must name the projector instead of the model
        WriteGreenHome();
        var projector = Path.Combine(_home, "mmproj-model-BF16.gguf");   //the projector path is deliberately never written to disk.
        var modelDir = Path.Combine(_home, "models", "test-model");
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""
            {"files":[{"path":"{{_modelPath.Replace("\\", "\\\\")}}","active":true}],
             "mmproj":"{{projector.Replace("\\", "\\\\")}}",
             "port":{{Port}},"context":8192}
            """);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "models:");
        Assert.Contains("'test-model'", line);
        Assert.Contains("vision projector", line);
        Assert.Contains(projector, line);
        Assert.DoesNotContain("model file not found", line);   //only the projector is missing, so the model phrase must stay out.
    }

    [Fact]
    public async Task Check3_LocalBaseUrlPortMismatchesActiveModelPort_Fails()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:9999" } },
              "default_endpoint": "local",
              "default_model": "test-model",
              "llama_server": "{{_llamaServerPath.Replace("\\", "\\\\")}}",
              "glyphs": "unicode"
            }
            """);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "models:");
        Assert.StartsWith("✗", line);
        Assert.Contains("does not match model 'test-model' port", line);
        Assert.Contains("fix the listed model(s)", line);
    }

    [Fact]
    public async Task NoModelsAtAll_Check3AndCheck5Skip_Check1HasNothingToProbe()
    {
        //no local model exists, and the endpoint names no base URL. with no default model either, the server check has no port to probe
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"local":{}},"default_endpoint":"local","glyphs":"unicode"}
            """);
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{}");
        File.WriteAllText(Path.Combine(_home, "GATTO.md"), "# GATTO.md\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);   //the server check fails here, since there is nothing to probe and no model to derive a port from
        var serverLine = Line(output, "server reachable");
        Assert.StartsWith("✗", serverLine);
        Assert.Contains("no local.base_url and no default_model", serverLine);
        Assert.Contains("run:", serverLine);

        var modelsLine = Line(output, "models:");
        Assert.StartsWith("✓", modelsLine);
        Assert.Contains("none configured", modelsLine);

        var llamaLine = Line(output, "llama_server:");
        Assert.StartsWith("✓", llamaLine);
        Assert.Contains("skipped", llamaLine);
    }

    [Fact]
    public async Task Check4_AnUnreadableRoleFile_NamesTheRoleAndTheReason()
    {
        //a role file doctor cannot use must be reported by name and with a reason
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), """{"surprise":1}""");
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "roles:");
        Assert.StartsWith("✗", line);
        Assert.Contains("'generalist'", line);
        Assert.Contains("surprise", line);            //the failure line names the unknown key itself.
        Assert.Contains("fix the listed role file", line);
    }

    [Fact]
    public async Task Check4_TheEmptyRolesFixNAMES_THE_HOME_BEING_CHECKED_not_the_default_one()
    {
        //the fix sentence must name the home under check. otherwise a second install is sent to edit a file it is not using
        WriteGreenHome();
        Directory.Delete(Path.Combine(_home, "roles"), recursive: true);

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();
        await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        var line = Line(output, "roles:");
        Assert.Contains(Path.Combine(_home, ".shipped"), line, StringComparison.Ordinal);
        Assert.DoesNotContain(@"~\.gatto\.shipped", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check4_BareShippedRoleResolvesViaDefaultModel_StaysGreen()
    {
        //a role with no model key resolves through default_model, the same precedence the launch path uses
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.StartsWith("✓", Line(output, "roles:"));
    }

    [Fact]
    public async Task Check4_A_LOCAL_role_carrying_the_single_MODEL_key_is_not_a_problem()
    {
        //a role's single model key is resolved by endpoint kind, so on a local endpoint it is a pass
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), """{"model":"test-model"}""");
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.StartsWith("✓", Line(output, "roles:"));
    }

    [Fact]
    public async Task Check4_A_CLOUD_role_with_no_model_anywhere_is_still_a_doctor_problem()
    {
        //a cloud role with no model must still fail doctor, so the guard must cover the cloud side too
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"cloudy":{"base_url":"https://example.invalid"}},"default_endpoint":"cloudy","glyphs":"unicode"}
            """);
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{}");   //neither the role nor the config supplies a model here.
        File.WriteAllText(Path.Combine(_home, "GATTO.md"), "# GATTO.md\n");

        var handler = new FakeHandler { Respond = req =>
            req.RequestUri!.AbsolutePath.EndsWith("/v1/models")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) };
        var doctor = NewDoctor(new HttpClient(handler));
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "roles:");
        Assert.StartsWith("✗", line);
        Assert.Contains("'generalist'", line);
    }

    [Fact]
    public async Task ExtensionsCheck_BrokenScript_ListsFileLineAndFailsAlone()
    {
        WriteGreenHome();
        var extDir = Path.Combine(_home, "extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "broken.csx"), "this is not valid c#$$$");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "extensions:");
        Assert.StartsWith("✗", line);
        Assert.Matches(@"broken\.csx:\d+", line);
        Assert.Contains("fix the listed extension script", line);

        //a broken extension must fail only its own check, leaving every other line green.
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in lines)
            if (!l.Contains("extensions:") )
                Assert.StartsWith("✓", l);
    }

    [Fact]
    public async Task ExtensionsCheck_ValidScript_CountsAndStaysGreen()
    {
        WriteGreenHome();
        var extDir = Path.Combine(_home, "extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "ok.csx"),
            """Gatto.Register("ok_tool", "ok", "{\"type\":\"object\"}", (a,c,t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "extensions:");
        Assert.StartsWith("✓", line);
        Assert.Contains("1 script(s) compile", line);
    }

    [Fact]
    public async Task Check5_LlamaServerNotSet_FailsWithSetFix()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{Port}}" } },
              "default_endpoint": "local",
              "default_model": "test-model",
              "glyphs": "unicode"
            }
            """);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "llama_server:");
        Assert.StartsWith("✗", line);
        Assert.Contains("not set", line);
        Assert.Contains("run: set \"llama_server\" in gatto.json", line);
    }

    [Fact]
    public async Task Check5_LlamaServerPathMissing_FailsNamingThePath()
    {
        WriteGreenHome();
        File.Delete(_llamaServerPath);
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "llama_server:");
        Assert.StartsWith("✗", line);
        Assert.Contains("does not exist", line);
        Assert.Contains("run: fix \"llama_server\"", line);
    }

    //the Visual C++ cause may appear only when the probe reports the runtime absent, so the false case must not name it
    [Theory]
    [InlineData(true, "Visual C++ Redistributable", "cudart")]
    [InlineData(false, "extracted into the same folder", "Visual C++")]
    public async Task Check5_A_DLL_NOT_FOUND_DEATH_names_the_CRT_only_on_the_discriminator(
        bool vcRuntimeAbsent, string mustSay, string mustNotSay)
    {
        WriteGreenHome();
        var doctor = new Doctor(HealthyClient(),
            probeBinary: _ => new ProbeResult(ProbeShape.DllNotFound,
                "exited with STATUS_DLL_NOT_FOUND", VcRuntimeAbsent: vcRuntimeAbsent));
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "llama_server:");
        Assert.StartsWith("✗", line);
        Assert.Contains(mustSay, line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mustNotSay, line, StringComparison.OrdinalIgnoreCase);
    }

    //the line must name the sibling program and the file that should be there, since naming the fault alone sends the user in circles
    [Fact]
    public async Task Check5_A_KNOWN_SIBLING_IS_NAMED_and_so_is_the_file_that_should_be_there()
    {
        WriteGreenHome();
        var doctor = new Doctor(HealthyClient(),
            probeBinary: _ => new ProbeResult(ProbeShape.KnownSibling,
                "a known llama.cpp release program, not the server", SiblingName: "llama-cli.exe"));
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, "llama_server:");
        Assert.StartsWith("✗", line);
        Assert.Contains("llama-cli.exe", line, StringComparison.Ordinal);
        //a check for the server name alone would pass on the configured path, so assert the guidance clause itself
        Assert.Contains($"point it at {Gatto.Core.Acquire.LlamaSiblings.ServerName}", line,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check6_CorruptPermissionsFile_FailsAlone_OtherChecksStayGreen()
    {
        WriteGreenHome();
        var gattoDir = Path.Combine(_cwd, ".gatto");
        Directory.CreateDirectory(gattoDir);
        File.WriteAllText(Path.Combine(gattoDir, "permissions.json"), "{ not json");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        //doctor prints the 10 checks and nothing else, since the version belongs to the shared banner
        Assert.Equal(10, lines.Length);

        var permLine = Line(output, "permissions file");
        Assert.StartsWith("✗", permLine);
        Assert.Contains("unreadable", permLine);
        Assert.Contains("fix or delete", permLine);
        Assert.Contains("run: gatto doctor", permLine);

        //one failure must never abort the other checks, so every other line stays green.
        foreach (var l in lines)
            if (!l.Contains("permissions file") )
                Assert.StartsWith("✓", l);
    }

    //the context line must report the set that actually loads, since existence lines can each be true and still mislead

    [Fact]
    public async Task Check7_ReportsTheEffectiveSet_IncludingACompatPulledClaudeMd()
    {
        WriteGreenHome();
        //the config leaves compat false by default, the project's .gatto.json flips it true, and CLAUDE.md is what loads
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"context_files":{"compat":true}}""");
        File.WriteAllText(Path.Combine(_cwd, "CLAUDE.md"), "# project context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.StartsWith("✓", line);
        //the home layer joins first and cwd is already in the prefix, so the line shows the arrow then the file
        Assert.Contains("→ CLAUDE.md", line);
        Assert.Contains("compat: true", line);                       //the displayed flag is the effective one that pulled the file in.
        //assert the rendered home token, since a check that the absolute path is absent could never fail
        Assert.Contains(HomeLayerToken(), line);
    }

    //the line must name the fallback it did not load, or an inert compat looks like a working one
    [Fact]
    public async Task Check7_NamesTheFallbackShadowedByAGattoMd_SoAnInertCompatIsVisible()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"context_files":{"compat":true}}""");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# gatto context\n");
        File.WriteAllText(Path.Combine(_cwd, "CLAUDE.md"), "# claude context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.StartsWith("✓", line);
        Assert.Contains("GATTO.md", line);        //names the file that loaded.
        Assert.Contains("compat: true", line);    //the flag reads true even though it did nothing here.
        Assert.Contains("shadowed", line);
        Assert.Contains("CLAUDE.md", line);
    }

    [Fact]
    public async Task Check7_SaysNothingAboutShadowing_WhenNoFallbackSitsBesideTheGattoMd()
    {
        //the note must stay silent in the ordinary case, or it becomes noise users learn to skip.
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# gatto context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.DoesNotContain("shadowed", Line(output, "context files:"));
    }

    //the memory line must say what is in force and which file decided it. a project file in an ancestor directory can turn memory off and leave the store quiet

    [Fact]
    public async Task Memory_Enabled_ReportsTheFactCount()
    {
        WriteGreenHome();
        var dir = Path.Combine(_cwd, ".gatto", "memory");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.md"), "- one fact\n");
        File.WriteAllText(Path.Combine(dir, "b.md"), "- another\n");

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        var line = Line(output, "memory:");
        Assert.StartsWith("✓", line);
        Assert.Contains("enabled", line);
        Assert.Contains("2 facts", line);
    }

    [Fact]
    public async Task Memory_DisabledByAProjectFile_NamesTheDecidingFile()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"memory":{"enabled":false}}""");

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        var line = Line(output, "memory:");
        Assert.Contains("disabled", line);
        Assert.Contains(".gatto.json", line);
    }

    [Fact]
    public async Task Memory_DisabledInGattoJson_SaysSo_NotBlamingAProjectFile()
    {
        //the config turns memory off here, so the line must not blame a project file that does not exist
        WriteGreenHome();
        //the config is written whole here, since a merge helper could produce invalid json and turn every assertion below into a load failure
        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{Port}}" } },
              "default_endpoint": "local",
              "default_model": "test-model",
              "llama_server": "{{_llamaServerPath.Replace("\\", "\\\\")}}",
              "memory": { "enabled": false }
            }
            """);

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        var line = Line(output, "memory:");
        Assert.Contains("disabled", line);
        Assert.Contains("gatto.json", line);
        Assert.DoesNotContain(".gatto.json", line);
    }

    [Fact]
    public async Task Check7_ReportsTheHomeLayer_WhenTheWalkFindsNothing()
    {
        WriteGreenHome();
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.StartsWith("✓", line);
        Assert.Contains(HomeLayerToken(), line);
        Assert.Contains("compat: false", line);
    }

    [Fact]
    public async Task Check7_ProjectFilesShortenAgainstCwd_AndListTheHomeLayerFirst()
    {
        //the line must stay unwrapped, and an absolute path would overflow the width, so files under cwd print relatively
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# project context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.StartsWith("✓", line);
        //the project path prints relatively, with the arrow join before it as the second entry
        Assert.Contains("→ GATTO.md", line);
        Assert.DoesNotContain(Path.Combine(_cwd, "GATTO.md"), line);
        //the home file layers under the project file instead of being suppressed, so it is listed first
        Assert.Contains(HomeLayerToken(), line);
        Assert.True(line.IndexOf(HomeLayerToken(), StringComparison.Ordinal)
                  < line.IndexOf("→ GATTO.md", StringComparison.Ordinal),
            "the home layer prints first, in load order");
    }

    //an assertion against wording no code path can emit can never fail, so it guards nothing

    [Fact]
    public async Task Check7_HomeAncestorOfCwd_IsCountedOnceNotTwice()
    {
        //home can be an ancestor of the project directory, so the ascent and the home layer find one file. two loaded rather than three is the dedup oracle
        WriteGreenHome();
        var sub = Path.Combine(_home, "proj");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "GATTO.md"), "# project context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        await doctor.RunAsync(_home, sub, output, CancellationToken.None);

        var line = Line(output, "context files:");
        Assert.Contains("2 loaded", line);                  //the two are the home file and the project file.
    }

    [Fact]
    public async Task Check7_HomeLayerOff_WithNoProjectFile_IsACheckAndNamesTheKey()
    {
        //with no project file, turning the home layer off is an explicit choice, and doctor flags only what is wrong
        WriteGreenHome();
        var gattoJson = Path.Combine(_home, "gatto.json");
        File.WriteAllText(gattoJson,
            File.ReadAllText(gattoJson).TrimEnd().TrimEnd('}')
            + ",\n  \"context_files\": { \"home\": false }\n}");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.StartsWith("✓", line);
        Assert.Contains("context_files.home is false", line);   //the line must name the responsible key itself.
        Assert.Contains("set home back to true", line);
        Assert.DoesNotContain("first run writes the default", line);
    }

    //an empty context set is normal on a fresh install and must pass, while a home layer turned off still fails
    [Fact]
    public async Task Check7_NoContextFiles_PassesAndSaysHowToWriteOne()
    {
        WriteGreenHome();
        File.Delete(Path.Combine(_home, "GATTO.md"));
        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files");
        Assert.StartsWith("✓", line);
        Assert.Contains("/init", line);
        //pin the absence of the first-run advice, so it cannot come back here
        Assert.DoesNotContain("first run writes the default", line);
    }

    [Fact]
    public async Task Doctor_ReportsGattoJsonOnWalk()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"context_files":{"compat":true}}""");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, ".gatto.json at");
        Assert.StartsWith("✓", line);
        Assert.Contains(_cwd, line);
        Assert.Contains("compat: true", line);
    }

    [Fact]
    public async Task Doctor_FailsCheckOnMalformedGattoJson()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"bogus":true}""");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, ".gatto.json");
        Assert.StartsWith("✗", line);
        Assert.Contains("unknown key", line);
        Assert.Contains("run: gatto doctor", line);

        //one failure must never abort the other checks, so every other line stays green.
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in lines)
            if (!l.Contains(".gatto.json") )
                Assert.StartsWith("✓", l);
    }

    [Fact]
    public async Task Doctor_PresentButNoRulingGattoJson_ReportedNotTreatedAsAbsent()
    {
        //a well-formed file that pins nothing is still a file, so it must be reported as present
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), "{}");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, ".gatto.json at");
        Assert.StartsWith("✓", line);
        Assert.Contains(_cwd, line);
        Assert.DoesNotContain("no .gatto.json on the walk", output.ToString());
    }

    [Fact]
    public async Task Doctor_PresentButUnreadableGattoJson_FailsInsteadOfSilentlyAbsent()
    {
        //the collect path survives an unreadable file, but doctor must say it exists and cannot be read. a silent skip hides it from the user
        WriteGreenHome();
        var path = Path.Combine(_cwd, ".gatto.json");
        File.WriteAllText(path, """{"context_files":{"compat":true}}""");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(1, exit);
        var line = Line(output, ".gatto.json");
        Assert.StartsWith("✗", line);
        Assert.Contains("unreadable", line);
        Assert.Contains("run: gatto doctor", line);
    }

    [Fact]
    public async Task Doctor_MalformedGattoJson_StillListsValidOnesFoundElsewhereOnTheWalk()
    {
        //a malformed file in one directory must not swallow a valid file found elsewhere on the walk, and the failure line reports both
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"bogus":true}""");
        var subDir = Path.Combine(_cwd, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, ".gatto.json"), """{"context_files":{"compat":true}}""");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, subDir, output, CancellationToken.None);

        Assert.Equal(1, exit);
        //use that check's own phrasing here, since a short string would also match another line naming the same file
        var line = Line(output, ".gatto.json at");
        Assert.StartsWith("✗", line);
        Assert.Contains("unknown key", line);      //the line reports the malformed ancestor's error.
        Assert.Contains(subDir, line);              //the valid nearer file survives on the same line.
        Assert.Contains("compat: true", line);
        Assert.Contains("run: gatto doctor", line);
    }

    [Fact]
    public async Task CloudOnlyConfig_NoLocalEndpoint_Check1SkipsAndStaysGreen()
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"cloudy":{"base_url":"https://example.invalid"}},"default_endpoint":"cloudy","glyphs":"unicode"}
            """);
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), """{"model":"gpt-x"}""");
        File.WriteAllText(Path.Combine(_home, "GATTO.md"), "# GATTO.md\n");

        //the reachability check probes the cloud endpoint as well, so the fake answers an OpenAI-shaped list. a bodyless 200 would read as something else owning the port
        var handler = new FakeHandler { Respond = req =>
            req.RequestUri!.AbsolutePath.EndsWith("/v1/models")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) };
        var doctor = NewDoctor(new HttpClient(handler));
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "server reachable");
        Assert.StartsWith("✓", line);
        Assert.Contains("skipped", line);
        Assert.Contains("cloud-only", line);
        var epLine = Line(output, "endpoint 'cloudy'");
        Assert.StartsWith("✓", epLine);
    }

    [Fact]
    public async Task Doctor_notes_a_model_living_in_Downloads_as_info_not_failure()
    {
        WriteGreenHome();

        //the fixture asks the same DownloadsDir that doctor asks, since a hard-coded profile path fails when Downloads is relocated
        var dl = Gatto.Core.Acquire.ModelLocation.DownloadsDir;
        var modelDir = Path.Combine(_home, "models", "test-model");
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{"files":[{"path":"{{Path.Combine(dl, "model.gguf").Replace("\\", "\\\\")}}","active":true}],"port":{{Port}},"context":8192}""");

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        //anchor on the info line's own wording, since the models failure line also holds the Downloads path
        var line = Line(output, "can be set to clean");
        Assert.StartsWith("i ", line);            //the line is info rather than a pass or a failure
    }

    //a silent veto gets an info line, and its whole content is the file name that vetoed it.
    [Fact]
    public async Task Doctor_names_the_project_file_that_vetoed_auto_compaction()
    {
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"auto_compact":false}""");

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        var line = Line(output, "auto-compaction: disabled by");
        Assert.StartsWith("i ", line);                       //a project veto reports as info rather than a failure
        Assert.Contains(".gatto.json", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_says_nothing_about_auto_compaction_when_no_project_file_vetoes_it()
    {
        //silence is the overwhelming majority case, since a report that comments on every unchanged setting stops being read
        WriteGreenHome();

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.DoesNotContain("auto-compaction:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_says_nothing_when_the_HOME_config_turned_auto_compaction_off()
    {
        //home's own auto_compact setting sits in the file the user edited, so a line about it would only add noise
        WriteGreenHome();
        var configPath = Path.Combine(_home, "gatto.json");
        var config = File.ReadAllText(configPath).TrimEnd().TrimEnd('}');
        File.WriteAllText(configPath, config + ",\"auto_compact\":false}");
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"auto_compact":false}""");

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        //prove the edited gatto.json still loads, since a broken config would suppress the line for another reason and the test would assert nothing
        Assert.StartsWith("✓", Line(output, "gatto.json"));

        Assert.DoesNotContain("auto-compaction:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_ignores_a_Downloads_sibling_directory()
    {
        WriteGreenHome();
        var dlSibling = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads-old");
        var modelDir = Path.Combine(_home, "models", "test-model");
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{"files":[{"path":"{{Path.Combine(dlSibling, "model.gguf").Replace("\\", "\\\\")}}","active":true}],"port":{{Port}},"context":8192}""");

        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.DoesNotContain("can be set to clean", output.ToString());   //a prefix match would read Downloads-old as the Downloads folder and show a false cleanup hint
    }

    [Fact]
    public async Task Doctor_reports_a_running_managed_server_as_info_and_stays_green()
    {
        WriteGreenHome();
        var doctor = new Doctor(HealthyClient(),
            probeBinary: _ => new ProbeResult(ProbeShape.ClassicServer, "version: 10076 (fake)"),
            describeRunning: () => new Gatto.Roles.RunningInfo("test-model", Port, DateTime.UtcNow.AddHours(-2).ToString("o")));
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);                    //the running-server line is info, so doctor still exits zero.
        var line = Line(output, "llama-server is running");
        Assert.StartsWith("i ", line);
        Assert.Contains("test-model", line);
        Assert.Contains("serve stop", line);
    }

    //price all three shard files, and go through Line() so a missing row fails before the size check
    [Fact]
    public async Task Doctor_prices_a_running_shard_set_as_the_WHOLE_model()
    {
        WriteGreenHome();

        var modelDir = Path.Combine(_home, "models", "test-model");
        var one = Path.Combine(_home, "m-00001-of-00003.gguf");
        foreach (var (name, bytes) in new (string, long)[]
                 {
                     ("m-00001-of-00003.gguf", 10_000_000),
                     ("m-00002-of-00003.gguf", 100_000_000),
                     ("m-00003-of-00003.gguf", 100_000_000),
                 })
        {
            using var fs = File.Create(Path.Combine(_home, name));
            fs.SetLength(bytes);
        }
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{"files":[{"path":"{{one.Replace("\\", "\\\\")}}","active":true}],"port":{{Port}},"context":8192}""");

        var doctor = new Doctor(HealthyClient(),
            probeBinary: _ => new ProbeResult(ProbeShape.ClassicServer, "version: 10076 (fake)"),
            describeRunning: () => new Gatto.Roles.RunningInfo("test-model", Port, DateTime.UtcNow.AddHours(-2).ToString("o")));
        var output = new StringWriter();

        await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        var line = Line(output, "llama-server is running");
        Assert.Contains("~0.2 GB", line, StringComparison.Ordinal);
        Assert.DoesNotContain("<0.1 GB", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_says_nothing_about_servers_when_none_is_running()
    {
        WriteGreenHome();
        var output = new StringWriter();
        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);
        Assert.DoesNotContain("llama-server is running", output.ToString());
    }

    [Fact]
    public async Task Doctor_notes_two_roles_targeting_different_managed_servers()
    {
        WriteGreenHome();
        var model2 = Path.Combine(_home, "models", "test-model2");
        Directory.CreateDirectory(model2);
        File.WriteAllText(Path.Combine(model2, "profile.json"),
            $$"""{"files":[{"path":"{{_modelPath.Replace("\\", "\\\\")}}","active":true}],"port":1236,"context":8192}""");
        File.WriteAllText(Path.Combine(_home, "roles", "oracle.json"), """{"model":"test-model2"}""");

        var output = new StringWriter();
        var exit = await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);                    //the note is an info line, so doctor exits zero
        var line = Line(output, "one at a time");
        Assert.StartsWith("i ", line);
        Assert.Contains("serve stop", line);
    }

    private async Task<(int Exit, string Text)> RunDoctorOn(string configJson, HttpClient client)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), configJson);
        var output = new StringWriter();
        var exit = await NewDoctor(client).RunAsync(_home, _cwd, output, CancellationToken.None);
        return (exit, output.ToString());
    }

    private const string ServerEndpointConfig = """
        {"endpoints":{"server":{"base_url":"http://127.0.0.1:59999","context":4096}},
         "default_endpoint":"server","default_model":"m"}
        """;

    [Fact]
    public async Task Doctor_reports_nonlocal_endpoint_unreachable_when_nothing_listens()
    {
        var handler = new FakeHandler { Respond = _ => throw new HttpRequestException("refused") };
        var (exit, text) = await RunDoctorOn(ServerEndpointConfig, new HttpClient(handler));
        Assert.Contains("server not running", text);
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Doctor_names_a_squatter_when_http_answers_but_is_not_openai_shaped()
    {
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>hi</html>") } };
        var (_, text) = await RunDoctorOn(ServerEndpointConfig, new HttpClient(handler));
        Assert.Contains("something else owns this port", text);   //an html body means something else owns the port, so don't read it as an auth failure
    }

    [Fact]
    public async Task Doctor_names_auth_shape_and_the_wrong_port_possibility()
    {
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var (_, text) = await RunDoctorOn(ServerEndpointConfig, new HttpClient(handler));
        Assert.Contains("auth", text);
        Assert.Contains("wrong port", text);              //the message must name both possibilities: an auth problem, or a port that is not gatto's server.
    }

    [Fact]
    public async Task Doctor_names_the_first_run_fix_when_no_model_is_configured()
    {
        //name the missing default_model as the first-run fix, and don't report the empty role file as a schema error
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}
            """);
        var rolesDir = Path.Combine(_home, "roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "generalist.json"), "{}");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();
        await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("default_model", text);
        Assert.DoesNotContain("\"model\" is required and \"model\" must be absent", text);
    }

    //the tint marks the verdict, so only the glyph takes it and the line text keeps the terminal foreground (no theme, no escape codes)

    private static Theme RichTheme() => new(new TermCaps(Rich: true, TrueColor: true));

    private static Doctor NewThemedDoctor(
        HttpClient http, Theme theme, Func<Gatto.Roles.RunningInfo?>? describeRunning = null) =>
        new(http,
            probeBinary: _ => new ProbeResult(ProbeShape.ClassicServer, "version: 10076 (fake)"),
            describeRunning: describeRunning,
            theme: theme);

    [Fact]
    public async Task Doctor_paints_the_pass_glyph_with_the_ok_token()
    {
        WriteGreenHome();
        var theme = RichTheme();
        var output = new StringWriter();

        await NewThemedDoctor(HealthyClient(), theme).RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Contains(theme.Paint("✓", Theme.Ok), output.ToString());
    }

    [Fact]
    public async Task Doctor_paints_the_fail_glyph_with_the_err_token()
    {
        WriteGreenHome();
        var theme = RichTheme();
        var output = new StringWriter();

        //the unreachable server is the only ✗ in an otherwise green home, so the painted ✗ must be there
        await NewThemedDoctor(UnreachableClient(), theme).RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Contains(theme.Paint("✗", Theme.Err), output.ToString());
    }

    [Fact]
    public async Task Doctor_dims_the_info_prefix()
    {
        WriteGreenHome();
        var theme = RichTheme();
        var output = new StringWriter();

        await NewThemedDoctor(HealthyClient(), theme,
                describeRunning: () => new Gatto.Roles.RunningInfo(
                    "test-model", Port, DateTime.UtcNow.AddHours(-2).ToString("o")))
            .RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Contains(theme.Paint("i", Theme.Dim), output.ToString());
    }

    //a captured writer must never receive an escape code, so output stays byte-for-byte plain until a theme is armed.
    [Fact]
    public async Task Doctor_emits_no_escape_codes_when_no_theme_is_armed()
    {
        WriteGreenHome();
        var output = new StringWriter();

        await NewDoctor(HealthyClient()).RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.DoesNotContain('\x1b', output.ToString());
    }

    //match the trailing path (the temp dir name plus GATTO.md), which survives either collapse and can't come from a project file
    private string HomeLayerToken() =>
        Path.GetFileName(_home) + Path.DirectorySeparatorChar + "GATTO.md";

    //builds a doctor whose binary probe reports ClassicServer, the green-home default. a probe test passes its own fake
    private static Doctor NewDoctor(HttpClient http) =>
        new(http, probeBinary: _ => new ProbeResult(ProbeShape.ClassicServer, "version: 10076 (fake)"));

    private static string Line(StringWriter output, string needle)
    {
        var line = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Contains(needle));
        Assert.True(line is not null, $"no line containing '{needle}' in:\n{output}");
        return line!;
    }

    [Fact]
    public async Task UpdateLine_IsAnINFO_line_never_a_cross_even_when_github_is_unreachable()
    {
        //an update line is info, so neither an old release nor an unreachable GitHub can turn doctor red
        WriteGreenHome();
        var unreachable = new Doctor(HealthyClient(), checkUpdate: _ => Task.FromResult<UpdateState?>(null));
        var behind = new Doctor(HealthyClient(),
            checkUpdate: _ => Task.FromResult<UpdateState?>(new UpdateState(DateTimeOffset.Now, "v99.0.0", false)));

        var outA = new StringWriter();
        await unreachable.RunAsync(_home, _home, outA, default);
        var outB = new StringWriter();
        await behind.RunAsync(_home, _home, outB, default);

        Assert.Contains("couldn't reach GitHub", outA.ToString(), StringComparison.Ordinal);
        Assert.Contains("v99.0.0", outB.ToString(), StringComparison.Ordinal);
        foreach (var text in new[] { outA.ToString(), outB.ToString() })
            foreach (var line in text.Split('\n').Where(l => l.Contains("updates:")))
                Assert.DoesNotContain("✗", line, StringComparison.Ordinal);
    }

    //a build ahead of the release says so in an info line (no setup fault, and the latest-release claim would be false)
    [Fact]
    public async Task UpdateLine_SAYS_WHEN_THIS_BUILD_IS_AHEAD_OF_THE_RELEASE()
    {
        WriteGreenHome();
        //the release is older than this build, the state every dev build and pre-release tag sees
        var ahead = new Doctor(HealthyClient(),
            checkUpdate: _ => Task.FromResult<UpdateState?>(new UpdateState(DateTimeOffset.Now, "v0.0.1", false)));
        var output = new StringWriter();

        await ahead.RunAsync(_home, _home, output, default);

        var line = Line(output, "updates:");
        Assert.Contains("is ahead of the latest release (v0.0.1)", line, StringComparison.Ordinal);
        //name the false claim in the negative assertion, so it cannot silently return.
        Assert.DoesNotContain("you're on the latest release", line, StringComparison.Ordinal);
        Assert.DoesNotContain("✗", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFetcher_NoUpdateLine_which_is_what_keeps_this_suite_hermetic()
    {
        //no fetcher means no network call, so a test that passes none stays hermetic
        WriteGreenHome();
        var output = new StringWriter();

        await new Doctor(HealthyClient()).RunAsync(_home, _home, output, default);

        Assert.DoesNotContain("updates:", output.ToString(), StringComparison.Ordinal);
    }

    //drive the context switch with a real gatto.json (a stubbed config would leave the caller untested)
    [Fact]
    public async Task Check7_HomeLayerOff_InTheConfig_LeavesTheHomeFileOutOfTheSet()
    {
        WriteGreenHome();   //write the home file first, so the negative assertion has a real file to rule out.
        var llamaServerJson = _llamaServerPath.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{Port}}" } },
              "default_endpoint": "local",
              "default_model": "test-model",
              "llama_server": "{{llamaServerJson}}",
              "context_files": { "home": false }
            }
            """);
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# project context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.DoesNotContain(HomeLayerToken(), line);   //the file exists on disk, yet the config switch excludes it from the set.
        Assert.Contains("home layer off", line);         //the report must name the switch that excluded the file.
    }

    [Fact]
    public async Task Check7_NoHomeFileAtAll_NamesThePathOneWouldGoAt()
    {
        //doctor is the only surface that tells the user where the home file goes, so the passing case must name the path.
        WriteGreenHome();
        File.Delete(Path.Combine(_home, "GATTO.md"));
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# project context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("no home GATTO.md", line);
        Assert.Contains(HomeLayerToken(), line);
    }

    [Fact]
    public async Task Check7_UnreadableHomeFile_SaysNotReadable_NotThatItIsAbsent()
    {
        //a locked file must be reported as unreadable, so don't tell the user it's missing (they'd hunt the wrong problem)
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# project context\n");
        using var hold = new FileStream(Path.Combine(_home, "GATTO.md"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("not readable", line);
        Assert.DoesNotContain("no home GATTO.md", line);   //the absent-file sentence must not appear when the file exists but cannot be read.
    }

    [Fact]
    public async Task Check7_CommentOnlyHomeFile_SaysSo_RatherThanGoingSilent()
    {
        //comment stripping leaves the file empty, so the report names it comment-only, or it looks loaded when it isn't
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "GATTO.md"), "<!-- notes to myself -->\n");
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "# project context\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("comment-only", line);
        Assert.Contains(HomeLayerToken(), line);
    }


    [Fact]
    public async Task Check7_EmptyWalk_CommentOnlyHomeFile_DoesNotClaimTheFileIsAbsent()
    {
        //the empty-result branch must not report the dropped file as absent
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_home, "GATTO.md"), "<!-- only notes -->\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("comment-only", line);
        Assert.Contains(HomeLayerToken(), line);
        Assert.DoesNotContain("no GATTO.md in", line);   //never report an existing file as missing, the reader would hunt the wrong problem
    }

    [Fact]
    public async Task Check7_EmptyWalk_UnreadableHomeFile_SaysSoAndOffersThePermissionFix()
    {
        WriteGreenHome();
        using var hold = new FileStream(Path.Combine(_home, "GATTO.md"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("not readable", line);
        Assert.Contains("permissions", line);            //every check's line ends in a fix the user can perform, so permissions appears here
        Assert.DoesNotContain("no GATTO.md in", line);
    }

    [Fact]
    public async Task Check7_CommentOnlyFileInCwd_IsNamed_NotSilentlyDropped()
    {
        //the user just wrote this file, so name it instead of dropping it in silence
        WriteGreenHome();
        File.WriteAllText(Path.Combine(_cwd, "GATTO.md"), "<!-- todo: write this -->\n");

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("GATTO.md here is comment-only", line);
    }

    [Fact]
    public async Task Check7_UnreadableFileInCwd_IsNamed_NotSilentlyDropped()
    {
        WriteGreenHome();
        var here = Path.Combine(_cwd, "GATTO.md");
        File.WriteAllText(here, "# project context\n");
        using var hold = new FileStream(here, FileMode.Open, FileAccess.Read, FileShare.None);

        var doctor = NewDoctor(HealthyClient());
        var output = new StringWriter();

        var exit = await doctor.RunAsync(_home, _cwd, output, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Line(output, "context files:");
        Assert.Contains("GATTO.md here is not readable", line);
    }

}
