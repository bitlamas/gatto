using System.Net.Http.Headers;
using System.Text.Json;

namespace Gatto.Core.Client;

//one slot's counters from /slots, n_prompt_tokens is a running count that equals processed so there is no prompt total
public readonly record struct SlotProgress(long PromptTotal, long PromptProcessed, long Decoded, bool Processing);

//the processing slot's progress behind an interface, so the ticker's poller is tested without HTTP
public interface ISlotsReader
{
    Task<SlotProgress?> ReadAsync(CancellationToken ct);
}

//the live reader for /slots, null on any failure so the ticker keeps its own estimate (send apiKey, --api-key guards /slots too)
public sealed class HttpSlotsReader(HttpClient http, string baseUrl, string? apiKey = null) : ISlotsReader
{
    public async Task<SlotProgress?> ReadAsync(CancellationToken ct)
    {
        try
        {
            var url = baseUrl.TrimEnd('/') + "/slots";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            JsonElement? best = null;
            var bestPrompt = -1L;
            foreach (var slot in doc.RootElement.EnumerateArray())
            {
                if (slot.ValueKind != JsonValueKind.Object) continue;
                if (!(slot.TryGetProperty("is_processing", out var ip) && ip.ValueKind == JsonValueKind.True)) continue;
                var total = LongOf(slot, "n_prompt_tokens");
                if (total > bestPrompt) { bestPrompt = total; best = slot; }
            }
            if (best is not { } b) return null;   //nothing is processing right now

            return new SlotProgress(
                LongOf(b, "n_prompt_tokens"),
                LongOf(b, "n_prompt_tokens_processed"),
                DecodedOf(b),
                Processing: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static long LongOf(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    //the decode count lives at next_token[0].n_decoded
    private static long DecodedOf(JsonElement slot) =>
        slot.TryGetProperty("next_token", out var nt) && nt.ValueKind == JsonValueKind.Array
            && nt.GetArrayLength() > 0 && nt[0].ValueKind == JsonValueKind.Object
            ? LongOf(nt[0], "n_decoded")
            : 0;
}
