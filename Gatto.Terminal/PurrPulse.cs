namespace Gatto.Terminal;

//ticks for a purr whose wait produces no output. injectable so a test fires it on command rather than sleeping
public interface IPurrPulse : IDisposable
{
    //fires once after the delay, then every period, until stopped. restarting replaces the pulse, the wait began now
    void Start(Action onTick, TimeSpan after, TimeSpan every);

    //stops firing, idempotent, safe from any thread. the face stops it from the paint path while a tick may be in flight
    void Stop();
}

//the callback runs on a pool thread, and stop doesn't wait for a tick in flight. a tick arriving after the stop must be unable to write
public sealed class TimerPurrPulse : IPurrPulse
{
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;

    public void Start(Action onTick, TimeSpan after, TimeSpan every)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => onTick(), null, after, every);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Stop();
}
