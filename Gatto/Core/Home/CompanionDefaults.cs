using System.Text.Json.Nodes;

namespace Gatto.Core.Home;

//the one reader and writer of an endpoint's saved model and effort: a contributed endpoint keeps them in its extension's companion file, a configured one in gatto.json
public static class CompanionDefaults
{
    public static string CompanionPath(string home, string owner) => new ExtensionSettings(home, owner).Path;

    //a null config is loaded only on the branch that reads gatto.json, so a caller with an owner never touches it
    public static string? Model(string home, GattoConfig? config, string endpoint, string? owner)
    {
        if (owner is not null) return ModelOf(Entry(new ExtensionSettings(home, owner), endpoint));
        config ??= GattoConfig.Load(home);
        if (config.Endpoints.ContainsKey(endpoint)) return config.ModelDefaultFor(endpoint);
        var found = Scan(home, endpoint);
        return found.Count == 1 ? found[0].Model : null;
    }

    public static string? Effort(string home, GattoConfig? config, string endpoint, string? owner, string model)
    {
        if (owner is not null)
            return Entry(new ExtensionSettings(home, owner), endpoint)?["effort"] is JsonObject effort
                   && effort[model] is JsonValue v && v.TryGetValue<string>(out var level) ? level : null;
        config ??= GattoConfig.Load(home);
        return config.Endpoints.ContainsKey(endpoint) ? config.EffortDefaultFor(endpoint, model) : null;
    }

    public static void SetModel(string home, string endpoint, string? owner, string modelId)
    {
        if (owner is not null)
        {
            new ExtensionSettings(home, owner).Mutate(root => WritableEntry(root, endpoint)["model"] = modelId);
            return;
        }
        RequireConfigured(home, endpoint);
        GattoConfigWriter.SetEndpointDefaultModel(home, endpoint, modelId);
    }

    public static void SetEffort(string home, string endpoint, string? owner, string modelId, string level)
    {
        if (owner is not null)
        {
            new ExtensionSettings(home, owner).Mutate(root =>
            {
                var entry = WritableEntry(root, endpoint);
                if (entry["effort"] is not JsonObject effort) entry["effort"] = effort = new JsonObject();
                effort[modelId] = level;
            });
            return;
        }
        RequireConfigured(home, endpoint);
        GattoConfigWriter.SetEndpointEffort(home, endpoint, modelId, level);
    }

    //every companion carrying defaults.<endpoint>, in ordinal file order, for a reader that knows no owner. a file that will not read is Broken's to report
    public static IReadOnlyList<(string File, string? Model)> Scan(string home, string endpoint)
    {
        var found = new List<(string, string?)>();
        foreach (var settings in Companions(home))
        {
            JsonObject root;
            try { root = settings.Read(); }
            catch (GattoConfigException) { continue; }
            if (root[ExtensionSettings.DefaultsKey] is not JsonObject defaults || !defaults.ContainsKey(endpoint)) continue;
            found.Add((settings.Path, ModelOf(Entry(settings, endpoint, root))));
        }
        return found;
    }

    //each companion that does not parse or is not an object, with the reader's own message, so doctor names what the launch would refuse on
    public static IReadOnlyList<(string File, string Problem)> Broken(string home)
    {
        var broken = new List<(string, string)>();
        foreach (var settings in Companions(home))
        {
            try { settings.Read(); }
            catch (GattoConfigException ex) { broken.Add((settings.Path, ex.Message)); }
        }
        return broken;
    }

    private static IEnumerable<ExtensionSettings> Companions(string home)
    {
        var dir = Path.Combine(home, "extensions");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => new ExtensionSettings(home, Path.GetFileNameWithoutExtension(f)));
    }

    private static JsonObject? Entry(ExtensionSettings settings, string endpoint, JsonObject? root = null)
    {
        root ??= settings.Read();
        if (root[ExtensionSettings.DefaultsKey] is not JsonObject defaults || !defaults.TryGetPropertyValue(endpoint, out var node) || node is null)
            return null;
        return node as JsonObject
               ?? throw new GattoConfigException($"{settings.Path} defaults.{endpoint} must be an object");
    }

    private static string? ModelOf(JsonObject? entry) =>
        entry?["model"] is JsonValue v && v.TryGetValue<string>(out var m) ? m : null;

    private static JsonObject WritableEntry(JsonObject root, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new GattoConfigException("endpoint name cannot be empty");
        if (root[ExtensionSettings.DefaultsKey] is not JsonObject defaults)
            root[ExtensionSettings.DefaultsKey] = defaults = new JsonObject();
        if (defaults[endpoint] is not JsonObject entry) defaults[endpoint] = entry = new JsonObject();
        return entry;
    }

    private static void RequireConfigured(string home, string endpoint)
    {
        if (!GattoConfig.Load(home).Endpoints.ContainsKey(endpoint))
            throw new GattoConfigException($"endpoint '{endpoint}' is not configured in gatto.json and no extension owns it here");
    }
}
