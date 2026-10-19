using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Xunit.Abstractions;

namespace Gatto.Tests.Render;

//alone, so the allocation and gen0 columns count this replay and not another test's work
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfCollection { public const string Name = "perf"; }

//a 500-chunk stream through the real client, loop and renderer, printed per delta and pinned nowhere
[Collection(PerfCollection.Name)]
[Trait("Category", "Perf")]
public sealed class RenderStreamBaselineTests(ITestOutputHelper output)
{
    private const int Chunks = 500;
    private static readonly int[] Widths = [80, 120, 200];
    private const int Runs = 3;
    private const int Height = 40;
    private static readonly Theme T = new(new TermCaps(true, true));

    //the closing words of the stream, so the test can tell a replay that reached the screen from one that did not
    private const string LastWords = "and that is the whole baseline stream";

    [Fact]
    public async Task THE_RENDER_AND_STREAM_BASELINE()
    {
        var pieces = Pieces();
        Assert.Equal(Chunks, pieces.Count);
        var sse = Sse(pieces);

        var table = new StringBuilder();
        table.AppendLine("render: width run | deltas | bytes/delta mean max | repaints/delta mean max | ms/repaint mean p95 | gen0 over replay");
        for (var run = 1; run <= Runs; run++)
            foreach (var width in Widths)
            {
                var r = await RenderReplay(sse, width);
                Assert.Equal(Chunks, r.Rows.Count);
                Assert.True(r.SawLastWords, $"the last words never reached the screen at width {width}");
                var painted = r.Rows.Where(x => x.Writes > 0).ToList();
                var msPerRepaint = painted.Select(x => x.Ms / x.Writes).OrderBy(x => x).ToList();
                table.AppendLine(
                    $"{width} {run} | {r.Rows.Count} | {r.Rows.Average(x => x.Bytes):F0} {r.Rows.Max(x => x.Bytes)} | "
                    + $"{r.Rows.Average(x => x.Writes):F2} {r.Rows.Max(x => x.Writes)} | "
                    + $"{(msPerRepaint.Count == 0 ? 0 : msPerRepaint.Average()):F3} {Percentile(msPerRepaint, 0.95):F3} | {r.Gen0}");
            }

        table.AppendLine("stream: run | chunks read | first chunk bytes | allocated bytes/chunk after the first, mean max | chunks off-thread | gen0 over replay");
        for (var run = 1; run <= Runs; run++)
        {
            var s = await ClientReplay(sse);
            Assert.Equal(Chunks, s.PerChunk.Count + s.OffThread);
            var rest = s.PerChunk.Skip(1).ToList();   //the first read also builds and sends the request
            table.AppendLine(
                $"{run} | {s.PerChunk.Count} | {s.PerChunk.FirstOrDefault()} | {(rest.Count == 0 ? 0 : rest.Average()):F0} {(rest.Count == 0 ? 0 : rest.Max())} | {s.OffThread} | {s.Gen0}");
        }
        output.WriteLine(table.ToString());
    }

    private sealed record RenderRun(IReadOnlyList<(long Bytes, int Writes, double Ms)> Rows, int Gen0, bool SawLastWords);

    //the session's own renderer over a counted screen, the ticker never started, so every write comes from a delta
    private static async Task<RenderRun> RenderReplay(string sse, int width)
    {
        var screen = new VtScreenSurface(width, Height);
        var surface = new CountingSurface(screen);
        var gate = new object();
        var model = new TranscriptModel("generalist");
        var status = new StatusInfo(@"C:\proj", "qwen", "generalist", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, gate, model, glyphs: GlyphSet.Unicode)
        {
            Frame = new InputFrame(surface, T, "generalist", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "generalist",
        };
        var ticker = new ChromeTicker(painter, gate);
        var convo = new Conversation("sys");
        var renderer = Gatto.Repl.Repl.BuildSessionRenderer(painter, surface, T, "generalist", ticker, gate,
            null, ReasoningMode.Collapsed, () => convo.Messages.Count > 0 ? convo.Messages[^1] : null, GlyphSet.Unicode);
        var meter = new Meter(renderer, surface);
        var loop = new AgentLoop(Client(sse), new ToolRegistry(), new HookBus(),
            new TestToolContext(Path.GetTempPath()), "m");

        renderer.BeginTurn();
        var gen0 = GC.CollectionCount(0);
        var result = await loop.RunTurnAsync(convo, "go", meter, default);
        gen0 = GC.CollectionCount(0) - gen0;
        renderer.EndTurn();

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var seen = string.Join(" ", string.Join(" ", screen.Viewport.Select(TermText.StripAnsiForWidth))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));   //rows rejoined, since the phrase may wrap
        return new RenderRun(meter.Rows, gen0, seen.Contains(LastWords, StringComparison.Ordinal));
    }

    private sealed record StreamRun(IReadOnlyList<long> PerChunk, int OffThread, int Gen0);

    //the real client over an in-memory response, so each read stays on this thread
    private static async Task<StreamRun> ClientReplay(string sse)
    {
        var client = Client(sse);
        var request = new ChatRequest("m", [new ChatMessage("user", "go")]);
        var perChunk = new List<long>();
        var offThread = 0;
        var gen0 = GC.CollectionCount(0);
        await using var e = client.StreamAsync(request).GetAsyncEnumerator();
        while (true)
        {
            var thread = Environment.CurrentManagedThreadId;
            var before = GC.GetAllocatedBytesForCurrentThread();
            if (!await e.MoveNextAsync()) break;
            var after = GC.GetAllocatedBytesForCurrentThread();
            if (e.Current is not (StreamEvent.TextDelta or StreamEvent.ReasoningDelta)) continue;
            if (Environment.CurrentManagedThreadId != thread) offThread++;
            else perChunk.Add(after - before);
        }
        return new StreamRun(perChunk, offThread, GC.CollectionCount(0) - gen0);
    }

    private static OpenAiCompatClient Client(string sse) =>
        new(new HttpClient(new Frames(sse)) { Timeout = Timeout.InfiniteTimeSpan }, "local", new EndpointConfig("http://127.0.0.1:1"));

    //answers every chat request with the whole stream at once, from memory
    private sealed class Frames(string sse) : HttpMessageHandler
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(sse);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(_bytes)) });
    }

    //counts what the painter writes, one write per repaint, since the compositor writes each frame whole
    private sealed class CountingSurface(VtScreenSurface inner) : ITermSurface
    {
        public long Bytes { get; private set; }
        public int Writes { get; private set; }
        public int Width => inner.Width;
        public int Height => inner.Height;

        public void Write(string s)
        {
            Bytes += Encoding.UTF8.GetByteCount(s);
            Writes++;
            inner.Write(s);
        }
    }

    //times each delta through the renderer and reads what the surface took during it
    private sealed class Meter(StreamRenderer inner, CountingSurface surface) : ITurnObserver
    {
        public List<(long Bytes, int Writes, double Ms)> Rows { get; } = [];

        private void Measure(Action a)
        {
            var bytes = surface.Bytes;
            var writes = surface.Writes;
            var start = Stopwatch.GetTimestamp();
            a();
            Rows.Add((surface.Bytes - bytes, surface.Writes - writes, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }

        public void OnTextDelta(string text) => Measure(() => inner.OnTextDelta(text));
        public void OnReasoningDelta(string text) => Measure(() => inner.OnReasoningDelta(text));
        public void OnToolCallDelta(string? name = null, string? partialArguments = null) => ((ITurnObserver)inner).OnToolCallDelta(name, partialArguments);
        public void OnPromptProgress(long total, long processed) => ((ITurnObserver)inner).OnPromptProgress(total, processed);
        public void OnToolCallStart(ToolCall call) => inner.OnToolCallStart(call);
        public void OnToolResult(ToolCall call, ToolResult result) => inner.OnToolResult(call, result);
        public void OnWarning(string message) => inner.OnWarning(message);
        public void OnUsage(Usage usage) => inner.OnUsage(usage);
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    //100 reasoning chunks, then prose, a code fence, a table and closing prose, cut where a model's tokens fall
    private static List<(bool Reasoning, string Text)> Pieces()
    {
        var list = new List<(bool, string)>();
        void Words(bool reasoning, string text, int count)
        {
            var words = text.Split(' ');
            for (var i = 0; i < count; i++) list.Add((reasoning, words[i % words.Length] + " "));
        }
        Words(true, "the user wants a short report on the stream so read the files first then write the table and keep the code small", 100);
        Words(false, "here is the report you asked for with the parts in order and a note on each", 120);
        list.Add((false, "\n\n```csharp\n"));
        var code = new[] { "var total = 0;", "foreach (var row in rows)", "{", "    total += row.Count;", "}", "return total;" };
        for (var i = 0; list.Count < 320; i++)
            foreach (var part in code[i % code.Length].Split(' '))
                if (list.Count < 320) list.Add((false, part + (part.EndsWith(';') || part is "{" or "}" || part.EndsWith(')') ? "\n" : " ")));
        list.Add((false, "```\n\n| file | lines | state |\n|---|---|---|\n"));
        for (var i = 0; list.Count < 440; i++)
        {
            list.Add((false, $"| part{i}.cs "));
            list.Add((false, $"| {i * 7 % 300} "));
            list.Add((false, "| read |\n"));
        }
        list.Add((false, "\n"));
        Words(false, "every row above was read in this session and nothing was left out", Chunks - list.Count - 7);
        foreach (var w in LastWords.Split(' ')) list.Add((false, w + " "));
        return list.Take(Chunks).ToList();
    }

    private static string Sse(List<(bool Reasoning, string Text)> pieces)
    {
        var sb = new StringBuilder();
        foreach (var (reasoning, text) in pieces)
            sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"")
              .Append(reasoning ? "reasoning_content" : "content").Append("\":").Append(JsonSerializer.Serialize(text))
              .Append("},\"finish_reason\":null}]}\n\n");
        sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":500}}\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }
}
