using Gatto.Core.Hardware;

namespace Gatto.Core.Models;

internal enum KvCacheKind { F16, Q8_0 }

//what a model costs, split so a caller can say why. assumed terms are recorded rather than penalized again, and unknown terms widen Judge's margin
internal sealed record FitEstimate(
    long WeightsBytes, long KvCacheBytes,
    IReadOnlyList<string> UnknownTerms, IReadOnlyList<string> AssumedTerms,
    long StreamedBytes = 0,   //the table taken off the weights, which still sits in system memory
    long? Blocks = null);   //the header's block count, every layer the weights split over, null when it is not plausible

//a split puts some layers on the card and the rest in system memory, the card's share priced with the whole KV cache beside it
internal enum FitRegime { FitsGpu, FitsSplit, FitsRamOnly, DoesNotFit, Unknown }

//the context setup writes for a file and the regime it was judged at
internal sealed record ContextPick(int Context, FitRegime Regime);

//computed from the file's own terms, never tabulated, so it cannot go stale. the estimate alone can run under a real load, and the reserve in Judge's budget is what keeps the answer conservative
internal static class FitArithmetic
{
    //the cache type every profile gatto writes, so a fit prices what the load allocates and no rung is lost to an f16 price
    internal const KvCacheKind ProfileKind = KvCacheKind.Q8_0;

    //a term past its cap counts as unknown, since the values come from a remote header and Estimate cannot throw
    private const long MaxBlockCount = 512;
    private const long MaxKvHeads = 256;
    private const long MaxHeadDimSum = 8192;      //key head length plus value head length
    //the caller clamps a header-derived context to this before Estimate, which throws over the cap
    public const int MaxContext = 10_000_000;

    //a size above this cap clamps rather than throwing, since the size is remote data. only the top is clamped, since zero is a legitimate caller value
    private const long MaxFileBytes = 4_398_046_511_104;

    //the widened margin, an integer ratio like every other term in this file
    private const long HeadroomNumerator = 125;
    private const long HeadroomDenominator = 100;

    //KV bytes come from the file's own terms, since this class touches no disk or network. an out-of-range context throws here, because that argument is a caller bug
    public static FitEstimate Estimate(GgufHeader h, long fileBytes, int contextLength, KvCacheKind kv,
        long? streamedBytes = null)   //bytes the engine never places on the GPU. null means the tensor tables were not read
    {
        if (contextLength <= 0 || contextLength > MaxContext)
            throw new ArgumentOutOfRangeException(nameof(contextLength), contextLength,
                $"context length must be between 1 and {MaxContext}");

        var unknown = new List<string>();
        var assumed = new List<string>();

        var blocks = Plausible(h.BlockCount, MaxBlockCount);
        if (blocks is null) unknown.Add("block_count");

        var kvHeads = KvHeads(h, blocks, assumed);
        if (kvHeads is null) unknown.Add("head_count_kv");

        var dims = HeadDims(h, assumed);
        if (dims is null) unknown.Add("head_dim");

        long kvBytes = 0;
        if (unknown.Count == 0)
        {
            //guards the byte multiply alone, the per-layer sum in KvElements stays inside long by the caps, worst case about 3.7e17
            checked
            {
                var elements = KvElements(h, blocks!.Value, kvHeads!, dims!.Value, contextLength);
                //the Q8_0 block is 34 bytes per 32 elements, so multiply by 34 before dividing, or the block overhead is lost to truncation
                kvBytes = kv == KvCacheKind.F16 ? elements * 2 : elements * 34 / 32;
            }
        }

        //record the clamped size and the claimed one, so a clamped verdict is not read as honest. the separator must not vary by locale
        var weights = fileBytes;
        if (weights > MaxFileBytes)
        {
            assumed.Add($"file size clamped from " +
                $"{fileBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes " +
                "(implausible for any local disk)");
            weights = MaxFileBytes;
        }

        //the streamed table is priced against no GPU. a claim bigger than the weights is clamped, so a lie cannot make the model free
        long table = 0;
        if (streamedBytes is > 0 and var streamed)
        {
            table = Math.Min(streamed, weights);
            weights -= table;
        }
        else if (streamedBytes is null
            && (h.DeclaresPerLayerInput || Gatto.Core.Models.StreamedTensors.Load().Streams(h.Architecture)))
            assumed.Add("priced as the whole file: the per-layer input table was not read, and the engine keeps it off the GPU");

        return new FitEstimate(weights, kvBytes, unknown, assumed, table, blocks);
    }

    //llama-server's default -ub, which sizes a window cache. a profile that passes another -ub holds window plus that many cells
    internal const int DefaultUbatch = 512;

    //the engine pads every cache to a multiple of this many cells
    private const long CellPad = 256;

    //each window layer holds the window plus one ubatch, padded and never past the context, so it does not grow with the context
    private static long WindowCells(long window, long context) =>
        (Math.Min(context, window + DefaultUbatch) + CellPad - 1) / CellPad * CellPad;

    //the families whose window pattern the engine fixes in its source when the header writes none: the period, and whether the full layer comes first in it. null means the header must write the pattern
    private static readonly Dictionary<string, (int? Period, bool DenseFirst)> WindowPatterns = new(StringComparer.Ordinal)
    {
        ["gemma2"] = (2, false), ["gemma3"] = (6, false), ["gemma4"] = (null, false),
        ["gpt-oss"] = (2, false), ["laguna"] = (4, true), ["muse-glimmer"] = (4, false),
    };

    //only these read shared_kv_layers, so a family that never does keeps a cache on every layer
    private static readonly HashSet<string> SharedKvFamilies = new(StringComparer.Ordinal) { "gemma4" };

    //the main context gives the trailing nextn blocks no cache, except in these: deepseek4 drops the count, graniteswitch attends that layer, and these hybrids filter layers without it
    private static readonly HashSet<string> NextnAttended = new(StringComparer.Ordinal)
        { "deepseek4", "graniteswitch", "nemotron_h", "nemotron_h_moe", "falcon-h1" };

    //latent attention caches the compressed K alone, and only these families cache it through the plain KV cache
    private static readonly HashSet<string> LatentFamilies = new(StringComparer.Ordinal)
        { "deepseek2", "glm-dsa", "kimi-linear", "bailingmoe3" };

    //the elements every cache holds, layer by layer: a layer holds none when it is recurrent, has no kv heads or reads another layer's cache. a family the tables above do not name is priced on every layer at the context, K and V
    private static long KvElements(GgufHeader h, long blocks, Func<long, long> kvHeads, (long K, long V) dims, long context)
    {
        var arch = h.Architecture ?? "";
        var interval = Plausible(h.FullAttentionInterval, blocks);
        var holding = SharedKvFamilies.Contains(arch) && Plausible(h.SharedKvLayers, blocks - 1) is { } shared
            ? blocks - shared : blocks;
        if (!NextnAttended.Contains(arch) && Plausible(h.NextnPredictLayers, blocks - 1) is { } nextn)
            holding = Math.Min(holding, blocks - nextn);
        long? window = WindowPatterns.ContainsKey(arch) && Plausible(h.SlidingWindow, MaxContext) is { } w
            ? WindowCells(w, context) : null;
        var windowDims = (K: Plausible(h.KeyLengthSwa, MaxHeadDimSum) ?? dims.K, V: Plausible(h.ValueLengthSwa, MaxHeadDimSum) ?? dims.V);
        var latent = LatentFamilies.Contains(arch) && h.KeyLengthMla is > 0 && h.ValueLengthMla is > 0;
        var indexer = IndexerWidth(h, arch);

        long total = 0;
        for (long layer = 0; layer < holding; layer++)
        {
            if (interval is { } i && (layer + 1) % i != 0) continue;   //full attention is the last block of every interval, the rest are recurrent
            var heads = kvHeads(layer);
            if (heads == 0) continue;
            var sliding = window is not null && IsWindowLayer(h, arch, layer);
            var (k, v) = sliding ? windowDims : dims;
            total += (sliding ? window!.Value : context) * heads * (latent ? k : k + v);
            if (indexer is { } width && IsIndexerLayer(h, arch, layer)) total += context * width;
        }
        return total;
    }

    //a per-layer flag wins, then a period the header writes, then the family's own, and a layer past the flags is priced full, the conservative reading
    private static bool IsWindowLayer(GgufHeader h, string arch, long layer)
    {
        if (h.SlidingWindowPattern is { } flags) return layer < flags.Count && flags[(int)layer] != 0;
        var (period, denseFirst) = WindowPatterns[arch];
        if (Plausible(h.SlidingWindowPeriod, MaxBlockCount) is { } written) period = (int)written;
        if (period is not { } p) return false;
        return denseFirst ? layer % p != 0 : layer % p < p - 1;
    }

    //the indexer's K cache per token on each layer that has one, one head and no V. qwen4exp keeps a raw and a pooled key when it pools
    private static long? IndexerWidth(GgufHeader h, string arch)
    {
        if (Plausible(h.IndexerKeyLength, MaxHeadDimSum) is not { } key) return null;
        return arch switch
        {
            "qwen4exp" => h.CompressRatios?.Any(r => r > 0) == true ? 2 * key : key,
            "glm-dsa" => key,
            _ => null,
        };
    }

    //glm-dsa trained past a million tokens indexes layers 0 and 1 and every fourth from 2 when the header writes no types, and every layer below that context
    private static bool IsIndexerLayer(GgufHeader h, string arch, long layer)
    {
        if (arch != "glm-dsa") return true;
        if (h.IndexerTypes is { Count: 1 } every) return every[0] != 0;
        if (h.IndexerTypes is { } types) return layer >= types.Count || types[(int)layer] != 0;
        if (h.ContextLength is not >= GlmDsaSparseContext) return true;
        return layer < 2 || (layer < GlmDsaTableLayers && (layer - 2) % 4 == 0);
    }

    private const long GlmDsaSparseContext = 1_048_576;
    private const long GlmDsaTableLayers = 78;   //the engine's default table covers this many layers and leaves the rest without an indexer

    //each layer's kv heads: an array as written, else the scalar on every layer, else head_count as if GQA did not exist. an array that does not cover the blocks prices its largest on every layer
    private static Func<long, long>? KvHeads(GgufHeader h, long? blocks, List<string> assumed)
    {
        if (h.HeadCountKvPerLayer is { Count: > 0 } list && list.All(n => n is >= 0 and <= MaxKvHeads) && list.Max() > 0)
        {
            if (blocks is null || list.Count == blocks) return layer => list[(int)layer];
            var most = list.Max();
            assumed.Add($"head_count_kv per layer covers {list.Count} of {blocks} blocks, priced at its largest on every block");
            return _ => most;
        }
        if (Plausible(h.HeadCountKv, MaxKvHeads) is { } n) return _ => n;
        if (Plausible(h.HeadCount, MaxKvHeads) is { } heads)
        {
            assumed.Add("head_count_kv absent — priced as head_count (as if no GQA)");
            return _ => heads;
        }
        return null;
    }

    //the declared key and value lengths, which some models make unequal. the fallback rounds head_dim up, since truncating would underestimate
    private static (long K, long V)? HeadDims(GgufHeader h, List<string> assumed)
    {
        var k = Plausible(h.KeyLength, MaxHeadDimSum);
        var v = Plausible(h.ValueLength, MaxHeadDimSum);
        if (k is not null && v is not null && k.Value + v.Value <= MaxHeadDimSum)
            return (k.Value, v.Value);

        if (h.EmbeddingLength is { } emb && emb > 0 && h.HeadCount is { } heads && heads > 0)
        {
            var dim = (emb + heads - 1) / heads;   //ceil, since truncating would shrink head_dim
            if (dim > 0 && 2 * dim <= MaxHeadDimSum)
            {
                assumed.Add("head_dim derived from embedding_length / head_count (rounded up)");
                return (dim, dim);
            }
        }
        return null;
    }

    //usable only when positive and inside the cap, since anything else is treated as absent
    private static long? Plausible(long? value, long cap) =>
        value is { } v && v > 0 && v <= cap ? v : null;

    //GPU then RAM, a CPU-only machine skips the GPU comparison, and a streamed table is memory too, beside the card in system memory or in the pool unified memory shares
    public static FitRegime Judge(FitEstimate e, HardwareClass hw)
    {
        var gpuBudget = hw.Topology == MemoryTopology.CpuOnly ? 0UL : hw.GpuBudgetBytes;

        checked
        {
            var total = e.WeightsBytes + e.KvCacheBytes;
            var needed = e.UnknownTerms.Count > 0
                ? total * HeadroomNumerator / HeadroomDenominator   //the widened margin for unknown terms
                : total;
            var table = e.StreamedBytes;

            var beside = table == 0 || hw.Topology switch
            {
                MemoryTopology.Unified => needed + table <= (long)HardwareClassifier.StreamPoolBytes(hw.Snapshot),
                _ => table <= (long)hw.RamBudgetBytes,
            };
            if (needed <= (long)gpuBudget && beside) return FitRegime.FitsGpu;
            if (SplitLayers(e, hw) is not null) return FitRegime.FitsSplit;
            if (needed + table <= (long)hw.RamBudgetBytes) return FitRegime.FitsRamOnly;
            return FitRegime.DoesNotFit;
        }
    }

    //the largest rung the card holds whole, else the split rung, else the largest memory holds, so setup and the shelf price one context. null with no usable ceiling or when nothing fits
    public static ContextPick? PickContext(GgufHeader h, long fileBytes, long? streamedBytes, HardwareClass hw)
    {
        if (h.ContextLength is not long c || c <= 0 || c > MaxContext) return null;
        var ceiling = (int)c;
        var rungs = new[] { ceiling, 131072, 65536, 32768, 16384, 8192, 4096 }.Where(r => r <= ceiling).Distinct().ToList();
        FitEstimate At(int context) => Estimate(h, fileBytes, context, ProfileKind, streamedBytes);
        foreach (var rung in rungs)
            if (Judge(At(rung), hw) == FitRegime.FitsGpu) return new ContextPick(rung, FitRegime.FitsGpu);
        if (SplitRung(rungs, At, hw) is { } split) return new ContextPick(split, FitRegime.FitsSplit);
        foreach (var rung in rungs)
            if (Judge(At(rung), hw) == FitRegime.FitsRamOnly) return new ContextPick(rung, FitRegime.FitsRamOnly);
        return null;
    }

    //a rung the card holds whole first, then the largest holding at least half the layers, then the one holding the most. null when no rung splits
    internal static int? SplitRung(IReadOnlyList<int> rungs, Func<int, FitEstimate> at, HardwareClass hw)
    {
        var whole = rungs.Where(r => Judge(at(r), hw) == FitRegime.FitsGpu).ToList();
        if (whole.Count > 0) return whole[0];
        var splits = rungs
            .Select(r => (Rung: r, Estimate: at(r)))
            .Where(x => Judge(x.Estimate, hw) == FitRegime.FitsSplit)
            .Select(x => (x.Rung, Layers: SplitLayers(x.Estimate, hw) ?? 0, Blocks: x.Estimate.Blocks ?? 0))
            .ToList();
        if (splits.Count == 0) return null;
        foreach (var s in splits)
            if (2L * s.Layers >= s.Blocks) return s.Rung;
        return splits.OrderByDescending(s => s.Layers).ThenByDescending(s => s.Rung).First().Rung;
    }

    //the layers the card holds beside the whole KV cache, at least one, with the rest of the weights in system memory. a discrete card only, and never on a term the header left unknown
    public static int? SplitLayers(FitEstimate e, HardwareClass hw)
    {
        if (hw.Topology != MemoryTopology.Discrete || e.UnknownTerms.Count > 0 || e.Blocks is not { } blocks) return null;
        var perLayer = e.WeightsBytes / blocks;
        var room = (long)hw.GpuBudgetBytes - e.KvCacheBytes;
        if (perLayer <= 0 || room < perLayer) return null;
        checked
        {
            var n = Math.Min(room / perLayer, blocks);
            //the floor of the per-layer share leaves the rounding with the remainder, which errs toward memory
            var rest = e.WeightsBytes - n * perLayer;
            return rest + e.StreamedBytes <= (long)hw.RamBudgetBytes ? (int)n : null;
        }
    }
}
