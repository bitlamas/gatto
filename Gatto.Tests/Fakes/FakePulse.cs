using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//the stop path keeps the callback, a real timer can fire a tick after stopping
internal sealed class FakePulse : IPurrPulse
{
    private Action? _tick;

    public TimeSpan? After { get; private set; }
    public TimeSpan? Every { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }

    public void Start(Action onTick, TimeSpan after, TimeSpan every)
    {
        _tick = onTick;
        After = after;
        Every = every;
        Starts++;
    }

    public void Stop() => Stops++;

    public void Dispose() { }

    //deliver a tick, even one that arrives after a stop.
    public void Fire() => _tick?.Invoke();
}
