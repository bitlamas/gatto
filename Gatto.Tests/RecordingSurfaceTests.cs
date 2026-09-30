using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class RecordingSurfaceTests
{
    [Fact]
    public void RecordsWritesInOrder()
    {
        var s = new RecordingSurface { Width = 40 };
        s.Write("a"); s.Write("b");
        Assert.Equal("ab", s.Text);
        Assert.Equal(40, s.Width);
    }
}
