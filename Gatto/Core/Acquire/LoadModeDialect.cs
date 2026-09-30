using System.Text.Json;

namespace Gatto.Core.Acquire;

//one flag an engine build stopped accepting, with its replacement tokens. an empty replacement drops the flag
internal sealed record RetiredFlag(string Flag, int RetiredIn, IReadOnlyList<string> Replacement);

//one table for the load-mode spelling and the retired flags, so the scaffold and the engine update never write two dialects
internal sealed record LoadModeDialect(
    string ReviewedRelease, string Reviewed, string LoadModeFlag, string LazyModeFlag, IReadOnlyList<RetiredFlag> Retired)
{
    private const string ResourceName = "Gatto.Core.Acquire.load-mode-dialect.json";

    private static readonly Lazy<LoadModeDialect> Cached = new(Read);

    public static LoadModeDialect Load() => Cached.Value;

    //the two tokens that set the load mode on the pinned build, as a profile's extra_args holds them
    public IReadOnlyList<string> LoadMode(string mode) => [LoadModeFlag, mode];

    //the tokens that set the lazy mode on the pinned build
    public IReadOnlyList<string> LazyMode(string mode) => [LazyModeFlag, mode];

    //the args with every flag retired at or before the target build replaced, order kept and every other token untouched
    public IReadOnlyList<string> Rewrite(IReadOnlyList<string> extraArgs, int targetBuild)
    {
        var result = new List<string>(extraArgs.Count);
        foreach (var arg in extraArgs)
        {
            var retired = Retired.FirstOrDefault(r => r.RetiredIn <= targetBuild && string.Equals(r.Flag, arg, StringComparison.Ordinal));
            if (retired is null) result.Add(arg);
            else result.AddRange(retired.Replacement);
        }
        return result;
    }

    //a missing resource is a build error surfaced at runtime, since an empty table would rewrite nothing and leave a profile the engine refuses
    private static LoadModeDialect Read()
    {
        using var s = typeof(LoadModeDialect).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing, and the Gatto.csproj EmbeddedResource entry is load-bearing");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var spelling = root.TryGetProperty("spelling", out var sp) && sp.ValueKind == JsonValueKind.Object ? sp : default;
        var retired = new List<RetiredFlag>();
        if (root.TryGetProperty("retired", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
            {
                var flag = Str(r, "flag");
                if (flag.Length == 0 || !r.TryGetProperty("retired_in", out var at) || !at.TryGetInt32(out var build)) continue;
                var replacement = r.TryGetProperty("replacement", out var rep) && rep.ValueKind == JsonValueKind.Array
                    ? rep.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];
                retired.Add(new RetiredFlag(flag, build, replacement));
            }
        return new LoadModeDialect(Str(root, "reviewed_release"), Str(root, "reviewed"),
            spelling.ValueKind == JsonValueKind.Object ? Str(spelling, "load_mode") : "",
            spelling.ValueKind == JsonValueKind.Object ? Str(spelling, "lazy_mode") : "",
            retired);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
