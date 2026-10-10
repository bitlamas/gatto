using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Hardware;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the fixtures are named for the shape they witness and their numbers come from the generator's MACH table
public class MachineTests
{
    private const ulong Gib = 1024UL * 1024 * 1024;

    //reads RenderedWalkTests.Unified96, so the two copies of this 128 GB machine cannot disagree
    private static HardwareSnapshot Unified96 => Gatto.Tests.Setup.RenderedWalkTests.Unified96;

    //a real machine's own words, don't tidy them, that would need a table of hardware words the naming rules refuse
    private static readonly HardwareNames Names = new("AMD RYZEN AI MAX+ 395", "AMD Radeon 8060S Graphics");

    private static SetupFlow Flow(HardwareSnapshot? hw, HardwareNames names) =>
        new(new WizardProbes { Llama = null, Snapshot = hw, Names = names });

    private static WizardScreen.Choice Machine(HardwareSnapshot? hw, HardwareNames names)
    {
        var flow = Flow(hw, names);
        flow.Start();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.WelcomeGo));
    }

    //a discrete card, 32 GB installed with an 8 GB card whose display reserve leaves about 6.5 GB for a model
    private static readonly HardwareSnapshot Discrete8 = new(32 * Gib, 34_281_705_472, GpuKind.Discrete, 8_573_157_376);

    //the carve-out, 1 GiB belongs to the chip and none of it is usable, so the budget is zero
    private static readonly HardwareSnapshot Carveout1 = new(32 * Gib, 33_155_784_704, GpuKind.Integrated, 1 * Gib);

    //a sweep from 78 to 120, two goldens cannot see a row that only overflows between them
    [Fact]
    public void EVERY_ROW_FITS_AT_EVERY_WIDTH_THE_LADDER_SERVES()
    {
        for (var w = 78; w <= 120; w++)
            foreach (var row in WalkRender.Choice(Machine(Unified96, Names), w).Rows)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= w,
                    $"at width {w} a row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells: {row}");
    }

    //a wrapped fact row must not end on a single word, so the guard keys on the hang indent
    [Fact]
    public void NO_FACT_ROW_ENDS_ON_A_ONE_WORD_CONTINUATION()
    {
        var hang = new string(' ', 2 + MachineFacts.Column);

        for (var w = 78; w <= 120; w++)
        {
            var rows = WalkRender.Choice(Machine(Unified96, Names), w).Rows;
            foreach (var row in rows.Where(r => r.StartsWith(hang, StringComparison.Ordinal)))
                Assert.True(row.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1,
                    $"at width {w} a wrapped fact row ends on one word: '{row.Trim()}'");
        }
    }

    [Fact]
    public void THE_DISCRETE_CARD_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s3", "discrete-8-done", 100,
            WalkRender.Choice(Machine(Discrete8, new HardwareNames("AMD Ryzen 7 6800H", "AMD Radeon RX 7600M")), 100).Rows);

    [Fact]
    public void THE_CARVEOUT_MACHINE_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s3", "carveout-1-done", 100,
            WalkRender.Choice(Machine(Carveout1, new HardwareNames("AMD Ryzen 5 PRO 2500U", "AMD Radeon Vega 8")), 100).Rows);

    //no usable graphics device, so the screen says so and prices the model against system memory
    [Fact]
    public void THE_CPU_ONLY_MACHINE_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s3", "cpu-only", 100,
            WalkRender.Choice(Machine(new HardwareSnapshot(32 * Gib, 16 * Gib, GpuKind.None, null),
                new HardwareNames("AMD Ryzen 5 PRO 2500U", null)), 100).Rows);

    //a real capture of a 128 GB machine with a dynamic share
    [Fact]
    public void THE_DYNAMIC_SHARE_MATCHES_ITS_GOLDEN_AT_100() =>
        Golden.AssertEquals("s3", "dynamic-83-done", 100,
            WalkRender.Choice(Machine(HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"), Names), 100).Rows);

    [Fact]
    public void AND_THE_DYNAMIC_SHARE_AT_80() =>
        Golden.AssertEquals("s3", "dynamic-83-80", 80,
            WalkRender.Choice(Machine(HwFixture.Fixture("unified-128gb-dynamic-83gb-heap.txt"), Names), 80).Rows);

    //the row names the bound the OS floor set, subtracting the budget from the heap would invent a reserve
    [Fact]
    public void THE_FLOOR_BOUND_MACHINE_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s3", "floor-bound-8-done", 100,
            WalkRender.Choice(
                Machine(HwFixture.Fixture("unified-8gb-visible-83gb-heap-floor-bound.txt"), Names), 100).Rows);

    //the 80-column twin, a single-width oracle is blind to the compact wording (its breaks come from the 62-cell value width)
    [Fact]
    public void AND_THE_FLOOR_BOUND_MACHINE_AT_80() =>
        Golden.AssertEquals("s3", "floor-bound-8-80", 80,
            WalkRender.Choice(
                Machine(HwFixture.Fixture("unified-8gb-visible-83gb-heap-floor-bound.txt"), Names), 80).Rows);

    //both HasRunsColumn and LegendFor key on Discrete, so the Discrete row is what proves the other three shapes get a sentence
    [Theory]
    [InlineData((int)MachineShape.UnifiedWithShare, false)]
    [InlineData((int)MachineShape.UnifiedNoShare, false)]
    [InlineData((int)MachineShape.CpuOnly, false)]
    [InlineData((int)MachineShape.Discrete, true)]
    public void THE_SHELF_READS_DISCRETE_VERSUS_NOT_AND_A_DYNAMIC_SHARE_IS_NOT_DISCRETE(
        int shape, bool runsColumn)
    {
        Assert.Equal(runsColumn, FitMarks.HasRunsColumn((MachineShape)shape));
        Assert.Equal(!runsColumn, FitMarks.LegendFor((MachineShape)shape, null).Text == "fewer params = faster");
    }

    //every heap in 64 MiB steps through the real flow, no row may say about 0 GB (WholeGb rounds)
    [Fact]
    public void NO_SHAPE_EVER_PRINTS_ABOUT_0_GB_AT_ANY_HEAP()
    {
        const ulong step = 64UL << 20;
        var offenders = new List<string>();

        for (ulong heap = 0; heap <= 8UL << 30; heap += step)
        {
            foreach (var (kind, installed, visible) in new (GpuKind, ulong, ulong)[]
            {
                (GpuKind.Integrated, 16UL << 30, 15UL << 30),              //dynamic: nothing hidden
                (GpuKind.Integrated, 16UL << 30, (16UL << 30) - heap),     //carve-out: the heap is hidden
                (GpuKind.Discrete, 16UL << 30, 15UL << 30),
            })
            {
                //a zero heap is the device-less machine, the parser never emits a kind without a heap
                var snapshot = heap == 0
                    ? new HardwareSnapshot(installed, visible, GpuKind.None, null)
                    : new HardwareSnapshot(installed, visible, kind, heap, "1002", heap);

                foreach (var row in WalkRender.Choice(Machine(snapshot, Names), 100).Rows)
                    if (row.Contains("about 0 GB", StringComparison.Ordinal))
                        offenders.Add($"{kind} heap {heap}: {row.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A machine screen printed a figure the reader cannot act on:\n  "
            + string.Join("\n  ", offenders.Take(12))
            + (offenders.Count > 12 ? $"\n  ... and {offenders.Count - 12} more" : ""));

        //this string must match, otherwise the sweep above would pass with a pattern that finds nothing
        Assert.Contains("about 0 GB", "a model can use about 0 GB of it", StringComparison.Ordinal);
    }

    [Fact]
    public void THE_UNIFIED_MACHINE_MATCHES_ITS_GOLDEN_AT_100() =>
        Golden.AssertEquals("s3", "unified-96-done", 100,
            WalkRender.Choice(Machine(Unified96, Names), 100).Rows);

    //at 80 the why-clauses shorten and the facts stay, dropping a number would answer a different question
    [Fact]
    public void AND_AT_80() =>
        Golden.AssertEquals("s3", "unified-96-80", 80,
            WalkRender.Choice(Machine(Unified96, Names), 80).Rows);
}
