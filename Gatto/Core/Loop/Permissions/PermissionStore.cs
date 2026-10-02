using System.Text.Json;
using Gatto.Core.Home;

namespace Gatto.Core.Loop.Permissions;

//any chain metacharacter anywhere in a command forces a prompt. the guard is deliberately quote-unaware, an extra prompt is the safe way to err
public static class ShellChainingGuard
{
    private static readonly string[] ChainTokens = { ";", "|", "`", "$(", "&", ">", "<", "\n", "\r" };

    public static bool IsChained(string command)
    {
        foreach (var token in ChainTokens)
            if (command.Contains(token, StringComparison.Ordinal))
                return true;
        return false;
    }
}

//the three grant kinds the store holds, one per private list
public enum PermissionKind { ShellPrefix, WriteDir, Tool }

//one grant as /permissions shows it, the raw text as granted with no normalization so it matches what the user saw
public sealed record PermissionEntry(PermissionKind Kind, string Text);

//the standing grants of the folder gatto was launched in, held in the home so a repository can't bring its own. anything with no grant or an unreadable file prompts
public sealed class PermissionStore
{
    private readonly string _projectRoot;
    private readonly string? _path;
    private readonly List<string> _shellPrefixes = new();
    private readonly List<string> _writeDirs = new();
    private readonly List<string> _tools = new();
    private bool _wild;
    private bool _attachMany;

    //wild mode for this project, as set this session or persisted in the file
    public bool Wild => _wild;

    //the project's standing yes to attaching a batch of pasted images, a grant like the others
    public bool AttachMany => _attachMany;

    //a per-kind list property needs a caller first, the entries view is the only surface these lists need

    private PermissionStore(string projectRoot, string? path)
    {
        _projectRoot = projectRoot;
        _path = path;
    }

    //the root the store was loaded for, what a write grant inside the project is anchored to
    public string ProjectRoot => _projectRoot;

    //the file the store reads and writes, null for a store that lives in memory only
    public string? FilePath => _path;

    //where a project's grants live in the home, one file per launch folder under its project key
    public static string PathFor(string home, string projectRoot) =>
        Path.Combine(home, "permissions", ProjectKey.Of(projectRoot) + ".json");

    //a store with no file, for a run whose grants must never reach the user's home
    public static PermissionStore InMemory(string projectRoot) => new(projectRoot, null);

    //a missing file is silent, a corrupt one or one that records another folder gives an empty store and a warning, and Load never throws
    public static PermissionStore Load(string home, string projectRoot, out string? warning)
    {
        warning = null;
        var path = PathFor(home, projectRoot);
        var store = new PermissionStore(projectRoot, path);
        if (!File.Exists(path))
            return store;

        //adopt the fresh store only after every entry applied, and normalize inside the try so a NUL path fails closed
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("must be a JSON object");
            var prefixes = ReadStringArray(root, "shell_prefixes");
            var dirs = ReadStringArray(root, "write_dirs");
            var tools = ReadStringArray(root, "tools");
            var wild = ReadBool(root, "wild");
            var attachMany = ReadBool(root, "attach_many");

            //the key only names the file, so the folder it records must be this one. the shape is read first, a corrupt file says what is wrong with it
            var recorded = root.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (recorded != ProjectKey.PathOf(projectRoot))
                throw new FormatException($"it records the folder {recorded ?? "(none)"}, not this one");

            var loaded = new PermissionStore(projectRoot, path);
            foreach (var x in prefixes) loaded.AddShellPrefix(x);
            foreach (var d in dirs) loaded.AddWriteDir(d);
            foreach (var t in tools) loaded.AddTool(t);
            loaded._wild = wild;
            loaded._attachMany = attachMany;
            return loaded;
        }
        catch (Exception ex)
        {
            warning = $"permissions file {path} is unreadable ({ex.Message}); ignoring all grants";
            return store;   //a pristine empty store, any partial application is discarded
        }
    }

    //an absent key is false, any value but a boolean makes the whole file corrupt
    private static bool ReadBool(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"\"{key}\" must be a boolean");
        return v.GetBoolean();
    }

    //an absent key is an empty list, a wrong-shaped element throws so the whole file counts as corrupt
    private static List<string> ReadStringArray(JsonElement root, string key)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(key, out var arr))
            return result;
        if (arr.ValueKind != JsonValueKind.Array)
            throw new FormatException($"\"{key}\" must be an array of strings");
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new FormatException($"\"{key}\" must contain only strings");
            result.Add(item.GetString()!);
        }
        return result;
    }

    //a prefix grant is a whole-token authority, the character after the prefix must be whitespace or nothing, and a chained command never matches
    public bool AllowsShell(string command)
    {
        if (ShellChainingGuard.IsChained(command))
            return false;
        var trimmed = command.TrimStart();
        foreach (var prefix in _shellPrefixes)
            if (prefix.Length > 0 && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == prefix.Length || char.IsWhiteSpace(trimmed[prefix.Length])))
                return true;
        return false;
    }

    //the granted directory is matched with a trailing separator, so a grant on proj never covers proj2
    public bool AllowsWrite(string fullPath)
    {
        var normalized = TerminateWithSeparator(Path.GetFullPath(fullPath));
        foreach (var dir in _writeDirs)
        {
            var baseDir = TerminateWithSeparator(Path.GetFullPath(dir));
            if (normalized.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    //append the separator when it is missing, both sides terminated so the grant itself matches and a name-prefix sibling doesn't
    private static string TerminateWithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    public void GrantShellPrefix(string prefix, bool persist)
    {
        if (!AddShellPrefix(prefix))
            return;       //empty or duplicate, so there is nothing new to persist
        if (persist)
            Persist();
    }

    public void GrantWriteDir(string dir, bool persist)
    {
        if (!AddWriteDir(dir))
            return;
        if (persist)
            Persist();
    }

    //an exact match on the tool name, a grant on web_search covers only web_search
    public bool AllowsTool(string toolName) => _tools.Contains(toolName, StringComparer.Ordinal);

    public void GrantTool(string toolName, bool persist)
    {
        if (!AddTool(toolName))
            return;       //empty or duplicate, so there is nothing new to persist
        if (persist)
            Persist();
    }

    //sets wild for the session, and off is written by leaving the key out so the file stays minimal
    public void SetWild(bool on, bool persist)
    {
        _wild = on;
        if (persist) Persist();
    }

    //the standing yes to a batch of pasted images, always persisted, since the question it answers is asked once per project
    public void GrantAttachMany()
    {
        _attachMany = true;
        Persist();
    }

    //permissions listing and revoke

    //shell prefixes, then write dirs, then tools: the one order a listing and RevokeAt both number, so an index names the same entry in both
    public IReadOnlyList<PermissionEntry> ListEntries()
    {
        var entries = new List<PermissionEntry>(_shellPrefixes.Count + _writeDirs.Count + _tools.Count);
        foreach (var p in _shellPrefixes) entries.Add(new PermissionEntry(PermissionKind.ShellPrefix, p));
        foreach (var d in _writeDirs) entries.Add(new PermissionEntry(PermissionKind.WriteDir, d));
        foreach (var t in _tools) entries.Add(new PermissionEntry(PermissionKind.Tool, t));
        return entries;
    }

    //remove at this index, the exact slot the listing printed, so two entries that alias to one directory can't both go
    public PermissionEntry? RevokeAt(int index)
    {
        var entries = ListEntries();
        if (index < 0 || index >= entries.Count)
            return null;
        var entry = entries[index];
        switch (entry.Kind)
        {
            case PermissionKind.ShellPrefix:
                _shellPrefixes.RemoveAt(index);
                break;
            case PermissionKind.WriteDir:
                _writeDirs.RemoveAt(index - _shellPrefixes.Count);
                break;
            case PermissionKind.Tool:
                _tools.RemoveAt(index - _shellPrefixes.Count - _writeDirs.Count);
                break;
        }
        Persist();
        return entry;
    }

    //removal is index-based through RevokeAt only, so don't add a text-matching removal (the next reader would assume RevokeAt routes through it)

    //true if a new prefix was added, false for empty or duplicate (case-insensitive)
    private bool AddShellPrefix(string prefix)
    {
        var trimmed = prefix.Trim();
        if (trimmed.Length == 0)
            return false;
        if (_shellPrefixes.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;
        _shellPrefixes.Add(trimmed);
        return true;
    }

    //true if a new dir was added, false for empty or duplicate (compared case-insensitive and separator-terminated, the same as AllowsWrite)
    private bool AddWriteDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return false;
        var trimmed = dir.Trim();
        var full = TerminateWithSeparator(Path.GetFullPath(trimmed));
        if (_writeDirs.Any(d => string.Equals(TerminateWithSeparator(Path.GetFullPath(d)), full, StringComparison.OrdinalIgnoreCase)))
            return false;
        _writeDirs.Add(trimmed);
        return true;
    }

    //true if a new tool name was added, false for empty or duplicate (ordinal, the same casing rule as AllowsTool)
    private bool AddTool(string toolName)
    {
        var trimmed = toolName.Trim();
        if (trimmed.Length == 0)
            return false;
        if (_tools.Contains(trimmed, StringComparer.Ordinal))
            return false;
        _tools.Add(trimmed);
        return true;
    }

    //rewrite the whole file atomically, a temp write moved over the old one, so a failure mid-write leaves the previous file intact
    private void Persist()
    {
        if (_path is null) return;   //a store in memory keeps its grants for the run and writes nothing
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("project", ProjectKey.PathOf(_projectRoot));
            w.WriteStartArray("shell_prefixes");
            foreach (var p in _shellPrefixes) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("write_dirs");
            foreach (var d in _writeDirs) w.WriteStringValue(d);
            w.WriteEndArray();
            if (_tools.Count > 0)
            {
                //omit the key while the list is empty so a permissions file without it round-trips byte-identical
                w.WriteStartArray("tools");
                foreach (var t in _tools) w.WriteStringValue(t);
                w.WriteEndArray();
            }
            if (_wild) w.WriteBoolean("wild", true);
            if (_attachMany) w.WriteBoolean("attach_many", true);
            w.WriteEndObject();
        }

        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, _path, overwrite: true);
    }

    //the prefix to offer for a command, its first two whitespace-separated tokens, or the one token when there is only one
    public static string SuggestShellPrefix(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length switch
        {
            0 => string.Empty,
            1 => tokens[0],
            _ => $"{tokens[0]} {tokens[1]}",
        };
    }

    //true when the second token is flag-shaped, which gets no Always offer since the grant would cover every later use of that flag
    public static bool HasFlagShapedSecondToken(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 && (tokens[1][0] == '-' || tokens[1][0] == '/');
    }
}
