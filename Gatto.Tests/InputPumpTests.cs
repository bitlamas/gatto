using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Tests;

public class InputPumpTests
{
    //the fake source delivers what the test feeds, and ReadKey blocks like a real console
    private sealed class FeedSource : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }

    private sealed class DeadSource : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("console gone");
        public bool KeyAvailable => false;
    }

    private static ConsoleKeyInfo K(char c) => new(c, ConsoleKey.A, false, false, false);

    //read on a worker with a deadline, so a blocked take fails the test instead of hanging the runner.
    private static ConsoleKeyInfo? Read(IKeySource s, int ms = 2000)
    {
        var t = Task.Run(s.ReadKey);
        return t.Wait(ms) ? t.Result : null;
    }

    //deadline-guarded read for composer sources, unwrapping a keystroke to its key. a selection here means the test itself is wrong, so it throws.
    private static ConsoleKeyInfo? Read(IComposerSource s, int ms = 2000)
    {
        var t = Task.Run(s.Read);
        if (!t.Wait(ms)) return null;
        return Assert.IsType<ComposerInput.Key>(t.Result).K;
    }

    private static void WaitUntil(Func<bool> cond, int ms = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(5);
        Assert.True(cond(), "condition not reached in time");
    }

    //a throw on the pump thread kills the process, so the dispatch is guarded like the read. a dispatch that never recovers must reach Kill()

    private static ConsoleKeyInfo PageUp() => new('\0', ConsoleKey.PageUp, false, false, false);

    [Fact]
    public void One_throwing_key_dispatch_does_not_kill_the_pump()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var warnings = new List<string>();
        pump.SetInputFaultSink(warnings.Add);

        var seen = 0;
        pump.SetScrollSink(_ => { seen++; if (seen == 1) throw new InvalidOperationException("cursed key"); });
        pump.Start();

        src.Feed.Add(PageUp());   //the first routed event throws inside the sink.
        src.Feed.Add(PageUp());   //routing must continue after a sink throw.

        WaitUntil(() => seen >= 2);
        Assert.Single(warnings);
    }

    [Fact]
    public void The_dispatch_fault_warning_is_latched_to_one_shot()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var warnings = new List<string>();
        pump.SetInputFaultSink(warnings.Add);

        var seen = 0;
        pump.SetScrollSink(_ => { seen++; throw new InvalidOperationException("always cursed"); });
        pump.Start();

        for (var i = 0; i < 10; i++) src.Feed.Add(PageUp());

        WaitUntil(() => seen >= 10);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task ThrowingDispatch_KillsPump_ComposerReadThrows()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.SetInputFaultSink(_ => { });
        pump.SetScrollSink(_ => throw new InvalidOperationException("always cursed"));
        pump.Start();

        //the dispatch failures raise the same consecutive-failure counter as a dead read. shape the guard like its twin, or the asymmetry is the bug
        for (var i = 0; i < 150; i++) src.Feed.Add(PageUp());

        var t = Task.Run(() => Assert.ThrowsAny<InvalidOperationException>((Action)(() => pump.Composer.Read())));
        await t.WaitAsync(TimeSpan.FromSeconds(5));   //a hang here fails the test rather than the runner.
    }

    [Fact]
    public void A_dispatch_that_recovers_never_reaches_the_kill_ceiling()
    {
        //the counter resets on success, so a sparse fault warns once and the pump keeps running. that behaviour is pinned on purpose
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.SetInputFaultSink(_ => { });

        var seen = 0;
        pump.SetScrollSink(_ => { seen++; if (seen % 2 == 1) throw new InvalidOperationException("every other key"); });
        pump.Start();

        for (var i = 0; i < 300; i++) src.Feed.Add(PageUp());

        WaitUntil(() => seen >= 300, 5000);
        //a key routed after 150 failures proves the counter reset on each success.
        src.Feed.Add(K('a'));
        Assert.Equal('a', Read(pump.Composer)!.Value.KeyChar);
    }

    [Fact]
    public void Keys_FlowToComposer_WhenNoFocusHeld()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        src.Feed.Add(K('a'));
        Assert.Equal('a', Read(pump.Composer)!.Value.KeyChar);
    }

    [Fact]
    public void FocusScope_ReceivesKeys_ComposerGetsNothing()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        using var focus = pump.PushFocus();
        src.Feed.Add(K('y'));
        Assert.Equal('y', Read(focus.Keys)!.Value.KeyChar);
        Assert.False(pump.Composer.KeyAvailable);
    }

    [Fact]
    public void Pop_SubsequentKeysReachComposer()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        var focus = pump.PushFocus();
        src.Feed.Add(K('y'));
        Assert.Equal('y', Read(focus.Keys)!.Value.KeyChar);
        focus.Dispose();
        src.Feed.Add(K('b'));
        Assert.Equal('b', Read(pump.Composer)!.Value.KeyChar);
    }

    [Fact]
    public void TypeAhead_BeforePush_StaysInComposer()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        src.Feed.Add(K('t'));                              //the type-ahead key is fed before any prompt exists.
        //wait for ComposerPending (means routed), KeyAvailable also reports the source's peek, so the focus scope can swallow the type-ahead
        WaitUntil(() => pump.ComposerPending == 1);        //pending means the key was routed
        using (var focus = pump.PushFocus())
        {
            src.Feed.Add(K('n'));                          //this key answers the prompt.
            Assert.Equal('n', Read(focus.Keys)!.Value.KeyChar);
        }
        //the type-ahead was neither eaten by the prompt nor lost.
        Assert.Equal('t', Read(pump.Composer)!.Value.KeyChar);
    }

    [Fact]
    public void NestedFocus_TopWins_ThenUnwinds()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        using var outer = pump.PushFocus();
        var inner = pump.PushFocus();
        src.Feed.Add(K('1'));
        Assert.Equal('1', Read(inner.Keys)!.Value.KeyChar);
        inner.Dispose();
        src.Feed.Add(K('2'));
        Assert.Equal('2', Read(outer.Keys)!.Value.KeyChar);
    }

    [Fact]
    public async Task DeadSource_KillsPump_ComposerReadThrows()
    {
        var pump = new InputPump(new KeyInputSource(new DeadSource()));
        pump.Start();
        //after 100 consecutive source failures the pump completes every channel, so a blocked read throws.
        var t = Task.Run(() => Assert.ThrowsAny<InvalidOperationException>((Action)(() => pump.Composer.Read())));
        await t.WaitAsync(TimeSpan.FromSeconds(5));   //the bounded wait fails this test on a hang instead of stalling the test runner forever.
    }

    [Fact]
    public async Task PushFocus_AfterConsoleDeath_KeysReadThrowsInsteadOfHanging()
    {
        var pump = new InputPump(new KeyInputSource(new DeadSource()));
        pump.Start();
        //the blocked read throwing is the evidence that 100 consecutive source failures killed the pump.
        var died = Task.Run(() => Assert.ThrowsAny<InvalidOperationException>((Action)(() => pump.Composer.Read())));
        await died.WaitAsync(TimeSpan.FromSeconds(5));

        //a scope pushed after the pump dies is born completed, so reading its keys throws instead of hanging.
        var focus = pump.PushFocus();
        var t = Task.Run(() => Assert.ThrowsAny<InvalidOperationException>((Action)(() => focus.Keys.ReadKey())));
        await t.WaitAsync(TimeSpan.FromSeconds(5));
        focus.Dispose();   //dispose must stay safe on a channel that was never placed on the focus stack.
    }

    [Fact]
    public void Dispose_DiscardsUnreadKeys()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        var focus = pump.PushFocus();
        src.Feed.Add(K('y'));
        //wait on the focus PendingCount, KeyAvailable also reports the source peek. disposing early routes the key to the composer instead of discarding it
        WaitUntil(() => focus.PendingCount == 1);   //count 1 proves the key reached the scope and stayed unread.
        focus.Dispose();
        src.Feed.Add(K('b'));
        Assert.Equal('b', Read(pump.Composer)!.Value.KeyChar);   //reading b next proves the discarded key was never replayed to the composer
        Assert.False(pump.Composer.KeyAvailable);
    }

    [Fact]
    public void InjectToComposer_DeliversLikeATypedKey()
    {
        var pump = new InputPump(new KeyInputSource(new FeedSource()));
        pump.Start();
        pump.InjectToComposer(K('j'));
        Assert.Equal('j', Read(pump.Composer)!.Value.KeyChar);
    }

    //keys and select gestures share one ComposerInput channel, in arrival order. draining it leaves the editor thread the only writer of selection state

    [Fact]
    public void ComposerChannel_PreservesArrivalOrder_ForKeysAndGestures()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();

        src.Feed.Add(K('a'));
        WaitUntil(() => pump.ComposerPending == 1);   //this wait proves the key was routed before the gesture entered the channel, so the fixed read order reflects true arrival order.
        pump.EnqueueComposerGesture(new ComposerInput.Select(0, 1, ComposerGesture.Begin));
        WaitUntil(() => pump.ComposerPending == 2);
        src.Feed.Add(K('b'));
        WaitUntil(() => pump.ComposerPending == 3);

        Assert.Equal('a', Assert.IsType<ComposerInput.Key>(pump.Composer.Read()).K.KeyChar);
        var sel = Assert.IsType<ComposerInput.Select>(pump.Composer.Read());
        Assert.Equal(0, sel.Line);
        Assert.Equal(1, sel.Col);
        Assert.Equal(ComposerGesture.Begin, sel.Kind);
        Assert.Equal('b', Assert.IsType<ComposerInput.Key>(pump.Composer.Read()).K.KeyChar);
    }

    //each push reports true and each dispose reports false, in that order. the ticker counts depth from those events, so the pump reports exactly one event per edge
    [Fact]
    public void ModalWaitSink_ReportsBothEdgesOfAScope()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        var seen = new List<bool>();
        pump.SetModalWaitSink(open => { lock (seen) seen.Add(open); });

        using (pump.PushFocus())
            lock (seen) Assert.Equal(new[] { true }, seen);

        lock (seen) Assert.Equal(new[] { true, false }, seen);
    }

    //nested scopes report every edge, the pump never flattens them. the consumer decides that a prompt over a prompt is one wait
    [Fact]
    public void ModalWaitSink_ReportsNestedScopesSeparately()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();
        var seen = new List<bool>();
        pump.SetModalWaitSink(open => { lock (seen) seen.Add(open); });

        var outer = pump.PushFocus();
        var inner = pump.PushFocus();
        inner.Dispose();
        outer.Dispose();

        lock (seen) Assert.Equal(new[] { true, true, false, false }, seen);
    }

    //an unstarted pump's scope is completed, no key reaches it. it still reports both edges, or the turn clock skips its pause for an unanswerable prompt
    [Fact]
    public void ModalWaitSink_ReportsAScopeBornCompleted()
    {
        var pump = new InputPump(new KeyInputSource(new FeedSource()));   //the pump is never started, so no key can ever reach a scope on it.
        var seen = new List<bool>();
        pump.SetModalWaitSink(open => { lock (seen) seen.Add(open); });

        using (pump.PushFocus()) { }

        lock (seen) Assert.Equal(new[] { true, false }, seen);
    }
}
