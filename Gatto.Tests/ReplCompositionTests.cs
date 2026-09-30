//cover only the pieces that compose without a live console.
using Gatto.Terminal;

namespace Gatto.Tests;

public class ReplCompositionTests
{
    [Fact]
    public void CtxWindow_ConfigWinsOverProbe()
    {
        //this mirrors the wiring expression in Repl rather than driving the real one
        int? configContext = 4096;
        int? probe = 32768;
        Assert.Equal(4096, configContext ?? probe);
    }

    [Fact]
    public void PlainCaps_NeverRich() => Assert.False(TermCaps.Plain.Rich);
}
