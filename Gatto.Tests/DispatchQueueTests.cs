//these tests exercise the mid-turn dispatch FIFO and its permit invariants directly, without a terminal.
using Gatto.Repl;

namespace Gatto.Tests;

public class DispatchQueueTests
{
    //every blocking wait takes a deadline, so a stuck wait fails the test instead of freezing the runner.
    private static CancellationTokenSource Deadline(int ms = 2000) => new(ms);

    [Fact]
    public async Task Fifo_OldestFirst()
    {
        var q = new DispatchQueue(10);
        Assert.True(q.TryEnqueue("a"));
        Assert.True(q.TryEnqueue("b"));
        using var cts = Deadline();
        Assert.Equal("a", (await q.TakeAsync(cts.Token))?.Text);
        Assert.Equal("b", (await q.TakeAsync(cts.Token))?.Text);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Cap_RejectsBeyondCap_WithoutLosingTheQueue()
    {
        var q = new DispatchQueue(3);
        Assert.True(q.TryEnqueue("1"));
        Assert.True(q.TryEnqueue("2"));
        Assert.True(q.TryEnqueue("3"));
        Assert.False(q.TryEnqueue("4"));   //on rejection the caller beeps and puts the text back in the composer.
        Assert.Equal(3, q.Count);
        Assert.Equal(new[] { "1", "2", "3" }, q.Texts().Select(v => v.Text));
    }
    [Fact]
    public async Task TakeAsync_ReturnsNull_WhenTheMessageWasDroppedAfterItsPermitWasTaken()
    {
        //the pull can take the only queued message between the permit and the dequeue, so the take answers null on an empty queue. the dispatcher skips that round
        var q = new DispatchQueue(10);
        q.TryEnqueue("a");
        q.OnPermitTakenForTest = () => q.DrainAll();     //the hook runs the pull between taking the permit and dequeuing.
        using var cts = Deadline();
        Assert.Null(await q.TakeAsync(cts.Token));
        Assert.Equal(0, q.Count);

        //no phantom permit survives the race, so a second take blocks until cancelled.
        q.OnPermitTakenForTest = null;
        using var shortCts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.TakeAsync(shortCts.Token));
    }
    //the drain returns the texts in queue order, since the pull hands them to the composer
    [Fact]
    public void DrainAll_empties_the_queue_and_returns_what_it_took()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("a"); q.TryEnqueue("b"); q.TryEnqueue("c");
        Assert.Equal(new[] { "a", "b", "c" }, q.DrainAll());
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task DrainAll_consumes_every_permit_so_no_phantom_message_survives()
    {
        //a permit left behind by the drain would make a second take return a phantom. the timeout here is the assertion
        var q = new DispatchQueue(10);
        q.TryEnqueue("a"); q.TryEnqueue("b");
        q.DrainAll();
        q.TryEnqueue("d");

        using var cts = Deadline();
        Assert.Equal("d", (await q.TakeAsync(cts.Token))?.Text);   //the one surviving message takes the one real permit.
        using var shortCts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.TakeAsync(shortCts.Token));
    }

    [Fact]
    public void DrainAll_on_an_empty_queue_is_a_no_op_returning_nothing()
    {
        var q = new DispatchQueue(10);
        Assert.Empty(q.DrainAll());
        Assert.Equal(0, q.Count);
    }

    private static Gatto.Core.Tools.BackgroundEnd End(int n) =>
        new(n, 0, $"background task {n} ended · exit 0 · 1s\noutput {n}", "exit 0", false, Gatto.Core.Tools.BackgroundTasks.Exit, TimeSpan.FromSeconds(1));

    private static async Task Blocks(DispatchQueue q)
    {
        using var shortCts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.TakeAsync(shortCts.Token));
    }

    [Fact]
    public void THE_CAP_COUNTS_TYPED_MESSAGES_AND_NEVER_REFUSES_AN_END()
    {
        var q = new DispatchQueue(2);
        q.EnqueueTask(End(1));
        Assert.True(q.TryEnqueue("a"));
        Assert.True(q.TryEnqueue("b"));
        Assert.False(q.TryEnqueue("c"));
        q.EnqueueTask(End(2));

        Assert.Equal(4, q.Count);
        Assert.Equal([("background task 1 ended · exit 0 · 1s", true), ("a", false), ("b", false), ("background task 2 ended · exit 0 · 1s", true)],
            q.Texts().Select(v => (v.Text, v.Task)));
    }

    [Fact]
    public async Task THE_PULL_TAKES_TYPED_MESSAGES_AND_LEAVES_ENDS_WITH_THEIR_PERMITS()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("a");
        q.EnqueueTask(End(1));
        q.TryEnqueue("b");

        Assert.Equal(["a", "b"], q.DrainAll());
        using var cts = Deadline();
        Assert.Equal(1, (await q.TakeAsync(cts.Token))?.End?.Number);
        await Blocks(q);
    }

    [Fact]
    public async Task A_PARKED_END_WAITS_FOR_THE_NEXT_ENTRY_AND_GOES_BEFORE_IT()
    {
        var q = new DispatchQueue(10);
        q.Park(new QueueEntry(End(1).Text, End(1)));
        await Blocks(q);   //no permit of its own, so the dispatcher does not retry it at once

        q.TryEnqueue("next");
        using var cts = Deadline();
        Assert.Equal(1, (await q.TakeAsync(cts.Token))?.End?.Number);
        Assert.Equal("next", (await q.TakeAsync(cts.Token))?.Text);
        await Blocks(q);
    }

    [Fact]
    public async Task A_PARKED_END_GOES_BEFORE_A_MESSAGE_ALREADY_WAITING_AND_THE_MESSAGE_KEEPS_ITS_PERMIT()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("waiting");
        q.Park(new QueueEntry(End(1).Text, End(1)));

        using var cts = Deadline();
        Assert.Equal(1, (await q.TakeAsync(cts.Token))?.End?.Number);
        Assert.Equal("waiting", (await q.TakeAsync(cts.Token))?.Text);
        await Blocks(q);
    }

    [Fact]
    public async Task DROP_TASKS_TAKES_QUEUED_AND_PARKED_ENDS_WITH_THEIR_PERMITS()
    {
        var q = new DispatchQueue(10);
        q.EnqueueTask(End(1));
        q.TryEnqueue("typed");
        q.Park(new QueueEntry(End(2).Text, End(2)));

        q.DropTasks();

        Assert.Equal(["typed"], q.Texts().Select(v => v.Text));
        using var cts = Deadline();
        Assert.Equal("typed", (await q.TakeAsync(cts.Token))?.Text);
        await Blocks(q);
    }

    [Fact]
    public async Task Concurrent_ProducersAndOneConsumer_LoseNothing_AndDoNotDoubleDispatch()
    {
        var q = new DispatchQueue(1000);
        var got = new List<string>();
        using var cts = Deadline(5000);
        var consumer = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var m = await q.TakeAsync(cts.Token);
                if (m is { } entry) got.Add(entry.Text);
            }
        });
        for (var i = 0; i < 200; i++) Assert.True(q.TryEnqueue("m" + i));
        await consumer;
        Assert.Equal(200, got.Count);
        Assert.Equal(200, got.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 200).Select(i => "m" + i).ToList(), got);   //the consumer received every message in queue order.
    }

    [Fact]
    public async Task THE_TURN_TAKES_PLAIN_MESSAGES_AND_LETTERS_IN_ORDER_AND_LEAVES_THE_REST_WITH_THEIR_PERMITS()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("one");
        q.TryEnqueue("/compact", EntryKind.Command);
        q.EnqueueWatch("C:\\inbox\\a.md\n\nletter a");
        q.EnqueueTask(End(1));
        q.TryEnqueue("look at [Image #1]", EntryKind.Attachment);
        q.TryEnqueue("two");

        var taken = q.TakeQueuedForTurn(watchBudget: 6);

        Assert.Equal([("one", EntryKind.Plain), ("C:\\inbox\\a.md\n\nletter a", EntryKind.Watch), ("two", EntryKind.Plain)],
            taken.Select(e => (e.Text, e.Kind)));
        using var cts = Deadline();
        Assert.Equal("/compact", (await q.TakeAsync(cts.Token))?.Text);
        Assert.Equal(1, (await q.TakeAsync(cts.Token))?.End?.Number);
        Assert.Equal("look at [Image #1]", (await q.TakeAsync(cts.Token))?.Text);
        await Blocks(q);   //one permit went with each taken entry, so nothing phantom is left
    }

    [Fact]
    public void THE_TURN_TAKES_NOTHING_FROM_A_QUEUE_OF_ENDS()
    {
        var q = new DispatchQueue(10);
        q.EnqueueTask(End(1));
        q.EnqueueTask(End(2));
        Assert.Empty(q.TakeQueuedForTurn(watchBudget: 6));
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public async Task A_BUDGET_OF_TWO_OVER_EIGHT_LETTERS_TAKES_TWO_AND_LEAVES_SIX_SHOWN()
    {
        var q = new DispatchQueue(10);
        for (var i = 1; i <= 8; i++) q.EnqueueWatch($"C:\\inbox\\{i}.md\n\nletter {i}");

        var taken = q.TakeQueuedForTurn(watchBudget: 2);

        Assert.Equal(["C:\\inbox\\1.md\n\nletter 1", "C:\\inbox\\2.md\n\nletter 2"], taken.Select(e => e.Text));
        Assert.Equal(Enumerable.Range(3, 6).Select(i => ($"C:\\inbox\\{i}.md", true)), q.Texts().Select(v => (v.Text, v.Task)));
        using var cts = Deadline();
        for (var i = 3; i <= 8; i++) Assert.Equal(EntryKind.Watch, (await q.TakeAsync(cts.Token))?.Kind);
        await Blocks(q);
    }

    [Fact]
    public void A_LETTER_IS_NEVER_REFUSED_AND_NEVER_COUNTED_TOWARD_THE_CAP()
    {
        var q = new DispatchQueue(2);
        for (var i = 0; i < 5; i++) q.EnqueueWatch($"C:\\inbox\\{i}.md\n\nbody");
        Assert.True(q.TryEnqueue("a"));
        Assert.True(q.TryEnqueue("b", EntryKind.Command));
        Assert.False(q.TryEnqueue("c", EntryKind.Attachment));
        Assert.Equal(7, q.Count);
        Assert.Equal(("C:\\inbox\\0.md", true), q.Texts().Select(v => (v.Text, v.Task)).First());
    }

    [Fact]
    public async Task THE_PULL_RETURNS_TYPED_TEXT_ONLY_AND_LEAVES_LETTERS_QUEUED()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("a");
        q.EnqueueWatch("C:\\inbox\\x.md\n\nletter");
        q.TryEnqueue("/role coder", EntryKind.Command);

        Assert.Equal(["a", "/role coder"], q.DrainAll());
        using var cts = Deadline();
        Assert.Equal(EntryKind.Watch, (await q.TakeAsync(cts.Token))?.Kind);
        await Blocks(q);
    }

    [Fact]
    public async Task DROP_WATCH_TAKES_QUEUED_AND_PARKED_LETTERS_AND_LEAVES_MESSAGES_AND_ENDS()
    {
        var q = new DispatchQueue(10);
        q.EnqueueWatch("C:\\inbox\\a.md\n\nletter a");
        q.TryEnqueue("typed");
        q.EnqueueTask(End(1));
        q.Park(new QueueEntry("C:\\inbox\\b.md\n\nletter b", null, Drawn: true, Kind: EntryKind.Watch, Counted: true));

        q.DropWatch();

        Assert.Equal(["typed", "background task 1 ended · exit 0 · 1s"], q.Texts().Select(v => v.Text));
        using var cts = Deadline();
        Assert.Equal("typed", (await q.TakeAsync(cts.Token))?.Text);
        Assert.Equal(1, (await q.TakeAsync(cts.Token))?.End?.Number);
        await Blocks(q);
    }

    [Fact]
    public async Task A_PARKED_LETTER_KEEPS_ITS_COUNTED_MARK_AND_GOES_BEFORE_THE_NEXT_ENTRY()
    {
        var q = new DispatchQueue(10);
        q.Park(new QueueEntry("C:\\inbox\\a.md\n\nletter a", null, Drawn: true, Kind: EntryKind.Watch, Counted: true));
        await Blocks(q);

        q.TryEnqueue("next");
        using var cts = Deadline();
        var retaken = await q.TakeAsync(cts.Token);
        Assert.True(retaken is { Kind: EntryKind.Watch, Counted: true, Drawn: true });
        Assert.Equal("next", (await q.TakeAsync(cts.Token))?.Text);
        await Blocks(q);
    }
}
