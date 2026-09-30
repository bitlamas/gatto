namespace Gatto.Core;

//warnings go here, a raw Console.Error write corrupts the alt-screen frame. the sink is called outside the lock, so a slow sink cannot invert lock order
public sealed class WarningSink
{
    private readonly object _lock = new();
    private Action<string> _sink;

    public WarningSink(Action<string> initial) => _sink = initial;

    public void Warn(string message)
    {
        Action<string> sink;
        lock (_lock) sink = _sink;
        sink(message);
    }

    //rebind warnings to sink until the scope is disposed, which restores the previous one. dispose twice is safe, so an exit path can call it unconditionally
    public IDisposable Route(Action<string> sink)
    {
        lock (_lock)
        {
            var previous = _sink;
            _sink = sink;
            return new Scope(this, previous);
        }
    }

    //hold warnings while the caller resets the display, then emit them in arrival order. a warning raised during recompose is wiped by the display reset
    public HeldWarnings Hold() => new(this);

    //the Hold scope, which buffers warnings from any thread. a warning raised after the release goes straight through, so nothing is lost before the sink restore
    public sealed class HeldWarnings : IDisposable
    {
        private readonly WarningSink _owner;
        private readonly IDisposable _scope;
        private readonly List<string> _held = new();
        private bool _released;

        internal HeldWarnings(WarningSink owner)
        {
            _owner = owner;
            _scope = owner.Route(Capture);
        }

        private void Capture(string message)
        {
            lock (_held)
            {
                if (!_released) { _held.Add(message); return; }
            }
            //the release already happened, so send it through instead of buffering it forever
            _owner.Warn(message);
        }

        //restore the previous sink and emit everything held in order, safe to call twice. call it where the replay belongs, after the display reset
        public void Release()
        {
            //restore the sink before marking it released. the other order lets Capture re-enter itself and the replay re-buffer into the list it is draining
            _scope.Dispose();
            string[] pending;
            lock (_held)
            {
                if (_released) return;
                _released = true;
                pending = _held.ToArray();
                _held.Clear();
            }
            foreach (var m in pending) _owner.Warn(m);   //outside the lock, the same no-inversion rule Warn keeps
        }

        public void Dispose() => Release();
    }

    private sealed class Scope(WarningSink owner, Action<string> previous) : IDisposable
    {
        private bool _restored;
        public void Dispose()
        {
            lock (owner._lock)
            {
                if (_restored) return;
                _restored = true;
                owner._sink = previous;
            }
        }
    }
}
