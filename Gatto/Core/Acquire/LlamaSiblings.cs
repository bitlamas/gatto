using System.Text.Json;

namespace Gatto.Core.Acquire;

//the --version banner is identical on every tool in a release, so only known sibling names are refused. the list and the pinned release bump in one commit
internal sealed record LlamaSiblings(
    string Release, string Generated, IReadOnlySet<string> Executables)
{
    private const string ResourceName = "Gatto.Core.Acquire.llama-siblings.json";

    //the name of the one program gatto drives, kept here because the sibling list is the release minus this and the two must agree
    public const string ServerName = "llama-server.exe";

    //true only for a name on the list, compared case-insensitively on the file name alone. a renamed server is not a sibling
    public bool IsSibling(string? fileName) =>
        fileName is { Length: > 0 } && Executables.Contains(fileName);

    //a missing embedded resource throws, since an empty list would disarm the refusal of known siblings
    public static LlamaSiblings Load()
    {
        using var s = typeof(LlamaSiblings).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing — the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("executables", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } name)
                    names.Add(name);

        return new LlamaSiblings(Str(root, "release"), Str(root, "generated"), names);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
