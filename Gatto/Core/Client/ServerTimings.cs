namespace Gatto.Core.Client;

//the server's own decode numbers, one reader for every caller (dividing by the whole request would fold the prefill into a decode number)
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
}
