using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core.Home;

namespace Gatto.Roles;

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
    public static void Add(string modelsDir, string id, string ggufPath)
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
        files.Add(entry);
        Save(path, root);
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
