using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//an input source over a script of events and actions, an action runs before the next event is read. it reports nothing available, as the rig's key fake does, since the script is queued whole and a true would let a drain read all of it
internal sealed class ScriptedInputSource(Action? beforeRead = null, params object[] script) : IInputSource
{
    private readonly Queue<object> _q = new(script.Select(s => s is InputEvent or Action
        ? s : throw new ArgumentException($"a script item is an InputEvent or an Action, not {s.GetType().Name}")));

    public ScriptedInputSource() : this(null) { }

    public int Remaining => _q.Count(s => s is InputEvent);

    public bool KeyDownAvailable => false;

    public InputEvent Read()
    {
        while (_q.TryPeek(out var next) && next is Action act) { _q.Dequeue(); act(); }
        beforeRead?.Invoke();
        return _q.Count > 0 ? (InputEvent)_q.Dequeue() : throw new InvalidOperationException("scripted too few events");
    }
}
