
namespace Gatto.Repl;

//the semaphore count equals queued messages minus unredeemed permits. the take returns null from an empty queue, so no message is handed out twice
internal sealed class DispatchQueue(int cap)
{
    private readonly object _lock = new();
    private readonly Queue<string> _q = new();
    private readonly SemaphoreSlim _signal = new(0);

    public int Count { get { lock (_lock) return _q.Count; } }

    //false means the cap is full, and the caller beeps and re-seeds the composer with the text
    public bool TryEnqueue(string text)
    {
        lock (_lock)
        {
            if (_q.Count >= cap) return false;
            _q.Enqueue(text);
            _signal.Release();   //inside the lock, so a message and its permit appear together
            return true;
        }
    }
    //take every queued message and consume one permit each, in queue order, under one lock. taking and reporting in one call is what makes a half-pull impossible
    public IReadOnlyList<string> DrainAll()
    {
        lock (_lock)
        {
            var taken = _q.ToList();
            _q.Clear();
            for (var i = 0; i < taken.Count; i++) _signal.Wait(0);   //never blocks, since a permit exists for each message
            return taken;
        }
    }

    //the queued message texts, oldest first, the identity source for the chrome and the pull. the strings are the same instances TryEnqueue was handed
    public IReadOnlyList<string> Texts()
    {
        lock (_lock) return _q.ToList();
    }

    //wait for a permit and take the oldest queued message. null means that permit's message was pulled back into the composer, so the caller skips the round
    public async Task<string?> TakeAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);
        OnPermitTakenForTest?.Invoke();   //the hook is null outside tests
        lock (_lock) return _q.Count > 0 ? _q.Dequeue() : null;
    }

    //a test seam that fires between the permit and the dequeue, where the null return happens. it lets a test drive the Esc against dispatcher race on purpose
    internal Action? OnPermitTakenForTest;
}
