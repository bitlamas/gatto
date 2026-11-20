using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a gate keeps per-session state in a process that outlives /new, so both faces clear it there and nowhere else
[Collection("e2e")]
public sealed class NewSessionResetTests
{
    [Fact]
    public async Task THE_RICH_FACE_CLEARS_THE_GATE_AT_NEW()
    {
        var resets = 0;
        var h = new RichReplHarness(Path.GetTempPath(), newSession: () => resets++);
        h.Keys.Line("/new").Line("/quit");

        await h.RunAsync();

        Assert.Equal(1, resets);
    }

    [Fact]
    public void THE_PLAIN_FACE_CLEARS_THE_GATE_AT_NEW_AND_NOT_AT_A_MESSAGE()
    {
        var resets = 0;
        var client = new FakeChatClient();
        client.EnqueueTurn(new Gatto.Core.Client.StreamEvent.TextDelta("ok"), new Gatto.Core.Client.StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "default", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new RoleSwitchResult(false, "", null, null, "unused"), newSession: () => resets++);
        var priorIn = Console.In;
        var priorOut = Console.Out;
        Console.SetIn(new StringReader("hello\n/new\n/quit\n"));
        Console.SetOut(new StringWriter());
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetIn(priorIn); Console.SetOut(priorOut); }

        Assert.Equal(1, resets);
    }
}
