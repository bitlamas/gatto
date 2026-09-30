using Gatto.Terminal;

namespace Gatto.Tests;

//the console surface must report that it can be resized, since the wizard polls the window size only on a surface that says so
public class ConsoleSurfaceTests
{
    private sealed class Plain : ITermSurface
    {
        public int Width => 80;
        public int Height => 24;
        public void Write(string s) { }
    }

    [Fact]
    public void THE_CONSOLE_SAYS_THE_USER_CAN_RESIZE_IT() =>
        Assert.True(new ConsoleSurface().ReportsResize);

    [Fact]
    public void A_SURFACE_THAT_DOES_NOT_SAY_SO_IS_NOT_LOOKED_AT() =>
        Assert.False(((ITermSurface)new Plain()).ReportsResize);
}
