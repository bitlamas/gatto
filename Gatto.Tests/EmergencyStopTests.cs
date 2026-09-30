using System.Collections.Concurrent;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Term;
using Gatto.Terminal;

namespace Gatto.Tests;

//ctrl+break cancels the turn token. a prompt waiting on a key must end on that cancel rather than wait for the next key
public class EmergencyStopTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private sealed class FeedKeys(BlockingCollection<ConsoleKeyInfo> q) : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => q.Take();
        public bool KeyAvailable => q.Count > 0;
    }

    private sealed class NeverKeys : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("constructor keys must not be used when a pump is present");
        public bool KeyAvailable => false;
    }

    private sealed class SyncSurface : ITermSurface
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 80;
        public int Height { get; set; }
        public void Write(string s) { lock (_sb) _sb.Append(s); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
    }

    private static InputPump StartedPump()
    {
        var pump = new InputPump(new KeyInputSource(new FeedKeys(new BlockingCollection<ConsoleKeyInfo>())));
        pump.Start();
        return pump;
    }

    //the task completing is the event waited on, and the ten-second ceiling only turns a hang into a failure
    private static async Task AssertEndsCancelled(Task task, string what)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(10_000)) == task, what + " did not end after its token was cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_FOCUS_READ_ENDS_WHEN_ITS_TOKEN_CANCELS()
    {
        var pump = StartedPump();
        using var focus = pump.PushFocus();
        using var cts = new CancellationTokenSource();
        var read = Task.Run(() => focus.Keys.ReadKey(cts.Token));
        Assert.False(read.IsCompleted);
        cts.Cancel();
        await AssertEndsCancelled(read, "a focus read");
    }

    [Fact]
    public async Task THE_CONSOLE_READ_ENDS_WHEN_ITS_TOKEN_CANCELS()
    {
        //the cancel waits until the read is polling, or a check before the read would pass for one that interrupts a wait
        using var polling = new ManualResetEventSlim();
        IKeySource keys = new ConsoleKeySource(keyAvailable: () => { polling.Set(); return false; }, readKey: () => throw new InvalidOperationException("no key was ever available"));
        using var cts = new CancellationTokenSource();
        var read = Task.Run(() => keys.ReadKey(cts.Token));
        Assert.True(polling.Wait(10_000), "the read never began polling for a key");
        cts.Cancel();
        await AssertEndsCancelled(read, "a console read");
    }

    [Fact]
    public async Task AN_ASK_USER_QUESTION_ENDS_WHEN_THE_TURN_TOKEN_CANCELS()
    {
        var pump = StartedPump();
        var surface = new SyncSurface();
        var prompter = new RichPrompter(surface, T, new NeverKeys(), pump);
        using var cts = new CancellationTokenSource();
        var question = new AskQuestion("Which?", "Lang", new List<AskOption> { new("Go"), new("Rust") }, false);
        var ask = Task.Run(() => prompter.AskAsync([question], cts.Token));

        //wait for the question to paint, which is the event that proves the read is blocked on a key
        var painted = System.Diagnostics.Stopwatch.StartNew();
        while (!surface.Text.Contains("Which?", StringComparison.Ordinal) && painted.ElapsedMilliseconds < 5000) await Task.Delay(5);
        Assert.Contains("Which?", surface.Text, StringComparison.Ordinal);

        cts.Cancel();
        await AssertEndsCancelled(ask, "an ask_user question");
    }

    //the default body cannot interrupt a blocked read, so it is for scripted test sources only and every product source must declare its own
    [Fact]
    public void NO_PRODUCT_KEY_SOURCE_USES_THE_DEFAULT_CANCELLABLE_READ()
    {
        var method = typeof(IKeySource).GetMethod(nameof(IKeySource.ReadKey), [typeof(CancellationToken)]);
        Assert.NotNull(method);
        var sources = new[] { typeof(IKeySource).Assembly, typeof(RichPrompter).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IKeySource).IsAssignableFrom(t))
            .ToList();
        Assert.True(sources.Count >= 2, $"only {sources.Count} key sources found, so the census is not reading the product assemblies");
        var defaulted = sources
            .Where(t => t.GetInterfaceMap(typeof(IKeySource)).TargetMethods
                .Single(m => m.Name.EndsWith(nameof(IKeySource.ReadKey), StringComparison.Ordinal) && m.GetParameters().Length == 1)
                .DeclaringType == typeof(IKeySource))
            .Select(t => t.FullName)
            .ToList();
        Assert.True(defaulted.Count == 0, "these product key sources use the default read, which cannot interrupt a blocked key: " + string.Join(", ", defaulted));
    }
}
