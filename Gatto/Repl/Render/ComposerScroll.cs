namespace Gatto.Repl.Render;

//a scroll window over a row list rebuilt each paint, so no item is anchored. a scroll toward older rows is positive, and the caller resets it on arrival
public sealed class ComposerScroll
{
    private bool _following = true;
    private int _scrollUp;

    public bool Following => _following;

    public int Offset(int totalRows, int allowed)
    {
        var maxScroll = System.Math.Max(0, totalRows - allowed);
        if (maxScroll == 0) return 0;
        var clamped = System.Math.Clamp(_scrollUp, 0, maxScroll);
        return maxScroll - clamped;
    }

    public void ScrollBy(int linesTowardOlder)
    {
        if (linesTowardOlder == 0) return;
        _following = false;
        _scrollUp = System.Math.Max(0, _scrollUp + linesTowardOlder);
        if (_scrollUp <= 0) { _scrollUp = 0; _following = true; }
    }

    public void Reset() { _following = true; _scrollUp = 0; }
}
