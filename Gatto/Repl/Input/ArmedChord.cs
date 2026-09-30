namespace Gatto.Repl.Input;

//the two-press chord currently armed
public enum Chord { None, ClearComposer, Quit }

//the press-again state machine behind Esc·Esc and Ctrl+C·Ctrl+C, arming one chord disarms the other, and time is passed in
public sealed class ArmedChord
{
    private Chord _armed = Chord.None;
    private long _armedAtMs;

    public Chord ArmedAt(long nowMs) =>
        _armed != Chord.None && nowMs - _armedAtMs <= Repl.CtrlCDoubleTapMs ? _armed : Chord.None;

    public string? HintAt(long nowMs) => ArmedAt(nowMs) switch
    {
        Chord.ClearComposer => "Esc again to clear",
        Chord.Quit => "Ctrl+C again to quit",
        _ => null,
    };

    public Chord Armed => ArmedAt(Environment.TickCount64);
    public string? Hint => HintAt(Environment.TickCount64);

    //how long the arm has left, or null, for a caller that has to wake and repaint when the hint expires
    public long? RemainingMs(long nowMs) =>
        ArmedAt(nowMs) == Chord.None ? null : Math.Max(0, Repl.CtrlCDoubleTapMs - (nowMs - _armedAtMs));

    //register a press, true when it completes the chord and false when it only armed and needs a repaint
    public bool Fire(Chord chord, long nowMs)
    {
        if (ArmedAt(nowMs) == chord) { Disarm(); return true; }
        _armed = chord;
        _armedAtMs = nowMs;
        return false;
    }

    public void Disarm() { _armed = Chord.None; _armedAtMs = 0; }
}
