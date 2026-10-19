namespace Gatto.Core.Client;

//llama-server's two counting routes, the exact path of /context
public interface ITokenCounter
{
    Task<int?> CountInputTokensAsync(ChatRequest request, CancellationToken ct);
    Task<int?> TokenizeCountAsync(string text, CancellationToken ct);
}
