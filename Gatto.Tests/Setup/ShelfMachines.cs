using Gatto.Core.Hardware;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the three machines the shelf wave measured, classified by gatto's own classifier
internal static class ShelfMachines
{
    private const ulong GiB = 1UL << 30;

    //an 8 GiB discrete card with 32 GiB of system memory
    public static readonly HardwareClass Vega = HardwareClassifier.Classify(
        new HardwareSnapshot(32 * GiB, 32 * GiB, GpuKind.Discrete, 8 * GiB, "0x1002"));

    //a 128 GiB APU sharing its memory with the integrated GPU, from the captured probe report
    public static readonly HardwareClass Apu8060S = HardwareClassifier.Classify(
        HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"));

    //a unified machine whose share gatto cannot use, so its card prices nothing
    public static readonly HardwareClass NoShare = HardwareClassifier.Classify(
        HwFixture.Fixture("unified-32gb-1gb-carveout.txt"));

    //no graphics card at all
    public static readonly HardwareClass NoCard = HardwareClassifier.Classify(
        new HardwareSnapshot(32 * GiB, 32 * GiB, GpuKind.None, null));

    //no card and 16 GiB, the machine where nothing in any generation is fast
    public static readonly HardwareClass NoCard16 = HardwareClassifier.Classify(
        new HardwareSnapshot(16 * GiB, 16 * GiB, GpuKind.None, null));
}
