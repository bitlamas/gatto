using Gatto.Core.Hardware;

namespace Gatto.Cli.Setup;

//the fact rows under the hardware line, worded the way a person names their own machine
internal static class MachineFacts
{
    //compute the column from the labels, a longer label moves it and nothing has to be told twice
    private static readonly string[] Labels =
        ["memory", "graphics", "on the card", "in memory", "models", "gatto", "cpu", "windows"];

    //the face splits a row at this column, so it is shared rather than private, two constants would be two answers
    internal static readonly int Column = Labels.Max(l => l.Length) + 3;

    //below 90 columns the why clauses shorten, the numbers they qualify stay put
    private const int CompactBelow = 90;

    public static IReadOnlyList<WizardRow> Rows(HardwareSnapshot? hw, HardwareNames names, int width)
    {
        if (hw is null) return Unread();

        var c = HardwareClassifier.Classify(hw);
        var compact = width < CompactBelow;
        var cpu = names.Cpu ?? "the processor";
        var gpu = names.Gpu ?? "the graphics chip";

        return c.Shape switch
        {
            MachineShape.CpuOnly => CpuOnly(hw, c, cpu, compact),
            MachineShape.UnifiedWithShare => Unified(hw, c, cpu, gpu, compact),
            MachineShape.Discrete => Discrete(hw, c, cpu, gpu, compact),
            _ => Carveout(hw, c, cpu, gpu, compact),
        };
    }

    //the probe read nothing, so these rows claim no numbers and must not borrow the CPU-only ones
    private static IReadOnlyList<WizardRow> Unread() =>
    [
        Fact("memory", "couldn't be read on this machine"),
        Fact("models", "nothing on the shelf is priced against this machine"),
    ];

    //no gpu argument here, a chip name above 'no graphics driver' would name what the row just said was missing
    private static IReadOnlyList<WizardRow> CpuOnly(
        HardwareSnapshot hw, HardwareClass c, string cpu, bool compact) =>
    [
        Fact("cpu", cpu),
        Fact("graphics", "no graphics driver gatto can use was found"),
        Fact("memory", $"{Gb(hw.InstalledBytes ?? hw.OsVisibleBytes)} installed"),
        Fact("models", (compact ? "run from memory: " : "run from system memory: ") + Gb(c.RamBudgetBytes)
            + (compact
                ? ", half free for Windows, browser, apps"
                : ", half kept free for Windows, browser and other apps")),
    ];

    private static IReadOnlyList<WizardRow> Unified(
        HardwareSnapshot hw, HardwareClass c, string cpu, string gpu, bool compact)
    {
        var vram = hw.GraphicsMemoryBytes ?? 0;
        //use the reserve the classifier actually subtracted, the bound may have been the OS floor rather than the share
        var carved = c.Share == ShareKind.CarvedOut;

        //print the device-local total for the memory row, a driver may hide one carve-out behind several heaps
        var hidden = hw.GraphicsLocalTotalBytes ?? vram;

        //compute what stays free from the graphics budget, half of visible memory is only true on a carve-out
        var free = hw.OsVisibleBytes > c.GpuBudgetBytes ? hw.OsVisibleBytes - c.GpuBudgetBytes : 0;

        return
        [
            Fact("cpu", cpu),
            Fact("graphics", gpu + (compact
                ? ", integrated, same memory as the processor"
                : ", integrated, it reads the same memory the processor does")),
            Fact("memory", $"{Gb(hw.InstalledBytes ?? hw.OsVisibleBytes)} installed, " + (carved
                ? Gb(hidden) + (compact ? " belongs to the graphics chip" : " of it belongs to the graphics chip")
                : "the graphics chip draws from it as needed")),
            Fact("models", $"up to about {Gb(c.GpuBudgetBytes)}" + (c.Bound == BudgetBound.OsFloor
                ? compact
                    ? ", limited by what Windows needs"
                    : $", limited by what Windows needs from {Gb(hw.OsVisibleBytes)}"
                : compact
                    ? $", {Gb(c.ReserveBytes)} kept for the engine"
                    : $", gatto keeps {Gb(c.ReserveBytes)} of the share for the engine")),
            Fact("windows", $"sees {Gb(hw.OsVisibleBytes)}; " + (carved
                ? compact
                    ? "half stays free for Windows, browser and other apps"
                    : "half of that stays free for Windows, browser and other apps"
                : $"at least {Gb(free)} stays free for Windows, browser and other apps")),
        ];
    }

    private static IReadOnlyList<WizardRow> Discrete(
        HardwareSnapshot hw, HardwareClass c, string cpu, string gpu, bool compact)
    {
        var vram = hw.GraphicsMemoryBytes ?? 0;
        return
        [
            Fact("cpu", cpu),
            Fact("graphics", gpu + ", its own " + Gb(vram)
                + (compact ? "; 1.5 GB stays with the display" : "; 1.5 GB of it stays with the display")),
            Fact("memory", $"{Gb(hw.InstalledBytes ?? hw.OsVisibleBytes)} installed"),
            //a card whose budget clamped to zero says it is too small for a model, rather than printing about 0 GB
            c.GpuBudgetBytes == 0
                ? Fact("on the card", compact
                    ? "too small for a model; the card helps part-way"
                    : "too small for a model; the card still helps part-way")
                : Fact("on the card", $"about {Gb(c.GpuBudgetBytes)}" + (compact
                    ? " for a model, full speed"
                    : " for a model, at the card's full speed")),
            Fact("in memory", $"about {Gb(c.RamBudgetBytes)}" + (compact
                ? " more, visibly slower; the card helps part-way"
                : " more, visibly slower; the card still helps part-way")),
        ];
    }

    //the memory row follows the share kind, carved out is only true of memory the firmware took from Windows
    private static IReadOnlyList<WizardRow> Carveout(
        HardwareSnapshot hw, HardwareClass c, string cpu, string gpu, bool compact)
    =>
    [
        Fact("cpu", cpu),
        Fact("graphics", gpu + ", integrated, almost no memory of its own"),
        Fact("memory", $"{Gb(hw.InstalledBytes ?? hw.OsVisibleBytes)} installed, " + (c.Share == ShareKind.CarvedOut
            ? $"{Gb(hw.GraphicsLocalTotalBytes ?? hw.GraphicsMemoryBytes ?? 0)} carved out for the graphics chip"
            : $"the graphics chip can reach only {Gb(hw.GraphicsMemoryBytes ?? 0)} of it")),
        Fact("models", (compact ? "run from memory: " : "run from system memory: ") + Gb(c.RamBudgetBytes)
            + (compact
                ? ", half free for Windows, browser, apps"
                : ", half kept free for Windows, browser and other apps")),
    ];

    //put any wrapped remainder under the text column, and highlight the GB figures, the goldens would match either way
    private static WizardRow Fact(string label, string text) =>
        new(label.PadRight(Column) + text, RowTone.Aside, Highlight: Figures(text), Indent: 0, Hang: Column);

    //sort the spans longest first, the face matches greedily and 8 GB inside 88 GB would split the number
    private static IReadOnlyList<string> Figures(string text) =>
    [
        .. System.Text.RegularExpressions.Regex.Matches(text, @"\d+(?:\.\d+)? GB")
            .Select(m => m.Value).Distinct().OrderByDescending(v => v.Length)
    ];

    //whole gigabytes, the same form the hardware line above uses
    private static string Gb(ulong bytes) => SizeWords.WholeGb(bytes);
}

//what this machine's parts are called, display-only so the classifier can't price from a name
internal readonly record struct HardwareNames(string? Cpu, string? Gpu);

//the flow holds what was measured and the face lays it out at its own width
internal readonly record struct MachineView(Gatto.Core.Hardware.HardwareSnapshot? Hardware, HardwareNames Names);
