using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Term;

public sealed class AnsiCupTests
{
    [Fact]
    public void Cup_emits_1_based_absolute_position()
    {
        //the cup sequence is ESC[ then the 1-based row and col, and \x1b[ is safe because ESC is followed by a bracket
        Assert.Equal("\x1b[1;1H", Ansi.Cup(1, 1));
        Assert.Equal("\x1b[24;80H", Ansi.Cup(24, 80));
    }
}
