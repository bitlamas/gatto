using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a byte order mark at the start of piped stdin is not the user's text: it never reaches the model, and a command behind it still runs
[Collection("e2e")]
public sealed class PlainReplBomTests
{
    private static readonly string Bom = char.ConvertFromUtf32(0xFEFF);

    private static FakeChatClient Run(string stdin)
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new Gatto.Core.Client.StreamEvent.TextDelta("ok"), new Gatto.Core.Client.StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"));
        var priorIn = Console.In;
        var priorOut = Console.Out;
        Console.SetIn(new StringReader(stdin));
        Console.SetOut(new StringWriter());
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetIn(priorIn); Console.SetOut(priorOut); }
        return client;
    }

    [Fact]
    public void A_quit_behind_a_byte_order_mark_ends_the_session_and_sends_nothing() =>
        Assert.Empty(Run(Bom + "/quit\n").Requests);

    [Fact]
    public void A_message_behind_a_byte_order_mark_reaches_the_model_without_it()
    {
        var client = Run(Bom + "hello\n/quit\n");
        var user = client.Requests.Single().Messages.Last(m => m.Role == "user");
        Assert.Equal("hello", user.Content);
    }
}
