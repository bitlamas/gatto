using Gatto.Cli.Setup;
using Xunit;

namespace Gatto.Tests.Setup;

//the fixtures are raw strings read from real hardware reports, and a tidier one would test the cleaning rule against text no machine sends
public class HardwareNamingTests
{
    private const string UnifiedCpu = "AMD RYZEN AI MAX+ 395 w/ Radeon 8060S          ";
    private const string UnifiedGpu = "AMD Radeon(TM) 8060S Graphics";

    //the measured strings run through every cleaning rule at once, and this asserts the whole cleaned name
    [Fact]
    public void THE_MEASURED_MACHINE_CLEANS_TO_WHAT_THE_SCREEN_SHOWS()
    {
        var n = HardwareNaming.Clean(UnifiedCpu, UnifiedGpu);

        Assert.Equal("AMD RYZEN AI MAX+ 395", n.Cpu);
        Assert.Equal("AMD Radeon 8060S Graphics", n.Gpu);
    }

    //cleaning must never change case or drop a trailing noun, since both would need a table of hardware words
    [Fact]
    public void CASE_IS_NEVER_CHANGED_AND_A_TRAILING_NOUN_IS_NEVER_DROPPED()
    {
        var n = HardwareNaming.Clean(UnifiedCpu, UnifiedGpu);

        Assert.Contains("RYZEN AI MAX+", n.Cpu!, StringComparison.Ordinal);   //uppercase output proves cleaning never recases the name.
        Assert.EndsWith("Graphics", n.Gpu!, StringComparison.Ordinal);        //the trailing noun stays, so cleaning never shortens the name.
    }

    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics", "AMD Radeon 8060S Graphics")]
    [InlineData("NVIDIA GeForce RTX(R) 4090", "NVIDIA GeForce RTX 4090")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel Arc A770 Graphics")]
    [InlineData("Some Vendor™ Card®", "Some Vendor Card")]
    [InlineData("  padded  and   spaced  ", "padded and spaced")]
    public void TRADEMARK_MARKS_AND_STRAY_SPACE_ARE_THE_ONLY_THINGS_REMOVED(string raw, string want) =>
        Assert.Equal(want, HardwareNaming.Clean(null, raw).Gpu);

    //a cut may only remove words the graphics row already shows, so the delimiter list never becomes a hardware vocabulary table
    [Theory]
    [InlineData("AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", "AMD Radeon 8060S Graphics")]
    [InlineData("Intel Core Ultra 7 with Arc Graphics", "Intel Arc Graphics")]
    [InlineData("Chip 9 w/ Nothing In Common", "AMD Radeon 8060S Graphics")]
    [InlineData("Chip 9", "AMD Radeon 8060S Graphics")]
    public void NOTHING_IS_EVER_CUT_THAT_IS_NOT_ALREADY_ON_THE_NEXT_ROW(string cpuRaw, string gpuRaw)
    {
        var n = HardwareNaming.Clean(cpuRaw, gpuRaw);

        var kept = n.Cpu!;
        var removed = HardwareNaming.Clean(cpuRaw, null).Cpu![kept.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        //each removed word must already appear on the graphics row, except the delimiter, which states no fact about the machine
        foreach (var word in removed)
            Assert.True(n.Gpu!.Contains(word, StringComparison.OrdinalIgnoreCase) || Connective(word),
                $"'{word}' was removed from the processor name and is nowhere on the graphics row");
    }

    private static bool Connective(string w) =>
        w.Equals("w/", StringComparison.OrdinalIgnoreCase) || w.Equals("with", StringComparison.OrdinalIgnoreCase);

    //that property would hold even if nothing were ever cut, so this test shows the cut fires on the measured machine
    [Fact]
    public void THE_CUT_ACTUALLY_FIRES_ON_THE_MEASURED_MACHINE() =>
        Assert.DoesNotContain("Radeon", HardwareNaming.Clean(UnifiedCpu, UnifiedGpu).Cpu!, StringComparison.Ordinal);

    //a delimiter alone does not authorize a cut, so a tail the graphics row does not show survives whole
    [Fact]
    public void A_TAIL_THAT_IS_NOT_A_DUPLICATE_SURVIVES() =>
        Assert.Equal("Chip 9 w/ Nothing In Common",
            HardwareNaming.Clean("Chip 9 w/ Nothing In Common", "AMD Radeon 8060S Graphics").Cpu);

    //with no graphics row nothing can be a duplicate, so no cut runs and the processor keeps its companion clause
    [Fact]
    public void WITH_NO_GRAPHICS_NAME_THE_PROCESSOR_KEEPS_ITS_COMPANION_CLAUSE()
    {
        var n = HardwareNaming.Clean(UnifiedCpu, null);

        Assert.Equal("AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", n.Cpu);
        Assert.Null(n.Gpu);
    }

    //unreadable names and names that clean to empty return null, so the screen falls back instead of claiming gatto read the machine
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(TM)")]
    public void AN_UNREADABLE_NAME_IS_NULL_NOT_EMPTY(string? raw)
    {
        var n = HardwareNaming.Clean(raw, raw);

        Assert.Null(n.Cpu);
        Assert.Null(n.Gpu);
    }
}
