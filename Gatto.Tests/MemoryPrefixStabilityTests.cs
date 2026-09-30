using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a mid-session memory_write must not change the composed prefix, and the next reload boundary picks it up. prefill is the tax, so a re-prefill is user-chosen
public class MemoryPrefixStabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-mem-prefix-").FullName;
    private const int Budget = 1000;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RoleFile Role() =>
        new("r", "p", null, Array.Empty<string>(), false, null, ThinkingLevel.Medium, null);

    private void SeedIndex(string text) =>
        MemoryDir.Write(MemoryDir.FindProjectRoot(_root), "seeded", text);

    //the app's recomposeSystem closure and its index load, copied here because the end-to-end tests drive a real launch
    private string Recompose()
    {
        var loaded = MemoryIndex.Compose(MemoryDir.FindProjectRoot(_root), Budget);
        return RoleComposition.Compose(
            Role(), null, Array.Empty<(string, string)>(), null, cwd: _root,
            memoryIndex: loaded.Text, memoryTruncatedLines: loaded.TruncatedLines, memoryNudge: true).SystemText;
    }

    [Fact]
    public async Task MemoryWrite_MidSession_DoesNotChangeComposedPrefix()
    {
        SeedIndex("- seeded fact");

        //compose once at launch and hand the result to the conversation exactly as the app does.
        var systemA = Recompose();
        var convo = new Conversation(systemA);
        Assert.Contains("- seeded fact", systemA, StringComparison.Ordinal);

        //a new fact is written to disk as its own file. one fact per slug means a new fact never touches the file an existing fact lives in.
        await new MemoryWriteTool(() => Budget).ExecuteAsync(
            JsonDocument.Parse("""{"slug":"new-fact","content":"- new fact","mode":"write"}""").RootElement,
            new TestToolContext(_root), default);
        Assert.Contains("- new fact", File.ReadAllText(MemoryDir.PathFor(_root, "new-fact")), StringComparison.Ordinal);

        //the running prefix has not moved: no boundary was crossed, so nothing re-read. the live system message stays byte-identical to the launch text.
        Assert.Equal(systemA, convo.Messages[0].Content);
        Assert.DoesNotContain("- new fact", convo.Messages[0].Content!, StringComparison.Ordinal);

        //composing again from the same launch-time load must be byte-identical, compose never reads the disk
        var launchLoad = new MemoryIndex.LoadResult("- seeded fact", 0);
        Assert.Equal(systemA, RoleComposition.Compose(
            Role(), null, Array.Empty<(string, string)>(), null, cwd: _root,
            memoryIndex: launchLoad.Text, memoryTruncatedLines: launchLoad.TruncatedLines,
            memoryNudge: true).SystemText);

        //at a boundary (/new, /role, /compact, /model) the fresh composition does include the new fact
        var systemB = Recompose();
        Assert.Contains("- seeded fact", systemB, StringComparison.Ordinal);
        Assert.Contains("- new fact", systemB, StringComparison.Ordinal);
        Assert.NotEqual(systemA, systemB);
    }

    [Fact]
    public void MemoryDisabled_ComposesNoBlockAndNoNudgeEvenWithAnIndexOnDisk()
    {
        //with memory consent off the app skips the load and passes no nudge. a seeded index still adds nothing to the prefix
        SeedIndex("- seeded fact");

        var systemText = RoleComposition.Compose(
            Role(), null, Array.Empty<(string, string)>(), null, cwd: _root).SystemText;

        Assert.DoesNotContain("## Memory", systemText, StringComparison.Ordinal);
        Assert.DoesNotContain("memory_write", systemText, StringComparison.Ordinal);
    }
}

//the notice prints after the banner and before the first prompt. the check drives the plain loop, the rich loop can't run with stdout redirected
[Collection("e2e")]
public class MemoryLaunchWarningTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose() { Console.SetIn(_origIn); Console.SetOut(_origOut); }

    private static async Task<string> RunPlainRepl(string? memoryWarning)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            memoryWarning: memoryWarning);

        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader("/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    [Fact]
    public async Task LaunchWarning_PrintedAfterTheBanner_BeforeTheFirstPrompt()
    {
        var warning = Gatto.Cli.GattoApp.MemoryTruncationWarning(3);

        var output = await RunPlainRepl(warning);

        var iBanner = output.IndexOf("/quit to exit", StringComparison.Ordinal);
        var iWarn = output.IndexOf("♯ " + warning, StringComparison.Ordinal);
        var iPrompt = output.IndexOf("\n> ", StringComparison.Ordinal);
        Assert.True(iBanner >= 0 && iWarn > iBanner && iWarn < iPrompt, output);
    }

    [Fact]
    public async Task NoLaunchWarning_NothingExtraPrinted()
    {
        Assert.DoesNotContain("memory index over budget", await RunPlainRepl(null), StringComparison.Ordinal);
    }
}
