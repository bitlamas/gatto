using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the REPL's /new starts a new session, so the file it writes next names a new id, the projection of that file's own name
[Collection("e2e")]
public sealed class SessionIdentityReplTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-repl-id-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-repl-cwd-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _home, _cwd })
            try { Directory.Delete(d, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void NEW_IN_THE_REPL_STARTS_A_SESSION_WITH_ITS_OWN_ID()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("one"), new StreamEvent.Finished("stop", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("two"), new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_cwd), "m");
        var sessions = new SessionStore(_home, _cwd);
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", sessions, client, null, 65_536, _cwd,
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"));
        var priorIn = Console.In;
        var priorOut = Console.Out;
        Console.SetIn(new StringReader("hi\n/new\nagain\n/quit\n"));
        Console.SetOut(new StringWriter());
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetIn(priorIn); Console.SetOut(priorOut); }

        var files = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl")
            .Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        var before = SessionStore.IdentityOf(files[0])!;
        var after = SessionStore.IdentityOf(files[1])!;
        Assert.NotEqual(before.Id, after.Id);
        Assert.Equal(SessionIdentity.Project(Path.GetFileName(files[0])), before.Id);
        Assert.Equal(SessionIdentity.Project(Path.GetFileName(files[1])), after.Id);
        Assert.Equal(after, sessions.Identity);
    }
}
