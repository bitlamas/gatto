using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//a session's id is its origin transcript's name projected to a GUID, carried across compaction and resume, and handed to every shell child
public class SessionIdentityTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-session-id-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-session-cwd-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _home, _cwd })
            try { Directory.Delete(d, recursive: true); } catch (Exception) { }
    }

    //the system record found by role, never by line
    private static JsonElement SystemRecord(string path) =>
        File.ReadLines(path).Select(l => JsonDocument.Parse(l).RootElement)
            .First(e => e.TryGetProperty("role", out var r) && r.GetString() == "system");

    private static Conversation Convo() => new("you are a test");

    //the value the JOURNAL header's own PowerShell line prints for this name, so the C# cannot drift from every entry already written
    [Fact]
    public void THE_PROJECTION_IS_THE_ONE_THE_JOURNAL_USES()
    {
        Assert.Equal("41b105c1-aa89-bfb6-2bec-9ed7a5da1409",
            SessionIdentity.Project("1538ad9681b8-0639268568497889356.jsonl").ToString("D"));
    }

    [Fact]
    public void THE_START_IS_LOCAL_TIME_WITH_ITS_OFFSET_AND_READS_BACK()
    {
        var identity = new SessionIdentity(Guid.NewGuid(), new DateTimeOffset(2026, 10, 6, 16, 59, 33, TimeSpan.Zero));

        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", identity.StartText);
        var back = SessionIdentity.Parse(identity.IdText, identity.StartText)!;
        Assert.Equal(identity.Id, back.Id);
        Assert.Equal(identity.Start, back.Start);
    }

    //the id exists before the first save, since the first shell call runs before it, and is the projection of the file that save then writes
    [Fact]
    public void A_FRESH_SESSION_IS_NAMED_AFTER_THE_FILE_IT_WRITES()
    {
        var store = new SessionStore(_home, _cwd);
        var before = store.Identity;
        Assert.Null(store.CurrentPath);

        store.Save(Convo());

        Assert.Equal(SessionIdentity.Project(Path.GetFileName(store.CurrentPath!)), before.Id);
        var system = SystemRecord(store.CurrentPath!);
        Assert.Equal(before.IdText, system.GetProperty("session_id").GetString());
        Assert.Equal(before.StartText, system.GetProperty("session_start").GetString());
    }

    [Fact]
    public void A_COMPACTIONS_SUCCESSOR_KEEPS_THE_ORIGINS_ID_AND_NAMES_IT()
    {
        var store = new SessionStore(_home, _cwd);
        store.Save(Convo());
        var origin = store.CurrentPath!;
        var identity = store.Identity;

        store.StartSuccessor();
        store.Save(Convo());

        Assert.NotEqual(origin, store.CurrentPath);
        Assert.Equal(identity, store.Identity);
        Assert.Equal(identity, SessionStore.IdentityOf(store.CurrentPath!));
        Assert.NotEqual(SessionIdentity.Project(Path.GetFileName(store.CurrentPath!)), identity.Id);
    }

    [Fact]
    public void NEW_STARTS_A_NEW_SESSION_NAMED_AFTER_ITS_OWN_FILE()
    {
        var store = new SessionStore(_home, _cwd);
        store.Save(Convo());
        var first = store.Identity;

        store.StartNew();
        store.Save(Convo());

        Assert.NotEqual(first.Id, store.Identity.Id);
        Assert.Equal(SessionIdentity.Project(Path.GetFileName(store.CurrentPath!)), store.Identity.Id);
    }

    [Fact]
    public void A_RESUME_KEEPS_THE_RESUMED_SESSIONS_ID_IN_ITS_NEW_FILE()
    {
        var first = new SessionStore(_home, _cwd);
        first.Save(Convo());

        var resumed = new SessionStore(_home, _cwd);
        resumed.Adopt(SessionStore.IdentityOf(first.CurrentPath!)!);
        resumed.Save(Convo());

        Assert.NotEqual(first.CurrentPath, resumed.CurrentPath);
        Assert.Equal(first.Identity, resumed.Identity);
        Assert.Equal(first.Identity, SessionStore.IdentityOf(resumed.CurrentPath!));
    }

    //a file whose system record names no session does not apply the fields, so it is read as its own origin: its name and its first time
    [Fact]
    public void A_FILE_WHOSE_SYSTEM_RECORD_NAMES_NO_SESSION_IS_ITS_OWN_ORIGIN()
    {
        var path = Path.Combine(_home, "abc-0639268568497889356.jsonl");
        File.WriteAllLines(path,
        [
            """{"event":"system","text":"banner"}""",
            """{"role":"system","content":"x","schema_version":1,"ts":"2026-10-06T08:13:29.0000000Z"}""",
        ]);

        var identity = SessionStore.IdentityOf(path)!;

        Assert.Equal(SessionIdentity.Project("abc-0639268568497889356.jsonl"), identity.Id);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 8, 13, 29, TimeSpan.Zero), identity.Start);
    }

    private sealed class SessionContext(string cwd, SessionIdentity? session) : IToolContext
    {
        public string Cwd => cwd;
        public string HomePath => cwd;
        public IUserPrompter? Prompter => null;
        public SessionIdentity? Session => session;
    }

    //read through a real PowerShell child, since the subject is what a child process sees
    [Fact]
    public async Task A_SHELL_CHILD_READS_BOTH_VALUES_OF_THE_LIVE_SESSION()
    {
        var identity = new SessionStore(_home, _cwd).Identity;
        var args = JsonSerializer.SerializeToElement(new
        {
            command = "Write-Output \"$env:GATTO_SESSION_ID|$env:GATTO_SESSION_START\"",
        });

        var r = await new ShellTool().ExecuteAsync(args, new SessionContext(_cwd, identity), default);

        Assert.False(r.IsError, r.Text);
        Assert.Equal($"{identity.IdText}|{identity.StartText}", r.Text.Trim());
    }
}
