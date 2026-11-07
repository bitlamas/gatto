using Gatto.Core;

namespace Gatto.Tests;

//the prompt names the system the runtime reports, the three gatto names first and the runtime's own words for anything else
public class OsWordsTests
{
    [Fact]
    public void THIS_MACHINE_IS_NAMED_BY_THE_RUNTIME() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "macOS", OsWords.Name());

    [Theory]
    [InlineData(true, false, false, "Windows")]
    [InlineData(false, true, false, "Linux")]
    [InlineData(false, false, true, "macOS")]
    [InlineData(false, false, false, "FreeBSD 14.1-RELEASE")]
    public void EACH_SYSTEM_HAS_ITS_NAME_AND_ANY_OTHER_ITS_DESCRIPTION(bool windows, bool linux, bool mac, string named) =>
        Assert.Equal(named, OsWords.Name(windows, linux, mac, "FreeBSD 14.1-RELEASE"));
}
