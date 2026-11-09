using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the plain face has no inbox, so under the home's inbox key it says so once and creates nothing in the project
[Collection("e2e")]
public sealed class PlainInboxLineTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-plain-inbox-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_cwd, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void UNDER_THE_INBOX_KEY_THE_PLAIN_FACE_SAYS_IT_HAS_NONE_AND_OPENS_NOTHING()
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_cwd), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", null, client, null, null, _cwd,
            () => Composed.Text("sys"), _ => new RoleSwitchResult(false, "", null, null, "unused"), inbox: true);
        var priorIn = Console.In;
        var priorOut = Console.Out;
        var output = new StringWriter();
        Console.SetIn(new StringReader("/new\n/quit\n"));
        Console.SetOut(output);
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetIn(priorIn); Console.SetOut(priorOut); }

        var text = output.ToString();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "inbox not opened, the plain face has no inbox"));
        Assert.False(Directory.Exists(Path.Combine(_cwd, ".gatto")));
        Assert.Empty(client.Requests);
    }
}
