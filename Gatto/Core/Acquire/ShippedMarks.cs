using System.Text.Json;

namespace Gatto.Core.Acquire;

//one file gatto tested itself, keyed by the sha256 of its bytes. the tasks it fell short on come from the row, never recounted here
internal sealed record ShippedMark(string Sha256, string File, string? RepoId, int Runs, int Passed,
    IReadOnlyList<string> FellShort);

//the register of files gatto tested, shipped inside the build. it is written whole by the seeder from the lab's runs and never by hand
internal static class ShippedMarks
{
    private const string ResourceName = "Gatto.Core.Acquire.tool-use-marks.json";

    //parsed once per process, lazily as the families list is, so a missing resource throws where the caller can see it
    private static readonly Lazy<IReadOnlyDictionary<string, ShippedMark>> Cached = new(Load, isThreadSafe: true);

    //the row for a file's sha256, or null when gatto did not test those bytes. a null hash is a file gatto cannot match
    public static ShippedMark? Lookup(string? sha256) =>
        sha256 is { Length: > 0 } key && Cached.Value.TryGetValue(key, out var row) ? row : null;

    private static IReadOnlyDictionary<string, ShippedMark> Load()
    {
        using var s = typeof(ShippedMarks).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing, the Gatto.csproj EmbeddedResource entry is load-bearing");
        return Parse(s);
    }

    //a register from another battery measured other tasks, so every one of its rows reads as untested
    internal static IReadOnlyDictionary<string, ShippedMark> Parse(Stream s)
    {
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var rows = new Dictionary<string, ShippedMark>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("battery", out var battery) || !battery.TryGetInt32(out var took)
            || took != BadgeRegister.Battery)
            return rows;

        if (root.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var r in list.EnumerateArray())
            {
                if (Str(r, "sha256") is not { Length: > 0 } sha || Str(r, "file") is not { } file) continue;
                IReadOnlyList<string> fellShort = r.TryGetProperty("fell_short", out var f) && f.ValueKind == JsonValueKind.Array
                    ? [.. f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                    : Array.Empty<string>();
                rows[sha] = new ShippedMark(sha, file, Str(r, "repo_id"), Int(r, "runs"), Int(r, "passed"), fellShort);
            }
        return rows;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
}
