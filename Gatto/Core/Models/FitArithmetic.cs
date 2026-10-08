using Gatto.Core.Hardware;

namespace Gatto.Core.Models;

internal enum KvCacheKind { F16, Q8_0 }

//what a model costs, split so a caller can say why. assumed terms are recorded rather than penalized again, and unknown terms widen Judge's margin
internal sealed record FitEstimate(
    long WeightsBytes, long KvCacheBytes,
    IReadOnlyList<string> UnknownTerms, IReadOnlyList<string> AssumedTerms,
    long StreamedBytes = 0);   //the table taken off the weights, which still sits in system memory

internal enum FitRegime { FitsGpu, FitsRamOnly, DoesNotFit, Unknown }

//fit is computed from the file's own terms rather than tabulated, so it cannot go stale. it errs conservative, since a failed load is worse than a wrong pick
internal static class FitArithmetic
{
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
        //full attention comes every interval blocks, so the cache covers blocks / interval layers. an interval past the block count prices every block
        else if (Plausible(h.FullAttentionInterval, blocks.Value) is { } interval)
            blocks = blocks.Value / interval;

        //with no head_count_kv, price the cache from head_count, as if GQA did not exist
        var kvHeads = Plausible(h.HeadCountKv, MaxKvHeads);
        if (kvHeads is null)
        {
            kvHeads = Plausible(h.HeadCount, MaxKvHeads);
            if (kvHeads is not null)
                assumed.Add("head_count_kv absent — priced as head_count (as if no GQA)");
        }
        if (kvHeads is null) unknown.Add("head_count_kv");

        var headDimSum = HeadDimSum(h, assumed);
        if (headDimSum is null) unknown.Add("head_dim");

        long kvBytes = 0;
        if (unknown.Count == 0)
        {
            //a backstop the caps make unreachable, since the worst case is about 3.7e17, far inside long
            checked
            {
                var elements = blocks!.Value * contextLength * kvHeads!.Value * headDimSum!.Value;
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
            && (h.DeclaresPerLayerInput || Gatto.Core.Acquire.StreamedTensors.Load().Streams(h.Architecture)))
            assumed.Add("priced as the whole file: the per-layer input table was not read, and the engine keeps it off the GPU");

        return new FitEstimate(weights, kvBytes, unknown, assumed, table);
    }

    //the declared key and value lengths, which some models make unequal. the fallback rounds head_dim up, since truncating would underestimate
    private static long? HeadDimSum(GgufHeader h, List<string> assumed)
    {
        var k = Plausible(h.KeyLength, MaxHeadDimSum);
        var v = Plausible(h.ValueLength, MaxHeadDimSum);
        if (k is not null && v is not null && k.Value + v.Value <= MaxHeadDimSum)
            return k.Value + v.Value;

        if (h.EmbeddingLength is { } emb && emb > 0 && h.HeadCount is { } heads && heads > 0)
        {
            var dim = (emb + heads - 1) / heads;   //ceil, since truncating would shrink head_dim
            var sum = 2 * dim;
            if (sum > 0 && sum <= MaxHeadDimSum)
            {
                assumed.Add("head_dim derived from embedding_length / head_count (rounded up)");
                return sum;
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
            if (needed + table <= (long)hw.RamBudgetBytes) return FitRegime.FitsRamOnly;
            return FitRegime.DoesNotFit;
        }
    }
}
