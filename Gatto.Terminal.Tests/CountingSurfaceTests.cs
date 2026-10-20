using Gatto.Terminal;

namespace Gatto.Terminal.Tests;

//the counting surface passes every write through unchanged and keeps three plain counters a caller reads before and after
public class CountingSurfaceTests
{
    private sealed class Recording(int width = 80, int height = 24) : ITermSurface
    {
        public List<string> Writes { get; } = [];
        public int Width => width;
        public int Height => height;
        public void Write(string s) { lock (Writes) Writes.Add(s); }
        public bool ReportsResize => true;
    }

    [Fact]
    public void A_write_reaches_the_inner_surface_and_is_counted()
    {
        var inner = new Recording(100, 30);
        var counting = new CountingSurface(inner);
        counting.Write("ab");
        counting.Write("é\x1b[0m");

        Assert.Equal(new[] { "ab", "é\x1b[0m" }, inner.Writes);
        Assert.Equal(2, counting.Writes);
        Assert.Equal(2 + 2 + 4, counting.Bytes);   //UTF-8, so the é is two bytes
        Assert.True(counting.WriteTicks >= 0);
        Assert.Equal(100, counting.Width);
        Assert.Equal(30, counting.Height);
        Assert.True(counting.ReportsResize);
    }

    //the ticker writes from its own thread, so the counts must add up across threads
    [Fact]
    public void Writes_from_several_threads_are_all_counted()
    {
        var counting = new CountingSurface(new Recording());
        Parallel.For(0, 4, _ => { for (var i = 0; i < 2_500; i++) counting.Write("x"); });
        Assert.Equal(10_000, counting.Writes);
        Assert.Equal(10_000, counting.Bytes);
    }
}
