using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Core.Acquire;

//what answered on a local port, the model ids are attacker text so sanitize at render (a null NCtx asks the user)
internal sealed record ConnectProbe(string BaseUrl, IReadOnlyList<string> Models, int? NCtx);

//the capability enumeration as data, each row says what is missing and what degrades without it (the wizard and doctor share this list)
internal sealed record ServerCapability(string Name, string IfAbsent);

//connecting to a server someone else runs is a probe only, no home path means it cannot write
internal static class ServerConnect
{
    //the ports tried in order, 1234 and 11434 are never named in copy and gatto's own port goes first
    public static readonly IReadOnlyList<int> ProbePorts = [1235, 8080, 1234, 11434];

    //the capability table, read by the wizard and by doctor

    //these sentences are shown to users, so say what the user loses and what happens instead
    public static readonly IReadOnlyList<ServerCapability> Capabilities =
    [
        new("OpenAI-compatible /v1/chat/completions with streaming + tool calls",
            //no em dash, and the wizard's check is the only screen that draws this sentence
            "gatto cannot talk to this server at all, it needs an OpenAI-compatible chat endpoint that streams."),
        new("/v1/models",
            "gatto cannot list what this server is serving, so it asks you to name the model instead"),
        new("/props (n_ctx)",
            "gatto cannot read this server's context window, so it asks you for one instead"),
        new("llama.cpp's exceed_context_size_error shape",
            "gatto cannot show this server's own too-much-context message; your conversation is still kept inside the context you set"),
    ];

    //try each port and stop at the first that answers, a null result means nothing is running (skip names the rungs already spent)
    public static async Task<ConnectProbe?> ProbeAsync(
        HttpClient http, CancellationToken ct, IReadOnlyList<int>? skip = null)
    {
        foreach (var port in ProbePorts)
        {
            if (skip is { Count: > 0 } spent && spent.Contains(port)) continue;
            ct.ThrowIfCancellationRequested();
            var baseUrl = $"http://127.0.0.1:{port}";

            var models = await TryListModelsAsync(http, baseUrl, ct).ConfigureAwait(false);
            if (models is null) continue;                 //silent port, try the next

            //best-effort context from ServeProbe, so no try/catch here (its contract is null on any failure)
            var nctx = (await ServeProbe.ProbeAsync(http, baseUrl, ct).ConfigureAwait(false))?.NCtx;

            return new ConnectProbe(baseUrl, models, nctx);
        }
        return null;
    }

    //the typed address, the ladder's body so the two can't drift on which read may fail
    public static async Task<ConnectProbe?> ProbeOneAsync(
        HttpClient http, string baseUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var trimmed = baseUrl.TrimEnd('/');

        var models = await TryListModelsAsync(http, trimmed, ct).ConfigureAwait(false);
        if (models is null) return null;

        var nctx = (await ServeProbe.ProbeAsync(http, trimmed, ct).ConfigureAwait(false))?.NCtx;
        return new ConnectProbe(trimmed, models, nctx);
    }

    //a server that answers with an empty list still counts as answering, the degradation is asking the user to name one
    private static async Task<IReadOnlyList<string>?> TryListModelsAsync(
        HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync($"{baseUrl}/v1/models", ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            var ids = new List<string>();
            foreach (var el in data.EnumerateArray())
                if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
            return ids;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return null;      //silence and nonsense are the same answer here, this port did not answer usefully
        }
    }
}
