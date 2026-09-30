using Gatto.Core.Client;

namespace Gatto.Tests.Fakes;

public sealed class FakeChatClient : IChatClient
{
    //a queued turn is either a StreamEvent array or an exception that fails the next call
    private readonly Queue<object> _turns = new();
    public List<ChatRequest> Requests { get; } = new();
    public void EnqueueTurn(params StreamEvent[] events) => _turns.Enqueue(events);

    //the next StreamAsync call throws, and the request is recorded first like a real client that fails after sending
    public void EnqueueThrow(Exception ex) => _turns.Enqueue(ex);

    //milliseconds to hold before the first event (zero keeps tests instant), and the only way to make a finished purr line visible
    public int FirstEventDelayMs { get; set; }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(request);
        var turn = _turns.Dequeue();
        if (turn is Exception ex) throw ex;
        if (FirstEventDelayMs > 0) await Task.Delay(FirstEventDelayMs, ct);
        foreach (var e in (StreamEvent[])turn) { ct.ThrowIfCancellationRequested(); yield return e; }
        await Task.CompletedTask;
    }
}
