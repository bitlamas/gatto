namespace Gatto.Core.Client;

public interface IChatClient
{
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);

    //the same stream with the span the request belongs to, which a tracing client opens its request span under and every other client ignores
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, long traceParent, CancellationToken ct) => StreamAsync(request, ct);

    //best-effort context window, null when the endpoint can't say, and it never throws
    Task<int?> TryGetContextLengthAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
}
