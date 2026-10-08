namespace Gatto.Core.Acquire;

//one original model's source repo at the releaser, with the model it builds and the generation it belongs to
internal sealed record Source(string Id, string ModelKey, int Generation);

//which of a releaser's repos are the originals of one generation, read by name prefix, kind and the tuned rule
internal static class Originals
{
    //the model a source name belongs to: build suffixes removed from the end, longest first, until none matches
    public static string ModelKey(string sourceName, IReadOnlyList<string> builds)
    {
        var ordered = builds.Where(b => b.Length > 0).OrderByDescending(b => b.Length).ToList();
        var key = sourceName;
        for (var stripped = true; stripped;)
        {
            stripped = false;
            foreach (var b in ordered)
                if (key.Length > b.Length && key.EndsWith(b, StringComparison.OrdinalIgnoreCase))
                {
                    key = key[..^b.Length];
                    stripped = true;
                    break;
                }
        }
        return key;
    }

    //a name in the generation starts with its prefix, or is the prefix less its dash, since a flagship carries nothing after its version
    public static bool InGeneration(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        || name.Equals(prefix.TrimEnd('-'), StringComparison.OrdinalIgnoreCase);

    //the sources of one generation, refused kinds and base models dropped, the releaser's own GGUF repos subtracted
    public static IReadOnlyList<Source> Of(FamilyEntry family, string prefix,
        IReadOnlyList<HubListing> releaserRows, IReadOnlyList<HubListing> releaserGgufRows, ModelKinds kinds)
    {
        var generation = IndexOf(family, prefix);
        var ggufs = releaserGgufRows.Select(r => r.RepoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new List<Source>();
        foreach (var row in releaserRows)
        {
            if (ggufs.Contains(row.RepoId)) continue;
            var name = NameOf(row.RepoId);
            if (!InGeneration(name, prefix)) continue;
            if (kinds.WillNotServe(row.PipelineTag, row.Causal, row.Arch)) continue;
            var key = ModelKey(name, family.Builds);
            if (!family.Tuned.IsTuned(key)) continue;
            sources.Add(new Source(row.RepoId, key, generation));
        }
        return sources;
    }

    //the releaser's own GGUF repos keyed to the model their name gives, dropped when that model was refused or is a base model
    public static IReadOnlyList<(string ModelKey, HubListing Repo)> ReleaserBuilds(FamilyEntry family,
        string prefix, IReadOnlyList<HubListing> releaserGgufRows, IReadOnlyList<Source> sources)
    {
        var keys = sources.Select(s => s.ModelKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var joined = new List<(string, HubListing)>();
        foreach (var row in releaserGgufRows)
        {
            var name = NameOf(row.RepoId);
            if (!InGeneration(name, prefix)) continue;
            var key = ModelKey(name, family.Builds);
            if (keys.Contains(key)) joined.Add((key, row));
        }
        return joined;
    }

    private static int IndexOf(FamilyEntry family, string prefix)
    {
        for (var i = 0; i < family.Generations.Count; i++)
            if (family.Generations[i].Contains(prefix, StringComparer.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string NameOf(string repoId) => repoId[(repoId.IndexOf('/') + 1)..];
}
