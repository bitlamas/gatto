namespace Gatto.Terminal;

//the unit between source and pump: a key or a mouse, one reader for both. keys go downstream as ConsoleKeyInfo as before
public abstract record InputEvent;

public sealed record KeyEvent(ConsoleKeyInfo Key) : InputEvent;

//raw console wheel delta, one notch is 120 on most devices, the controller scales it. triple click is our own timing, the OS only gives 1 and 2
public sealed record MouseEvent(int X, int Y, MouseKind Kind, MouseButton Button,
                                int WheelDelta, ConsoleModifiers Modifiers) : InputEvent
{
    public int Clicks { get; init; } = 1;
}

public enum MouseKind { Press, Release, Move, Wheel }   //downstream acts on Press and Wheel only
public enum MouseButton { None, Left, Right, Middle }
