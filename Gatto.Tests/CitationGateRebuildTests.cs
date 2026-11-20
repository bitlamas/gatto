using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Gatto.Roles;

namespace Gatto.Tests;

//a resume rebuilds what the session was shown from its files, and a link reads the same before and after
public sealed class CitationGateRebuildTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gate-rebuild-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static CitationGate Gate() => new(new CitationLedger(() => null), _ => null);

    private static ToolCall Call(string id, string tool, string args) => new(id, tool, args);

    //one session as its file holds it: the head, a seeded opening, typed words, a write and its note, reads and a fetch
    private static List<ChatMessage> Session()
    {
        var write = Call("w", "write_file", "{\"path\":\"out.md\"}");
        var readOwn = Call("r1", "read_file", "{\"path\":\"out.md\"}");
        var readOther = Call("r2", "read_file", "{\"path\":\"notes.md\"}");
        var fetch = Call("f", "web_fetch", "{\"url\":\"https://a.example/page\"}");
        var child = Call("c", "run_agent", "{}");
        return
        [
            new("system", "GATTO.md names https://a.example/head\n\n" + Compactor.ContextMarker + "old.jsonl)\n\nsummary https://a.example/summary"),
            new("assistant", "start at https://a.example/opening"),
            new("user", "look at https://a.example/typed"),
            new("assistant", null, [write]),
            new("tool", "wrote out.md\n\n[citation gate] not fetched and in no search result this session: https://a.example/flagged.", ToolCallId: "w"),
            new("assistant", null, [readOwn, readOther, fetch, child]),
            new("tool", "my own https://a.example/mine", ToolCallId: "r1"),
            new("tool", "the user's https://a.example/theirs", ToolCallId: "r2"),
            new("tool", "the page's text", ToolCallId: "f"),
            new("tool", "the child says https://a.example/child", ToolCallId: "c"),
            new("user", "images read this round: https://a.example/pic", By: "read_file"),
            new("assistant", "my reply cites https://a.example/replied"),
        ];
    }

    private static readonly string[] Links =
    [
        "https://a.example/head", "https://a.example/summary", "https://a.example/opening", "https://a.example/typed",
        "https://a.example/flagged", "https://a.example/mine", "https://a.example/theirs", "https://a.example/page",
        "https://a.example/child", "https://a.example/pic", "https://a.example/replied",
    ];

    //the live gate sees the records as the loop passes them, the head and its opening, the user's records and each tool result
    private static async Task<CitationGate> Live(List<ChatMessage> records)
    {
        var gate = Gate();
        var calls = records.Where(r => r.ToolCalls is not null).SelectMany(r => r.ToolCalls!).ToDictionary(c => c.Id);
        gate.OnShown(records[0]);
        gate.OnShown(records[1]);
        foreach (var r in records.Skip(2))
        {
            if (r.Role == "user") gate.OnShown(r);
            if (r.Role == "tool") await gate.OnToolResultAsync(new HookPayload(Call: calls[r.ToolCallId!], Result: new ToolResult(r.Content!, IsError: r.IsError)));
        }
        return gate;
    }

    [Fact]
    public async Task A_LINK_READS_THE_SAME_BEFORE_AND_AFTER_A_RESUME()
    {
        var records = Session();
        var live = await Live(records);
        var rebuilt = Gate();

        rebuilt.Rebuild([(records, p => p)]);

        Assert.Equal(Links.Select(live.WasShown), Links.Select(rebuilt.WasShown));
        Assert.Equal(
            ["https://a.example/head", "https://a.example/opening", "https://a.example/typed", "https://a.example/theirs", "https://a.example/page"],
            Links.Where(rebuilt.WasShown));
    }

    //the opening is the reply before every user record of its file, and a later reply is the model's own
    [Fact]
    public void ONLY_THE_REPLY_BEFORE_EVERY_USER_RECORD_IS_THE_OPENING()
    {
        var gate = Gate();

        gate.Rebuild([([new("system", "s"), new("user", "go"), new("assistant", "late https://a.example/late")], p => p)]);

        Assert.False(gate.WasShown("https://a.example/late"));
    }

    //a file's own folder resolves its paths, so a write made in one folder and read back by its full path is still the model's own
    [Fact]
    public void EACH_FILE_RESOLVES_ITS_PATHS_AGAINST_ITS_OWN_FOLDER()
    {
        var first = Path.Combine(Path.GetTempPath(), "gate-first");
        var gate = Gate();
        List<ChatMessage> wrote = [new("assistant", null, [Call("w", "write_file", "{\"path\":\"out.md\"}")]), new("tool", "wrote out.md", ToolCallId: "w")];
        var full = System.Text.Json.JsonSerializer.Serialize(Path.Combine(first, "out.md"));
        List<ChatMessage> read = [new("assistant", null, [Call("r", "read_file", "{\"path\":" + full + "}")]), new("tool", "mine https://a.example/mine", ToolCallId: "r")];

        gate.Rebuild([(wrote, p => ToolArgs.Resolve(first, p)), (read, p => ToolArgs.Resolve(Path.Combine(Path.GetTempPath(), "gate-second"), p))]);

        Assert.False(gate.WasShown("https://a.example/mine"));
    }

    [Fact]
    public void A_FAILED_WRITE_IN_A_FILE_LEAVES_ITS_PATH_READABLE()
    {
        var gate = Gate();

        gate.Rebuild([([
            new("assistant", null, [Call("w", "write_file", "{\"path\":\"out.md\"}"), Call("r", "read_file", "{\"path\":\"out.md\"}")]),
            new("tool", "permission denied", ToolCallId: "w", IsError: true),
            new("tool", "the user's https://a.example/theirs", ToolCallId: "r"),
        ], p => p)]);

        Assert.True(gate.WasShown("https://a.example/theirs"));
    }

    //the chain is the session's files oldest first, and another session's file is not in it
    [Fact]
    public void THE_CHAIN_IS_THE_SESSIONS_FILES_OLDEST_FIRST()
    {
        var store = new SessionStore(_home, _home);
        var convo = new Conversation("sys");
        convo.AddUser("one");
        store.Save(convo);
        var first = store.CurrentPath!;
        store.StartSuccessor();
        store.Save(convo);
        var second = store.CurrentPath!;
        var other = new SessionStore(_home, _home);
        other.Save(convo);
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal([first, second], store.ChainOf(store.Identity.Id));
    }
}
