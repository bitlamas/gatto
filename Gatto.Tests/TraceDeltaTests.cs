using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;
using Trace = Gatto.Core.Trace;

namespace Gatto.Tests;

//a traced rich session wraps its surface in the counter and writes one delta span per delta, each under its turn
public class TraceDeltaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-trace-delta-").FullName;
    private string File_ => Path.Combine(_dir, "s.trace.jsonl");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task A_traced_rich_turn_writes_a_delta_per_delta_under_its_turn()
    {
        using var trace = new Trace(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => File_, () => "s", "b", 1);
        var h = new RichReplHarness(_dir, trace: trace);
        h.Client.EnqueueTurn(
            new StreamEvent.ReasoningDelta("think "),
            new StreamEvent.ReasoningDelta("more"),
            new StreamEvent.TextDelta("the answer"),
            new StreamEvent.ToolCallDelta("echo", "{\"m"),
            new StreamEvent.TextDelta(" is 42"),
            new StreamEvent.Finished("stop", null, null));
        h.Keys.Line("go").Line("/quit");

        await h.RunAsync();
        trace.Flush();

        var spans = File.ReadAllLines(File_).Skip(1).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        var turn = spans.Single(s => s.GetProperty("s").GetString() == "turn").GetProperty("id").GetInt64();
        var deltas = spans.Where(s => s.GetProperty("s").GetString() == "delta").ToList();
        Assert.Equal(new[] { "reasoning", "reasoning", "text", "tool_call", "text" }, deltas.Select(d => d.GetProperty("kind").GetString()));
        Assert.All(deltas, d => Assert.Equal(turn, d.GetProperty("p").GetInt64()));
        Assert.Equal(new[] { 6, 4, 10, 3, 6 }, deltas.Select(d => d.GetProperty("chars").GetInt32()));
        //the counter sits under the painter, so a delta that painted shows the bytes and the frames it wrote
        Assert.Contains(deltas, d => d.GetProperty("repaints").GetInt32() > 0 && d.GetProperty("bytes").GetInt64() > 0);
        Assert.All(deltas, d => Assert.True(d.GetProperty("write_us").GetInt64() >= 0));
    }

    //untraced, the session paints through the surface it was handed, with no counter between
    [Fact]
    public async Task An_untraced_rich_turn_writes_no_trace()
    {
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null, null));
        h.Keys.Line("go").Line("/quit");
        await h.RunAsync();
        Assert.True(h.Saw("ok"));
        Assert.Empty(Directory.GetFiles(_dir, "*.trace.jsonl"));
    }
}
