using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//a gpu budget is no evidence on a cpu-only machine. the zeroing happens at the point of use, so a hand-built HardwareClass can't slip past it
public class CpuOnlyMachineFitTests
{
    private static GgufHeader Header() => new(
        GgufOutcome.Complete, null, "qwen3", "t", 32768, null,
        BlockCount: 48, HeadCount: 40, HeadCountKv: 8,
        EmbeddingLength: 5120, KeyLength: 128, ValueLength: 128, ChatTemplate: null);

    private static HardwareClass CpuOnly(ulong ramBudget, ulong gpuBudget = 0UL) => new(
        MemoryTopology.CpuOnly, ShareKind.None, gpuBudget, ramBudget,
        new HardwareSnapshot(null, 1UL, GpuKind.None, null), 0, BudgetBound.None);

    [Fact]
    public void A_CPU_ONLY_MACHINE_IS_JUDGED_AGAINST_THE_MEMORY_IT_DOES_KNOW()
    {
        var e = FitArithmetic.Estimate(Header(), 4_000_000_000, 4096, KvCacheKind.F16);
        Assert.Equal(FitRegime.FitsRamOnly, FitArithmetic.Judge(e, CpuOnly(32_000_000_000UL)));
    }

    [Fact]
    public void A_CPU_ONLY_MACHINE_STILL_REPORTS_DoesNotFit_WHEN_IT_GENUINELY_DOES_NOT()
    {
        var e = FitArithmetic.Estimate(Header(), 20_000_000_000, 4096, KvCacheKind.F16);
        Assert.Equal(FitRegime.DoesNotFit, FitArithmetic.Judge(e, CpuOnly(1_000_000_000UL)));
    }

    [Fact]
    public void A_CPU_ONLY_MACHINE_NEVER_FITS_THE_GPU_EVEN_WHEN_HANDED_A_BUDGET()
    {
        //the classify path never passes a gpu budget on this topology, so only a hand-built class can ask. a gpu fit here would recommend from an unreliable number
        var e = FitArithmetic.Estimate(Header(), 20_000_000_000, 4096, KvCacheKind.F16);
        var regime = FitArithmetic.Judge(e, CpuOnly(64_000_000_000UL, gpuBudget: 80_000_000_000UL));
        Assert.NotEqual(FitRegime.FitsGpu, regime);
        Assert.Equal(FitRegime.FitsRamOnly, regime);
    }

    private sealed class OneModelHandler : HttpMessageHandler
    {
        private const string Model =
            "{\"id\":\"unsloth/qwen3.5-9B-GGUF\",\"downloads\":900,\"gated\":false,"
            + "\"pipeline_tag\":\"text-generation\","
            + "\"gguf\":{\"architecture\":\"qwen3\",\"context_length\":32768,"
            + "\"total\":9000000000,\"causal\":true}}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.RequestUri!.ToString().Contains("/tree/main")
                ? "[{\"type\":\"file\",\"path\":\"qwen3.5-9B-Q4_K_M.gguf\",\"size\":5600000000,"
                  + "\"lfs\":{\"oid\":\"abc\"}}]"
                : "[" + Model + "]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
