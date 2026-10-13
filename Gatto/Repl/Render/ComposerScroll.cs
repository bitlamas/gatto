namespace Gatto.Repl.Render;

//a scroll window over a row list rebuilt each paint, so no item is anchored. a scroll toward older rows is positive, and the caller resets it on arrival
public sealed class ComposerScroll
{
    private bool _following = true;
    private int _scrollUp;
    private int _lastMax = int.MaxValue;   //the furthest scroll the last paint allowed, unknown until a paint asks

    public bool Following => _following;

    public int Offset(int totalRows, int allowed)
    {
        var maxScroll = System.Math.Max(0, totalRows - allowed);
        _lastMax = maxScroll;
        if (maxScroll == 0) return 0;
        var clamped = System.Math.Clamp(_scrollUp, 0, maxScroll);
        return maxScroll - clamped;
    }

    //a move starts from where the last paint drew, so a window parked past its top answers the first notch back
    public void ScrollBy(int linesTowardOlder)
    {
        if (linesTowardOlder == 0) return;
        _following = false;
        long from = System.Math.Clamp(_scrollUp, 0, _lastMax);
        _scrollUp = (int)System.Math.Clamp(from + linesTowardOlder, 0, int.MaxValue);
        if (_scrollUp <= 0) { _scrollUp = 0; _following = true; }
    }

    public void Reset() { _following = true; _scrollUp = 0; _lastMax = int.MaxValue; }
}
