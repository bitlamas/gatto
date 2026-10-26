using Gatto.Core.Tools;
using Gatto.Repl.Render.Chrome;

namespace Gatto.Repl;

//one queued item, a typed message or a background task's end, which the dispatcher sends on its own arm
internal readonly record struct QueueEntry(string Text, BackgroundEnd? End, bool Drawn = false)   //drawn is a parked end whose block is on screen
{
    public bool IsTask => End is not null;
}

//the semaphore count equals takeable entries minus unredeemed permits. the take returns null from an empty queue, so no message is handed out twice
internal sealed class DispatchQueue(int cap)
{
    private readonly object _lock = new();
    private readonly List<QueueEntry> _q = new();
    private readonly SemaphoreSlim _signal = new(0);
    //ends put back at the head with no permit, so they wait for the next entry rather than retrying at once
    private int _parked;

    public int Count { get { lock (_lock) return _q.Count; } }

    //false means the cap is full, and the caller beeps and re-seeds the composer with the text. only typed messages count against it
    public bool TryEnqueue(string text)
    {
        lock (_lock)
        {
            if (_q.Count(e => !e.IsTask) >= cap) return false;
            Add(new QueueEntry(text, null));
            return true;
        }
    }

    //a background task's end is never refused, since refusing it would lose a result the model is owed
    public void EnqueueTask(BackgroundEnd end)
    {
        lock (_lock) Add(new QueueEntry(end.Text, end));
    }

    //every new entry releases the parked ends ahead of it with its own permit, inside the lock, so an entry and its permit appear together
    private void Add(QueueEntry entry)
    {
        _q.Add(entry);
        _signal.Release(_parked + 1);
        _parked = 0;
    }

    //a taken end whose turn rolled back goes back to the head with no permit, so the model takes it before the next message and not in a loop
    public void Park(QueueEntry entry)
    {
        lock (_lock)
        {
            _q.Insert(0, entry);
            _parked++;
        }
    }

    //take every typed message and consume one permit each, in queue order, under one lock. task entries stay with their permits
    public IReadOnlyList<string> DrainAll()
    {
        lock (_lock)
        {
            var taken = _q.Where(e => !e.IsTask).Select(e => e.Text).ToList();
            _q.RemoveAll(e => !e.IsTask);
            ConsumePermits(taken.Count);
            return taken;
        }
    }

    //drops every task entry, the parked ones with no permit to consume, for a conversation the ends no longer belong to
    public void DropTasks()
    {
        lock (_lock)
        {
            var parked = _parked;
            var takeable = _q.Count(e => e.IsTask) - parked;
            _q.RemoveAll(e => e.IsTask);
            _parked = 0;
            ConsumePermits(takeable);
        }
    }

    //takes the permits of entries just removed under the lock, which never blocks since each takeable entry holds one
    private void ConsumePermits(int count)
    {
        for (var i = 0; i < count; i++) _signal.Wait(0);
    }

    //the queued entries, oldest first, as the chrome draws them. a task entry shows its head line
    public IReadOnlyList<QueueEntryView> Texts()
    {
        lock (_lock) return [.. _q.Select(e => new QueueEntryView(e.IsTask ? BackgroundTasks.HeadOf(e.Text) : e.Text, e.IsTask))];
    }

    //wait for a permit and take the oldest entry. null means that permit's message was pulled back into the composer, so the caller skips the round
    public async Task<QueueEntry?> TakeAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);
        OnPermitTakenForTest?.Invoke();   //the hook is null outside tests
        lock (_lock)
        {
            if (_q.Count == 0) return null;
            var head = _q[0];
            _q.RemoveAt(0);
            //a parked head had no permit of its own, so the one just taken belongs to an entry behind it and goes back
            if (_parked > 0) { _parked--; _signal.Release(); }
            return head;
        }
    }

    //a test seam that fires between the permit and the dequeue, where the null return happens. it lets a test drive the Esc against dispatcher race on purpose
    internal Action? OnPermitTakenForTest;
}
