using Gatto.Core.Hardware;

namespace Gatto.Tests;

//the tests target ParseReport, the pure half, since the suite never starts a process. fixture files and one live run witness the child's own output
public class HardwareProbeTests
{
    //this text is a real capture, and the same bytes stand as a fixture file.
    private const string DynamicShare = """
        installed=137438953472
        visible=136524402688
        cpu=AMD RYZEN AI MAX+ 395 w/ Radeon 8060S          
        vulkan=ok physical_devices=1
        device_0_name=AMD Radeon(TM) 8060S Graphics
        device_0_type=INTEGRATED_GPU (1)
        device_0_vendor=0x1002 device_id=0x1586
        device_0_api=1.4.349 driver_raw=8389003
        device_0_heap_count=2 type_count=16
        device_0_heap_0=44321800192 flags=0x00 -
        device_0_heap_1=88643731456 flags=0x03 DEVICE_LOCAL|MULTI_INSTANCE
        device_0_type_0=heap 1 flags=0x0001
        """;

    [Fact]
    public void A_DYNAMIC_SHARE_REPORT_PARSES_TO_AN_INTEGRATED_DEVICE_WITH_ITS_DEVICE_LOCAL_HEAP()
    {
        var r = HardwareProbe.ParseReport(DynamicShare);
        Assert.NotNull(r.Snapshot);
        Assert.Equal(GpuKind.Integrated, r.Snapshot!.GraphicsKind);
        Assert.Equal(88_643_731_456UL, r.Snapshot.GraphicsMemoryBytes);   //heap 1 wins because bit 0 of its flags is set, which an equality test on 0x01 would miss
        Assert.Equal("1002", r.Snapshot.GraphicsVendorId);
        Assert.Equal("AMD Radeon(TM) 8060S Graphics", r.GpuName);
        Assert.Equal(137_438_953_472UL, r.Snapshot.InstalledBytes);
    }

    [Fact]
    public void A_DEVICE_LOCAL_HEAP_IS_ONE_WITH_BIT_0_SET_WHATEVER_ELSE_IS_SET()
    {
        //bit 0 of the flags marks a device-local heap. the larger heap in this synthetic fixture sets only bit 1, so an any-bit reading would pick it
        const string twoBits = """
            visible=34359738368
            vulkan=ok physical_devices=1
            device_0_name=synthetic
            device_0_type=INTEGRATED_GPU (1)
            device_0_vendor=0x1002 device_id=0x0000
            device_0_heap_count=2 type_count=1
            device_0_heap_0=68719476736 flags=0x02 -|MULTI_INSTANCE
            device_0_heap_1=34359738368 flags=0x03 DEVICE_LOCAL|MULTI_INSTANCE
            """;
        var r = HardwareProbe.ParseReport(twoBits);
        Assert.Equal(GpuKind.Integrated, r.Snapshot!.GraphicsKind);
        Assert.Equal(34_359_738_368UL, r.Snapshot.GraphicsMemoryBytes);
    }

    [Theory]
    [InlineData("vulkan=absent (vulkan-1.dll not found: no vendor GPU driver)")]
    [InlineData("vulkan=create_instance_failed result=-9")]
    [InlineData("error_vulkan=anything")]
    [InlineData("vulkan=ok physical_devices=0")]
    public void NO_USABLE_VULKAN_IS_THE_NONE_KIND_WITH_NO_GRAPHICS_MEMORY(string vulkanLine)
    {
        var r = HardwareProbe.ParseReport("installed=1\nvisible=2147483648\n" + vulkanLine + "\n");
        Assert.Equal(GpuKind.None, r.Snapshot!.GraphicsKind);
        Assert.Null(r.Snapshot.GraphicsMemoryBytes);
        Assert.Null(r.GpuName);
    }

    [Fact]
    public void A_DISCRETE_CARD_BEATS_A_LARGER_INTEGRATED_HEAP()
    {
        const string laptop = """
            visible=34359738368
            vulkan=ok physical_devices=2
            device_0_name=Intel(R) Iris(R) Xe Graphics
            device_0_type=INTEGRATED_GPU (1)
            device_0_vendor=0x8086 device_id=0x9A49
            device_0_heap_count=1 type_count=1
            device_0_heap_0=16106127360 flags=0x01 DEVICE_LOCAL
            device_1_name=NVIDIA GeForce RTX 4060 Laptop GPU
            device_1_type=DISCRETE_GPU (2)
            device_1_vendor=0x10DE device_id=0x28E0
            device_1_heap_count=3 type_count=5
            device_1_heap_0=8589934592 flags=0x01 DEVICE_LOCAL
            device_1_heap_1=17179869184 flags=0x00 -
            device_1_heap_2=268435456 flags=0x01 DEVICE_LOCAL
            """;
        var r = HardwareProbe.ParseReport(laptop);
        Assert.Equal(GpuKind.Discrete, r.Snapshot!.GraphicsKind);
        Assert.Equal(8_589_934_592UL, r.Snapshot.GraphicsMemoryBytes);   //the budget figure is the largest device-local heap rather than the sum of both
        //assert the sum as well, a one-heap fixture can't tell the largest heap from the sum
        Assert.Equal(8_858_370_048UL, r.Snapshot.GraphicsLocalTotalBytes);
        Assert.Equal("10DE", r.Snapshot.GraphicsVendorId);
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", r.GpuName);
        Assert.Null(r.ServeOnlyGpu);   //the integrated budget is larger but under the floor, so llama.cpp's own pick stands and nothing is pinned
    }

    //a unified pool past the floor and larger than every discrete card together is the GPU, pinned since llama-server alone takes the card
    [Fact]
    public void A_LARGE_UNIFIED_POOL_BEATS_A_SMALL_DISCRETE_EGPU()
    {
        var r = HardwareProbe.ParseReport(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hwprobe",
                "unified-128gb-dynamic-plus-vega-egpu.txt")));

        Assert.Equal(GpuKind.Integrated, r.Snapshot!.GraphicsKind);
        Assert.Equal(88_643_731_456UL, r.Snapshot.GraphicsMemoryBytes);
        Assert.Null(r.Snapshot.MoreDiscreteHeaps);
        Assert.Equal("AMD Radeon(TM) 8060S Graphics", r.GpuName);
        Assert.Equal("AMD Radeon(TM) 8060S Graphics", r.ServeOnlyGpu);

        var c = HardwareClassifier.Classify(r.Snapshot);
        Assert.Equal(MachineShape.UnifiedWithShare, c.Shape);
        Assert.True(c.GpuBudgetBytes > 64UL << 30, $"budget {c.GpuBudgetBytes}");
    }

    //two discrete cards add up, each less its own reserve, since llama-server spreads a model over every discrete device it sees
    [Fact]
    public void TWO_DISCRETE_CARDS_ADD_THEIR_BUDGETS()
    {
        const ulong card = 24UL << 30;
        var two = "visible=68719476736\nvulkan=ok physical_devices=2\n"
            + $"device_0_name=card a\ndevice_0_type=DISCRETE_GPU (2)\ndevice_0_vendor=0x10DE device_id=0x1\ndevice_0_heap_count=1 type_count=1\ndevice_0_heap_0={card} flags=0x01 DEVICE_LOCAL\n"
            + $"device_1_name=card b\ndevice_1_type=DISCRETE_GPU (2)\ndevice_1_vendor=0x10DE device_id=0x1\ndevice_1_heap_count=1 type_count=1\ndevice_1_heap_0={card} flags=0x01 DEVICE_LOCAL\n";
        var r = HardwareProbe.ParseReport(two);

        Assert.Equal(GpuKind.Discrete, r.Snapshot!.GraphicsKind);
        Assert.Equal(new[] { card }, r.Snapshot.MoreDiscreteHeaps);
        Assert.Equal("card a + card b", r.GpuName);
        Assert.Null(r.ServeOnlyGpu);

        var c = HardwareClassifier.Classify(r.Snapshot);
        Assert.Equal(MachineShape.Discrete, c.Shape);
        Assert.Equal(2 * (card - 1_610_612_736UL), c.GpuBudgetBytes);
    }

    //the largest heap is what one allocation can reach, so it feeds the budget. the sum is what the operating system cannot see, so it feeds the carve-out check
    [Fact]
    public void A_CARVE_OUT_MACHINE_SPLITS_ITS_CARVE_OUT_ACROSS_TWO_HEAPS()
    {
        var r = HardwareProbe.ParseReport(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hwprobe",
                "unified-32gb-1gb-carveout.txt")));

        Assert.Equal(GpuKind.Integrated, r.Snapshot!.GraphicsKind);
        Assert.Equal(805_306_368UL, r.Snapshot.GraphicsMemoryBytes);        //the larger heap of the split carve-out.
        Assert.Equal(1_073_741_824UL, r.Snapshot.GraphicsLocalTotalBytes);  //the sum of both device-local heaps.
        Assert.Equal("1002", r.Snapshot.GraphicsVendorId);
        Assert.Equal("AMD Radeon(TM) Vega 8 Graphics", r.GpuName);
    }

    //one device-local heap of exactly half the visible memory, so an integrated driver takes its heap from memory the operating system can see
    [Fact]
    public void AN_INTEL_INTEGRATED_HEAP_IS_HALF_OF_VISIBLE_MEMORY()
    {
        var r = HardwareProbe.ParseReport(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hwprobe",
                "unified-12gb-intel-dynamic-6gb-heap.txt")));

        Assert.Equal(GpuKind.Integrated, r.Snapshot!.GraphicsKind);
        Assert.Equal(6_398_564_352UL, r.Snapshot.GraphicsMemoryBytes);
        Assert.Equal(6_398_564_352UL, r.Snapshot.GraphicsLocalTotalBytes);
        Assert.Equal(12_797_128_704UL, r.Snapshot.OsVisibleBytes);
        Assert.Equal("8086", r.Snapshot.GraphicsVendorId);
    }

    //a failed graphics read counts as a missing one, so a fault after ok must drop every device. both spellings, vulkan=error and error_vulkan, close the same gate
    [Theory]
    [InlineData("vulkan=error InvalidOperationException: the driver went away")]
    [InlineData("error_vulkan=the driver went away")]
    public void A_FAILURE_AFTER_OK_DISCARDS_EVERY_DEVICE(string failureLine)
    {
        var partial = "visible=34359738368\n"
            + "vulkan=ok physical_devices=2\n"
            + "device_0_name=NVIDIA GeForce RTX 4060 Laptop GPU\n"
            + "device_0_type=DISCRETE_GPU (2)\n"
            + "device_0_vendor=0x10DE device_id=0x28E0\n"
            + "device_0_heap_count=1 type_count=1\n"
            + "device_0_heap_0=8589934592 flags=0x01 DEVICE_LOCAL\n"
            + failureLine + "\n";

        var r = HardwareProbe.ParseReport(partial);

        Assert.Equal(GpuKind.None, r.Snapshot!.GraphicsKind);
        Assert.Null(r.Snapshot.GraphicsMemoryBytes);
        Assert.Null(r.Snapshot.GraphicsLocalTotalBytes);
        Assert.Null(r.Snapshot.GraphicsVendorId);
        Assert.Null(r.GpuName);
    }

    //the lowercase row is the point, since real machines and every other fixture print uppercase. a case rule needs the other case, or it silently stops working
    [Theory]
    [InlineData("0x10DE", "10DE")]
    [InlineData("0x10de", "10DE")]
    [InlineData("0x8086", "8086")]
    public void A_VENDOR_ID_IS_NORMALISED_UPWARD(string printed, string expected)
    {
        var report = "visible=34359738368\n"
            + "vulkan=ok physical_devices=1\n"
            + "device_0_name=a card\n"
            + "device_0_type=DISCRETE_GPU (2)\n"
            + "device_0_vendor=" + printed + " device_id=0x28E0\n"
            + "device_0_heap_count=1 type_count=1\n"
            + "device_0_heap_0=8589934592 flags=0x01 DEVICE_LOCAL\n";

        Assert.Equal(expected, HardwareProbe.ParseReport(report).Snapshot!.GraphicsVendorId);
    }

    //an overlay layer in a windowless child hangs until the deadline, so implicit layers must be off. read the start info the product builds
    [Fact]
    public void THE_CHILD_STARTS_WITH_IMPLICIT_LAYERS_OFF_AND_NO_WINDOW()
    {
        var psi = HardwareProbe.ChildStartInfo(@"C:\somewhere\gatto.exe");

        Assert.Equal("~all~", psi.Environment["VK_LOADER_LAYERS_DISABLE"]);
        Assert.Equal("", psi.Environment["VK_INSTANCE_LAYERS"]);
        Assert.Equal(new[] { HardwareProbe.ChildWord }, psi.ArgumentList.ToArray());
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(@"C:\somewhere\gatto.exe", psi.FileName);
        //assert the output encoding, since without this line a mutation that deletes it leaves the suite green.
        Assert.Equal(System.Text.Encoding.UTF8, psi.StandardOutputEncoding);
    }

    [Theory]
    [InlineData("CPU (4)")]
    [InlineData("OTHER (0)")]
    [InlineData("VIRTUAL_GPU (3)")]
    public void CPU_OTHER_AND_VIRTUAL_DEVICES_ARE_NOT_GRAPHICS(string kind)
    {
        var r = HardwareProbe.ParseReport("visible=2147483648\nvulkan=ok physical_devices=1\ndevice_0_name=x\ndevice_0_type=" + kind
            + "\ndevice_0_vendor=0x0000 device_id=0x0000\ndevice_0_heap_count=1 type_count=1\ndevice_0_heap_0=68719476736 flags=0x01 DEVICE_LOCAL\n");
        Assert.Equal(GpuKind.None, r.Snapshot!.GraphicsKind);
    }

    [Fact]
    public void TIES_KEEP_THE_FIRST_DEVICE()
    {
        var two = "visible=2147483648\nvulkan=ok physical_devices=2\n"
            + "device_0_name=first\ndevice_0_type=DISCRETE_GPU (2)\ndevice_0_vendor=0x1002 device_id=0x1\ndevice_0_heap_count=1 type_count=1\ndevice_0_heap_0=8589934592 flags=0x01 DEVICE_LOCAL\n"
            + "device_1_name=second\ndevice_1_type=DISCRETE_GPU (2)\ndevice_1_vendor=0x10DE device_id=0x2\ndevice_1_heap_count=1 type_count=1\ndevice_1_heap_0=8589934592 flags=0x01 DEVICE_LOCAL\n";
        var r = HardwareProbe.ParseReport(two);
        Assert.Equal("1002", r.Snapshot!.GraphicsVendorId);   //the first card leads, so its vendor steers the engine
        Assert.Equal("first + second", r.GpuName);
    }

    [Fact]
    public void Missing_installed_is_a_nullable_hole_not_a_failure()   //firmware can hide its modules, so installed can be missing and the snapshot still parses
    {
        var s = HardwareProbe.ParseReport("visible=33982058496\n").Snapshot;
        Assert.NotNull(s);
        Assert.Null(s!.InstalledBytes);
    }

    [Fact]
    public void Missing_visible_fails_the_parse()          //visible memory is the only value the parse requires.
    {
        Assert.Null(HardwareProbe.ParseReport("installed=137438953472\n").Snapshot);
    }

    [Fact]
    public void Garbage_lines_and_error_lines_are_ignored_not_fatal()
    {
        var s = HardwareProbe.ParseReport(
            "error_installed=RPC server unavailable\nvisible=33982058496\nnoise\n"
            + "vulkan=ok physical_devices=1\ndevice_0_name=x\ndevice_0_type=INTEGRATED_GPU (1)\n"
            + "device_0_vendor=0x1002 device_id=0x0\ndevice_0_heap_count=1 type_count=1\n"
            + "device_0_heap_0=notanumber flags=0x01 DEVICE_LOCAL\n").Snapshot;
        Assert.NotNull(s);
        Assert.Null(s!.InstalledBytes);
        Assert.Equal(GpuKind.None, s.GraphicsKind);         //unparseable heap bytes leave no heap rather than a zero heap
        Assert.Null(s.GraphicsMemoryBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n \n")]
    public void Degenerate_input_parses_to_null(string text) =>
        Assert.Null(HardwareProbe.ParseReport(text).Snapshot);                 //degenerate input stands in for an empty stdout read.

    [Fact]
    public void THE_CPU_NAME_COMES_OFF_ITS_OWN_LINE_AND_IS_ABSENT_WHEN_UNREAD()
    {
        //the parser keeps the cpu name's own spacing, since trimming is a display choice with its own tests
        var read = HardwareProbe.ParseReport("visible=1\ncpu=AMD RYZEN AI MAX+ 395 w/ Radeon 8060S\n");
        Assert.Equal("AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", read.CpuName);

        Assert.Null(HardwareProbe.ParseReport("visible=1\nerror_cpu=RPC server unavailable\n").CpuName);
    }

    //the classifier's input must never hold a device or cpu name, so this test reads the record's own string fields. a name field added later fails here
    [Fact]
    public void NO_NAME_REACHES_THE_SNAPSHOT()
    {
        var r = HardwareProbe.ParseReport(
            "visible=1\ncpu=AMD RYZEN AI MAX+ 395\nvulkan=ok physical_devices=1\n"
            + "device_0_name=AMD Radeon(TM) 8060S Graphics\ndevice_0_type=INTEGRATED_GPU (1)\n"
            + "device_0_vendor=0x1002 device_id=0x1586\ndevice_0_heap_count=1 type_count=1\n"
            + "device_0_heap_0=1073741824 flags=0x01 DEVICE_LOCAL\n");

        Assert.NotNull(r.CpuName);                             //the names must be read, so the check below is not vacuous.
        Assert.NotNull(r.GpuName);

        //the only string in the snapshot is the vendor id, four hex digits that cannot be a name
        var strings = typeof(HardwareSnapshot).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(r.Snapshot))
            .ToList();
        Assert.Equal(["1002"], strings);
    }
}
