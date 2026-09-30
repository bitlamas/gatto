using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Cli.Setup;

//price the resulting regime rather than the increment, and feed the estimate the model's header
internal static class ProjectorPair
{
    //the regime with the projector loaded beside the model, from the model's own header
    public static FitRegime Regime(
        GgufHeader modelHeader, long modelBytes, long projectorBytes,
        int contextLength, KvCacheKind kv, HardwareClass hw, long? streamedBytes = null) =>
        FitArithmetic.Judge(
            FitArithmetic.Estimate(modelHeader, modelBytes + projectorBytes, contextLength, kv, streamedBytes), hw);

    //true only when the pair's regime differs from the model's, a pair that fits the same needs no warning
    public static bool CostsARegime(
        GgufHeader modelHeader, long modelBytes, long projectorBytes,
        int contextLength, KvCacheKind kv, HardwareClass hw, long? streamedBytes = null) =>
        Regime(modelHeader, modelBytes, projectorBytes, contextLength, kv, hw, streamedBytes)
        != FitArithmetic.Judge(FitArithmetic.Estimate(modelHeader, modelBytes, contextLength, kv, streamedBytes), hw);
}
