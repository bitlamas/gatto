using System.Text.Json;

namespace Gatto.Core.Acquire;

//a deny-set, since an allow-set would hide every tag the Hub invents next. llama.cpp loads these architectures, so the supported set cannot catch them
internal sealed record ModelKinds(
    string Reviewed, IReadOnlySet<string> UnservableKinds, IReadOnlySet<string> EmbeddingArchitectures,
    IReadOnlySet<string> SidecarLeading, IReadOnlySet<string> SidecarAnywhere)
{
    private const string ResourceName = "Gatto.Core.Acquire.model-kinds.json";

    //three signals, each with rows only it catches. causal counts only when it is false, since null and true are silence
    public bool WillNotServe(string? pipelineTag, bool? causal, string? arch) =>
        (pipelineTag is { Length: > 0 } t && UnservableKinds.Contains(t))
        || causal is false
        || (arch is { Length: > 0 } a && EmbeddingArchitectures.Contains(a));

    //a missing embedded resource throws, since silently empty sets would disarm the exclusion
    public static ModelKinds Load()
    {
        using var s = typeof(ModelKinds).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing — the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var reviewed = doc.RootElement.TryGetProperty("reviewed", out var r) ? r.GetString() ?? "" : "";
        return new ModelKinds(reviewed, Set(doc, "unservable_kinds"), Set(doc, "embedding_architectures"),
            Set(doc, "file_sidecar_leading"), Set(doc, "file_sidecar_anywhere"));

        static IReadOnlySet<string> Set(JsonDocument doc, string name)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } v)
                        set.Add(v);
            return set;
        }
    }
}
