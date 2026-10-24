using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core;

namespace Gatto.Roles.Models;

//the sha256 of a file with the size and UTC write time it was taken over. a stat that still reads both is what lets a later reader trust the hash
internal sealed record FileKey(string Sha256, long Size, DateTime Written)
{
    //stats the file where it now lies, so a key taken after a move names the bytes at their final path
    public static FileKey Of(string sha256, string path)
    {
        var info = new FileInfo(path);
        return new FileKey(sha256.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc);
    }
}

//edit the profile as a document so the user's other keys keep their shape, and re-load through Model.Load first so the schema refuses early
internal static class ModelFiles
{
    //match by quant label, the screen sorts files lightest-first while the profile keeps insertion order. setting the already-active one succeeds as a no-op
    public static void SetActive(string modelsDir, string id, string quant)
    {
        var (path, root, files) = Open(modelsDir, id);

        if (NoSetActive(files, id, quant) is { } why) throw new GattoConfigException(why);

        foreach (var f in files)
            f!["active"] = Matches(f, quant);

        Save(path, root);
    }

    //remove from the list only, the file stays on disk. never the active one or the last one: the profile would stop loading, or the model would be gone
    public static void Remove(string modelsDir, string id, string quant)
    {
        var (path, root, files) = Open(modelsDir, id);

        if (NoRemove(files, id, quant) is { } why) throw new GattoConfigException(why);

        files.Remove(files.First(f => Matches(f, quant)));
        Save(path, root);
    }

    //the new file joins inactive, a later question makes it active. a quant label missing or already taken is refused, commands address files by label
    public static void Add(string modelsDir, string id, string ggufPath, string? sha256 = null)
    {
        var resolved = System.IO.Path.GetFullPath(ggufPath);
        var (path, root, files) = Open(modelsDir, id);

        if (files.Any(f => string.Equals(f?["path"]?.GetValue<string>(), resolved,
                StringComparison.OrdinalIgnoreCase)))
            throw new GattoConfigException(
                $"model '{id}' already lists {System.IO.Path.GetFileName(resolved)}");

        if (Gatto.Core.Acquire.QuantToken.Of(resolved) is not { Length: > 0 } quant)
            throw new GattoConfigException(
                $"{System.IO.Path.GetFileName(resolved)} carries no quant in its name, so '{id}' "
                + "would list a file no command could name — rename it or set it up on its own");

        if (files.Any(f => Matches(f, quant)))
            throw new GattoConfigException(
                $"model '{id}' already has a file called '{quant}' — it holds {Names(files)}");

        var entry = new JsonObject { ["path"] = resolved, ["quant"] = quant, ["active"] = false };
        if (sha256 is { Length: > 0 } hash) Write(entry, FileKey.Of(hash, resolved));
        files.Add(entry);
        Save(path, root);
    }

    //the audition's write-back: the three keys on the entry naming the file, false when no entry names it
    public static bool RecordKey(string modelsDir, string id, string filePath, FileKey key)
    {
        var resolved = System.IO.Path.GetFullPath(filePath);
        var (path, root, files) = Open(modelsDir, id);
        if (files.FirstOrDefault(f => string.Equals(f?["path"]?.GetValue<string>(), resolved,
                StringComparison.OrdinalIgnoreCase)) is not JsonObject entry)
            return false;

        Write(entry, key);
        Save(path, root);
        return true;
    }

    //written through JsonObject as a DateTime, which keeps every tick, since a key whose time lost precision would never match its file again
    internal static void Write(JsonObject entry, FileKey key)
    {
        entry["sha256"] = key.Sha256;
        entry["size"] = key.Size;
        entry["written"] = DateTime.SpecifyKind(key.Written, DateTimeKind.Utc);
    }

    //the hash only while the file still has the size and write time it was taken over. one stat and no read, and every reader of the key goes through it
    public static string? LiveKey(ModelFile entry)
    {
        if (entry is not { Sha256: { Length: > 0 } hash, Size: { } size, Written: { } written }) return null;
        try
        {
            var info = new FileInfo(entry.Path);
            return info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == written.ToUniversalTime().Ticks
                ? hash : null;
        }
        catch (Exception) { return null; }   //a path the stat cannot read is a key nobody can check
    }

    //the live key of the entry naming the file in one model's profile, null when the profile does not load or names no such file
    public static string? LiveKey(string modelsDir, string id, string filePath)
    {
        try
        {
            var resolved = System.IO.Path.GetFullPath(filePath);
            return Model.Load(modelsDir, id).Profile.Files
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFullPath(f.Path), resolved, StringComparison.OrdinalIgnoreCase))
                is { } entry ? LiveKey(entry) : null;
        }
        catch (Exception) { return null; }   //a broken profile shows no mark, the shelf is not where it gets reported
    }

    //ask the mutator's rules without mutating, so a refusal comes before a consent screen. a null here is no promise, the mutator re-checks
    public static string? WhyNotSetActive(string modelsDir, string id, string quant) =>
        NoSetActive(Open(modelsDir, id).Files, id, quant);

    //same as WhyNotSetActive, for the remove side
    public static string? WhyNotRemove(string modelsDir, string id, string quant) =>
        NoRemove(Open(modelsDir, id).Files, id, quant);

    private static string? NoSetActive(JsonArray files, string id, string quant) =>
        files.Any(f => Matches(f, quant)) ? null : NoSuchFile(files, id, quant);

    private static string? NoRemove(JsonArray files, string id, string quant)
    {
        if (files.FirstOrDefault(f => Matches(f, quant)) is not { } target)
            return NoSuchFile(files, id, quant);

        if (files.Count == 1)
            return $"'{quant}' is the only file model '{id}' has — removing it would leave no model, "
                + "so remove the model itself instead";

        if (target["active"]?.GetValue<bool>() == true)
            return $"'{quant}' is the file model '{id}' is set to use — switch to another one first, "
                + "then remove it";

        return null;
    }

    private static string NoSuchFile(JsonArray files, string id, string quant) =>
        $"model '{id}' has no file called '{quant}' — it holds {Names(files)}";

    //path, root and files array for editing, the loader vets the profile first
    private static (string Path, JsonNode Root, JsonArray Files) Open(string modelsDir, string id)
    {
        Model.Load(modelsDir, id);   //the loader's refusals run here, reused rather than copied

        var path = System.IO.Path.Combine(modelsDir, id, "profile.json");
        var root = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new GattoConfigException($"model '{id}' profile at {path} is empty");
        return (path, root, root["files"]!.AsArray());
    }

    private static bool Matches(JsonNode? file, string quant) =>
        string.Equals(file?["quant"]?.GetValue<string>(), quant, StringComparison.OrdinalIgnoreCase);

    //the labels the model holds, so a refusal can show the options
    private static string Names(JsonArray files) =>
        string.Join(", ", files.Select(f => f?["quant"]?.GetValue<string>() ?? "(unlabelled)"));

    //write a temp file and move it, a half-written profile is a model that no longer loads
    private static void Save(string path, JsonNode root)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
