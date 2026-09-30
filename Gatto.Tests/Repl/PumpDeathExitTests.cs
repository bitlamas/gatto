//exiting with the alt screen or mouse mode still on is the failure, and only the rendered output can show the restore
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

[Collection("e2e")]
public class PumpDeathExitTests
{
    //every read throws, the shape of a console that is gone. a run of consecutive failures reaches the pump's ceiling.
    private sealed class DeadKeySource : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("console gone");
        public bool KeyAvailable => false;
    }

    [Fact]
    public async Task A_dead_pump_restores_the_terminal_and_says_what_happened()
    {
        var h = new RichReplHarness(Path.GetTempPath(), keySource: new DeadKeySource());

        await h.RunAsync();   //the harness run throws on timeout, so a hung loop fails the test instead of passing it.

        //the alt screen must be entered and then left, which is what makes the captured frame non-null.
        Assert.NotNull(h.Surface.AltFrameAtExit);

        //the session must not vanish without a message on the main screen.
        var mainScreen = string.Join("\n", h.Surface.Viewport.Select(TermText.StripAnsiForWidth));
        Assert.Contains("your session is saved", mainScreen, StringComparison.Ordinal);
    }
}
