using System.Text;
using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

public sealed class RecordingSurface : ITermSurface
{
    public int Width { get; set; } = 80;
    public int Height { get; set; } = 0;   //height 0 means unknown, so existing tests keep their current math.
    //a test sets this when it resizes the surface while code waits for a key.
    public bool ReportsResize { get; set; }
    public StringBuilder Output { get; } = new();
    public string Text => Output.ToString();
    public void Write(string s) => Output.Append(s);
    public void Clear() => Output.Clear();
}

public sealed class ThrowingWidthSurface : Gatto.Terminal.ITermSurface
{
    public RecordingSurface Inner { get; } = new();
    public int Width => throw new InvalidOperationException("boom");
    public int Height => 0;
    public void Write(string s) => Inner.Write(s);
}
