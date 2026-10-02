using System.Text.Json;

namespace Gatto.Core.Home;

//a project file may only ever remove capability, and an unreadable file reads as no ruling while bad content throws
public static class ProjectFileConfig
{
    public const string FileName = ".gatto.json";

    //narrower than GattoConfig's arrays on purpose: syncing them would let a project pin settings the home file owns
    private static readonly string[] TopKeys = { "context_files", "memory", "auto_compact", "wild" };
    private static readonly string[] ContextFilesKeys = { "compat" };
    private static readonly string[] MemoryKeys = { "enabled" };

    //rendered from the three arrays so it can't part from them, and it must stay below them, since static initializers run in textual order
    private static readonly string Accepts =
        ".gatto.json accepts only " + string.Join(", ",
            TopKeys.Select(k => k switch
            {
                "context_files" => string.Join(", ", ContextFilesKeys.Select(s => $"context_files.{s}")),
                "memory" => string.Join(", ", MemoryKeys.Select(s => $"memory.{s}")),
                _ => k,
            }));

    //the compat this directory pins for itself and everything below it, or null for a missing, unreadable or empty file
    public static bool? TryReadCompat(string dir) =>
        TryReadFlag(dir, "context_files", ContextFilesKeys, "compat");

    //the memory.enabled ruling this directory pins for its subtree, or null when there's no ruling here. the cascade is folded by EffectiveMemoryEnabled
    public static bool? TryReadMemoryEnabled(string dir) =>
        TryReadFlag(dir, "memory", MemoryKeys, "enabled");

    //memory is on only when the configured value and every .gatto.json from the drive root down are on. a project file can only turn it off for its subtree
    public static bool EffectiveMemoryEnabled(string cwd, bool configured) =>
        EffectiveMemoryEnabled(cwd, configured, out _);

    //the same resolution, plus the path of the .gatto.json that turned memory off (null when nothing did). doctor shows it beside the state
    public static bool EffectiveMemoryEnabled(string cwd, bool configured, out string? disabledBy)
    {
        disabledBy = null;
        if (!configured) return false;   //memory is off already, so nothing below can turn it back on

        var dirs = AncestorsOf(cwd);
        //a false anywhere on the path keeps memory off, so a deeper true can't switch it back on
        var effective = true;
        foreach (var d in dirs)
        {
            if (TryReadMemoryEnabled(d) is not false) continue;
            effective = false;
            //keep the first file that turned memory off, the loop is deepest-first so that one is the nearest
            disabledBy ??= Path.Combine(d, FileName);
        }

        return effective;
    }

    //the auto_compact ruling this directory pins, or null when there is no ruling here. a top-level bare boolean, matching the home gatto.json key
    public static bool? TryReadAutoCompact(string dir)
    {
        if (ReadRoot(dir) is not { } root) return null;
        if (!root.TryGetProperty("auto_compact", out var value)) return null;

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GattoConfigException(
                $"{Path.Combine(dir, FileName)} has an auto_compact with the wrong type — must be a boolean");

        return value.GetBoolean();
    }

    //auto-compaction is on only when the configured value and every .gatto.json up the path are on. a project can only turn it off for its subtree
    public static bool EffectiveAutoCompact(string cwd, bool configured) =>
        EffectiveAutoCompact(cwd, configured, out _);

    //the same resolution, plus the nearest .gatto.json that turned auto-compaction off (null when nothing did)
    public static bool EffectiveAutoCompact(string cwd, bool configured, out string? disabledBy)
    {
        disabledBy = null;
        if (!configured) return false;   //auto-compaction is off already, so nothing below can turn it back on

        var effective = true;
        foreach (var d in AncestorsOf(cwd))
        {
            if (TryReadAutoCompact(d) is not false) continue;
            effective = false;
            //keep the first file that turned auto-compaction off, the loop is deepest-first so that one is the nearest
            disabledBy ??= Path.Combine(d, FileName);
        }

        return effective;
    }

    //the wild ruling this directory pins, or null when there is none. a top-level bare boolean like auto_compact
    public static bool? TryReadWild(string dir)
    {
        if (ReadRoot(dir) is not { } root) return null;
        if (!root.TryGetProperty("wild", out var value)) return null;

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GattoConfigException(
                $"{Path.Combine(dir, FileName)} has a wild with the wrong type, it must be a boolean");

        return value.GetBoolean();
    }

    //wild mode may be turned on only when no .gatto.json up the path sets wild false. a true there is read and ignored, since a project only takes capability away
    public static bool EffectiveWildAllowed(string cwd, out string? forbiddenBy)
    {
        forbiddenBy = null;
        foreach (var d in AncestorsOf(cwd))
        {
            if (TryReadWild(d) is not false) continue;
            //the walk is deepest-first, so the first file found is the nearest one and the one named
            forbiddenBy ??= Path.Combine(d, FileName);
        }
        return forbiddenBy is null;
    }

    //cwd first, then each parent up to the drive root. the order matters because the first file found is the one named as nearest
    private static IEnumerable<string> AncestorsOf(string cwd)
    {
        var dirs = new List<string>();
        string? dir = Path.GetFullPath(cwd);
        while (dir is not null)
        {
            dirs.Add(dir);
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dirs;
    }

    //the root element with the top-level key check applied, or null when there is nothing to read. keep that check here, a second copy would stop enforcing it
    private static JsonElement? ReadRoot(string dir)
    {
        var path = Path.Combine(dir, FileName);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            //a missing file and a locked file both mean nothing to read here, so don't throw
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;   //denied access is treated like no file, nothing to read here
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new GattoConfigException($"{path} is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException($"{path} must be a JSON object — {Accepts}");

        foreach (var prop in root.EnumerateObject())
            if (!TopKeys.Contains(prop.Name))
                throw new GattoConfigException($"unknown key in {path}: {prop.Name} — {Accepts}");

        return root;
    }

    private static bool? TryReadFlag(string dir, string section, string[] sectionKeys, string flag)
    {
        var path = Path.Combine(dir, FileName);

        //the read and the top-level key check both come from ReadRoot, one copy of the rejection keeps the schema closed
        if (ReadRoot(dir) is not { } root) return null;

        if (!root.TryGetProperty(section, out var sec))
            return null;   //the file is fine but has no such section, which is the same as no file

        if (sec.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException(
                $"{path} has a \"{section}\" with the wrong type — must be an object");

        foreach (var k in sec.EnumerateObject())
            if (!sectionKeys.Contains(k.Name))
                throw new GattoConfigException($"unknown key in {path}: {section}.{k.Name} — {Accepts}");

        if (!sec.TryGetProperty(flag, out var value))
            return null;   //the section is there but has no such flag, which pins nothing

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GattoConfigException(
                $"{path} has a {section}.{flag} with the wrong type — must be a boolean");

        return value.GetBoolean();
    }
}
