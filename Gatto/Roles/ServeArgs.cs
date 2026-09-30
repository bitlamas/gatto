using System.Globalization;
using System.Text.Json;

namespace Gatto.Roles;

//turns a model profile into llama-server argv, pure and deterministic, so nothing spawns or writes here
public static class ServeArgs
{
    //the sampling keys the server takes as serve-time flags, with the rest left to the request side
    private static readonly (string Key, string Flag)[] SamplingFlags =
    {
        ("temperature", "--temp"),
        ("top_p", "--top-p"),
        ("top_k", "--top-k"),
        ("min_p", "--min-p"),
    };

    //the file holding the model's api_key, written by the caller with tight permissions, required whenever the profile sets ApiKey
    public static IReadOnlyList<string> Compose(ModelProfile p, string? apiKeyFile = null)
    {
        var argv = new List<string>
        {
            "-m", p.ActivePath,
            "--port", p.Port.ToString(CultureInfo.InvariantCulture),
            "-c", p.Context.ToString(CultureInfo.InvariantCulture),
            //always pass --slots, since the prefill progress reads it and older builds do not default it on
            "--slots",
        };

        //a seed is passed only when the profile has one, otherwise the server picks its random default
        if (p.Seed is int seed)
            argv.AddRange(new[] { "--seed", seed.ToString(CultureInfo.InvariantCulture) });

        //the multimodal projector, passed as one argv element since the launcher quotes, and without it an image request fails with HTTP 500
        if (p.MmProj is not null)
            argv.AddRange(new[] { "--mmproj", p.MmProj });

        if (p.GpuLayers is int gpuLayers)
            argv.AddRange(new[] { "--n-gpu-layers", gpuLayers.ToString(CultureInfo.InvariantCulture) });

        if (p.CacheTypeK is not null)
            argv.AddRange(new[] { "-ctk", p.CacheTypeK });
        if (p.CacheTypeV is not null)
            argv.AddRange(new[] { "-ctv", p.CacheTypeV });

        if (p.Sampling is JsonElement sampling)
        {
            foreach (var (key, flag) in SamplingFlags)
            {
                if (!sampling.TryGetProperty(key, out var value)) continue;

                //a wrongly typed value is skipped, since a broken flag value would be a silent misconfiguration and Compose must never throw here
                if (value.ValueKind != JsonValueKind.Number) continue;

                argv.AddRange(new[] { flag, FormatNumber(value) });
            }
        }

        //bind loopback explicitly, before ExtraArgs, since the server takes the last --host and a model's own one must win
        argv.AddRange(new[] { "--host", "127.0.0.1" });

        //the key goes in a file, since the value form would sit in the process list. a missing path throws, so no caller can quietly put the key on argv
        if (p.ApiKey is { Length: > 0 })
        {
            if (string.IsNullOrEmpty(apiKeyFile))
                throw new InvalidOperationException(
                    "ServeArgs.Compose: this model sets api_key, so an apiKeyFile path is required — "
                    + "the key must never be composed onto argv");

            argv.AddRange(new[] { "--api-key-file", apiKeyFile });
        }

        argv.AddRange(p.ExtraArgs);

        return argv;
    }

    //a warning rather than a refusal, for a model that widens --host with no api_key, and the caller does the printing
    public static string? ExposureWarning(ModelProfile p)
    {
        if (p.ApiKey is { Length: > 0 }) return null;

        //the last --host is the one that binds, so scan for it, and only the two-token form can reach here
        string? host = null;
        for (var i = 0; i < p.ExtraArgs.Count; i++)
            if (p.ExtraArgs[i] is "--host" && i + 1 < p.ExtraArgs.Count)
                host = p.ExtraArgs[i + 1];

        if (host is null || IsLoopback(host)) return null;

        return $"warning: this model binds --host {host} and sets no \"api_key\", so the server accepts "
             + "unauthenticated requests from anything that can reach that address. Add \"api_key\" to the "
             + "model profile, or bind it back to 127.0.0.1.";
    }

    //an address this doesn't recognise counts as exposed, since a false warning costs one line and a missed one leaves a keyless server open
    private static bool IsLoopback(string host)
    {
        var h = host.Trim().Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (h == "::1") return true;
        return System.Net.IPAddress.TryParse(h, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    //invariant culture, since a comma-decimal machine would write 0,7 and the server would reject it
    private static string FormatNumber(JsonElement value) =>
        value.GetDouble().ToString(CultureInfo.InvariantCulture);
}
