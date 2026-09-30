using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Fakes;

public sealed class VtScreenSurfaceAltTests
{
    [Fact]
    public void Enter_and_exit_alt_screen_saves_and_restores_the_main_viewport()
    {
        var s = new VtScreenSurface(20, 4);
        s.Write("main line one\n");
        s.Write("main line two");
        Assert.False(s.AltScreen);

        s.Write("\x1b[?1049h");
        Assert.True(s.AltScreen);
        Assert.All(s.Viewport, row => Assert.Equal("", row));

        s.Write("\x1b[?1049l");
        Assert.False(s.AltScreen);
        Assert.Equal("main line one", s.Viewport[0]);
        Assert.Equal("main line two", s.Viewport[1]);
    }

    [Fact]
    public void Cup_places_a_full_height_frame_at_absolute_rows()
    {
        var s = new VtScreenSurface(20, 4);
        s.Write("\x1b[?1049h");
        //each write targets its own absolute row, counted from one.
        s.Write(Ansi.Cup(1, 1) + "top");
        s.Write(Ansi.Cup(2, 1) + "middle");
        s.Write(Ansi.Cup(4, 1) + "> composer");

        Assert.Equal("top", s.Viewport[0]);
        Assert.Equal("middle", s.Viewport[1]);
        Assert.Equal("", s.Viewport[2]);
        Assert.Equal("> composer", s.Viewport[3]);
    }

    [Fact]
    public void Alt_screen_bottom_row_linefeed_does_not_grow_scrollback()
    {
        var s = new VtScreenSurface(20, 3);
        s.Write("\x1b[?1049h");
        //repeated feeds at the bottom row must not touch scrollback, an alt buffer has none
        s.Write(Ansi.Cup(3, 1) + "bottom");
        s.Write("\n\n\n");
        Assert.Empty(s.Scrollback);
        Assert.Equal(0, s.Scrolled);
    }

    [Fact]
    public void Main_screen_still_captures_scrollback_when_not_in_alt_mode()
    {
        var s = new VtScreenSurface(20, 2);
        s.Write("a\nb\nc\nd");   //two feeds past the bottom push the first two rows into scrollback.
        Assert.Equal(2, s.Scrolled);
        Assert.Equal("a", s.Scrollback[0]);
    }
}
