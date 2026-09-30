namespace Gatto.Core.Client;

//llama.cpp's exceed_context_size_error 400, typed so the rescue path can catch it and the generic sites still see a connection failure
public sealed class GattoContextOverflowException(
    string message, int? promptTokens, int? contextSize, int statusCode = 400, string body = "")
    : GattoHttpStatusException(message, statusCode, body)
{
    public int? PromptTokens { get; } = promptTokens;
    public int? ContextSize { get; } = contextSize;
}
