using System.Text.Json;

namespace Gatto.Core.Acquire;

//what a structure read decided for one file, the two values a row shows, kept instead of the header so a disk entry stays small
internal sealed record StructureFacts(string? Cell, (long Total, long Active)? Experts);

//header reads kept on disk between runs, keyed by repo, path and the file's hash from the tree, so a changed file is read again
internal sealed class HubReadStore(string homePath)
{
    //the schema version is in the folder name, so a change to what an entry means starts a new folder and the old one is never read
    internal const string Folder = "hub-reads-1";

    private readonly DiskEntries _entries = new(homePath, Folder);

    //where the entries live, for a test that inspects them
    internal string Dir => _entries.Dir;

    //every member's hash from the tree in shard order, or null when one is missing. without a hash a changed file looks unchanged
    internal static string? Fingerprint(HubQuant q)
    {
        var shas = q.Members.Select(m => m.Sha256).ToList();
        return shas.All(s => s is { Length: > 0 }) ? string.Join(",", shas) : null;
    }

    //the streamed bytes depend on the shipped tensor list too, so its release and review date are part of the key
    private static string StreamedVariant(StreamedTensors list) => list.Release + "|" + list.Reviewed;

    public bool TryGetStreamed(string repoId, HubQuant q, StreamedTensors list, out long bytes)
    {
        bytes = 0;
        if (_entries.Read(Key("streamed", repoId, q, StreamedVariant(list))) is not { } doc) return false;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("streamed", out var v) || DiskEntries.Long(v) is not { } b || b < 0)
                return false;
            bytes = b;
            return true;
        }
    }

    public void PutStreamed(string repoId, HubQuant q, StreamedTensors list, long bytes) =>
        _entries.Write(Key("streamed", repoId, q, StreamedVariant(list)), w => w.WriteNumber("streamed", bytes));

    public bool TryGetStructure(string repoId, HubQuant q, out StructureFacts? facts)
    {
        facts = null;
        if (_entries.Read(Key("structure", repoId, q, "")) is not { } doc) return false;
        using (doc)
        {
            var root = doc.RootElement;
            var cell = root.TryGetProperty("cell", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            (long, long)? experts =
                root.TryGetProperty("experts_total", out var t) && DiskEntries.Long(t) is { } total
                && root.TryGetProperty("experts_active", out var a) && DiskEntries.Long(a) is { } active
                    ? (total, active) : null;
            facts = new StructureFacts(cell, experts);
            return true;
        }
    }

    public void PutStructure(string repoId, HubQuant q, StructureFacts facts) =>
        _entries.Write(Key("structure", repoId, q, ""), w =>
        {
            if (facts.Cell is { } cell) w.WriteString("cell", cell);
            if (facts.Experts is { } e)
            {
                w.WriteNumber("experts_total", e.Total);
                w.WriteNumber("experts_active", e.Active);
            }
        });

    //null when the file has no hash, so nothing is stored or found for it and the read stays live
    private static string? Key(string kind, string repoId, HubQuant q, string variant) =>
        Fingerprint(q) is { } print ? $"{kind}|{repoId}|{q.RepoPath}|{print}|{variant}" : null;
}
