using System.Text;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the four seams a caller outside the repl arms with: a held set, a path-and-body sink, a pass-ended notice, and a tick with a reason to stop
public sealed class WatchSeamTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-seam-").FullName;
    private Watch? _watch;

    public void Dispose()
    {
        _watch?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Folder => Path.Combine(_root, "inbox");

    private static Task Settle() => Task.Delay(400);

    private string Letter(string folder, string name, string body)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public async Task A_GIVEN_HELD_SET_KEEPS_THE_NAMED_LETTER_AND_LISTENS_FOR_THE_OTHER_ONES()
    {
        Directory.CreateDirectory(Folder);
        var kept = Letter(Folder, "kept.md", "held by the caller");
        var free = Letter(Folder, "free.md", "arrives");
        var got = new List<string>();
        _watch = Watch.Arm(Folder, (_, text) => { lock (got) got.Add(text); return true; },
            _ => { }, held: [kept]);
        _watch.Start();
        Waits.Until(() => { lock (got) return got.Count >= 1; }, () => $"{got.Count} handed over");
        await Settle();
        lock (got) Assert.Equal([free + "\n\narrives"], [.. got]);
    }

    [Fact]
    public void A_TAKE_SINK_GETS_THE_PATH_AND_THE_RAW_BODY_WHILE_THE_ENQUEUE_KEEPS_TODAYS_TEXT()
    {
        Directory.CreateDirectory(Folder);
        var path = Letter(Folder, "a.md", "hello\nworld");
        string? tookPath = null, tookBody = null;
        _watch = Watch.Arm(Folder, (_, _) => true, _ => { },
            take: (_, p, b) => { tookPath = p; tookBody = b; return true; });
        _watch.Start();
        Waits.Until(() => tookBody is not null, () => "the take sink never ran");
        Assert.Equal(path, tookPath);
        Assert.Equal("hello\nworld", tookBody);
    }

    [Fact]
    public void WITHOUT_TAKE_THE_ENQUEUE_TEXT_IS_TODAYS_for_a_short_body_and_a_body_past_the_budget()
    {
        Directory.CreateDirectory(Folder);
        var shortPath = Letter(Folder, "short.md", "brief");
        var big = new string('x', GrepTool.MaxTotalChars + 1);
        var bigPath = Letter(Folder, "big.md", big);
        var got = new List<string>();
        _watch = Watch.Arm(Folder, (_, text) => { lock (got) got.Add(text); return true; }, _ => { });
        _watch.Start();
        Waits.Until(() => { lock (got) return got.Count >= 2; }, () => $"{got.Count} of 2 handed over");
        lock (got) Assert.Equal([
            bigPath + $"\n\nthis letter holds {big.Length} characters, more than a tool result may carry; read_file reads it",
            shortPath + "\n\nbrief"], [.. got]);
    }

    [Fact]
    public async Task ONE_PASS_OF_THREE_LETTERS_ENDS_WITH_ONE_NOTICE()
    {
        Directory.CreateDirectory(Folder);
        for (var i = 1; i <= 3; i++) Letter(Folder, $"{i}.md", $"letter {i}");
        var takes = 0;
        var ended = 0;
        _watch = Watch.Arm(Folder, (_, _) => true, _ => { },
            take: (_, _, _) => { Interlocked.Increment(ref takes); return true; },
            passEnded: _ => Interlocked.Increment(ref ended));
        _watch.Start();
        Waits.Until(() => Volatile.Read(ref ended) >= 1, () => $"takes {takes}, ended {ended}");
        await Settle();
        Assert.Equal(3, Volatile.Read(ref takes));
        Assert.Equal(1, Volatile.Read(ref ended));
    }

    [Fact]
    public async Task A_PASS_STOPPED_AT_THE_BUDGET_ENDS_WITH_ONE_NOTICE_AND_HANDS_SIX()
    {
        Directory.CreateDirectory(Folder);
        for (var i = 1; i <= Watch.K + 2; i++) Letter(Folder, $"{i:00}.md", $"letter {i}");
        var takes = 0;
        var ended = 0;
        _watch = Watch.Arm(Folder, (_, _) => true, _ => { },
            take: (_, _, _) => { Interlocked.Increment(ref takes); return true; },
            passEnded: _ => Interlocked.Increment(ref ended));
        _watch.Start();
        Waits.Until(() => Volatile.Read(ref ended) >= 1, () => $"takes {takes}, ended {ended}");
        await Settle();
        Assert.Equal(Watch.K, Volatile.Read(ref takes));
        Assert.Equal(1, Volatile.Read(ref ended));
    }

    [Fact]
    public void A_SINK_THAT_REFUSES_LEAVES_THE_LETTER_UNHANDED_AND_THE_NEXT_PASS_OFFERS_IT_AGAIN()
    {
        Directory.CreateDirectory(Folder);
        Letter(Folder, "a.md", "offer me twice");
        var calls = 0;
        var taken = new List<string>();
        _watch = Watch.Arm(Folder, (_, _) => true, _ => { },
            take: (_, p, b) =>
            {
                calls++;
                if (calls > 1) lock (taken) taken.Add(p + "\n\n" + b);
                return calls > 1;
            });
        _watch.Start();
        Waits.Until(() => Volatile.Read(ref calls) >= 1, () => "the sink never ran");
        Assert.Empty(taken);
        _watch.Rescan();
        Waits.Until(() => { lock (taken) return taken.Count >= 1; }, () => "the second pass never offered the letter");
        lock (taken) Assert.Single(taken);
    }

    [Fact]
    public async Task AN_IDLE_TICK_RUNS_THE_CALLERS_CHECK_AND_A_STOPPED_WATCH_HANDS_NOTHING_OVER()
    {
        Directory.CreateDirectory(Folder);
        var checks = 0;
        var takes = 0;
        _watch = Watch.Arm(Folder, (_, _) => true, _ => { },
            take: (_, _, _) => { Interlocked.Increment(ref takes); return true; },
            tick: TimeSpan.FromMilliseconds(100),
            keepWatching: () => Interlocked.Increment(ref checks) < 2);
        _watch.Start();
        Waits.Until(() => Volatile.Read(ref checks) >= 2, () => $"{checks} checks ran");
        await Settle();
        Assert.Equal(2, Volatile.Read(ref checks));
        Letter(Folder, "late.md", "too late");
        await Task.Delay(1000);
        Assert.Equal(0, Volatile.Read(ref takes));
    }

    [Fact]
    public async Task THE_TAKE_SINK_WAITS_FOR_AN_EXCLUSIVE_WRITER_LIKE_THE_ENQUEUE_DOES()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "held.md");
        var bodies = new List<string>();
        _watch = Watch.Arm(Folder, (_, _) => true, _ => { },
            take: (_, _, b) => { lock (bodies) bodies.Add(b); return true; });
        _watch.Start();
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writer.Write(Encoding.UTF8.GetBytes("half one "));
            await Settle();
            Assert.Empty(bodies);
            writer.Write(Encoding.UTF8.GetBytes("half two"));
        }
        Waits.Until(() => { lock (bodies) return bodies.Count >= 1; }, () => "the sink never got the letter");
        lock (bodies) Assert.Equal(["half one half two"], [.. bodies]);
    }
}
