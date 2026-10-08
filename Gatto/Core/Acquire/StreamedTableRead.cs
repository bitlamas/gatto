using System.Buffers.Binary;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//the read of a quant's streamed table on the Hub path, before the pick prices it, and only for a listed architecture
internal static class StreamedTableRead
{
    //magic, version, tensor count, kv count: enough to skip a member that holds no tensors without reading its key-value section
    private const int ProbeBytes = 24;
    private const uint Magic = 0x46554747;

    //the most a header read may fetch, past which a lying header prices its quant whole
    public const long Budget = 32 << 20;

    //the bytes of this quant's streamed tensors, or null when any member could not be read to the end of its table
    public static async Task<long?> BytesAsync(
        HubQuant quant, Func<string, RangeFetch> fetchFor, StreamedTensors list, CancellationToken ct,
        SemaphoreSlim? gate = null, TokenizerMemory? tokenizers = null)
    {
        gate ??= new SemaphoreSlim(HubSearch.HubConcurrency);
        var tables = await Task.WhenAll(quant.Members.Select(m => MemberAsync(fetchFor(m.RepoPath), m.Bytes, gate, tokenizers, ct)))
            .ConfigureAwait(false);
        return list.BytesIn(tables);
    }

    //a member's table, an empty table for a member with no tensors, or null when it could not be read
    private static async Task<GgufTensors?> MemberAsync(RangeFetch fetch, long bytes, SemaphoreSlim gate,
        TokenizerMemory? tokenizers, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var probe = await fetch(0, ProbeBytes, ct).ConfigureAwait(false);
            if (probe.Length < ProbeBytes || BinaryPrimitives.ReadUInt32LittleEndian(probe) != Magic) return null;
            //the first shard of a split set holds the tokenizer and no tensors, so its megabytes are never read
            if (BinaryPrimitives.ReadUInt64LittleEndian(probe.AsSpan(8)) == 0) return new GgufTensors(0, []);
            //the parser walks the remote file through windows, skipping fixed-width arrays and jumping a tokenizer another file walked
            using var jumps = tokenizers?.For(ct);
            return GgufHeaderParser.Parse(new RangeStream(fetch, bytes, ct, Budget), jumps).Tensors;
        }
        finally { gate.Release(); }
    }

    //every candidate of a listed architecture gets its streamed bytes, and a failed read leaves that quant as it was
    public static async Task<HubTree> WithStreamedAsync(
        HubTree tree, string? architecture, long? repoParams, Func<string, RangeFetch> fetchFor, CancellationToken ct,
        HubReadStore? disk = null, string? repoId = null,
        Func<HubQuant, bool>? couldMove = null,   //false for a quant whose verdict no table could change, which then costs no read
        TokenizerMemory? tokenizers = null)
    {
        var list = StreamedTensors.Load();
        if (!list.Streams(architecture)) return tree;

        var candidates = HubSearch.Candidates(tree.Quants, repoParams).ToHashSet();
        //one repo's reads share one gate, so a dozen quants cost a few round trips instead of a hundred
        using var gate = new SemaphoreSlim(HubSearch.HubConcurrency);
        var quants = await Task.WhenAll(tree.Quants.Select(async q =>
        {
            if (!candidates.Contains(q)) return q;
            //a table read in an earlier run is not read again while the file's hash is unchanged
            if (disk is not null && repoId is not null && disk.TryGetStreamed(repoId, q, list, out var kept))
            {
                HubTrace.Hit("table", "disk", repoId, q.RepoPath);
                return q with { StreamedBytes = kept };
            }
            if (couldMove is not null && !couldMove(q)) return q;
            long? streamed;
            try { streamed = await BytesAsync(q, fetchFor, list, ct, gate, tokenizers).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException) { streamed = null; }
            //only a whole read goes to disk, a failure is asked again next run
            if (streamed is { } read && disk is not null && repoId is not null) disk.PutStreamed(repoId, q, list, read);
            return q with { StreamedBytes = streamed };
        })).ConfigureAwait(false);
        return tree with { Quants = quants };
    }
}
