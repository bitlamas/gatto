using Gatto.Core.Hardware;
using Gatto.Roles;

namespace Gatto.Tests;

//every steering branch must return a reason, so the user can explain the choice rather than just accept it
public class LlamaAssetSteeringTests
{
    private static HardwareClass Machine(ulong gpuBudget, string? vendor, MemoryTopology topology = MemoryTopology.Discrete) =>
        new(topology, topology == MemoryTopology.Unified ? ShareKind.CarvedOut : ShareKind.None,
            gpuBudget, 16_000_000_000,
            new HardwareSnapshot(34_359_738_368, 17_179_869_184, GpuKind.Discrete, 8_589_934_592, vendor), 0, BudgetBound.None);

    [Fact]
    public void An_NVIDIA_adapter_gets_CUDA_AND_the_cudart_companion_as_one_unit()
    {
        //a non-null CudartZipName is what makes the ui present both as one download
        var a = LlamaAssetSteering.Choose(Machine(8_000_000_000, "10DE"));

        Assert.Contains("cuda", a.ZipName);
        Assert.NotNull(a.CudartZipName);
        Assert.Contains("cudart", a.CudartZipName);
        Assert.Equal("CUDA", a.BuildName);
    }

    [Fact]
    public void An_AMD_discrete_card_gets_Vulkan_with_no_companion()
    {
        var a = LlamaAssetSteering.Choose(Machine(6_962_544_640, "1002"));
        Assert.Contains("vulkan", a.ZipName);
        Assert.Null(a.CudartZipName);
        Assert.Contains("discrete", a.HardwareFact);
    }

    //the words shown to the user must name Intel, since an intel adapter is only read through the vulkan loader
    [Fact]
    public void An_Intel_integrated_adapter_with_a_budget_is_named_in_words_not_hex()
    {
        var a = LlamaAssetSteering.Choose(
            Machine(4_251_080_704, "8086", MemoryTopology.Unified));

        Assert.Equal("an Intel integrated adapter sharing one memory pool", a.HardwareFact);
        Assert.Equal("Vulkan", a.BuildName);
        Assert.Null(a.CudartZipName);
        Assert.DoesNotContain("8086", a.HardwareFact, StringComparison.Ordinal);
    }

    //an intel adapter with no budget must not count as usable, or the arm fires on vendor alone
    [Fact]
    public void An_Intel_adapter_with_no_budget_is_still_no_usable_GPU()
    {
        var a = LlamaAssetSteering.Choose(Machine(0, "8086"));

        Assert.DoesNotContain("Intel", a.HardwareFact, StringComparison.Ordinal);
        Assert.DoesNotContain("8086", a.HardwareFact, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unified_AMD_APU_gets_Vulkan_and_the_reason_says_so()
    {
        var a = LlamaAssetSteering.Choose(
            Machine(94_832_877_896, "1002", MemoryTopology.Unified));
        Assert.Contains("vulkan", a.ZipName);
        Assert.Contains("one memory pool", a.HardwareFact);
    }

    [Fact]
    public void An_unknown_vendor_with_a_real_budget_still_gets_Vulkan()
    {
        //vulkan runs on every vendor, so an unreadable vendor id is not a dead end.
        var a = LlamaAssetSteering.Choose(Machine(6_000_000_000, null));
        Assert.Contains("vulkan", a.ZipName);
        Assert.Contains("could not be read", a.HardwareFact);
    }

    [Fact]
    public void No_GPU_budget_gets_the_cpu_build()
    {
        //a carve-out clamped to zero budget makes the machine a cpu target for steering, whatever the topology reads.
        var a = LlamaAssetSteering.Choose(Machine(0, "1002", MemoryTopology.Unified));
        Assert.Contains("cpu", a.ZipName);
        Assert.Null(a.CudartZipName);
    }

    //the windows assets the pinned release publishes, so a pin bump fails this list until someone reads the new release page
    private static readonly string[] Published =
    [
        "cudart-llama-bin-win-cuda-12.4-x64.zip",
        "cudart-llama-bin-win-cuda-13.4-arm64.zip",
        "cudart-llama-bin-win-cuda-13.4-x64.zip",
        "llama-b11071-bin-win-cpu-arm64.zip",
        "llama-b11071-bin-win-cpu-x64.zip",
        "llama-b11071-bin-win-cuda-12.4-x64.zip",
        "llama-b11071-bin-win-cuda-13.4-arm64.zip",
        "llama-b11071-bin-win-cuda-13.4-x64.zip",
        "llama-b11071-bin-win-opencl-adreno-arm64.zip",
        "llama-b11071-bin-win-openvino-2026.4-x64.zip",
        "llama-b11071-bin-win-rocm-10.0-x64.zip",
        "llama-b11071-bin-win-sycl-x64.zip",
        "llama-b11071-bin-win-vulkan-x64.zip",
    ];

    //a steered name the release does not publish is a download that fails on the user's machine, far from the choice that made it.
    [Theory]
    [InlineData("10DE")]
    [InlineData("1002")]
    [InlineData("8086")]
    [InlineData(null)]
    public void EVERY_STEERED_ZIP_IS_ONE_THE_PINNED_RELEASE_PUBLISHES(string? vendor)
    {
        foreach (var budget in new ulong[] { 8_000_000_000, 0 })
            foreach (var topology in new[] { MemoryTopology.Discrete, MemoryTopology.Unified })
            {
                var a = LlamaAssetSteering.Choose(Machine(budget, vendor, topology));
                Assert.Contains(a.ZipName, Published);
                if (a.CudartZipName is { } c) Assert.Contains(c, Published);
            }
    }

    [Fact]
    public void An_NVIDIA_card_with_no_spendable_budget_is_a_CPU_machine()
    {
        //a gpu whose budget is spent is not a gpu for steering. the classifier already subtracted the reserve, and a cpu-only machine reports zero.
        var a = LlamaAssetSteering.Choose(Machine(0, "10DE"));
        Assert.Contains("cpu", a.ZipName);
        Assert.Null(a.CudartZipName);
    }

    [Theory]
    [InlineData("10DE")]
    [InlineData("1002")]
    [InlineData("8086")]
    [InlineData(null)]
    public void Every_branch_returns_a_reason_and_the_pinned_release(string? vendor)
    {
        foreach (var budget in new ulong[] { 8_000_000_000, 0 })
        {
            var a = LlamaAssetSteering.Choose(Machine(budget, vendor));
            Assert.False(string.IsNullOrWhiteSpace(a.HardwareFact));
            Assert.False(string.IsNullOrWhiteSpace(a.BuildName));
            Assert.Contains(LlamaAssetSteering.PinnedRelease, a.ZipName);
        }
    }

    [Fact]
    public void The_pinned_release_is_a_smoke_tested_build_never_latest()
    {
        Assert.DoesNotContain("latest", LlamaAssetSteering.PinnedRelease);
        Assert.StartsWith("b", LlamaAssetSteering.PinnedRelease);
    }

    [Theory]
    [InlineData("\"C:\\tools\\llama\\llama-server.exe\"", "C:\\tools\\llama\\llama-server.exe")]  //windows copy-as-path wraps the path in double quotes.
    [InlineData("C:\\tools\\llama\\llama-server.exe", "C:\\tools\\llama\\llama-server.exe")]
    [InlineData("C:/tools/llama/llama-server.exe", "C:\\tools\\llama\\llama-server.exe")]
    [InlineData("  \"C:/tools\\llama/llama-server.exe\"  ", "C:\\tools\\llama\\llama-server.exe")]
    public void NormalizePath_strips_quotes_and_accepts_either_slash(string input, string expected)
        => Assert.Equal(expected, LlamaAssetSteering.NormalizePath(input));

    [Theory]
    [InlineData("'D:/llama'", @"D:\llama")]           //powershell quotes a pasted path with a single quote.
    [InlineData("it's/a/folder", @"it's\a\folder")]   //a lone quote inside a name stays part of the name
    public void NormalizePath_takes_SINGLE_quotes_too_and_leaves_a_lone_one_alone(string typed, string expected)
    {
        //this normalization has one home, so every caller must route through this method
        Assert.Equal(expected, LlamaAssetSteering.NormalizePath(typed));
    }
}
