using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Home;
using Gatto.Roles.Agents;
using Gatto.Terminal;

namespace Gatto.Roles;

//the user's role file: the model, the gates, the discipline text and the thinking level, read from ~\.gatto\roles\<name>.json
public sealed record RoleFile(
    string Name,
    string? Model,
    string? Endpoint,
    IReadOnlyList<string> Gates,
    bool Raw,
    string? Append,
    //null when the role file has no thinking key, since the default depends on the model, which the loader never sees
    ThinkingLevel? ThinkingRequested,
    string? Description,
    //null arms the six built-ins when the role is called, an empty list arms none
    IReadOnlyList<string>? Tools,
    int Turns,
    bool ContextFiles,
    IReadOnlyList<string>? Cat,
    string? Tint,
    string? TintLight)
{
    public const int DefaultTurns = 10;

    //the file's colours parsed for the theme, null when it carries no tint, since a light colour alone tints nothing
    public (RgbColor Dark, RgbColor? Light)? ParsedTint() =>
        Tint is { } t ? (Theme.ParseTint(t), TintLight is { } l ? Theme.ParseTint(l) : null) : null;

    //one key for the model, and the endpoint kind decides what it means. sampling belongs to the model now, so a role's sampling key is accepted and never read
    private static readonly string[] Keys =
        { "model", "endpoint", "gates", "raw", "append", "thinking", "sampling", "description", "tools", "turns", "context_files", "cat", "tint", "tint_light" };

    //copied once, since Builtins allocates its factories on every call and builds no tool
    private static readonly HashSet<string> CallableTools = new(SubagentWiring.Builtins().Keys, StringComparer.Ordinal);
    private static readonly string CallableToolList = string.Join(", ", SubagentWiring.Builtins().Keys);

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

        //a lone surrogate escape parses as JSON and throws only when read, so it gets a load sentence too
        try { return Read(root, name); }
        catch (InvalidOperationException) { throw new GattoConfigException($"role '{name}' at {path} holds text that is not valid UTF-16"); }
    }

    private static RoleFile Read(JsonElement root, string name)
    {
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

        var raw = false;
        if (root.TryGetProperty("raw", out var rw))
        {
            if (rw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException($"role '{name}' has a \"raw\" with the wrong type — must be true or false");
            raw = rw.GetBoolean();
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

        string? description = null;
        if (root.TryGetProperty("description", out var de) && de.ValueKind != JsonValueKind.Null)
        {
            if (de.ValueKind != JsonValueKind.String)
                throw new GattoConfigException($"role '{name}' has a \"description\" of {de.GetRawText()}; must be a string");
            description = de.GetString()!;
            if (description.Contains('\n') || description.Contains('\r'))
                throw new GattoConfigException($"role '{name}' has a \"description\" of {de.GetRawText()}; must be one line");
        }

        List<string>? tools = null;
        if (root.TryGetProperty("tools", out var tl))
        {
            if (tl.ValueKind != JsonValueKind.Array)
                throw new GattoConfigException($"role '{name}' has a \"tools\" of {tl.GetRawText()}; must be an array of {CallableToolList}");
            tools = new List<string>();
            foreach (var item in tl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !CallableTools.Contains(item.GetString()!))
                    throw new GattoConfigException($"role '{name}' has {item.GetRawText()} in \"tools\"; must be one of {CallableToolList}");
                if (tools.Contains(item.GetString()!))
                    throw new GattoConfigException($"role '{name}' has {item.GetRawText()} twice in \"tools\"");
                tools.Add(item.GetString()!);
            }
        }

        var turns = DefaultTurns;
        if (root.TryGetProperty("turns", out var tu))
        {
            if (tu.ValueKind != JsonValueKind.Number || !tu.TryGetInt32(out turns) || turns < 1)
                throw new GattoConfigException($"role '{name}' has a \"turns\" of {tu.GetRawText()}; must be a whole number of 1 or more");
        }

        var contextFiles = true;
        if (root.TryGetProperty("context_files", out var cf))
        {
            if (cf.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException($"role '{name}' has a \"context_files\" of {cf.GetRawText()}; must be true or false");
            contextFiles = cf.GetBoolean();
        }

        List<string>? cat = null;
        if (root.TryGetProperty("cat", out var ct))
        {
            if (ct.ValueKind != JsonValueKind.Array)
                throw new GattoConfigException($"role '{name}' has a \"cat\" of {ct.GetRawText()}; must be an array of 1 to 6 rows");
            var rows = ct.EnumerateArray().ToList();
            if (rows.Count is < 1 or > 6)
                throw new GattoConfigException($"role '{name}' has a \"cat\" of {rows.Count} rows; must be 1 to 6");
            cat = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i].ValueKind == JsonValueKind.String ? rows[i].GetString()! : null;
                if (row is null || UnicodeWidth.Of(row) is < 1 or > 16 || row.Any(char.IsControl))
                    throw new GattoConfigException($"role '{name}' has a \"cat\" row {i} of {rows[i].GetRawText()}; must be 1 to 16 cells with no control character");
                cat.Add(row);
            }
        }

        var tint = GetTint(root, name, "tint");
        var tintLight = GetTint(root, name, "tint_light");

        return new RoleFile(name, model, endpoint, gates, raw, append, thinking, description, tools, turns, contextFiles, cat, tint, tintLight);
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

    //the hex the theme parses later, refused here so a bad colour fails the load with the file and the key
    private static string? GetTint(JsonElement root, string roleName, string key)
    {
        var value = GetOptionalString(root, roleName, key);
        if (value is not null && !Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}\z"))
            throw new GattoConfigException($"role '{roleName}' has a \"{key}\" of {root.GetProperty(key).GetRawText()}; must be #rrggbb");
        return value;
    }

    private static string? GetOptionalString(JsonElement root, string roleName, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String)
            throw new GattoConfigException($"role '{roleName}' has a \"{key}\" with the wrong type — must be a string");
        return v.GetString();
    }
}
