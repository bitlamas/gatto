//with mouse: false the rich loop must never touch the console input mode, the shell keeps its own mouse settings
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

[Collection("e2e")]
public class RichReplMouseOptOutTests
{
    //record at the raw control, the wrapper is never built under mouse: false, and a zero count proves the mode was untouched
    private sealed class RecordingModeControl : IConsoleModeControl
    {
        public int Gets, Sets;
        public uint Get() { Gets++; return 0; }
        public void Set(uint mode) => Sets++;
    }

    [Fact]
    public async Task MouseOff_NeverTouchesTheConsoleInputMode()
    {
        var mode = new RecordingModeControl();
        var h = new RichReplHarness(Path.GetTempPath(), mouseEnabled: false, modeControl: mode);
        h.Keys.Line("/new").Line("/quit");

        await h.RunAsync();

        //assert the frame content too, an exit code of 0 would also pass a run that idled to timeout
        Assert.Contains("new conversation", h.ScreenText());

        Assert.Equal(0, mode.Sets);
        Assert.Equal(0, mode.Gets);
    }
}
