using System.Diagnostics;
using System.Text.Json;
using Gatto.Core;
using Trace = Gatto.Core.Trace;

namespace Gatto.Tests;

//the trace writes one header and one line per closed span beside the session, and off it costs nothing
public class TraceTests
{
    private static Trace On(string dir, Func<string?> path, string session = "s1") =>
        new(Stopwatch.GetTimestamp(), DateTimeOffset.Now, path, () => session, "0.0.0-test", 4242);

    private static List<JsonElement> Lines(string file) =>
        File.ReadAllLines(file).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    //10,000 calls of every span API on the off path allocate nothing, since the off trace sits in every turn of every session
    [Fact]
    public void The_off_path_allocates_nothing()
    {
        var off = Trace.Off;
        void Round()
        {
            using var s = off.Open(Trace.Turn, 7);
            s.Set("rounds", 3);
            s.Set("result", "Completed");
            off.MarkResume();
            _ = s.Id;
        }
        Round();   //warm the jit, which allocates on first call
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) Round();
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(off.Enabled);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void A_flush_writes_the_header_then_each_span_with_its_parent()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-trace-").FullName;
        try
        {
            var file = Path.Combine(dir, "a.trace.jsonl");
            using var trace = On(dir, () => file);
            long turnId;
            using (var turn = trace.Open(Trace.Turn))
            {
                turnId = turn.Id;
                using (var tool = trace.Open(Trace.Tool, turn.Id))
                {
                    tool.Set("name", "shell");
                    tool.Set("error", 0);
                }
                turn.Set("result", "Completed");
            }
            trace.Flush();

            var lines = Lines(file);
            Assert.Equal(3, lines.Count);
            Assert.Equal(1, lines[0].GetProperty("trace").GetInt32());
            Assert.Equal("s1", lines[0].GetProperty("session").GetString());
            Assert.Equal("0.0.0-test", lines[0].GetProperty("build").GetString());
            Assert.Equal(4242, lines[0].GetProperty("pid").GetInt32());
            Assert.True(DateTimeOffset.TryParse(lines[0].GetProperty("t0").GetString(), out _));
            //a span is written when it closes, so the inner tool comes before the turn that holds it
            var toolLine = lines[1];
            var turnLine = lines[2];
            Assert.Equal("tool", toolLine.GetProperty("s").GetString());
            Assert.Equal(turnId, toolLine.GetProperty("p").GetInt64());
            Assert.Equal("shell", toolLine.GetProperty("name").GetString());
            Assert.Equal(0, toolLine.GetProperty("error").GetInt32());
            Assert.Equal("turn", turnLine.GetProperty("s").GetString());
            Assert.Equal(0, turnLine.GetProperty("p").GetInt64());
            Assert.Equal("Completed", turnLine.GetProperty("result").GetString());
            Assert.True(turnLine.GetProperty("t").GetInt64() <= toolLine.GetProperty("t").GetInt64());
            Assert.True(turnLine.GetProperty("d").GetInt64() >= toolLine.GetProperty("d").GetInt64());
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    //spans wait in memory until a session file exists, so the launch span reaches the sidecar with the first turn
    [Fact]
    public void Spans_before_the_session_exists_wait_for_its_first_flush()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-trace-").FullName;
        try
        {
            string? path = null;
            using var trace = On(dir, () => path);
            using (trace.Open(Trace.Launch)) { }
            trace.Flush();
            Assert.Empty(Directory.GetFiles(dir));

            path = Path.Combine(dir, "a.trace.jsonl");
            using (trace.Open(Trace.Turn)) { }
            trace.Flush();
            Assert.Equal(new[] { "launch", "turn" }, Lines(path).Skip(1).Select(l => l.GetProperty("s").GetString()));
        }
        finally { Directory.Delete(dir, true); }
    }

    //a /new or a compaction opens a new session file, and the trace follows it with a header of its own
    [Fact]
    public void A_new_session_file_gets_its_own_sidecar_and_header()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-trace-").FullName;
        try
        {
            var first = Path.Combine(dir, "a.trace.jsonl");
            var second = Path.Combine(dir, "b.trace.jsonl");
            var path = first;
            var session = "s1";
            using var trace = new Trace(Stopwatch.GetTimestamp(), DateTimeOffset.Now, () => path, () => session, "b", 1);
            using (trace.Open(Trace.Turn)) { }
            trace.Flush();
            path = second;
            session = "s2";
            using (trace.Open(Trace.Turn)) { }
            trace.Flush();
            trace.Dispose();

            Assert.Equal(2, Lines(first).Count);
            var b = Lines(second);
            Assert.Equal(2, b.Count);
            Assert.Equal("s2", b[0].GetProperty("session").GetString());
            //one clock for the process, so the second file's span starts after the first file's
            Assert.True(b[1].GetProperty("t").GetInt64() >= Lines(first)[1].GetProperty("t").GetInt64());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_resume_mark_is_taken_once()
    {
        var trace = On(Path.GetTempPath(), () => null);
        Assert.False(trace.TakeResume());
        trace.MarkResume();
        Assert.True(trace.TakeResume());
        Assert.False(trace.TakeResume());
    }

    //the server's fractional timings keep the server's own number token
    [Fact]
    public void A_number_element_is_written_as_the_server_sent_it()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-trace-").FullName;
        try
        {
            var file = Path.Combine(dir, "a.trace.jsonl");
            using var trace = On(dir, () => file);
            using var doc = JsonDocument.Parse("""{"prompt_ms":123.456,"name":"x"}""");
            using (var s = trace.Open(Trace.Request))
            {
                s.Set("prompt_ms", doc.RootElement.GetProperty("prompt_ms"));
                s.Set("skipped", doc.RootElement.GetProperty("name"));
            }
            trace.Flush();
            var line = File.ReadAllLines(file)[1];
            Assert.Contains("\"prompt_ms\":123.456", line);
            Assert.DoesNotContain("skipped", line);
        }
        finally { Directory.Delete(dir, true); }
    }
}
