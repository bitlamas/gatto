using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//at rest /context runs through the queue like any command and its screen goes into the transcript
public class ContextSlashTests : IDisposable
{
    private readonly TextReader _in = Console.In;
    private readonly TextWriter _out = Console.Out;
    public void Dispose() { Console.SetIn(_in); Console.SetOut(_out); }

    private static async Task<string> Feed(Gatto.Repl.Repl repl, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input + "\n/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    private static Gatto.Repl.Repl Repl(Func<ContextUsageState, ContextInputs>? inputs)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        return new Gatto.Repl.Repl(loop, new Conversation("sys"), "generalist", "m", null, client, null, 65_536, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            contextInputs: inputs);
    }

    [Fact]
    public async Task At_rest_the_screen_is_written_with_the_system_and_the_tools_before_any_request()
    {
        var spec = new ToolSpec("read_file", "reads", JsonDocument.Parse("{\"type\":\"object\"}").RootElement);
        var output = await Feed(Repl(_ => new ContextInputs("m", "sys", [new ContextPartText("system prompt", ContextGroup.Prefix, new string('s', 400))],
            [spec], [], new RequestShape([spec], null, null, ReasoningHistory.All), 65_536, null, 1.0, null, null)), "/context");

        Assert.Contains("context · ", output);
        Assert.Contains("before the first request", output);
        Assert.Contains("system prompt", output);
        Assert.Contains("cache  no request yet", output);
    }

    [Fact]
    public async Task Without_the_figures_the_command_says_so()
    {
        var output = await Feed(Repl(null), "/context");
        Assert.Contains(Gatto.Repl.Repl.ContextUnavailable, output);
    }
}
