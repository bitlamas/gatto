using System.Text.Json;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//the tensors the pinned engine keeps off the GPU, by architecture, a list of data so a new architecture is a row
internal sealed record StreamedTensors(
    string Release, string Reviewed, IReadOnlyDictionary<string, IReadOnlySet<string>> Architectures)
{
    private const string ResourceName = "Gatto.Core.Acquire.streamed-tensors.json";

    private static readonly Lazy<StreamedTensors> Cached = new(Read);

    public static StreamedTensors Load() => Cached.Value;

    //every streamed tensor name of every listed architecture, for a local file whose own header already says whether it has a table
    public IReadOnlySet<string> Names { get; } = Architectures.Values.SelectMany(n => n).ToHashSet(StringComparer.Ordinal);

    //whether files of this architecture hold a streamed table, which the Hub shelf asks before it reads any header
    public bool Streams(string? architecture) => architecture is { Length: > 0 } a && Architectures.ContainsKey(a);

    //bytes of every named tensor across the tables, or null when any table is missing
    public long? BytesIn(IEnumerable<GgufTensors?> tables)
    {
        long total = 0;
        foreach (var t in tables)
        {
            if (t is null) return null;
            foreach (var e in t.Entries)
                if (Names.Contains(e.Name)) total = checked(total + e.Bytes);
        }
        return total;
    }

    //a missing resource is a build error surfaced at runtime, since an empty list would silently price every file whole
    private static StreamedTensors Read()
    {
        using var s = typeof(StreamedTensors).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing, and the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var archs = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("architectures", out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var a in obj.EnumerateObject())
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (a.Value.ValueKind == JsonValueKind.Array)
                    foreach (var n in a.Value.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
                            names.Add(name);
                if (a.Name.Length > 0 && names.Count > 0) archs[a.Name] = names;
            }
        return new StreamedTensors(Str(root, "release"), Str(root, "reviewed"), archs);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
