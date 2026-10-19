using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Session;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//a ledger and a trace beside a session, each shaped like a session, and every glob still sees one session
public class SidecarGlobTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("gatto-sidecar-").FullName;
    private string Home => Path.Combine(_base, "home");
    private string Cwd => Path.Combine(_base, "proj");

    public SidecarGlobTests() => Directory.CreateDirectory(Cwd);

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string SessionWithBothSidecars()
    {
        var store = new SessionStore(Home, Cwd);
        var convo = new Conversation("sys");
        convo.AddUser("the nginx port is 8080");
        store.Save(convo);
        var session = store.CurrentPath!;
        //each sidecar holds a system record with this cwd and a user record with the query word, so a glob that reads one would find it
        var asSession = string.Join("\n",
            JsonSerializer.Serialize(new { role = "system", content = "sys", schema_version = 1, cwd = Path.GetFullPath(Cwd) }),
            JsonSerializer.Serialize(new { role = "user", content = "nginx in a sidecar" })) + "\n";
        File.WriteAllText(SessionFiles.Sidecar(session, SessionFiles.Ledger), asSession);
        File.WriteAllText(SessionFiles.Sidecar(session, SessionFiles.Trace), asSession);
        return session;
    }

    [Fact]
    public void The_sidecar_paths_share_the_session_stem()
    {
        var session = Path.Combine("C:\\h", "sessions", "k-0001.jsonl");
        Assert.Equal(Path.Combine("C:\\h", "sessions", "k-0001.ledger.jsonl"), SessionFiles.Sidecar(session, SessionFiles.Ledger));
        Assert.Equal(Path.Combine("C:\\h", "sessions", "k-0001.trace.jsonl"), SessionFiles.Sidecar(session, SessionFiles.Trace));
        Assert.True(SessionFiles.IsSidecar(SessionFiles.Sidecar(session, SessionFiles.Trace)));
        Assert.True(SessionFiles.IsSidecar("X.LEDGER.JSONL"));
        Assert.False(SessionFiles.IsSidecar(session));
    }

    [Fact]
    public void Continue_and_the_session_count_see_one_session()
    {
        var session = SessionWithBothSidecars();
        var store = new SessionStore(Home, Cwd);
        Assert.Equal(session, store.LatestPathForCwd());
        Assert.Equal(1, store.CountForCwd());
    }

    [Fact]
    public void An_id_resolves_to_the_session_alone()
    {
        var session = SessionWithBothSidecars();
        var stem = Path.GetFileNameWithoutExtension(session);
        Assert.Equal(session, SessionStore.ResolvePathById(Home, stem));
        Assert.Equal(session, SessionStore.ResolvePathById(Home, stem[..^3]));   //a prefix the sidecars share stays unambiguous
    }

    [Fact]
    public async Task Recall_memory_reads_the_session_alone()
    {
        var session = SessionWithBothSidecars();
        var result = await new RecallMemoryTool().ExecuteAsync(
            JsonDocument.Parse("""{"query":"nginx"}""").RootElement, new RecallTestContext(Cwd, Home), default);
        using var doc = JsonDocument.Parse(result.Text);
        var sessions = doc.RootElement.EnumerateArray().Select(h => h.GetProperty("session").GetString()).ToList();
        Assert.Equal(new[] { session }, sessions);
    }
}
