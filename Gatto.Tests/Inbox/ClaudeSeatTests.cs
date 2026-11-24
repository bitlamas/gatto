using System.Text;
using System.Text.Json;
using Gatto.Cli.Inbox;

namespace Gatto.Tests.Inbox;

//the seat name from the model id, the newest named model of a transcript, the proof a seat received a letter, all read off fake transcripts
public sealed class ClaudeSeatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-seat-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Transcript(params string[] lines)
    {
        var p = Path.Combine(_root, "transcript-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
        File.WriteAllLines(p, lines, new UTF8Encoding(false));
        return p;
    }

    [Theory]
    [InlineData("claude-tail-5-5", "tail-5-5")]
    [InlineData("claude-test-5-5[1m]", "test-5-5")]
    [InlineData("claude-deep/knot:x", "deep-knot-x")]
    [InlineData("qwen3.8-flash-next", null)]
    [InlineData("", null)]
    [InlineData("claude-", null)]
    [InlineData(null, null)]
    public void THE_SEAT_COMES_FROM_THE_MODEL_ID(string? id, string? want) =>
        Assert.Equal(want, ClaudeSeat.FromModelId(id));

    [Fact]
    public void THE_NEWEST_NAMED_MODEL_OF_THE_ASSISTANT_LINES_WINS_SYNTHETICS_SKIPPED()
    {
        var p = Transcript(
            """{"type":"user","message":{"role":"user"}}""",
            """{"type":"assistant","message":{"model":"claude-first-5-5"}}""",
            """{"type":"assistant","message":{"model":"claude-mid-5-5"}}""",
            """{"type":"assistant","message":{"model":"<synthetic>"}}""");
        Assert.Equal("claude-mid-5-5", ClaudeSeat.NewestModel(p));
    }

    [Fact]
    public void NO_ASSISTANT_LINE_NO_GARBAGE_LINE_AND_NO_FILE_ALL_READ_AS_NO_MODEL()
    {
        Assert.Null(ClaudeSeat.NewestModel(Transcript("""{"type":"user","message":{"role":"user"}}""")));
        Assert.Null(ClaudeSeat.NewestModel(Transcript("not json at all", "{broken")));
        Assert.Null(ClaudeSeat.NewestModel(Path.Combine(_root, "absent.jsonl")));
    }

    [Fact]
    public void THE_NOTICE_LINE_OF_A_USER_RECORD_PROVES_RECEIPT_A_QUEUE_OR_ASSISTANT_LINE_DOES_NOT()
    {
        var path = Path.Combine(_root, "a.md");
        var queue = Queue("inbox: " + path);
        var assistant = Line("assistant", "I saw inbox: " + path);
        var user = Line("user", "inbox: " + path);

        Assert.False(ClaudeSeat.Received(Transcript(queue), path, 0));
        Assert.False(ClaudeSeat.Received(Transcript(queue, assistant), path, 0));
        Assert.True(ClaudeSeat.Received(Transcript(queue, assistant, user), path, 0));
    }

    //a reused name: the notice of an earlier letter under the same path, written before the handover, proves nothing
    [Fact]
    public void A_NOTICE_WRITTEN_BEFORE_THE_HANDOVER_PROVES_NOTHING()
    {
        var path = Path.Combine(_root, "a.md");
        var t = Transcript(Line("user", "inbox: " + path));
        var from = ClaudeSeat.Length(t);
        Assert.False(ClaudeSeat.Received(t, path, from));
        File.AppendAllText(t, Line("user", "inbox: " + path) + "\n", new UTF8Encoding(false));
        Assert.True(ClaudeSeat.Received(t, path, from));
        Assert.True(ClaudeSeat.Received(t, path, from + 1_000_000));   //a file shorter than the offset was replaced, so it reads whole
        Assert.Equal(0, ClaudeSeat.Length(Path.Combine(_root, "absent.jsonl")));
    }

    [Fact]
    public void A_PATH_WITH_ACCENTS_AND_AN_APOSTROPHE_PROVES_RECEIPT_WRITTEN_RAW()
    {
        var path = Path.Combine(_root, "it's-é.md");   //the transcript holds these raw, the default encoder would escape both
        Assert.True(ClaudeSeat.Received(Transcript(Line("user", "inbox: " + path)), path, 0));
    }

    //one transcript line for a top-level type, its message content a json string value as the transcript writes it
    private static string Line(string type, string content) =>
        "{\"type\":\"" + type + "\",\"message\":{\"content\":" + Raw(content) + "}}";

    private static string Queue(string content) =>
        "{\"type\":\"queue-operation\",\"content\":" + Raw(content) + "}";

    //a json string value as the transcript writes it, backslashes escaped but accents and apostrophes raw
    private static string Raw(string s) =>
        JsonSerializer.Serialize(s, new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public void A_NAME_THE_WATCH_REFUSES_IS_NOT_A_LETTER()
    {
        var inbox = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(inbox);
        File.WriteAllText(Path.Combine(inbox, "a.md"), "a");
        File.WriteAllText(Path.Combine(inbox, "a.md.tmp"), "half written");
        File.WriteAllText(Path.Combine(inbox, "b.MD"), "b");
        Assert.Equal(["a.md", "b.MD"], ClaudeSeat.Letters(inbox).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void THE_INBOX_SITS_UNDER_THE_PROJECT_MAILBOX_AND_SHOWS_FROM_THE_PROJECT()
    {
        var inbox = ClaudeSeat.Inbox(@"C:\proj", "tail-5-5");
        Assert.Equal(Path.Combine(@"C:\proj", ".gatto", "mailbox", "claude", "tail-5-5"), inbox);
        Assert.Equal(Path.Combine(".gatto", "mailbox", "claude", "tail-5-5"), ClaudeSeat.Shown(inbox, @"C:\proj"));
    }
}
