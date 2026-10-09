namespace Gatto.Terminal;

//the Read call blocks for the next event. the peek for a waiting key must not consume, once mouse mode is on Console.KeyAvailable eats the records it peeks
public interface IInputSource
{
    InputEvent Read();
    bool KeyDownAvailable { get; }
    //true when Read will return a key down, a press or a wheel without blocking. the default keeps every fake compiling
    bool EventAvailable => KeyDownAvailable;
    //drops queued mouse events and keeps keys, a source that queues nothing has nothing to drop
    void DropMouse() { }
}

//wraps a plain key source for the pump. each ReadKey becomes a key event, no mouse can pass through
public sealed class KeyInputSource(IKeySource inner) : IInputSource
{
    public InputEvent Read() => new KeyEvent(inner.ReadKey());
    public bool KeyDownAvailable => inner.KeyAvailable;
}
