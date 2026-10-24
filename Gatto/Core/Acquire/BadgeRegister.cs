using System.Text.Json;

namespace Gatto.Core.Acquire;

//one audition of a file on this machine, its verdict and the tasks it fell short on, keyed by the GGUF file name and holding stamps rather than a score
internal sealed record Badge(string ModelKey, string? Sha256, bool Passed, IReadOnlyList<string> FellShort,
    DateOnly Measured, string GattoBuild, string SamplingNote, string? Server = null, string? RepoId = null);

//the user's own record, read side only. the last audition of a file here, a pass or a fail, and nothing a record does not hold
internal static class BadgeRegister
{
    //one JSON file per measurement, kept under ~\.gatto\audition
    public const string DirectoryName = "audition";

    //the record shape this reader accepts. an absent optional field means it does not apply rather than a fallback to an older format
    public const int Schema = 3;

    //the battery whose measurements count. a record from another battery measured other tasks, so it is not a badge for this one
    public const int Battery = 4;

    //the record for a model key, or null when nothing was measured. a malformed record is skipped, since one bad file must not take down a search
    public static Badge? Lookup(string homePath, string modelKey)
    {
        var path = Path.Combine(homePath, DirectoryName, FileNameFor(modelKey));
        return File.Exists(path) ? Read(path, modelKey) : null;
    }

    //one record to a badge, or null when it is not one, and the single home for what makes a record readable
    private static Badge? Read(string path, string fallbackKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var key = Str(root, "model_key") ?? fallbackKey;

            //the verdict key decides, so a record that states neither verdict is not a measurement
            if (Str(root, "verdict") is not { } verdict || verdict is not ("pass" or "fail")) return null;

            //a run on an older battery says nothing about these tasks, and a record with no battery number cannot say which it took
            if (!root.TryGetProperty("battery_version", out var battery) || battery.ValueKind != JsonValueKind.Number
                || !battery.TryGetInt32(out var took) || took != Battery)
                return null;

            if (Str(root, "measured") is not { } measuredText
                || !DateOnly.TryParse(measuredText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var measured))
                return null;   //a record with no readable date is not a measurement, so there is no badge

            var passed = verdict == "pass";
            return new Badge(key, Str(root, "sha256"), passed,
                //a failed run names nothing on the shelf, the audition report on disk holds its tasks
                passed ? FailedTasks(root) : [],
                measured, Str(root, "gatto_build") ?? "",
                //absent stays null rather than an empty string, which would read as a server we knew and could not name
                Str(root, "sampling_note") ?? "", Str(root, "server"),
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

    //the ids of the tasks that ran and failed, read off the record's own rows. a skipped row is not a failure, the model was never asked
    private static IReadOnlyList<string> FailedTasks(JsonElement root)
    {
        if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return [];
        return [.. tasks.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.Object
                && !(t.TryGetProperty("skipped", out var s) && s.ValueKind == JsonValueKind.True)
                && t.TryGetProperty("pass", out var p) && p.ValueKind == JsonValueKind.False)
            .Select(t => Str(t, "id"))
            .OfType<string>()];
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
