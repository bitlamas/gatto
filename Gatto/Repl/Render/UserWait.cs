using System;

namespace Gatto.Repl.Render;

//how much of a turn gatto spent waiting on the user, so the purr reports work. the modal focus seam feeds it, the call every prompt must make to read a key
internal struct UserWait
{
    private long _closed;      //completed waits in ms
    private int _depth;        //open modal scopes
    private long _openedAt;    //the clock reading when the depth went from 0 to 1

    //a modal scope opened, nested scopes count as one wait so a prompt over a prompt is not subtracted twice
    public void Begin(long nowMs)
    {
        if (_depth++ == 0) _openedAt = nowMs;
    }

    //a modal scope closed. a close with nothing open is ignored, or a double dispose would credit the turn with time since a stale start
    public void End(long nowMs)
    {
        if (_depth == 0) return;
        if (--_depth == 0) _closed += Math.Max(0, nowMs - _openedAt);
    }

    //reset for a new turn, depth included. a depth that survives to here is a leaked scope, and inheriting it would freeze the clock of every later turn
    public void Restart()
    {
        _closed = 0;
        _depth = 0;
        _openedAt = 0;
    }

    //every wait so far, the open one included
    public long TotalAt(long nowMs) => _closed + OpenAt(nowMs);

    //the age of the wait that's open right now, 0 when gatto isn't waiting. the waiting row counts this while the turn clock is frozen
    public long OpenAt(long nowMs) => _depth > 0 ? Math.Max(0, nowMs - _openedAt) : 0;
}
