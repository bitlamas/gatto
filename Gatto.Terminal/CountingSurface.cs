using System.Diagnostics;
using System.Text;

namespace Gatto.Terminal;

//a surface over another that counts bytes, writes and the ticks spent inside each write, for a caller that reads them before and after its own work
public sealed class CountingSurface(ITermSurface inner) : ITermSurface
{
    private long _bytes;
    private long _writes;
    private long _writeTicks;

    public int Width => inner.Width;
    public int Height => inner.Height;
    public bool ReportsResize => inner.ReportsResize;

    //added from every writing thread, the ticker's among them, and never reset, so two readers cannot take each other's counts
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Writes => Interlocked.Read(ref _writes);
    public long WriteTicks => Interlocked.Read(ref _writeTicks);

    public void Write(string s)
    {
        var from = Stopwatch.GetTimestamp();
        inner.Write(s);
        Interlocked.Add(ref _writeTicks, Stopwatch.GetTimestamp() - from);
        Interlocked.Add(ref _bytes, Encoding.UTF8.GetByteCount(s));
        Interlocked.Increment(ref _writes);
    }
}
