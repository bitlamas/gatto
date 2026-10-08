using System.Text.Json;

namespace Gatto.Core.Acquire;

//what a structure read decided for one file, the two values a row shows, kept instead of the header so a disk entry stays small
internal sealed record StructureFacts(string? Cell, (long Total, long Active)? Experts);

//header reads kept on disk between runs, keyed by repo, path and the file's hash from the tree, so a changed file is read again. older listings are kept a day, trees until the repo changes
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

    //one source's conversion listing, reused until it is a day old, so an older generation costs no request inside the Hub's window
    public bool TryGetListing(string sourceId, DateTimeOffset now, out IReadOnlyList<HubListing> rows)
    {
        rows = [];
        if (_entries.Read("listing|" + sourceId) is not { } doc) return false;
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("at", out var at) || DiskEntries.Long(at) is not { } seconds) return false;
            var age = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (age > ListingLife || age < TimeSpan.Zero) return false;
            if (!root.TryGetProperty("rows", out var list) || list.ValueKind != JsonValueKind.Array) return false;
            var read = new List<HubListing>();
            foreach (var r in list.EnumerateArray())
            {
                if (Str(r, "id") is not { Length: > 0 } repoId) return false;
                read.Add(new HubListing(repoId, Str(r, "arch"), Num(r, "ctx"),
                    r.TryGetProperty("gated", out var g) && g.ValueKind == JsonValueKind.True,
                    Num(r, "downloads") ?? 0,
                    Num(r, "modified") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                    Num(r, "params"), Str(r, "tag"),
                    r.TryGetProperty("causal", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? c.GetBoolean() : null,
                    Str(r, "quantized")));
            }
            rows = read;
            return true;
        }
    }

    public void PutListing(string sourceId, IReadOnlyList<HubListing> rows, DateTimeOffset now) =>
        _entries.Write("listing|" + sourceId, w =>
        {
            w.WriteNumber("at", now.ToUnixTimeSeconds());
            w.WriteStartArray("rows");
            foreach (var r in rows)
            {
                w.WriteStartObject();
                w.WriteString("id", r.RepoId);
                if (r.Arch is { } arch) w.WriteString("arch", arch);
                if (r.NativeCtx is { } ctx) w.WriteNumber("ctx", ctx);
                w.WriteBoolean("gated", r.Gated);
                w.WriteNumber("downloads", r.Downloads);
                //the tree store keys on it, so a listing read from disk still finds the tree kept for it
                if (r.LastModified is { } modified) w.WriteNumber("modified", modified.ToUnixTimeMilliseconds());
                if (r.Params is { } p) w.WriteNumber("params", p);
                if (r.PipelineTag is { } tag) w.WriteString("tag", tag);
                if (r.Causal is { } causal) w.WriteBoolean("causal", causal);
                if (r.QuantizedFrom is { } q) w.WriteString("quantized", q);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

    //one repo's tree, kept while the repo's last change is the one it was read under, so an unchanged repo costs no request. no date, no entry
    public bool TryGetTree(string repoId, DateTimeOffset? modified, out HubTree tree)
    {
        tree = new HubTree([], []);
        if (_entries.Read(TreeKey(repoId, modified)) is not { } doc) return false;
        using (doc)
        {
            var root = doc.RootElement;
            if (QuantsOf(root, "quants") is not { } quants || QuantsOf(root, "projectors") is not { } projectors) return false;
            tree = new HubTree(quants, projectors, (int)(Num(root, "files") ?? 0));
            return true;
        }
    }

    public void PutTree(string repoId, DateTimeOffset? modified, HubTree tree) =>
        _entries.Write(TreeKey(repoId, modified), w =>
        {
            w.WriteNumber("files", tree.FileCount);
            WriteQuants(w, "quants", tree.Quants);
            WriteQuants(w, "projectors", tree.Projectors);
        });

    private static string? TreeKey(string repoId, DateTimeOffset? modified) =>
        modified is { } m ? $"tree|{repoId}|{m.ToUnixTimeMilliseconds()}" : null;

    //a candidate keeps its members only when it had them, so a single file reads back as the tree gave it
    private static void WriteQuants(Utf8JsonWriter w, string name, IReadOnlyList<HubQuant> quants)
    {
        w.WriteStartArray(name);
        foreach (var q in quants)
        {
            w.WriteStartObject();
            WriteFile(w, q.FileName, q.Bytes, q.Sha256, q.Path);
            w.WriteNumber("shards", q.ShardCount);
            if (q.Files is { } members)
            {
                w.WriteStartArray("members");
                foreach (var m in members)
                {
                    w.WriteStartObject();
                    WriteFile(w, m.FileName, m.Bytes, m.Sha256, m.Path);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteFile(Utf8JsonWriter w, string fileName, long bytes, string? sha, string? path)
    {
        w.WriteString("name", fileName);
        w.WriteNumber("bytes", bytes);
        if (sha is { } s) w.WriteString("sha", s);
        if (path is { } p) w.WriteString("path", p);
    }

    //null when any entry is malformed, so a bad tree reads as a miss and is asked again
    private static IReadOnlyList<HubQuant>? QuantsOf(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var quants = new List<HubQuant>();
        foreach (var q in list.EnumerateArray())
        {
            if (Str(q, "name") is not { Length: > 0 } fileName || Num(q, "bytes") is not { } bytes) return null;
            List<HubFile>? members = null;
            if (q.TryGetProperty("members", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                members = [];
                foreach (var m in ms.EnumerateArray())
                {
                    if (Str(m, "name") is not { Length: > 0 } member || Num(m, "bytes") is not { } memberBytes) return null;
                    members.Add(new HubFile(member, memberBytes, Str(m, "sha"), Str(m, "path")));
                }
            }
            quants.Add(new HubQuant(fileName, bytes, Str(q, "sha"), (int)(Num(q, "shards") ?? 1), members,
                Path: Str(q, "path")));
        }
        return quants;
    }

    //how long a kept listing answers, after which the source is asked again
    internal static readonly TimeSpan ListingLife = TimeSpan.FromDays(1);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? DiskEntries.Long(v) : null;

    //null when the file has no hash, so nothing is stored or found for it and the read stays live
    private static string? Key(string kind, string repoId, HubQuant q, string variant) =>
        Fingerprint(q) is { } print ? $"{kind}|{repoId}|{q.RepoPath}|{print}|{variant}" : null;
}
