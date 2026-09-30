using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the session says so once per mismatch transition, and the line names only the probe's two ids
[Collection("e2e")]
public class ServingMismatchLineTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    //the probe closes over a mutable cell, so a test can move the server between turns
    private static (Gatto.Repl.Repl Repl, string?[] Serving) ReplWatching(
        string armed = "gemma-4-26b", bool onTheShelf = true)
    {
        var serving = new string?[] { null };
        var client = new FakeChatClient();
        for (var i = 0; i < 5; i++)
            client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", armed, null, client, null, null,
            Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            probeServing: () => new ServingProbe(serving[0], null,
                MismatchIsOnTheShelf: onTheShelf));
        return (repl, serving);
    }

    private static async Task<string> Feed(Gatto.Repl.Repl repl, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input + "\n/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    //nothing was wrong before this turn, and now something is
    [Fact]
    public async Task IT_SPEAKS_WHEN_THE_MISMATCH_APPEARS()
    {
        var (repl, serving) = ReplWatching(armed: "gemma-4-26b");

        await Feed(repl, "hello");                 //agreement between session and server gives nothing to say.
        serving[0] = "qwen3.6-35b";                //the server switches to another model under the session.
        var after = await Feed(repl, "hello again");

        Assert.Contains("the server is running qwen3.6-35b now", after, StringComparison.Ordinal);
        Assert.Contains("this session is on gemma-4-26b", after, StringComparison.Ordinal);
        Assert.Contains("/model qwen3.6-35b", after, StringComparison.Ordinal);
    }

    //the line is quiet on the next turn. the state has not changed, and a turn count is not news.
    [Fact]
    public async Task AND_IS_SILENT_ON_THE_NEXT_TURN_WITH_THE_SAME_MISMATCH()
    {
        var (repl, serving) = ReplWatching();
        serving[0] = "qwen3.6-35b";

        var first = await Feed(repl, "hello");
        var second = await Feed(repl, "hello again");

        Assert.Contains("the server is running", first, StringComparison.Ordinal);
        Assert.DoesNotContain("the server is running", second, StringComparison.Ordinal);
    }

    //a different model is a new fact, so the line speaks again, a latch on having spoken once would stay quiet forever
    [Fact]
    public async Task AND_SPEAKS_AGAIN_WHEN_THE_MISMATCH_CHANGES()
    {
        var (repl, serving) = ReplWatching();
        serving[0] = "qwen3.6-35b";
        await Feed(repl, "hello");

        serving[0] = "llama-3.3-8b";
        var after = await Feed(repl, "hello again");

        Assert.Contains("the server is running llama-3.3-8b now", after, StringComparison.Ordinal);
    }

    //unknown is never a mismatch, so a probe that cannot answer says nothing
    [Fact]
    public async Task AND_IS_SILENT_WHEN_THE_PROBE_CANNOT_ANSWER()
    {
        var (repl, serving) = ReplWatching();

        serving[0] = null;                          //null means the probe could not answer, so nothing is compared
        var output = await Feed(repl, "hello");

        Assert.DoesNotContain("the server is running", output, StringComparison.Ordinal);
    }

    //a file-stem name still gets the facts and no /model command, that command would name no model
    [Fact]
    public async Task AND_OFFERS_NO_COMMAND_FOR_A_NAME_THAT_IS_NOT_A_MODEL()
    {
        var (repl, serving) = ReplWatching(armed: "gemma-4-26b", onTheShelf: false);

        serving[0] = "mysterious-model-2.5-100b";     //a file stem rather than a shelf id
        var after = await Feed(repl, "hello");

        Assert.Contains("the server is running mysterious-model-2.5-100b now", after,
            StringComparison.Ordinal);
        Assert.Contains("this session is on gemma-4-26b", after, StringComparison.Ordinal);
        Assert.DoesNotContain("/model", after, StringComparison.Ordinal);
    }

    //the latch clears when the mismatch clears, so the same model speaks again after the gap
    [Fact]
    public async Task AND_THE_SAME_MODEL_SPEAKS_AGAIN_AFTER_THE_MISMATCH_CLEARS()
    {
        var (repl, serving) = ReplWatching();

        serving[0] = "qwen3.6-35b";
        var first = await Feed(repl, "hello");
        serving[0] = null;                            //no mismatch to report, so the latch clears
        await Feed(repl, "still here");
        serving[0] = "qwen3.6-35b";                   //the server drifts to the same model again.
        var third = await Feed(repl, "and again");

        Assert.Contains("the server is running qwen3.6-35b now", first, StringComparison.Ordinal);
        Assert.Contains("the server is running qwen3.6-35b now", third, StringComparison.Ordinal);
    }
}
