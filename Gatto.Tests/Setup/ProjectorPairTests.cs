using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests.Setup;

public class ProjectorPairTests
{
    //a header where every KV term is known, UnknownTerms stays empty and the margin is the narrow measured one
    private static GgufHeader ModelHeader() => new(
        GgufOutcome.Complete, Malformation: null,
        Architecture: "qwen3", Name: "qwen", ContextLength: 262144, PoolingType: null,
        BlockCount: 48, HeadCount: 32, HeadCountKv: 8,
        EmbeddingLength: 4096, KeyLength: 128, ValueLength: 128, ChatTemplate: null);

    //the projector's own header, clip architecture with no KV terms (the arithmetic has to refuse it)
    private static GgufHeader ProjectorHeader() => new(
        GgufOutcome.Complete, Malformation: null,
        Architecture: "clip", Name: "mmproj", ContextLength: null, PoolingType: null,
        BlockCount: null, HeadCount: null, HeadCountKv: null,
        EmbeddingLength: null, KeyLength: null, ValueLength: null, ChatTemplate: null);

    private static HardwareClass Machine(ulong gpu, ulong ram) =>
        new(MemoryTopology.Discrete, ShareKind.None, gpu, ram,
            new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 0UL), 0, BudgetBound.None);

    [Fact]
    public void THE_PAIR_IS_PRICED_AS_ONE_PROCESS_LOADING_TWO_FILES()
    {
        //the file sizes add together, the header does not change, one llama-server and one KV cache for two files
        var hw = Machine(12_000_000_000, 32_000_000_000);

        var alone = FitArithmetic.Judge(
            FitArithmetic.Estimate(ModelHeader(), 6_000_000_000, 8192, KvCacheKind.F16), hw);
        var paired = ProjectorPair.Regime(
            ModelHeader(), 6_000_000_000, 1_000_000_000, 8192, KvCacheKind.F16, hw);

        Assert.Equal(FitRegime.FitsGpu, alone);
        Assert.Equal(FitRegime.FitsGpu, paired);
        Assert.False(ProjectorPair.CostsARegime(
            ModelHeader(), 6_000_000_000, 1_000_000_000, 8192, KvCacheKind.F16, hw));
    }

    [Fact]
    public void A_PROJECTOR_THAT_PUSHES_THE_PAIR_OFF_THE_CARD_costs_a_regime()
    {
        //the projector costs a near-constant 1 GiB, which decides nothing on a roomy machine and everything on a tight one
        var tight = Machine(7_600_000_000, 32_000_000_000);

        Assert.Equal(FitRegime.FitsGpu, FitArithmetic.Judge(
            FitArithmetic.Estimate(ModelHeader(), 5_500_000_000, 8192, KvCacheKind.F16), tight));
        Assert.Equal(FitRegime.FitsRamOnly, ProjectorPair.Regime(
            ModelHeader(), 5_500_000_000, 1_100_000_000, 8192, KvCacheKind.F16, tight));
        Assert.True(ProjectorPair.CostsARegime(
            ModelHeader(), 5_500_000_000, 1_100_000_000, 8192, KvCacheKind.F16, tight));
    }

    //the estimate must use the model's header, the clip header zeroes the KV cache and adds a flat 25% instead
    [Fact]
    public void THE_MODELS_HEADER_IS_USED_and_the_projectors_lies_in_BOTH_directions()
    {
        const long model = 5_500_000_000, projector = 1_100_000_000;   //the two files total 6.6 GB.

        //direction 1, the honest pair totals 8.211 GB at small context, the clip header says 8.25 GB, a card in between forks the verdict
        var tight = Machine(8_230_000_000, 32_000_000_000);
        var pessimistic = FitArithmetic.Judge(
            FitArithmetic.Estimate(ProjectorHeader(), model + projector, 8192, KvCacheKind.F16), tight);
        var honest = FitArithmetic.Judge(
            FitArithmetic.Estimate(ModelHeader(), model + projector, 8192, KvCacheKind.F16), tight);

        Assert.Equal(FitRegime.FitsRamOnly, pessimistic);   //this is the misreport nobody would investigate.
        Assert.Equal(FitRegime.FitsGpu, honest);
        Assert.Equal(honest, ProjectorPair.Regime(ModelHeader(), model, projector, 8192, KvCacheKind.F16, tight));

        //direction 2, at large context the KV cache grows and the flat widening does not, the wrong header promises a pair that cannot load
        var roomy = Machine(9_000_000_000, 32_000_000_000);
        var optimistic = FitArithmetic.Judge(
            FitArithmetic.Estimate(ProjectorHeader(), model + projector, 32768, KvCacheKind.F16), roomy);
        var honestAt32k = FitArithmetic.Judge(
            FitArithmetic.Estimate(ModelHeader(), model + projector, 32768, KvCacheKind.F16), roomy);

        Assert.Equal(FitRegime.FitsGpu, optimistic);        //the worse misreport of the two, a promise the machine cannot keep.
        Assert.Equal(FitRegime.FitsRamOnly, honestAt32k);
        Assert.Equal(honestAt32k,
            ProjectorPair.Regime(ModelHeader(), model, projector, 32768, KvCacheKind.F16, roomy));
    }

    [Fact]
    public void A_CPU_ONLY_MACHINE_NEVER_YIELDS_A_GPU_PAIR_VERDICT()
    {
        //a CPU-only reading must never yield a GPU verdict for the pair, the 8 GB graphics budget in the fixture is bait
        var cpuOnly = new HardwareClass(
            MemoryTopology.CpuOnly, ShareKind.None, 8_000_000_000, 32_000_000_000,
            new HardwareSnapshot(0UL, 1UL, GpuKind.None, 0UL), 0, BudgetBound.None);

        Assert.Equal(FitRegime.FitsRamOnly, ProjectorPair.Regime(
            ModelHeader(), 4_000_000_000, 1_000_000_000, 8192, KvCacheKind.F16, cpuOnly));
        //the pair changes no regime here, no warning may fire
        Assert.False(ProjectorPair.CostsARegime(
            ModelHeader(), 4_000_000_000, 1_000_000_000, 8192, KvCacheKind.F16, cpuOnly));
    }
}
