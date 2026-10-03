namespace Gatto.Core.Hardware;

//what the memory is, and a machine with no usable graphics device is CpuOnly
internal enum MemoryTopology { Unified, Discrete, CpuOnly }

//where a unified machine's graphics memory comes from, which decides the budget bound and the sentence the memory row prints
internal enum ShareKind { None, CarvedOut, Dynamic }

//stated defaults the fixture round tightens, so tuning the band needs no code change (a value picked from a few fixtures is a guess)
internal sealed record ClassifierBand(
    double CarveoutTolerance, ulong OsFloorBytes, ulong OsFloorDivisor, ulong MinimumBudgetBytes)
{
    //the four defaults: 0.20 tolerance, 4 GiB or a quarter of visible free, 1 GiB minimum (below it the figure prints as about 0 GB)
    public static readonly ClassifierBand Default = new(0.20, 4294967296UL, 4, 1073741824UL);
}

//the record keeps what the memory is separate from what a model may spend, and ReserveBytes is the reserve actually subtracted
internal sealed record HardwareClass(
    MemoryTopology Topology, ShareKind Share, ulong GpuBudgetBytes, ulong RamBudgetBytes,
    HardwareSnapshot Snapshot, ulong ReserveBytes, BudgetBound Bound)
{
    //the shape comes from the budget rather than the topology label, and is honest only because Classify clamps a sub-minimum budget to zero
    public MachineShape Shape => Topology switch
    {
        MemoryTopology.CpuOnly => MachineShape.CpuOnly,
        MemoryTopology.Discrete => MachineShape.Discrete,
        _ => GpuBudgetBytes > 0 ? MachineShape.UnifiedWithShare : MachineShape.UnifiedNoShare,
    };
}

//what a machine is in the terms the copy needs: the topology crossed with whether there is a share to put a model in
internal enum MachineShape { Discrete, UnifiedWithShare, UnifiedNoShare, CpuOnly }

//which bound produced the budget, so the sentence names the bound rather than recomputing it from heap minus budget
internal enum BudgetBound { None, Heap, OsFloor }

internal static class HardwareClassifier
{
    //stated defaults tightened by a fixture round, and integer ratios so no cast truncation decides the number
    private const ulong UnifiedReserveFloorBytes = 2147483648;
    private const ulong UnifiedReservePercent = 8;                //room for llama.cpp compute buffers and fragmentation
    private const ulong DiscreteDisplayReserveBytes = 1610612736; //1.5 GiB for the compositor and display
    private const ulong RamBudgetDivisor = 2;                     //keeps half of visible RAM free for Windows and the browser

    //a laptop's integrated share of half its RAM stays under this, so only a machine built around its unified pool moves off the discrete card
    internal const ulong PoolOverCardsFloorBytes = 34359738368;

    //the shape comes from the device's kind rather than from memory arithmetic, and the installed minus visible gap says whether memory is hidden from Windows
    public static HardwareClass Classify(HardwareSnapshot s, ClassifierBand? band = null)
    {
        var b = band ?? ClassifierBand.Default;
        var ram = s.OsVisibleBytes / RamBudgetDivisor;

        //a budget under the minimum is clamped to zero, so nothing prints as about 0 GB while a GPU build is steered
        ulong Usable(ulong budget) => budget < b.MinimumBudgetBytes ? 0 : budget;

        switch (s.GraphicsKind)
        {
            case GpuKind.Discrete:
            {
                //each card pays its own reserve, which stands for its compute buffer as well as a display
                IReadOnlyList<ulong> heaps = [s.GraphicsMemoryBytes ?? 0, .. s.MoreDiscreteHeaps ?? []];
                ulong budget = 0;
                foreach (var heap in heaps)
                    budget += Usable(heap > DiscreteDisplayReserveBytes ? heap - DiscreteDisplayReserveBytes : 0);
                return new(MemoryTopology.Discrete, ShareKind.None, budget,
                    ram, s, DiscreteDisplayReserveBytes * (ulong)heaps.Count, BudgetBound.None);
            }

            case GpuKind.Integrated:
            {
                var heap = s.GraphicsMemoryBytes ?? 0;            //largest device-local heap: what one allocation reaches
                var hidden = s.GraphicsLocalTotalBytes ?? heap;   //sum of device-local heaps: what Windows can't see
                var reserve = Math.Max(UnifiedReserveFloorBytes, heap * UnifiedReservePercent / 100);
                var heapBudget = heap > reserve ? heap - reserve : 0;

                //compare against the sum of the device-local heaps, and with no installed reading assume dynamic (the errors aren't symmetric)
                var carved = false;
                if (s.InstalledBytes is { } installed && installed >= s.OsVisibleBytes)
                {
                    var gap = installed - s.OsVisibleBytes;
                    var tol = (ulong)(hidden * b.CarveoutTolerance);
                    carved = gap >= hidden ? gap - hidden <= tol : hidden - gap <= tol;
                }

                if (carved)
                    //a carve-out is already out of the pool Windows sees, so the floor never applies and the bound is the heap
                    return new(MemoryTopology.Unified, ShareKind.CarvedOut, Usable(heapBudget), ram, s,
                        reserve, BudgetBound.Heap);

                //a dynamic share is drawn from the memory Windows is using, so the budget is capped by what must stay free
                var keepFree = Math.Max(b.OsFloorBytes, s.OsVisibleBytes / b.OsFloorDivisor);
                var floorBudget = s.OsVisibleBytes > keepFree ? s.OsVisibleBytes - keepFree : 0;
                return new(MemoryTopology.Unified, ShareKind.Dynamic,
                    Usable(Math.Min(heapBudget, floorBudget)), ram, s,
                    reserve, heapBudget <= floorBudget ? BudgetBound.Heap : BudgetBound.OsFloor);
            }

            default:
                return new(MemoryTopology.CpuOnly, ShareKind.None, 0, ram, s, 0, BudgetBound.None);
        }
    }
}
