using Gatto.Terminal;
using System.Collections.Concurrent;

namespace Gatto.Tests;

//a scope pushed before Start() is born completed (no reader thread feeds it). the assertions read state, so a missing fix fails the test instead of hanging it
public class InputPumpUnstartedFocusTests
{
    private sealed class FeedSource : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }

    private static InputPump Unstarted() => new(new KeyInputSource(new FeedSource()));

    [Fact]
    public void A_SCOPE_PUSHED_BEFORE_START_IS_BORN_COMPLETED()
    {
        //no thread feeds a focus channel before Start(), so such a scope never receives a key and refuses rather than blocking
        var pump = Unstarted();

        using var focus = pump.PushFocus();

        Assert.True(focus.BornCompleted,
            "a scope pushed before Start() must be born completed, or the first read hangs forever");
    }

    [Fact]
    public void A_SCOPE_PUSHED_AFTER_START_IS_LIVE()
    {
        //the discriminating twin, a BornCompleted that is always true would still pass the other test
        var pump = Unstarted();
        pump.Start();

        using var focus = pump.PushFocus();

        Assert.False(focus.BornCompleted,
            "a scope pushed on a running pump must be live, or modal input is dead everywhere");
    }
}
