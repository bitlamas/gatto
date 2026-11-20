using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//what the session was shown, the set the reply check reads: links stored whole, filled from tool results, the user's records and the head of the prompt
public sealed class SeenLinksTests
{
    [Fact]
    public void A_LINK_IS_SEEN_WHOLE_AND_NEVER_BY_A_LONGER_ONE()
    {
        var set = new SeenLinks();
        set.Add("see https://a.example/xyz and https://b.example/doc.");

        Assert.True(set.Seen("https://a.example/xyz"));
        Assert.True(set.Seen("https://a.example/xyz/"));        //a trailing slash after it was shown without one
        Assert.True(set.Seen("https://b.example/doc"));         //stored without the full stop glued to it
        Assert.False(set.Seen("https://a.example/x"));          //a prefix of a shown link
        Assert.False(set.Seen("https://A.example/xyz"));        //a host in another case than it was shown
    }

    private static CitationGate Gate(bool armed = false) => new(new CitationLedger(() => null), _ => null) { Armed = armed };

    private static HookPayload Result(string tool, string args, string text, bool isError = false) =>
        new(Call: new ToolCall("c1", tool, args), Result: new ToolResult(text, IsError: isError));

    [Fact]
    public async Task A_RESULT_FILLS_THE_SET_IN_AN_UNARMED_SEAT_AND_ON_A_FAILED_CALL()
    {
        var gate = Gate();

        await gate.OnToolResultAsync(Result("grep", "{}", "match at https://a.example/one"));
        await gate.OnToolResultAsync(Result("web_fetch", "{\"url\":\"https://b.example/two\"}", "could not fetch https://b.example/two: 404", isError: true));

        Assert.True(gate.WasShown("https://a.example/one"));
        Assert.True(gate.WasShown("https://b.example/two"));
    }

    [Fact]
    public async Task A_CHILDS_REPLY_AND_A_WRITES_RESULT_SHOW_NOTHING()
    {
        var gate = Gate();

        await gate.OnToolResultAsync(Result("run_agent", "{}", "the child cites https://a.example/child"));
        await gate.OnToolResultAsync(Result("write_file", "{\"path\":\"out.md\"}", "wrote out.md\n\n[citation gate] not fetched and in no search result this session: https://b.example/note."));

        Assert.False(gate.WasShown("https://a.example/child"));
        Assert.False(gate.WasShown("https://b.example/note"));
    }

    [Fact]
    public async Task A_READ_OF_A_FILE_THE_SESSION_WROTE_SHOWS_NOTHING_AND_ANY_OTHER_READ_DOES()
    {
        var gate = Gate();

        await gate.OnToolResultAsync(Result("write_file", "{\"path\":\"Out.md\"}", "wrote Out.md"));
        await gate.OnToolResultAsync(Result("read_file", "{\"path\":\"out.md\"}", "my own https://a.example/mine"));
        await gate.OnToolResultAsync(Result("read_file", "{\"path\":\"notes.md\"}", "the user's https://a.example/theirs"));

        Assert.False(gate.WasShown("https://a.example/mine"));
        Assert.True(gate.WasShown("https://a.example/theirs"));
    }

    [Fact]
    public async Task A_FAILED_WRITE_LEAVES_ITS_FILE_READABLE()
    {
        var gate = Gate();

        await gate.OnToolResultAsync(Result("write_file", "{\"path\":\"out.md\"}", "permission denied", isError: true));
        await gate.OnToolResultAsync(Result("read_file", "{\"path\":\"out.md\"}", "the user's https://a.example/theirs"));

        Assert.True(gate.WasShown("https://a.example/theirs"));
    }

    //a fetched page's text rarely names its own address, so a successful fetch shows the address it asked for
    [Fact]
    public async Task A_FETCH_SHOWS_ITS_ADDRESS_ONLY_WHEN_IT_SUCCEEDED()
    {
        var gate = Gate();

        await gate.OnToolResultAsync(Result("web_fetch", "{\"url\":\"https://a.example/page\"}", "the page's text"));
        await gate.OnToolResultAsync(Result("web_fetch", "{\"url\":\"https://b.example/gone\"}", "fetch failed", isError: true));

        Assert.True(gate.WasShown("https://a.example/page"));
        Assert.False(gate.WasShown("https://b.example/gone"));
    }

    [Fact]
    public void THE_USERS_RECORDS_SHOW_THEIR_LINKS_AND_A_TOOLS_PICTURES_SHOW_NONE()
    {
        var gate = Gate();
        var convo = new Conversation("sys");

        gate.OnShown(convo.UserRecord("typed https://a.example/typed"));
        gate.OnShown(convo.UserRecord("a letter https://a.example/letter", watch: "letter.md"));
        gate.OnShown(convo.BackgroundRecord(1, "ended https://a.example/end", "exit 0", false));
        gate.OnShown(convo.ImageRecord("images read this round: https://a.example/pic", null, "read_file"));

        Assert.True(gate.WasShown("https://a.example/typed"));
        Assert.True(gate.WasShown("https://a.example/letter"));
        Assert.True(gate.WasShown("https://a.example/end"));
        Assert.False(gate.WasShown("https://a.example/pic"));
    }

    //the head counts up to a compaction's marker, and the summary and the last exchange after it do not
    [Fact]
    public void THE_HEAD_SHOWS_ITS_LINKS_AND_NOT_THE_SUMMARY_AFTER_ITS_MARKER()
    {
        var gate = Gate();

        gate.OnShown(new ChatMessage("system", "GATTO.md says https://a.example/head\n\n" + Compactor.ContextMarker + "old.jsonl)\n\nthe summary cites https://a.example/summary"));
        gate.OnShown(new ChatMessage("assistant", "hello, start at https://a.example/opening"));

        Assert.True(gate.WasShown("https://a.example/head"));
        Assert.False(gate.WasShown("https://a.example/summary"));
        Assert.True(gate.WasShown("https://a.example/opening"));
    }

    [Fact]
    public async Task A_RESET_FORGETS_WHAT_WAS_SHOWN_AND_WRITTEN()
    {
        var gate = Gate();
        await gate.OnToolResultAsync(Result("grep", "{}", "https://a.example/one"));
        await gate.OnToolResultAsync(Result("write_file", "{\"path\":\"out.md\"}", "wrote out.md"));

        gate.Reset();
        await gate.OnToolResultAsync(Result("read_file", "{\"path\":\"out.md\"}", "now the user's https://a.example/two"));

        Assert.False(gate.WasShown("https://a.example/one"));
        Assert.True(gate.WasShown("https://a.example/two"));
    }

    private static (CitationGate Gate, List<IReadOnlyList<string>> Named) ReadingGate(bool armed = true)
    {
        var named = new List<IReadOnlyList<string>>();
        var gate = Gate(armed);
        gate.Cited = named.Add;
        return (gate, named);
    }

    //the reply's links the session was never shown, once each and first seen first, and nothing at all when every link was shown
    [Fact]
    public async Task THE_REPLY_NAMES_EACH_UNSEEN_LINK_ONCE_IN_ORDER()
    {
        var (gate, named) = ReadingGate();
        gate.OnShown(new Conversation("sys").UserRecord("read https://a.example/shown"));

        await gate.OnMessageEndAsync(new HookPayload(AssistantText: "https://b.example/x, https://a.example/shown, https://c.example/y and https://b.example/x/ again"));
        await gate.OnMessageEndAsync(new HookPayload(AssistantText: "only https://a.example/shown"));

        Assert.Equal([["https://b.example/x", "https://c.example/y"]], named);
    }

    //the reply's own links are not shown to the model by being written, so a link invented twice is named twice
    [Fact]
    public async Task A_LINK_REPEATED_IN_THE_NEXT_REPLY_IS_NAMED_AGAIN()
    {
        var (gate, named) = ReadingGate();

        await gate.OnMessageEndAsync(new HookPayload(AssistantText: "see https://b.example/x"));
        await gate.OnMessageEndAsync(new HookPayload(AssistantText: "as I said, https://b.example/x"));

        Assert.Equal(2, named.Count);
    }

    [Fact]
    public async Task AN_UNARMED_SEAT_OR_AN_EMPTY_REPLY_NAMES_NOTHING()
    {
        var (unarmed, none) = ReadingGate(armed: false);
        await unarmed.OnMessageEndAsync(new HookPayload(AssistantText: "see https://b.example/x"));
        var (armed, empty) = ReadingGate();
        await armed.OnMessageEndAsync(new HookPayload(AssistantText: ""));

        Assert.Empty(none);
        Assert.Empty(empty);
    }

    private static (AgentLoop Loop, FakeChatClient Client, List<ChatMessage> Shown) ShowingLoop()
    {
        var client = new FakeChatClient();
        var shown = new List<ChatMessage>();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m") { OnShown = shown.Add };
        return (loop, client, shown);
    }

    private static StreamEvent[] Reply(string text) => [new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null)];

    //the head and its seeded opening are passed once however many requests carry them, and again when a new head replaces them
    [Fact]
    public async Task THE_LOOP_PASSES_THE_HEAD_ONCE_AND_AGAIN_WHEN_IT_IS_REPLACED()
    {
        var (loop, client, shown) = ShowingLoop();
        var convo = new Conversation("first head");
        convo.AddAssistant("the opening");
        client.EnqueueTurn(Reply("one"));
        client.EnqueueTurn(Reply("two"));
        client.EnqueueTurn(Reply("three"));

        await loop.RunTurnAsync(convo, "a", new RecordingObserver(), CancellationToken.None);
        await loop.RunTurnAsync(convo, "b", new RecordingObserver(), CancellationToken.None);
        convo.ReplaceSystem("second head", null);
        await loop.RunTurnAsync(convo, "c", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(["user:a", "system:first head", "assistant:the opening", "user:b", "user:c", "system:second head", "assistant:the opening"],
            shown.Select(m => m.Role + ":" + m.Content));
    }

    [Fact]
    public async Task A_RECORD_DELIVERED_INSIDE_A_TURN_IS_PASSED()
    {
        var (loop, client, shown) = ShowingLoop();
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "nosuch", "{}")), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(Reply("done"));
        var queued = new List<QueuedInput> { new("sent while it ran https://a.example/queued") };

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), CancellationToken.None, takeQueued: () =>
        {
            var take = queued.ToList();
            queued.Clear();
            return take;
        });

        Assert.Contains(shown, m => m.Content == "sent while it ran https://a.example/queued");
    }
}
