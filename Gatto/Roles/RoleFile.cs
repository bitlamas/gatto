using System.Text.Json;
using Gatto.Core.Home;

namespace Gatto.Roles;

//the user's role file: the model, the gates, the discipline text and the thinking level, read from ~\.gatto\roles\<name>.json
public sealed record RoleFile(
    string Name,
    string? Model,
    string? Endpoint,
    IReadOnlyList<string> Gates,
    bool Checkpoints,
    string? Append,
    //null when the role file has no thinking key, since the default depends on the model, which the loader never sees
    ThinkingLevel? ThinkingRequested,
    JsonElement? Sampling)
{
    //one key for the model, and the endpoint kind decides what it means, so don't add a special case for the retired pack word
    private static readonly string[] Keys =
        { "model", "endpoint", "gates", "checkpoints", "append", "thinking", "sampling" };

    public static RoleFile Load(string rolesDir, string name)
    {
        var path = Path.Combine(rolesDir, $"{name}.json");
        if (!File.Exists(path))
            throw new GattoConfigException($"no role file at {path} — check the role name, or run: gatto doctor");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex) { throw new GattoConfigException($"role '{name}' at {path} is not valid JSON: {ex.Message}"); }

        if (root.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException($"role '{name}' at {path} must be a JSON object");

        foreach (var prop in root.EnumerateObject())
            if (!Keys.Contains(prop.Name))
                throw new GattoConfigException($"unknown key in role '{name}': {prop.Name}");

        var model = GetOptionalString(root, name, "model");
        var endpoint = GetOptionalString(root, name, "endpoint");

        var gates = new List<string>();
        if (root.TryGetProperty("gates", out var g))
        {
            if (g.ValueKind != JsonValueKind.Array)
                throw new GattoConfigException($"role '{name}' has a \"gates\" with the wrong type — must be an array of strings");
            foreach (var item in g.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new GattoConfigException($"role '{name}' has a non-string entry in \"gates\"");
                gates.Add(item.GetString()!);
            }
        }

        var checkpoints = false;
        if (root.TryGetProperty("checkpoints", out var cp))
        {
            if (cp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException($"role '{name}' has a \"checkpoints\" with the wrong type — must be true or false");
            checkpoints = cp.GetBoolean();
        }

        var append = GetOptionalString(root, name, "append");

        //null leaves the choice to Compose, since the default depends on the model and the loader cannot see it
        ThinkingLevel? thinking = null;
        if (root.TryGetProperty("thinking", out var th))
        {
            if (th.ValueKind != JsonValueKind.String)
                throw new GattoConfigException($"role '{name}' has a \"thinking\" with the wrong type — must be a string");
            thinking = Thinking.Parse(th.GetString());
        }

        JsonElement? sampling = null;
        if (root.TryGetProperty("sampling", out var sm))
        {
            if (sm.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException($"role '{name}' has a \"sampling\" with the wrong type — must be an object");
            HarnessManagedKeys.Check(sm, key =>
                $"role '{name}' \"sampling\" must not set \"{key}\" — it is managed by gatto");
            sampling = sm.Clone();   //clone, since the source document is disposed when the read returns
        }

        return new RoleFile(name, model, endpoint, gates, checkpoints, append, thinking, sampling);
    }

    //the override wins, then the role's own model, then gatto.json's default_model, shared so the launch and doctor agree
    public static string? EffectiveModel(RoleFile role, string? cliOverride, string? defaultModel) =>
        cliOverride ?? role.Model ?? defaultModel;

    //the .json stems in the roles folder, sorted case-insensitively, with a missing folder giving an empty list
    public static IReadOnlyList<string> ListNames(string rolesDir)
    {
        if (!Directory.Exists(rolesDir)) return Array.Empty<string>();
        return Directory.GetFiles(rolesDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetOptionalString(JsonElement root, string roleName, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String)
            throw new GattoConfigException($"role '{roleName}' has a \"{key}\" with the wrong type — must be a string");
        return v.GetString();
    }
}
