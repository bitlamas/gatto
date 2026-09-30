using System.Text.Json;

namespace Gatto.Core.Client;

//an endpoint's configuration, BaseUrl is null only for local and OpenAiCompatClient demands one, ApiKey is the record's only secret
public sealed record EndpointConfig(
    string? BaseUrl,
    string? KeyEnv = null,
    int? Context = null,
    IReadOnlyDictionary<string, JsonElement?>? Thinking = null,
    string? ApiKey = null)
{
    //redacted on purpose, the record's own ToString would print ApiKey into any log line or error message
    public override string ToString() =>
        $"EndpointConfig {{ BaseUrl = {BaseUrl}, KeyEnv = {KeyEnv}, Context = {Context}, " +
        $"Thinking = {Thinking}, ApiKey = {(ApiKey is null ? "null" : "***")} }}";
}
