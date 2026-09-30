using Gatto.Cli.Hardware;

namespace Gatto.Tests.Hardware;

//the memory and cpu reads run in process, so the suite checks the machine it runs on. a driver mock would only agree with its author

//declare the windows platform claim once on the class, matching the subject's own claim. the reads use kernel32 and the registry.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class MachineProbeCommandTests
{
    [Fact]
    public void THE_MEMORY_LINES_PARSE_ON_THE_MACHINE_RUNNING_THE_SUITE()
    {
        //a machine that runs a local model has at least a gigabyte visible, so a smaller reading is a broken read
        var lines = MachineProbeCommand.MemoryLines().ToList();

        var visible = lines.Single(l => l.StartsWith("visible=", StringComparison.Ordinal));
        Assert.True(ulong.Parse(visible["visible=".Length..]) >= 1UL << 30, visible);

        Assert.Contains(lines, l => l.StartsWith("installed=", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("cpu=", StringComparison.Ordinal) && l.Length > "cpu=".Length);
    }

    //installed comes from firmware and visible from the operating system, so installed is never below visible. the gap is a reservation under half the total
    [Fact]
    public void INSTALLED_IS_AT_OR_ABOVE_VISIBLE_AND_THE_GAP_IS_A_RESERVATION()
    {
        var lines = MachineProbeCommand.MemoryLines().ToList();
        var installedLine = lines.SingleOrDefault(l => l.StartsWith("installed=", StringComparison.Ordinal));
        Assert.NotNull(installedLine);

        var installed = ulong.Parse(installedLine!["installed=".Length..]);
        var visible = ulong.Parse(
            lines.Single(l => l.StartsWith("visible=", StringComparison.Ordinal))["visible=".Length..]);

        Assert.True(installed >= visible, $"installed {installed} < visible {visible}");
        Assert.True(installed - visible < installed / 2,
            $"a gap of {installed - visible} is more than half of {installed}: that is a carve-out "
            + "read as a reservation, or a broken read");
    }

    //the parser splits each line on the first =, so a line with no key would vanish without a trace
    [Fact]
    public void EVERY_MEMORY_LINE_IS_A_KEY_VALUE_PAIR_THE_PARSER_CAN_READ()
    {
        foreach (var line in MachineProbeCommand.MemoryLines())
        {
            var eq = line.IndexOf('=');
            Assert.True(eq > 0, $"not a key=value line: {line}");
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
        }
    }

    //inject the graphics section, the real one loads a driver into the test host. a failure must become an error_ line while the rest still prints
    [Fact]
    public void THE_REPORT_SURVIVES_A_GRAPHICS_SECTION_THAT_THROWS()
    {
        var report = MachineProbeCommand.Report(() => throw new InvalidOperationException("the driver went away"));

        Assert.Contains("visible=", report, StringComparison.Ordinal);
        Assert.Contains("error_vulkan=the driver went away", report, StringComparison.Ordinal);
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(line.IndexOf('=') > 0, $"not a key=value line: {line.Trim()}");
    }

    //the memory lines come first and the graphics section is appended whole, so a device line cannot precede the reading it belongs to.
    [Fact]
    public void THE_MEMORY_LINES_COME_BEFORE_THE_GRAPHICS_SECTION()
    {
        var report = MachineProbeCommand.Report(() => "vulkan=ok physical_devices=0\n");

        Assert.EndsWith("vulkan=ok physical_devices=0\n", report, StringComparison.Ordinal);
        Assert.True(
            report.IndexOf("visible=", StringComparison.Ordinal)
            < report.IndexOf("vulkan=", StringComparison.Ordinal));
    }
}
