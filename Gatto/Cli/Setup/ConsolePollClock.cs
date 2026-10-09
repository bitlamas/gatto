using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the live clock behind the wizard's watch, one home for both faces. it waits on what the source can act on and knows nothing about directories
internal sealed class ConsolePollClock(Func<bool> available) : IPollClock
{
    public ConsolePollClock(IKeySource keys) : this(() => keys.KeyAvailable) { }
    public ConsolePollClock(IInputSource source) : this(() => source.EventAvailable) { }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public long ElapsedMs => _clock.ElapsedMilliseconds;

    public bool WaitForKey(TimeSpan budget)
    {
        var until = _clock.ElapsedMilliseconds + (long)budget.TotalMilliseconds;
        while (_clock.ElapsedMilliseconds < until)
        {
            try { if (available()) return true; }
            catch (Exception) { } //redirected stdin throws here, so nothing is ever typed
            Thread.Sleep(10);
        }
        try { return available(); }
        catch (Exception) { return false; }
    }
}

//the poll interval, seconds rather than sub-second, so a long watch does not hammer the disk. one constant keeps both faces at the same rate
internal static class Watch
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
}
