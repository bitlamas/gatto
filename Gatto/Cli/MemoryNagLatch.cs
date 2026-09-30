namespace Gatto.Cli;

//the warning fires on news, the same pair is suppressed and a new budget still warns. a zero-line call clears the latch, so callers invoke this at every boundary
internal sealed class MemoryNagLatch
{
    private (int Budget, int Lines)? _last;

    public bool ShouldWarn(int budget, int lines)
    {
        if (lines <= 0) { _last = null; return false; }
        var key = (budget, lines);
        if (_last == key) return false;
        _last = key;
        return true;
    }
}
