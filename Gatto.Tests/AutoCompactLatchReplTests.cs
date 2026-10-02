using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a failed auto-compaction stays off for the session, and a fresh session from /new arms it again
[Collection("e2e")]
public sealed class AutoCompactLatchReplTests
{
    private static AgentLoop Run(params string[] lines)
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("one"), new StreamEvent.Finished("stop", new Usage(60_000, 10)));
        client.EnqueueTurn(new StreamEvent.Finished("stop", null));   //the summary comes back empty, so the compaction fails
        client.EnqueueTurn(new StreamEvent.TextDelta("two"), new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m",
            budgetTokens: 65_536);
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", null, client, null, 65_536, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            autoCompact: 0.8);
        var priorIn = Console.In;
        var priorOut = Console.Out;
        Console.SetIn(new StringReader(string.Join("\n", lines) + "\n"));
        Console.SetOut(new StringWriter());
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetIn(priorIn); Console.SetOut(priorOut); }
        return loop;
    }

    [Fact]
    public void A_failed_auto_compaction_leaves_the_session_without_one() =>
        Assert.Null(Run("hi", "again", "/quit").ArmedAutoCompactAt);

    [Fact]
    public void New_arms_auto_compaction_again() =>
        Assert.Equal(0.8, Run("hi", "again", "/new", "/quit").ArmedAutoCompactAt);
}
