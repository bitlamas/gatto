using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Gatto.Cli;
using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Core.Sandbox;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Sandbox;

//the three doors and the process state. every folder a plant touches is one the test made, so no label outlives it
[SupportedOSPlatform("windows")]
[Collection("e2e")]
public sealed class SandboxDoorsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-doors-").FullName;
    private string Home => Path.Combine(_root, "home");
    private string Project => Path.Combine(_root, "project");

    public SandboxDoorsTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SandboxState State() => new(Project, Home, [],
        nugetFolder: Directory.CreateDirectory(Path.Combine(_root, "nuget")).FullName,
        httpCache: Directory.CreateDirectory(Path.Combine(_root, "http-cache")).FullName,
        tempParent: Directory.CreateDirectory(Path.Combine(_root, "tmp")).FullName);

    private void HomeConfig(Action<JsonObject> edit)
    {
        GattoHome.EnsureInitialized(Home);
        var path = Path.Combine(Home, "gatto.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    [Fact]
    public void THE_HOME_TURNS_IT_ON_AND_NAMES_ITS_GRANTS()
    {
        var extra = Directory.CreateDirectory(Path.Combine(_root, "extra")).FullName;
        HomeConfig(r => { r["sandbox"] = true; r["sandbox_grants"] = new JsonArray(extra); });
        var config = GattoConfig.Load(Home);
        Assert.True(config.Sandbox);
        Assert.Equal([extra], config.SandboxGrants);
    }

    [Theory]
    [InlineData("relative\\folder")]
    [InlineData("C:\\no\\such\\folder\\anywhere")]
    public void A_GRANT_THAT_IS_NOT_AN_EXISTING_FOLDER_IS_A_CONFIG_ERROR(string grant)
    {
        HomeConfig(r => r["sandbox_grants"] = new JsonArray(grant));
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(Home));
        Assert.Contains("sandbox_grants", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PROJECT_UP_THE_PATH_TURNS_IT_ON_AND_A_NEARER_FALSE_DOES_NOT_TURN_IT_OFF()
    {
        var child = Directory.CreateDirectory(Path.Combine(Project, "src", "app")).FullName;
        Assert.False(ProjectFileConfig.EffectiveSandbox(child, out var none));
        Assert.Null(none);

        File.WriteAllText(Path.Combine(Project, ".gatto.json"), """{"sandbox":true}""");
        File.WriteAllText(Path.Combine(child, ".gatto.json"), """{"sandbox":false}""");
        Assert.True(ProjectFileConfig.EffectiveSandbox(child, out var by));
        Assert.Equal(Path.Combine(Project, ".gatto.json"), by);
    }

    [Fact]
    public void A_PROJECT_SANDBOX_OF_THE_WRONG_TYPE_IS_A_CONFIG_ERROR()
    {
        File.WriteAllText(Path.Combine(Project, ".gatto.json"), """{"sandbox":"yes"}""");
        Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadSandbox(Project));
    }

    [Fact]
    public void THE_COMMAND_LINE_WINS_THEN_THE_HOME_THEN_THE_PROJECT()
    {
        Assert.True(ArgRouter.Parse(["--sandbox"]).Sandbox);
        Assert.False(ArgRouter.Parse(["--yes"]).Sandbox);
        File.WriteAllText(Path.Combine(Project, ".gatto.json"), """{"sandbox":true}""");
        Assert.Equal("--sandbox", GattoApp.SandboxSource(true, true, Project));
        Assert.Equal("gatto.json", GattoApp.SandboxSource(false, true, Project));
        Assert.Equal(Path.Combine(Project, ".gatto.json"), GattoApp.SandboxSource(false, false, Project));
        File.Delete(Path.Combine(Project, ".gatto.json"));
        Assert.Null(GattoApp.SandboxSource(false, false, Project));
    }

    [Fact]
    public void TURNED_ON_THE_STATE_NAMES_THE_PROJECT_KEEPS_A_TEMP_FOLDER_AND_REFUSES_OFF()
    {
        using var state = State();
        Assert.Equal("sandbox off", state.Line);
        Assert.True(state.TurnOn("--sandbox"));
        Assert.Equal("sandbox on: the shell tool and derivatives cannot write outside the project folder, excluding folders in sandbox_grants and the NuGet cache.", state.Line);
        Assert.Equal(Project, state.Project);
        Assert.Equal("sandbox stays on: turned on by --sandbox", state.OffRefusal);
        Assert.True(Directory.Exists(state.Temp));
        Assert.StartsWith(Path.Combine(_root, "tmp"), state.Temp);
        Assert.True(state.TurnOn("/sandbox on"));
        Assert.Equal("--sandbox", state.Source);
        var temp = state.Temp!;
        state.Dispose();
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public async Task SANDBOX_IN_THE_RICH_REPL_SAYS_THE_STATE_AND_REFUSES_OFF()
    {
        using var state = State();
        state.TurnOn("--sandbox");
        var h = new RichReplHarness(Project, sandbox: state);
        h.Keys.Line("/sandbox off").Line("/sandbox nope").Line("/quit");
        await h.RunAsync();
        Assert.True(h.Saw("sandbox stays on: turned on by --sandbox"), h.Written());
        Assert.True(h.Saw(Gatto.Repl.Repl.SandboxUsage), h.Written());
    }

    [Fact]
    public void SANDBOX_ON_THE_PLAIN_LOOP_TURNS_IT_ON_AND_PRINTS_THE_LINE()
    {
        using var state = State();
        var client = new FakeChatClient();
        var loop = new Gatto.Core.Loop.AgentLoop(client, new Gatto.Core.Tools.ToolRegistry(), new Gatto.Core.Loop.HookBus(),
            new TestToolContext(Project), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Gatto.Core.Session.Conversation("sys"), "generalist", "m", null, client, null, null, Project,
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"),
            sandbox: state);
        var output = new StringWriter();
        var oldOut = Console.Out;
        var oldIn = Console.In;
        Console.SetOut(output);
        Console.SetIn(new StringReader("/sandbox\n/sandbox on\n/quit\n"));
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetIn(oldIn);
        }
        Assert.Contains("sandbox off", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("sandbox on: the shell tool and derivatives cannot write outside the project folder", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("/sandbox on", state.Source);
    }
}
