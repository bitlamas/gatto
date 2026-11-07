namespace Gatto.Core.Client;

//the server's own timings, one reader for every caller (dividing by the whole request would fold the prefill into a decode number)
internal static class ServerTimings
{
    //tokens per second summed over every reply that reported timings, an unreadable one counts the same as silence
    public static double? DecodeRate(IEnumerable<string?> timings)
    {
        double tokens = 0, ms = 0;
        foreach (var t in timings)
        {
            if (t is not { Length: > 0 }) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                if (doc.RootElement.TryGetProperty("predicted_n", out var n)
                    && doc.RootElement.TryGetProperty("predicted_ms", out var d)
                    && n.TryGetDouble(out var nv) && d.TryGetDouble(out var dv))
                {
                    tokens += nv;
                    ms += dv;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return ms > 0 && tokens > 0 ? tokens / (ms / 1000.0) : null;
    }

    //one prompt read whole, its count and rate. null unless the server says it reused nothing, a cached head would make the read look short and fast
    public static (int Tokens, double TokensPerSecond)? ColdPrompt(string? timings)
    {
        if (timings is not { Length: > 0 }) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(timings);
            var root = doc.RootElement;
            if (root.TryGetProperty("cache_n", out var c) && c.TryGetInt32(out var cached) && cached == 0
                && root.TryGetProperty("prompt_n", out var n) && n.TryGetInt32(out var tokens) && tokens > 0
                && root.TryGetProperty("prompt_ms", out var d) && d.TryGetDouble(out var ms) && ms > 0)
                return (tokens, tokens / (ms / 1000.0));
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }
}
