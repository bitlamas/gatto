using System.Text.Json;

namespace Gatto.Core.Client;

//what a provider reported as left on a limit, the label names the bucket it charged and the total is null when the provider sends none
public sealed record QuotaReading(long Remaining, string Label, long? Total = null);

//an endpoint's configuration, BaseUrl is null only for local and OpenAiCompatClient demands one, ApiKey and whatever Headers returns are its secrets
public sealed record EndpointConfig(
    string? BaseUrl,
    string? KeyEnv = null,
    int? Context = null,
    IReadOnlyDictionary<string, JsonElement?>? Thinking = null,
    string? ApiKey = null,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? Headers = null,
    Func<JsonElement, QuotaReading?>? Quota = null,
    IReadOnlyList<string>? Models = null)   //the model names the endpoint offers, the first is the one a launch with no -m takes
{
    //redacted on purpose, the record's own ToString would print ApiKey into any log line or error message
    public override string ToString() =>
        $"EndpointConfig {{ BaseUrl = {BaseUrl}, KeyEnv = {KeyEnv}, Context = {Context}, " +
        $"Thinking = {Thinking}, ApiKey = {(ApiKey is null ? "null" : "***")}, " +
        $"Headers = {(Headers is null ? "null" : "set")}, Quota = {(Quota is null ? "null" : "set")}, " +
        $"Models = {(Models is null ? "null" : string.Join(",", Models))} }}";
}
