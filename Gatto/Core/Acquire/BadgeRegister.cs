using System.Text.Json;

namespace Gatto.Core.Acquire;

//one measurement we took, holding the date and the stamps rather than a score. the model key is the GGUF file name with no path
internal sealed record Badge(string ModelKey, DateOnly Measured, string GattoBuild, string SamplingNote,
    int Passed = 0, int Ran = 0, int Unfinished = 0, string? Server = null, string? RepoId = null);

//the badge register, read side only. a badge only ever comes from a record on disk, and a stale one is shown with its date
internal static class BadgeRegister
{
    //one JSON file per measurement, kept under ~\.gatto\audition
    public const string DirectoryName = "audition";

    //the record shape this reader accepts. an absent optional field means it does not apply rather than a fallback to an older format
    public const int Schema = 3;

    //the battery whose measurements count. a record from another battery measured other tasks, so it is not a badge for this one
    public const int Battery = 4;

    //the badge for a model key, or null when nothing was measured. a malformed record is skipped, since one bad file must not take down a search
    public static Badge? Lookup(string homePath, string modelKey)
    {
        var path = Path.Combine(homePath, DirectoryName, FileNameFor(modelKey));
        return File.Exists(path) ? Read(path, modelKey) : null;
    }

    //the badge for a hub repo, matched by field since a repo id cannot be a path. it reads through the same Read as Lookup
    public static Badge? LookupByRepoId(string homePath, string repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return null;

        var dir = Path.Combine(homePath, DirectoryName);
        string[] files;
        try { files = Directory.GetFiles(dir, "*.json"); }
        catch (Exception) { return null; }   //no register yet is the normal state rather than an error

        //sorted, so two records naming one repo answer the same way on every run
        foreach (var path in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            if (Read(path, fallbackKey: "") is { RepoId: { } id } badge
                && string.Equals(id, repoId, StringComparison.OrdinalIgnoreCase))
                return badge;

        return null;
    }

    //one record to a badge, or null when it is not one, and the single home for what makes a badge valid
    private static Badge? Read(string path, string fallbackKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var key = Str(root, "model_key") ?? fallbackKey;

            //a record that does not assert a pass is not a badge. the clause lives on the reader, so it holds whatever the writer omits
            if (Str(root, "verdict") != "pass") return null;

            //a pass on an older battery says nothing about these tasks, and a record with no battery number cannot say which it took
            if (!root.TryGetProperty("battery_version", out var battery) || battery.ValueKind != JsonValueKind.Number
                || !battery.TryGetInt32(out var took) || took != Battery)
                return null;

            if (Str(root, "measured") is not { } measuredText
                || !DateOnly.TryParse(measuredText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var measured))
                return null;   //a record with no readable date is not a measurement, so there is no badge

            var (passed, ran, unfinished) = Score(root);
            return new Badge(key, measured, Str(root, "gatto_build") ?? "",
                //absent stays null rather than an empty string, which would read as a server we knew and could not name
                Str(root, "sampling_note") ?? "", passed, ran, unfinished, Str(root, "server"),
                //null when the model did not come through the hub, since the field does not apply rather than being missing
                Str(root, "repo_id"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    //org/name maps to org__name.json and other path-hostile characters are replaced, since the name comes from attacker text
    public static string FileNameFor(string modelKey)
    {
        var mapped = modelKey.Replace("/", "__", StringComparison.Ordinal);
        var safe = new string(mapped.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return (safe.Length > 0 ? safe : "unknown") + ".json";
    }

    //the margin, counted off the record's own per-task rows so a stored number cannot drift from the evidence under it

    //the two shapes that mean gatto stopped the task. strings rather than the enum, since Core must not reference Roles, and Runaway is not here
    private static readonly string[] CutShortShapes = ["StoppedAtCap", "Stalled"];

    private static (int Passed, int Ran, int Unfinished) Score(JsonElement root)
    {
        if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return (0, 0, 0);

        var passed = 0;
        var ran = 0;
        var unfinished = 0;
        foreach (var t in tasks.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            //a skipped row is in neither count, it records only that it was skipped
            if (t.TryGetProperty("skipped", out var s) && s.ValueKind == JsonValueKind.True) continue;
            ran++;
            if (t.TryGetProperty("pass", out var p) && p.ValueKind == JsonValueKind.True) { passed++; continue; }

            //a failed task only, since a pass that also has a shape would make the unfinished count disagree with the score
            if (t.TryGetProperty("shapes", out var shapes) && shapes.ValueKind == JsonValueKind.Array
                && shapes.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String
                    && CutShortShapes.Contains(x.GetString(), StringComparer.Ordinal)))
                unfinished++;
        }
        return (passed, ran, unfinished);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
