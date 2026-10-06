using System.Text.Json;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//what the disk scan read for one model: the header's values without its tensor table or template, and the set's streamed bytes
internal sealed record LocalScanFacts(GgufHeader Header, long? StreamedBytes);

//header reads of the disk scan kept between runs, keyed by every file of the set with its length and last write, so a changed file is read again
internal sealed class LocalReadStore(string homePath)
{
    //the schema version is in the folder name, so a change to what an entry means starts a new folder and the old one is never read
    internal const string Folder = "local-reads-1";

    private readonly DiskEntries _entries = new(homePath, Folder);

    //where the entries live, for a test that inspects them
    internal string Dir => _entries.Dir;

    //each member's full path, length and last write in shard order, or null when one cannot be read. a copy over the file changes the length or the time
    internal static string? Fingerprint(IReadOnlyList<string> members)
    {
        try
        {
            var parts = new List<string>(members.Count);
            foreach (var m in members)
            {
                var f = new FileInfo(m);
                if (!f.Exists) return null;
                parts.Add($"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
            }
            return string.Join(",", parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException) { return null; }
    }

    //the streamed bytes depend on the shipped tensor list too, so its release and review date are part of the key
    private static string? Key(IReadOnlyList<string> members) =>
        Fingerprint(members) is { } print && StreamedTensors.Load() is { } list
            ? $"scan|{print}|{list.Release}|{list.Reviewed}" : null;

    //the header as the scan keeps it, since no reader of a found model reads the table or the template and both can run to megabytes
    internal static GgufHeader Slim(GgufHeader h) => h with { ChatTemplate = null, Tensors = null };

    public bool TryGet(IReadOnlyList<string> members, out LocalScanFacts? facts)
    {
        facts = null;
        if (_entries.Read(Key(members)) is not { } doc) return false;
        using (doc)
        {
            var root = doc.RootElement;
            var ok = true;
            //a value of the wrong kind is a bad entry and reads as a miss, an absent one is a field the header did not hold
            string? Text(string name) =>
                !root.TryGetProperty(name, out var v) ? null
                : v.ValueKind == JsonValueKind.String ? v.GetString() : Bad<string>();
            long? Number(string name) =>
                !root.TryGetProperty(name, out var v) ? null
                : DiskEntries.Long(v) is { } n ? n : Bad<long?>();
            T? Bad<T>() { ok = false; return default; }

            if (!Enum.TryParse<GgufOutcome>(Text("outcome"), out var outcome) || !Enum.IsDefined(outcome)) return false;
            var header = new GgufHeader(outcome, Text("malformation"),
                Text("architecture"), Text("name"), Number("context_length"), Number("pooling_type"),
                Number("block_count"), Number("head_count"), Number("head_count_kv"),
                Number("embedding_length"), Number("key_length"), Number("value_length"),
                ChatTemplate: null, TensorCount: Number("tensor_count"),
                SizeLabel: Text("size_label"), ExpertCount: Number("expert_count"),
                ExpertUsedCount: Number("expert_used_count"), Tensors: null,
                EmbeddingLengthPerLayerInput: Number("embedding_length_per_layer_input"),
                FullAttentionInterval: Number("full_attention_interval"));
            var streamed = Number("streamed");
            if (!ok) return false;
            facts = new LocalScanFacts(header, streamed);
            return true;
        }
    }

    public void Put(IReadOnlyList<string> members, LocalScanFacts facts) =>
        _entries.Write(Key(members), w =>
        {
            var h = facts.Header;
            w.WriteString("outcome", h.Outcome.ToString());
            void Text(string name, string? v) { if (v is not null) w.WriteString(name, v); }
            void Number(string name, long? v) { if (v is { } n) w.WriteNumber(name, n); }
            Text("malformation", h.Malformation);
            Text("architecture", h.Architecture);
            Text("name", h.Name);
            Number("context_length", h.ContextLength);
            Number("pooling_type", h.PoolingType);
            Number("block_count", h.BlockCount);
            Number("head_count", h.HeadCount);
            Number("head_count_kv", h.HeadCountKv);
            Number("embedding_length", h.EmbeddingLength);
            Number("key_length", h.KeyLength);
            Number("value_length", h.ValueLength);
            Number("tensor_count", h.TensorCount);
            Text("size_label", h.SizeLabel);
            Number("expert_count", h.ExpertCount);
            Number("expert_used_count", h.ExpertUsedCount);
            Number("embedding_length_per_layer_input", h.EmbeddingLengthPerLayerInput);
            Number("full_attention_interval", h.FullAttentionInterval);
            Number("streamed", facts.StreamedBytes);
        });
}
