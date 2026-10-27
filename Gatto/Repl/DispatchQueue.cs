using Gatto.Core.Tools;
using Gatto.Repl.Render.Chrome;

namespace Gatto.Repl;

//what a queued text is, marked at enqueue, so the loop's take never parses it. a command and an attachment wait for the dispatcher
internal enum EntryKind { Plain, Command, Attachment, Watch }

//one queued item, a typed message, a watched letter or a background task's end, which the dispatcher sends on its own arm
internal readonly record struct QueueEntry(string Text, BackgroundEnd? End, bool Drawn = false, EntryKind Kind = EntryKind.Plain, bool Counted = false)   //drawn is a parked entry whose block is on screen, counted a letter the guard already counted
{
    public bool IsTask => End is not null;
    //what the user typed, which the cap counts and the pull-back returns. a letter and an end are neither
    public bool IsTyped => End is null && Kind != EntryKind.Watch;
}

//the semaphore count equals takeable entries minus unredeemed permits. the take returns null from an empty queue, so no message is handed out twice
internal sealed class DispatchQueue(int cap)
{
    private readonly object _lock = new();
    private readonly List<QueueEntry> _q = new();
    private readonly SemaphoreSlim _signal = new(0);
    //entries put back at the head with no permit, so they wait for the next entry rather than retrying at once
    private int _parked;

    public int Count { get { lock (_lock) return _q.Count; } }

    //false means the cap is full, and the caller beeps and re-seeds the composer with the text. only typed messages count against it
    public bool TryEnqueue(string text, EntryKind kind = EntryKind.Plain)
    {
        lock (_lock)
        {
            if (_q.Count(e => e.IsTyped) >= cap) return false;
            Add(new QueueEntry(text, null, Kind: kind));
            return true;
        }
    }

    //a background task's end is never refused, since refusing it would lose a result the model is owed
    public void EnqueueTask(BackgroundEnd end)
    {
        lock (_lock) Add(new QueueEntry(end.Text, end));
    }

    //a watched letter is never refused and never counted, so a full folder cannot push the user's typing out
    public void EnqueueWatch(string text)
    {
        lock (_lock) Add(new QueueEntry(text, null, Kind: EntryKind.Watch));
    }

    //every new entry releases the parked ends ahead of it with its own permit, inside the lock, so an entry and its permit appear together
    private void Add(QueueEntry entry)
    {
        _q.Add(entry);
        _signal.Release(_parked + 1);
        _parked = 0;
    }

    //a taken entry whose turn rolled back goes back to the head with no permit, so the model takes it before the next message and not in a loop
    public void Park(QueueEntry entry)
    {
        lock (_lock)
        {
            _q.Insert(0, entry);
            _parked++;
        }
    }

    //take every typed message and consume one permit each, in queue order, under one lock. letters and ends stay with their permits
    public IReadOnlyList<string> DrainAll()
    {
        lock (_lock) return [.. TakeWhere(e => e.IsTyped).Select(e => e.Text)];
    }

    //what the loop delivers at a round boundary: every plain message and at most watchBudget letters, in queue order. commands, attachments and ends wait for the dispatcher
    public IReadOnlyList<QueueEntry> TakeQueuedForTurn(int watchBudget)
    {
        lock (_lock)
            return TakeWhere(e =>
            {
                if (e.IsTask) return false;
                if (e.Kind == EntryKind.Plain) return true;
                if (e.Kind != EntryKind.Watch || watchBudget <= 0) return false;
                watchBudget--;
                return true;
            });
    }

    //drops every task entry, the parked ones with no permit to consume, for a conversation the ends no longer belong to
    public void DropTasks()
    {
        lock (_lock) TakeWhere(e => e.IsTask);
    }

    //drops every queued letter for a watch that is off or a conversation that is new. the files stay in the folder
    public void DropWatch()
    {
        lock (_lock) TakeWhere(e => e.Kind == EntryKind.Watch && !e.IsTask);
    }

    //removes the matching entries in queue order, the caller holds the lock. a parked entry sits at the head with no permit, so only the others give one back
    private List<QueueEntry> TakeWhere(Func<QueueEntry, bool> match)
    {
        var at = new List<int>();
        for (var i = 0; i < _q.Count; i++)
            if (match(_q[i])) at.Add(i);
        var taken = at.Select(i => _q[i]).ToList();
        var permits = 0;
        for (var k = at.Count - 1; k >= 0; k--)
        {
            if (at[k] < _parked) _parked--;
            else permits++;
            _q.RemoveAt(at[k]);
        }
        ConsumePermits(permits);
        return taken;
    }

    //takes the permits of entries just removed under the lock, which never blocks since each takeable entry holds one
    private void ConsumePermits(int count)
    {
        for (var i = 0; i < count; i++) _signal.Wait(0);
    }

    //the queued entries, oldest first, as the chrome draws them. an end shows its head line and a letter its path, both off the user's marker
    public IReadOnlyList<QueueEntryView> Texts()
    {
        lock (_lock) return [.. _q.Select(View)];
    }

    private static QueueEntryView View(QueueEntry e) =>
        e.IsTask ? new(BackgroundTasks.HeadOf(e.Text), true)
        : e.Kind == EntryKind.Watch ? new(e.Text.Split('\n')[0].TrimEnd('\r'), true)
        : new(e.Text, false);

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
