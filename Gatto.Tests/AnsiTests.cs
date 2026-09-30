using Gatto.Terminal;

namespace Gatto.Tests;

public class AnsiTests
{
    private static readonly RgbColor Orange = new(0xF2, 0xA6, 0x5A, 215);

    [Fact] public void Fg_TrueColor() => Assert.Equal("\x1b[38;2;242;166;90m", Ansi.Fg(Orange, trueColor: true));
    [Fact] public void Fg_256() => Assert.Equal("\x1b[38;5;215m", Ansi.Fg(Orange, trueColor: false));
    [Fact] public void Bg_TrueColor() => Assert.Equal("\x1b[48;2;242;166;90m", Ansi.Bg(Orange, trueColor: true));
    [Fact] public void Up_ZeroIsEmpty() => Assert.Equal("", Ansi.Up(0));
    [Fact] public void Up_N() => Assert.Equal("\x1b[3A", Ansi.Up(3));
    [Fact] public void Down_ZeroIsEmpty() => Assert.Equal("", Ansi.Down(0));
    [Fact] public void Down_N() => Assert.Equal("\x1b[3B", Ansi.Down(3));

}
