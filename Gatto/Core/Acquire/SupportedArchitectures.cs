using System.Text.Json;

namespace Gatto.Core.Acquire;

//which architectures the pinned build cannot load, no positive lookup exists on purpose (Release lets a test catch a stale set)
internal sealed record SupportedArchitectures(
    string Release, string Generated, IReadOnlySet<string> Architectures)
{
    private const string ResourceName = "Gatto.Core.Acquire.llama-architectures.json";

    //false when the architecture is blank, the set is empty or the name is known, each is a refusal to claim (compare ordinal)
    public bool WillNotLoad(string? arch) =>
        Architectures.Count > 0
        && !string.IsNullOrWhiteSpace(arch)
        && !Architectures.Contains(arch);

    //read the embedded set, a missing resource throws (an empty set would disarm a user-facing warning)
    public static SupportedArchitectures Load()
    {
        using var s = typeof(SupportedArchitectures).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing — the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;

        var archs = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("architectures", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } name)
                    archs.Add(name);

        return new SupportedArchitectures(Str(root, "release"), Str(root, "generated"), archs);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
