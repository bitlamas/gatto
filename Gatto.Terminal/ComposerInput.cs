namespace Gatto.Terminal;

//a keystroke and a mouse-select gesture share one channel in arrival order, and Select holds a resolved line and column from the hit-test
public abstract record ComposerInput
{
    //an ordinary keystroke, exactly as the pump received it
    public sealed record Key(ConsoleKeyInfo K) : ComposerInput;

    //a mouse-resolved composer-select gesture, where Kind says which end it is and Clear ignores Line and Col. the pump enqueues it, the editor thread applies it
    public sealed record Select(int Line, int Col, ComposerGesture Kind) : ComposerInput;
}

//which end of a composer-select gesture Select names, plus Clear, sent when a transcript selection starts elsewhere and drops the composer's own
public enum ComposerGesture { Begin, Extend, Clear }

//the composer's input source: keys and mouse-select gestures in one FIFO channel. modal scopes keep the plain IKeySource, a gesture never reaches a modal reader
public interface IComposerSource
{
    //blocks until the next key or gesture arrives, in the order the pump enqueued them
    ComposerInput Read();

    //a non-consuming peek for pending input, true for a gesture in the channel as well as a key
    bool KeyAvailable { get; }
}
