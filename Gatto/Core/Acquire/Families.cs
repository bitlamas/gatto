using System.Text.Json;

namespace Gatto.Core.Acquire;

//how an architecture sits on the shelf, as its own row or folded into one
internal enum ArchRole
{
    //a model, and it gets a row
    Standalone,

    //a same-origin build of a model, listed so its repos fold into the row they belong to rather than rendered as a row
    Variant,
}

//how a tuned name is told from a base name: gemma marks the tuned model, qwen the base one, and the rule reads the model key once
internal sealed record TunedRule(string? Mark, string? BaseMark)
{
    public bool IsTuned(string modelKey) =>
        Mark is { Length: > 0 } m ? modelKey.Contains(m, StringComparison.OrdinalIgnoreCase)
        : BaseMark is not { Length: > 0 } b || !HoldsSegment(modelKey, b);

    //the mark as a whole segment, at the end or before a dash, so GLM-4-32B-Base-0414 reads as base and -Baseline would not
    private static bool HoldsSegment(string key, string mark)
    {
        for (var at = key.IndexOf(mark, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = key.IndexOf(mark, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + mark.Length;
            if (end == key.Length || key[end] == '-') return true;
        }
        return false;
    }
}

//where a family's originals come from: the releaser org, its generations newest first as groups of name prefixes, the tuned rule and the build suffixes
internal sealed record FamilyEntry(
    string Releaser, IReadOnlyList<IReadOnlyList<string>> Generations, bool Ordered,
    TunedRule Tuned, IReadOnlyList<string> Builds);

//which chip a model sits under. family membership controls what is listed, the base_model fold controls what gets a row, and the two must stay apart
internal sealed record Families(
    string Reviewed,
    string ArchitecturesRelease,
    IReadOnlyList<string> Ladder,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ArchRole>> Members,
    IReadOnlyDictionary<string, string> Excluded,
    IReadOnlyDictionary<string, string> CurrentTier,
    IReadOnlyDictionary<string, string> ArchTiers,
    string CurrentTierRelease = "") //the engine release the tier pin was last re-read against, held equal to the engine pin by a test
{
    private const string ResourceName = "Gatto.Core.Acquire.families.json";

    //the families lit when the shelf opens
    public IReadOnlyList<string> Landing { get; init; } = [];

    //per family, where its originals come from. a family with no entry finds nothing on the shelf
    public IReadOnlyDictionary<string, FamilyEntry> Entries { get; init; } =
        new Dictionary<string, FamilyEntry>(StringComparer.OrdinalIgnoreCase);

    //the naming stems the validator's second direction tests, matched as substrings. they are the chip names, so they cannot drift from the ladder
    internal static readonly IReadOnlyList<string> Stems = ["gemma", "qwen", "deepseek", "glm", "mistral"];

    //which chip this architecture sits under, or null when it is on none. the match is ordinal and case-insensitive, culture has no place in an arch name
    public string? FamilyOf(string? arch)
    {
        if (arch is not { Length: > 0 }) return null;
        foreach (var (family, members) in Members)
            if (members.ContainsKey(arch)) return family;
        return null;
    }

    //whether this architecture is a build rather than a model. null when it is on no shelf, since the caller needs both answers
    public ArchRole? RoleOf(string? arch)
    {
        if (arch is not { Length: > 0 }) return null;
        foreach (var members in Members.Values)
            if (members.TryGetValue(arch, out var role)) return role;
        return null;
    }

    //the tier a repo sits in, or null when this family has no tiers. an arch-keyed family always answers null, which is not the same as unversioned
    public ModelTier? TierOf(string family, string? repoId) =>
        CurrentTier.ContainsKey(family) ? QwenTier.Of(repoId) : null;

    //the tier a row sits in, by arch where that is the honest key and by name where it is not. the arch map is read first, so the two never both fire
    public ModelTier TierFor(string? arch, string? repoId)
    {
        if (arch is not null && ArchTiers.TryGetValue(arch, out var tier))
            return QwenTier.Of("Qwen" + tier);
        var family = FamilyOf(arch);
        return family is not null && CurrentTier.ContainsKey(family)
            ? QwenTier.Of(repoId)
            : ModelTier.Unversioned;
    }

    //whether this tier is the family's dated pin, compared on the parsed major and minor rather than on the label
    public bool IsCurrent(string family, ModelTier tier) =>
        PinFor(family) is { IsVersioned: true } p
        && p.Major == tier.Major && p.Minor == tier.Minor && tier.IsVersioned;

    //the generation a family's default shelf opens on: a dated pin for a name-parsed family, the top of the arch table for the others
    public ModelTier PinFor(string family)
    {
        if (CurrentTier.TryGetValue(family, out var pin)) return QwenTier.Of("Qwen" + pin);

        var top = ModelTier.Unversioned;
        if (!Members.TryGetValue(family, out var archs)) return top;
        foreach (var arch in archs.Keys)
        {
            if (!ArchTiers.TryGetValue(arch, out var tier)) continue;
            var t = QwenTier.Of("Qwen" + tier);
            if (!top.IsVersioned || ModelTier.Compare(t, top) < 0) top = t;
        }
        return top;
    }

    //parsed once per process. it is Lazy rather than a field initialiser, so a missing resource throws where the caller can see it
    private static readonly Lazy<Families> Cached = new(Parse, isThreadSafe: true);

    //reads the embedded dated lists. a missing resource is a build error that shows itself at runtime rather than a silently empty set
    public static Families Load() => Cached.Value;

    private static Families Parse()
    {
        using var s = typeof(Families).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing — the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;

        var members = new Dictionary<string, IReadOnlyDictionary<string, ArchRole>>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, FamilyEntry>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("families", out var fams))
            foreach (var family in fams.EnumerateObject())
            {
                if (family.Name.StartsWith('_')) continue;
                var set = new Dictionary<string, ArchRole>(StringComparer.OrdinalIgnoreCase);
                foreach (var arch in Names(family.Value, "standalone")) set[arch] = ArchRole.Standalone;
                foreach (var arch in Names(family.Value, "variant")) set[arch] = ArchRole.Variant;
                members[family.Name] = set;
                if (EntryOf(family.Value) is { } entry) entries[family.Name] = entry;
            }

        return new Families(
            Str(root, "reviewed"),
            Str(root, "architectures_release"),
            [.. Names(root, "ladder")],
            members,
            Map(root, "excluded"),
            Map(root, "current_tier"),
            Map(root, "arch_tiers"),
            Str(root, "current_tier_release"))
        {
            Landing = [.. Names(root, "landing")],
            Entries = entries,
        };
    }

    //null when the family names no releaser, so it lists nothing rather than everything
    private static FamilyEntry? EntryOf(JsonElement family)
    {
        if (Str(family, "releaser") is not { Length: > 0 } releaser) return null;
        var ordered = !family.TryGetProperty("ordered", out var o) || o.ValueKind != JsonValueKind.False;
        TunedRule tuned = new(null, null);
        if (family.TryGetProperty("tuned", out var t) && t.ValueKind == JsonValueKind.Object)
            tuned = new(t.TryGetProperty("mark", out var m) ? m.GetString() : null,
                t.TryGetProperty("base_mark", out var b) ? b.GetString() : null);
        return new FamilyEntry(releaser, GroupsOf(family), ordered, tuned, [.. Names(family, "builds")]);
    }

    //each generation is an array of prefixes, a versioned family's a group of one
    private static IReadOnlyList<IReadOnlyList<string>> GroupsOf(JsonElement family)
    {
        if (!family.TryGetProperty("generations", out var g) || g.ValueKind != JsonValueKind.Array) return [];
        return [.. g.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Array)
            .Select(x => (IReadOnlyList<string>)[.. x.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0)])
            .Where(x => x.Count > 0)];
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static IEnumerable<string> Names(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)
            : [];

    private static IReadOnlyDictionary<string, string> Map(JsonElement e, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in v.EnumerateObject())
            if (!p.Name.StartsWith('_')) map[p.Name] = p.Value.GetString() ?? "";
        return map;
    }
}
