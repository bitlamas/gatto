using System.Text.Json;

namespace Gatto.Core.Client;

//which number a provider reports for a limit, a Label row carries none and is drawn as its label alone
public enum AllowanceShape { Percent, Remaining, UsedOfTotal, Label }

//one limit as the provider reported it, a null number is one it did not send and the footer draws a dash for it
public sealed record Allowance(
    string Label,
    AllowanceShape Shape,
    long? Used = null,
    long? Remaining = null,
    long? Total = null,
    int? UsedPercent = null,   //the share spent, never inverted by a reader
    DateTimeOffset? ResetAt = null);

//one model a provider lists live, with the effort levels and context sizes it offers for it
public sealed record ModelEntry(
    string Id,
    IReadOnlyList<string>? Efforts = null,
    string? DefaultEffort = null,
    IReadOnlyList<int>? Windows = null,
    int? DefaultWindow = null);

//the live model list an extension read at launch, the first entry is the one a launch with no -m takes
public sealed record ModelCatalogue(IReadOnlyList<ModelEntry> Entries)
{
    public IReadOnlyList<string> Ids => Entries.Select(e => e.Id).ToList();

    public ModelEntry? EntryOf(string id) => Entries.FirstOrDefault(e => e.Id == id);
}

//what a login handshake may do on screen, core supplies both so a script needs no process and no voice of its own
public sealed class LoginScreen(Action<string> say, Action<string> open)
{
    public void Say(string row) => say(row);

    public void Open(string url) => open(url);
}

//an endpoint's configuration, BaseUrl is null only for local and OpenAiCompatClient demands one, ApiKey and whatever Headers returns are its secrets
public sealed record EndpointConfig(
    string? BaseUrl,
    string? KeyEnv = null,
    int? Context = null,
    IReadOnlyDictionary<string, JsonElement?>? Thinking = null,
    string? ApiKey = null,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? Headers = null,
    Func<JsonElement, Allowance?>? Quota = null,
    IReadOnlyList<string>? Models = null,   //the model names the endpoint offers, the first is the one a launch with no -m takes
    string? Owner = null,   //the extension that contributed it, stamped by the host at commit and never by a script
    Func<LoginScreen, CancellationToken, Task<string>>? Login = null,   //runs the handshake and returns the account label
    Func<bool>? LoggedIn = null,
    Action? Logout = null,
    Func<int, string, CancellationToken, Task<bool>>? Retry = null,   //status and body of a refused request, true sends it once more
    Func<CancellationToken, Task<ModelCatalogue>>? Catalogue = null,
    Func<CancellationToken, Task<IReadOnlyList<Allowance>>>? Usage = null,
    Func<JsonElement, decimal?>? Cost = null)
{
    //redacted on purpose, the record's own ToString would print ApiKey into any log line or error message
    public override string ToString() =>
        $"EndpointConfig {{ BaseUrl = {BaseUrl}, KeyEnv = {KeyEnv}, Context = {Context}, " +
        $"Thinking = {Thinking}, ApiKey = {(ApiKey is null ? "null" : "***")}, " +
        $"Headers = {Set(Headers)}, Quota = {Set(Quota)}, " +
        $"Models = {(Models is null ? "null" : string.Join(",", Models))}, Owner = {Owner ?? "null"}, " +
        $"Login = {Set(Login)}, LoggedIn = {Set(LoggedIn)}, Logout = {Set(Logout)}, Retry = {Set(Retry)}, " +
        $"Catalogue = {Set(Catalogue)}, Usage = {Set(Usage)}, Cost = {Set(Cost)} }}";

    private static string Set(Delegate? d) => d is null ? "null" : "set";
}
