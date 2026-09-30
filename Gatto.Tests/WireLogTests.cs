using System.Text;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the wire log records the bytes gatto sends, and they must be byte-faithful so two dumps can be diffed
public class WireLogTests
{
    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static FakeResponse OneTurn() => new(Frames: new[]
    {
        Chunk("{\"content\":\"ok\"}", finish: "stop"),
        "data: [DONE]\n\n",
    });

    private static async Task Drain(IChatClient c, ChatRequest req)
    {
        await foreach (var _ in c.StreamAsync(req)) { }
    }

    //records every body it is given, in order, since the point here is the client's duty to pass it on
    private sealed class CapturingWireLog : IWireLog
    {
        public List<string> Bodies { get; } = new();
        public void Record(string body) => Bodies.Add(body);
    }

    //the oracle is what the server received, byte for byte, since comparing against BuildBody's own return would be tautological
    [Fact]
    public async Task The_logged_body_is_byte_identical_to_what_the_server_received()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(OneTurn());
        var log = new CapturingWireLog();
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl), log);

        //a logger that re-serializes would lose these bytes, so the content holds a non-ASCII path, a quote, a newline and a tab
        await Drain(client, new ChatRequest("m", new[]
        {
            new ChatMessage("user", "écrire \"un\" test\n\tavec des onglets"),
        }));

        var recorded = Assert.Single(log.Bodies);
        Assert.Equal(s.LastRequestRaw, recorded);
    }

    //a client built without the log parameter must keep working, which is the shape every call site uses
    [Fact]
    public async Task No_log_means_the_client_is_unchanged()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(OneTurn());
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl));

        await Drain(client, new ChatRequest("m", new[] { new ChatMessage("user", "hi") }));

        Assert.Equal(1, s.RequestCount);
    }

    //a failing log must never end the turn, since losing the turn to it is worse than losing the evidence
    [Fact]
    public async Task A_throwing_log_never_breaks_the_turn()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(OneTurn());
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl),
            new ThrowingWireLog());

        await Drain(client, new ChatRequest("m", new[] { new ChatMessage("user", "hi") }));

        Assert.Equal(1, s.RequestCount);
    }

    private sealed class ThrowingWireLog : IWireLog
    {
        public void Record(string body) => throw new IOException("disk full");
    }

    //the file writer

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gatto-wirelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    //the file holds the request body alone, and the assertion reads bytes since ReadAllText hides a BOM
    [Fact]
    public void The_file_holds_the_body_verbatim()
    {
        var dir = TempDir();
        try
        {
            var log = new WireLog(dir);
            const string body = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"é\\t\\\"q\\\"\"}]}";

            log.Record(body);

            var file = Assert.Single(Directory.GetFiles(dir));
            Assert.Equal(new UTF8Encoding(false).GetBytes(body), File.ReadAllBytes(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    //the names must sort in send order, zero-padded, since a naive name puts 10 before 2
    [Fact]
    public void Files_sort_in_send_order()
    {
        var dir = TempDir();
        try
        {
            var log = new WireLog(dir);
            for (var i = 1; i <= 11; i++) log.Record($"{{\"n\":{i}}}");

            var names = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var contents = names.Select(n => File.ReadAllText(Path.Combine(dir, n!))).ToList();

            Assert.Equal(Enumerable.Range(1, 11).Select(i => $"{{\"n\":{i}}}"), contents);
        }
        finally { Directory.Delete(dir, true); }
    }

    //each run writes into its own directory, with the timestamp separating runs and the pid separating two started in the same second
    [Fact]
    public void Each_run_gets_its_own_directory()
    {
        var t = new DateTimeOffset(2026, 8, 3, 17, 45, 12, TimeSpan.Zero);

        var a = WireLog.RunDirectory("H", t, 100);
        var sameSecondOtherProcess = WireLog.RunDirectory("H", t, 101);
        var laterSameProcess = WireLog.RunDirectory("H", t.AddSeconds(1), 100);

        Assert.NotEqual(a, sameSecondOtherProcess);
        Assert.NotEqual(a, laterSameProcess);
        Assert.StartsWith(Path.Combine("H", "wire"), a, StringComparison.Ordinal);
    }

    //only the literal 1 arms it, the flag comes in as an argument so no test has to set the process-global environment
    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    public void Armed_only_by_the_documented_flag_value(string? flag, bool expected)
    {
        var dir = TempDir();
        try
        {
            Assert.Equal(expected, WireLog.FromFlag(flag, dir) is not null);
        }
        finally { Directory.Delete(dir, true); }
    }
}
