using Gatto.Cli.Setup;
using Gatto.Core.Hardware;
using Xunit;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//one hardware reading per run feeds both names and figures, or two reads could mix one run's figures with another run's chip name
public class HardwareReadingTests
{
    private static ProbeOutcome Reading() => new(
        new HardwareSnapshot(137438953472, 33982058496, GpuKind.Integrated, 103079215104, "1002"), "ok",
        CpuName: "AMD RYZEN AI MAX+ 395 w/ Radeon 8060S          ",
        GpuName: "AMD Radeon(TM) 8060S Graphics");

    [Fact]
    public void THE_MACHINE_IS_ASKED_EXACTLY_ONCE_HOWEVER_OFTEN_THE_FLOW_ASKS()
    {
        var reads = 0;
        using var probes = new LiveSetupProbes(
            Path.Combine(Path.GetTempPath(), "gatto-reading-home"), GlyphSet.Unicode, TextWriter.Null,
            readHardware: () => { reads++; return Reading(); });

        //the repeated calls mirror real use, where one screen asks twice and the flow asks six times across a run
        _ = probes.Hardware();
        _ = probes.HardwareNames();
        _ = probes.Hardware();
        _ = probes.HardwareNames();

        Assert.Equal(1, reads);
    }

    //a failed reading is latched too, or a retry could leave the welcome saying gatto cannot tell while the machine section shows numbers
    [Fact]
    public void A_FAILED_READING_IS_NOT_RETRIED()
    {
        var reads = 0;
        using var probes = new LiveSetupProbes(
            Path.Combine(Path.GetTempPath(), "gatto-reading-home"), GlyphSet.Unicode, TextWriter.Null,
            readHardware: () => { reads++; return new ProbeOutcome(null, "could not run PowerShell"); });

        Assert.Null(probes.Hardware());
        Assert.Null(probes.Hardware());
        Assert.Null(probes.HardwareNames().Cpu);

        Assert.Equal(1, reads);
    }

    //the names and the figures must come from one reading, with the names cleaned on the way out
    [Fact]
    public void THE_NAMES_ARE_CLEANED_FROM_THE_SAME_READING_AS_THE_NUMBERS()
    {
        using var probes = new LiveSetupProbes(
            Path.Combine(Path.GetTempPath(), "gatto-reading-home"), GlyphSet.Unicode, TextWriter.Null,
            readHardware: Reading);

        var names = probes.HardwareNames();

        Assert.Equal("AMD RYZEN AI MAX+ 395", names.Cpu);          //cleaning trims the padding and cuts the companion clause.
        Assert.Equal("AMD Radeon 8060S Graphics", names.Gpu);      //cleaning removes the trademark mark and nothing else.
        Assert.Equal(103079215104UL, probes.Hardware()!.GraphicsMemoryBytes);
    }
}
