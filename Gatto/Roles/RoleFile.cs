using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Roles.Models;
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
    //the seat's whole tool list, null arms every tool the session arms and an empty list arms none
    IReadOnlyList<string>? Tools,
    int Turns,
    bool ContextFiles,
    IReadOnlyList<string>? Avatar,
    string? Tint,
    string? TintLight,
    //the opening line committed before the first request, refused when it holds a control token since a resume strips assistant records
    string? FirstMessage,
    //false keeps the role out of run_agent's list and refuses a call to it, the role still launches as a main seat
    bool Callable = true,
    //the one folder under the working folder that write_file and edit_file may write in, as the file wrote it, null for no limit
    string? WriteDir = null)
{
    public const int DefaultTurns = 10;

    //the file's colours parsed for the theme, null when it carries no tint, since a light colour alone tints nothing
    public (RgbColor Dark, RgbColor? Light)? ParsedTint() =>
        Tint is { } t ? (Theme.ParseTint(t), TintLight is { } l ? Theme.ParseTint(l) : null) : null;

    //one key for the model, and the endpoint kind decides what it means
    private static readonly string[] Keys =
        { "model", "endpoint", "gates", "raw", "append", "thinking", "description", "tools", "turns", "context_files", "avatar", "tint", "tint_light", "first_message", "callable", "write_dir" };

    //the gate names gatto knows, so a misspelt gate fails the load instead of arming nothing
    private static readonly string[] KnownGates = { CitationGate.Name };

    //the name of the seat with no role, which no file in roles\ can take
    public const string DefaultName = "default";

    private static RoleFile? _default;

    //the seat with no role, built in code with every key at its default, so it composes exactly what an empty role file composes
    public static RoleFile Default => _default ??= BuildDefault();

    private static RoleFile BuildDefault()
    {
        using var doc = JsonDocument.Parse("{}");
        return Read(doc.RootElement.Clone(), DefaultName);
    }

    //true for the seat with no role in any casing, the one name that never reaches the roles folder
    public static bool IsDefault(string? name) => string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase);

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
                if (!KnownGates.Contains(item.GetString()!, StringComparer.OrdinalIgnoreCase))
                    throw new GattoConfigException($"role '{name}' has {item.GetRawText()} in \"gates\" (known: {string.Join(", ", KnownGates)})");
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
                throw new GattoConfigException($"role '{name}' has a \"tools\" of {tl.GetRawText()}; must be an array of tool names");
            tools = new List<string>();
            foreach (var item in tl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !ToolRegistry.IsValidName(item.GetString()!))
                    throw new GattoConfigException($"role '{name}' has {item.GetRawText()} in \"tools\"; must be a tool name of a-z, 0-9 and _");
                if (tools.Contains(item.GetString()!))
                    throw new GattoConfigException($"role '{name}' has {item.GetRawText()} twice in \"tools\"");
                tools.Add(item.GetString()!);
            }
        }

        var callable = true;
        if (root.TryGetProperty("callable", out var ca))
        {
            if (ca.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException($"role '{name}' has a \"callable\" of {ca.GetRawText()}; must be true or false");
            callable = ca.GetBoolean();
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

        List<string>? avatar = null;
        if (root.TryGetProperty("avatar", out var ct))
        {
            if (ct.ValueKind != JsonValueKind.Array)
                throw new GattoConfigException($"role '{name}' has an \"avatar\" of {ct.GetRawText()}; must be an array of 1 to 40 rows");
            var rows = ct.EnumerateArray().ToList();
            if (rows.Count is < 1 or > 40)
                throw new GattoConfigException($"role '{name}' has an \"avatar\" of {rows.Count} rows; must be 1 to 40");
            avatar = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i].ValueKind == JsonValueKind.String ? rows[i].GetString()! : null;
                if (row is null || UnicodeWidth.Of(row) is < 1 or > 80 || row.Any(char.IsControl))
                    throw new GattoConfigException($"role '{name}' has an \"avatar\" row {i} of {rows[i].GetRawText()}; must be 1 to 80 cells with no control character");
                avatar.Add(row);
            }
        }

        var tint = GetTint(root, name, "tint");
        var tintLight = GetTint(root, name, "tint_light");

        var firstMessage = GetOptionalString(root, name, "first_message");
        if (firstMessage is not null && string.IsNullOrWhiteSpace(firstMessage))
            throw new GattoConfigException($"role '{name}' has a \"first_message\" of {root.GetProperty("first_message").GetRawText()}; must be a non-empty string");
        if (firstMessage is not null && ControlTokens.Strip(firstMessage).Count > 0)
            throw new GattoConfigException($"role '{name}' has a \"first_message\" holding a control token");

        //read by hand, so a number gets the same sentence and its raw value as a path that leaves the working folder
        string? writeDir = null;
        if (root.TryGetProperty("write_dir", out var wd) && wd.ValueKind != JsonValueKind.Null)
        {
            if (wd.ValueKind != JsonValueKind.String || !NamesAFolderInside(wd.GetString()!))
                throw new GattoConfigException($"role '{name}' has a \"write_dir\" of {wd.GetRawText()} (must name a folder inside the working folder)");
            writeDir = wd.GetString();
        }

        return new RoleFile(name, model, endpoint, gates, raw, append, thinking, description, tools, turns, contextFiles, avatar, tint, tintLight, firstMessage, callable, writeDir);
    }

    //relative, with no .. segment, no leading ~ the tools would read as the user's folder, and no character a folder name cannot hold. the test is by segment, so a..b is a name
    private static bool NamesAFolderInside(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
        var segments = value.Split('\\', '/');
        if (segments[0] == "~") return false;
        var invalid = Path.GetInvalidFileNameChars();
        return segments.All(s => s != ".." && s.IndexOfAny(invalid) < 0);
    }

    //the override wins, then the role's own model, then gatto.json's default_model, shared so the launch and doctor agree
    public static string? EffectiveModel(RoleFile role, string? cliOverride, string? defaultModel) =>
        cliOverride ?? role.Model ?? defaultModel;

    //the file in roles\ named default in any casing, as it is cased on disk, which is never loaded, or null
    public static string? DefaultFileOnDisk(string rolesDir) =>
        Directory.Exists(rolesDir)
            ? Directory.GetFiles(rolesDir, "*.json").Select(Path.GetFileName).FirstOrDefault(f => IsDefault(Path.GetFileNameWithoutExtension(f)))
            : null;

    //the .json stems in the roles folder but default, sorted case-insensitively, with a missing folder giving an empty list
    public static IReadOnlyList<string> ListNames(string rolesDir)
    {
        if (!Directory.Exists(rolesDir)) return Array.Empty<string>();
        return Directory.GetFiles(rolesDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !IsDefault(n))
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
