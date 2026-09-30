namespace Gatto.Repl.Render;

//feed one Sample per poll, it fires once the size has held steady for a full interval. coming back to a size it already saw fires too
public sealed class ResizeWatch(int width, int height)
{
    private int _w = width, _h = height;
    private bool _settling;   //a change was seen, fire on the first sample that matches the new size

    public bool Sample(int w, int h)
    {
        if (w != _w || h != _h)
        {
            _w = w; _h = h;
            _settling = true;
            return false;
        }
        if (!_settling) return false;
        _settling = false;
        return true;
    }
}
