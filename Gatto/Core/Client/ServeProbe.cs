using System.Net.Http.Headers;
using System.Text.Json;

namespace Gatto.Core.Client;

//what llama-server has in memory now, the only trustworthy answer to which model gatto talks to (a missing field is unknown)
public sealed record LoadedModel(string? ModelPath, int? NCtx, ThinkCapability Capability = ThinkCapability.None,
    bool? Vision = null, string? BuildInfo = null);

//one GET {base_url}/props against llama-server
public static class ServeProbe
{
    //null on any failure, unknown rather than a mismatch (send apiKey, --api-key guards /props too)
    public static async Task<LoadedModel?> ProbeAsync(
        HttpClient http, string baseUrl, CancellationToken ct, string? apiKey = null)
    {
        try
        {
            var url = baseUrl.TrimEnd('/') + "/props";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            //a missing model_path leaves the field null rather than failing the probe, the n_ctx is still wanted
            var modelPath = root.TryGetProperty("model_path", out var mp) && mp.ValueKind == JsonValueKind.String
                ? mp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(modelPath)) modelPath = null;

            //n_ctx sits under default_generation_settings and is optional, failing the probe over a missing window would hide a real mismatch chip
            int? nCtx = root.TryGetProperty("default_generation_settings", out var dgs)
                && dgs.ValueKind == JsonValueKind.Object
                && dgs.TryGetProperty("n_ctx", out var nctx)
                && nctx.ValueKind == JsonValueKind.Number
                && nctx.TryGetInt32(out var n)
                ? n
                : null;

            bool? vision = root.TryGetProperty("modalities", out var mods)
                && mods.ValueKind == JsonValueKind.Object
                && mods.TryGetProperty("vision", out var vis)
                && vis.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? vis.GetBoolean()
                : null;

            //sniff the chat template for the reasoning switch, a missing one means no thinking capability
            var template = root.TryGetProperty("chat_template", out var tmpl) && tmpl.ValueKind == JsonValueKind.String
                ? tmpl.GetString()
                : null;

            //the binary that is serving, left null when the server does not report it so a mismatch chip survives
            var buildInfo = root.TryGetProperty("build_info", out var bi) && bi.ValueKind == JsonValueKind.String
                ? bi.GetString()
                : null;

            return new LoadedModel(modelPath, nCtx, ThinkCapabilitySniff.Of(template), vision,
                string.IsNullOrWhiteSpace(buildInfo) ? null : buildInfo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   //a genuine cancel propagates, everything else is unknown
        }
        catch (Exception)
        {
            return null;
        }
    }
}
