using System.Diagnostics;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//a poll clock over the focus scope a prompt pushed, so the wait sees the keys that scope will read, and a wake token ends a wait at once
public sealed class ScopePollClock(InputPump.FocusScope? scope, CancellationToken wake = default) : IPollClock
{
    //how often a wait looks at the scope, short enough that a key reads as instant
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(20);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public long ElapsedMs => _clock.ElapsedMilliseconds;

    public bool WaitForKey(TimeSpan budget)
    {
        var until = _clock.Elapsed + budget;
        while (true)
        {
            if (scope?.Keys.KeyAvailable == true) return true;
            var left = until - _clock.Elapsed;
            if (left <= TimeSpan.Zero || wake.IsCancellationRequested) return false;
            //the wake token's handle ends the slice early, so a close is never slept out
            wake.WaitHandle.WaitOne(left < Slice ? left : Slice);
        }
    }
}
