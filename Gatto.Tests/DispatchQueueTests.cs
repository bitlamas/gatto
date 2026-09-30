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
        Assert.Equal("a", await q.TakeAsync(cts.Token));
        Assert.Equal("b", await q.TakeAsync(cts.Token));
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
        Assert.Equal(new[] { "1", "2", "3" }, q.Texts());
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
        Assert.Equal("d", await q.TakeAsync(cts.Token));   //the one surviving message takes the one real permit.
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
                if (m is not null) got.Add(m);
            }
        });
        for (var i = 0; i < 200; i++) Assert.True(q.TryEnqueue("m" + i));
        await consumer;
        Assert.Equal(200, got.Count);
        Assert.Equal(200, got.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 200).Select(i => "m" + i).ToList(), got);   //the consumer received every message in queue order.
    }
}
