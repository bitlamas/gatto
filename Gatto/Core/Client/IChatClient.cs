namespace Gatto.Core.Client;

public interface IChatClient
{
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);

    //best-effort context window, null when the endpoint can't say, and it never throws
    Task<int?> TryGetContextLengthAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
}
