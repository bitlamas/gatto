using Gatto.Core.Commands;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//a warning raised before the banner is said again after it, since the alt screen covers what stderr printed
[Collection("e2e")]
public class LaunchWarningsTests : IDisposable
{
    private const string Collision = "extension: zz.csx: /undo is taken by undo.csx";

    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public void Dispose() { Console.SetIn(_origIn); Console.SetOut(_origOut); Console.SetError(_origErr); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_launch_sink_writes_stderr_always_and_carries_only_when_asked(bool carry)
    {
        var err = new StringWriter();
        Console.SetError(err);
        var carried = new List<string>();

        Gatto.Cli.GattoApp.LaunchSink(carried, carry)(Collision);

        Assert.Equal($"! {Collision}{Environment.NewLine}", err.ToString());
        Assert.Equal(carry ? [Collision] : [], carried);
    }

    private static async Task<string> RunPlainRepl(string? memoryWarning, IReadOnlyList<string>? launchWarnings)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new RoleSwitchResult(false, "", null, null, "unused"),
            memoryWarning: memoryWarning, launchWarnings: launchWarnings);

        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader("/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    [Fact]
    public async Task Plain_a_launch_warning_prints_after_the_memory_warning_and_before_the_first_prompt()
    {
        var memory = Gatto.Cli.GattoApp.MemoryTruncationWarning(3);

        var output = await RunPlainRepl(memory, [Collision, "second warning"]);

        var iBanner = output.IndexOf("/quit to exit", StringComparison.Ordinal);
        var iMemory = output.IndexOf("♯ " + memory, StringComparison.Ordinal);
        var iWarn = output.IndexOf("♯ " + Collision, StringComparison.Ordinal);
        var iSecond = output.IndexOf("♯ second warning", StringComparison.Ordinal);
        var iPrompt = output.IndexOf("\n> ", StringComparison.Ordinal);
        Assert.True(iBanner >= 0 && iMemory > iBanner && iWarn > iMemory && iSecond > iWarn && iSecond < iPrompt, output);
    }

    [Fact]
    public async Task Plain_a_launch_warning_is_sanitised()
    {
        var output = await RunPlainRepl(null, ["bad" + (char)27 + "[31mred"]);

        Assert.Contains("♯ bad[31mred", output, StringComparison.Ordinal);
        Assert.DoesNotContain((char)27 + "[31m", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_no_launch_warning_prints_no_row()
    {
        var output = await RunPlainRepl(null, null);

        Assert.DoesNotContain("♯", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rich_a_launch_warning_is_a_system_row_after_the_banner_and_before_the_first_command()
    {
        var reg = new CommandRegistry();
        reg.Add(new CommandRow("/undo", "[n|path]", "list snapshots for this folder, or restore one",
            (_, _) => Task.FromResult("undo answered"), "undo.csx"));
        var h = new RichReplHarness(Path.GetTempPath(), commands: reg, launchWarnings: [Collision]);
        h.Keys.Line("/undo").Line("/quit");
        await h.RunAsync();

        var text = h.Written();
        var iBanner = text.IndexOf("test-model", StringComparison.Ordinal);
        var iWarn = text.IndexOf(Collision, StringComparison.Ordinal);
        var iAnswer = text.IndexOf("undo answered", StringComparison.Ordinal);
        Assert.True(iBanner >= 0 && iWarn > iBanner && iAnswer > iWarn, text);
    }
}
