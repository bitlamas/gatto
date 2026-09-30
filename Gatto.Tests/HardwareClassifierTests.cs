using Gatto.Core.Hardware;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//each row reads a captured machine from its fixture file, so a new machine arrives as a file plus a row
public class HardwareClassifierTests
{
    private const ulong Gib = 1UL << 30;

    [Theory]
    //the dynamic share is the heap minus the larger of two GiB and eight percent, and the operating-system floor does not bind here.
    [InlineData("unified-128gb-dynamic-83gb-heap.txt", (int)MemoryTopology.Unified, (int)ShareKind.Dynamic,
        81_552_232_940UL, 7_091_498_516UL, (int)BudgetBound.Heap)]
    //a carve-out returns before the floor is computed, so this row binds to the heap. applying the floor to every unified machine would read OsFloor here
    [InlineData("unified-128gb-96gb-carveout.txt", (int)MemoryTopology.Unified, (int)ShareKind.CarvedOut,
        94_832_877_896UL, 8_246_337_208UL, (int)BudgetBound.Heap)]
    //carve-out is decided by the sum of the two device-local heaps, and the budget uses the larger one, which is under the two GiB reserve
    [InlineData("unified-32gb-1gb-carveout.txt", (int)MemoryTopology.Unified, (int)ShareKind.CarvedOut,
        0UL, 2_147_483_648UL, (int)BudgetBound.Heap)]
    //one device-local heap of half the visible memory, and the two GiB reserve binds before the operating-system floor.
    [InlineData("unified-12gb-intel-dynamic-6gb-heap.txt", (int)MemoryTopology.Unified, (int)ShareKind.Dynamic,
        4_251_080_704UL, 2_147_483_648UL, (int)BudgetBound.Heap)]
    //a discrete card subtracts its own display reserve. the budget takes the largest device-local heap, since a small extra heap is a window onto the same memory
    [InlineData("discrete-32gb-8gb-vram.txt", (int)MemoryTopology.Discrete, (int)ShareKind.None,
        6_694_109_184UL, 1_610_612_736UL, (int)BudgetBound.None)]
    //windows ships vulkan-1.dll, so a driverless machine fails at vkCreateInstance, and the fixture must report that spelling
    [InlineData("cpu-only-sandbox.txt", (int)MemoryTopology.CpuOnly, (int)ShareKind.None,
        0UL, 0UL, (int)BudgetBound.None)]
    //this is the only row where the operating-system floor binds. the reserve must not be read off heap minus budget
    [InlineData("unified-8gb-visible-83gb-heap-floor-bound.txt", (int)MemoryTopology.Unified, (int)ShareKind.Dynamic,
        4_293_857_280UL, 7_091_498_516UL, (int)BudgetBound.OsFloor)]
    public void Classifies_the_fixture_machines(
        string fixture, int topology, int share, ulong gpuBudget, ulong reserve, int bound)
    {
        var c = HardwareClassifier.Classify(HwFixture.Fixture(fixture));
        Assert.Equal((MemoryTopology)topology, c.Topology);
        Assert.Equal((ShareKind)share, c.Share);
        Assert.Equal(gpuBudget, c.GpuBudgetBytes);
        Assert.Equal(reserve, c.ReserveBytes);
        Assert.Equal((BudgetBound)bound, c.Bound);
        Assert.Equal(c.Snapshot.OsVisibleBytes / 2, c.RamBudgetBytes);
    }

    //the synthetic snapshot pins the rule at a chosen boundary, while a fixture pins a machine. no driver reports the host heap with its own small visible memory
    [Fact]
    public void THE_DYNAMIC_BOUND_BINDS_ON_A_SMALL_MACHINE_AND_NOT_ON_A_LARGE_SHARE()
    {
        var small = new HardwareSnapshot(16 * Gib, (ulong)(15.8 * Gib), GpuKind.Integrated, 15 * Gib);
        var c = HardwareClassifier.Classify(small);
        Assert.Equal(ShareKind.Dynamic, c.Share);

        var keepFree = Math.Max(4 * Gib, small.OsVisibleBytes / 4);
        Assert.Equal(small.OsVisibleBytes - keepFree, c.GpuBudgetBytes);   //here the operating-system floor sets the budget
        Assert.True(c.GpuBudgetBytes < 15 * Gib - Math.Max(2 * Gib, 15 * Gib * 8 / 100));

        var dynamicShare = HardwareClassifier.Classify(HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"));
        Assert.Equal(81_552_232_940UL, dynamicShare.GpuBudgetBytes);              //on the measured machine the heap sets the budget instead.
    }

    //installed can be missing when firmware hides its modules, so the share reads as dynamic rather than risk spending the system's own pool
    [Fact]
    public void INSTALLED_UNREADABLE_ON_AN_INTEGRATED_DEVICE_IS_A_DYNAMIC_SHARE()
    {
        var c = HardwareClassifier.Classify(
            new HardwareSnapshot(null, 136_524_402_688, GpuKind.Integrated, 88_643_731_456));
        Assert.Equal(ShareKind.Dynamic, c.Share);
        Assert.Equal(81_552_232_940UL, c.GpuBudgetBytes);
    }

    [Fact]
    public void A_CARVE_OUT_IS_DECIDED_BY_THE_GAP_TRACKING_THE_HIDDEN_TOTAL()
    {
        //the gap tracks the hidden total within the tolerance, so the share is a carve-out.
        var carve = HardwareClassifier.Classify(
            new HardwareSnapshot(128 * Gib, 33_982_058_496, GpuKind.Integrated, 96 * Gib));
        Assert.Equal(ShareKind.CarvedOut, carve.Share);

        //the same heap with nothing hidden must read as dynamic, so the first case cannot pass by itself.
        var dyn = HardwareClassifier.Classify(
            new HardwareSnapshot(128 * Gib, 136_524_402_688, GpuKind.Integrated, 96 * Gib));
        Assert.Equal(ShareKind.Dynamic, dyn.Share);
    }

    //the comparison uses the device-local total, so the same machine without it reads as dynamic
    [Fact]
    public void THE_HIDDEN_TOTAL_DECIDES_THE_CARVE_OUT_NOT_THE_LARGEST_HEAP()
    {
        var withTotal = new HardwareSnapshot(34_359_738_368, 33_155_784_704, GpuKind.Integrated,
            805_306_368, "1002", 1_073_741_824);
        Assert.Equal(ShareKind.CarvedOut, HardwareClassifier.Classify(withTotal).Share);

        var largestOnly = withTotal with { GraphicsLocalTotalBytes = null };
        Assert.Equal(ShareKind.Dynamic, HardwareClassifier.Classify(largestOnly).Share);
    }

    //the reserve comes from the heap the model allocates from, otherwise it charges memory the model cannot reach
    [Fact]
    public void THE_RESERVE_IS_TAKEN_FROM_THE_HEAP_THE_MODEL_USES_NOT_FROM_THE_TOTAL()
    {
        //the gap tracks the 40 GiB total, while the model's own heap is 30 GiB.
        var s = new HardwareSnapshot(96 * Gib, 56 * Gib, GpuKind.Integrated, 30 * Gib, "1002", 40 * Gib);
        var c = HardwareClassifier.Classify(s);

        Assert.Equal(ShareKind.CarvedOut, c.Share);
        //the reserve is eight percent of the model's heap rather than of the larger total
        Assert.Equal(29_635_274_343UL, c.GpuBudgetBytes);
    }

    //the floor is a quarter of visible memory when that beats a flat 4 GiB, which happens above 16 GiB visible
    [Fact]
    public void THE_FLOOR_IS_A_QUARTER_OF_VISIBLE_WHEN_THAT_BEATS_FOUR_GIGABYTES()
    {
        var s = new HardwareSnapshot(65 * Gib, 64 * Gib, GpuKind.Integrated, 60 * Gib, null, 60 * Gib);
        var c = HardwareClassifier.Classify(s);

        Assert.Equal(ShareKind.Dynamic, c.Share);
        //the quarter floor takes 16 GiB, and the heap alone would have allowed a much larger budget.
        Assert.Equal(48 * Gib, c.GpuBudgetBytes);
    }

    [Fact]
    public void Band_is_injectable_data()
    {
        var s = new HardwareSnapshot(64 * Gib, 48 * Gib, GpuKind.Integrated, 16 * Gib);   //the gap tracks the total exactly.
        Assert.Equal(ShareKind.CarvedOut, HardwareClassifier.Classify(s).Share);

        //with zero tolerance only an exact track is a carve-out, so this one must still pass.
        var tight = new ClassifierBand(0.0, 4 * Gib, 4, Gib);
        Assert.Equal(ShareKind.CarvedOut, HardwareClassifier.Classify(s, tight).Share);

        //one byte off still passes under the default band, so only the injected band can fail it.
        var off = new HardwareSnapshot(64 * Gib, 48 * Gib + 1, GpuKind.Integrated, 16 * Gib);
        Assert.Equal(ShareKind.CarvedOut, HardwareClassifier.Classify(off).Share);
        Assert.Equal(ShareKind.Dynamic, HardwareClassifier.Classify(off, tight).Share);
    }

    //a budget under the minimum is no budget on both arms, since rounding prints about zero gigabytes. clamp the number rather than the sentence
    [Theory]
    //on the integrated arm the share stays under one GiB until the heap reaches 3 GiB.
    [InlineData((int)GpuKind.Integrated, 2_147_483_648UL, 0UL)]                 //a 2 GiB heap leaves zero even before the clamp.
    [InlineData((int)GpuKind.Integrated, 2_362_232_012UL, 0UL)]                 //0.2 GiB left, which would print about zero gigabytes.
    [InlineData((int)GpuKind.Integrated, 2_684_354_560UL, 0UL)]                 //0.5 GiB left, and rounding to even would print zero.
    [InlineData((int)GpuKind.Integrated, 2_791_728_742UL, 0UL)]                 //0.6 GiB left prints about one gigabyte, yet it is still no share.
    [InlineData((int)GpuKind.Integrated, 3_221_225_472UL, 1_073_741_824UL)]     //exactly one GiB left, the first heap that is a real share.
    //on the discrete arm the budget is the heap minus the 1.5 GiB display reserve.
    [InlineData((int)GpuKind.Discrete, 2_147_483_648UL, 0UL)]                   //a 2 GiB card leaves 0.5 GiB, which is no budget.
    [InlineData((int)GpuKind.Discrete, 2_684_354_560UL, 1_073_741_824UL)]       //a 2.5 GiB card leaves exactly one GiB.
    public void A_GRAPHICS_BUDGET_UNDER_A_GIGABYTE_IS_NO_BUDGET_ON_EITHER_ARM(
        int kind, ulong heap, ulong expected)
    {
        var c = HardwareClassifier.Classify(new HardwareSnapshot(16 * Gib, 15 * Gib, (GpuKind)kind, heap));
        Assert.Equal(expected, c.GpuBudgetBytes);

        //the shape must follow the clamped number, so a machine with no usable share never reports a share.
        Assert.Equal(
            expected > 0,
            c.Shape is MachineShape.UnifiedWithShare || (c.Shape == MachineShape.Discrete && c.GpuBudgetBytes > 0));
    }

    [Fact]
    public void A_discrete_card_takes_the_display_reserve_and_never_reads_the_gap()
    {
        //the 48 GiB gap is nonsense, and a discrete machine must ignore it.
        var c = HardwareClassifier.Classify(new HardwareSnapshot(64 * Gib, 16 * Gib, GpuKind.Discrete, 24 * Gib));
        Assert.Equal(MemoryTopology.Discrete, c.Topology);
        Assert.Equal(24 * Gib - 1_610_612_736, c.GpuBudgetBytes);
        Assert.Equal(ShareKind.None, c.Share);
    }

    //a machine with a dynamic share must never read as a graphics card whose budget is zero.
    [Fact]
    public void A_DYNAMIC_SHARE_IS_NEVER_A_GRAPHICS_CARD_WITH_NOTHING_IN_IT()
    {
        var c = HardwareClassifier.Classify(HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"));
        Assert.NotEqual(MemoryTopology.Discrete, c.Topology);
        Assert.Equal(MachineShape.UnifiedWithShare, c.Shape);
        Assert.True(c.GpuBudgetBytes > 70 * Gib);
    }
}
