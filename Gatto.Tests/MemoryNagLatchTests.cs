namespace Gatto.Tests;

//the latch keys on the pair of budget and truncated-count, so a /model switch into a tighter budget warns again
public class MemoryNagLatchTests
{
    [Fact]
    public void WarnsOncePerPair_ReWarnsOnAnyChange_ClearsOnZero()
    {
        var latch = new Gatto.Cli.MemoryNagLatch();

        Assert.True(latch.ShouldWarn(1000, 3));
        Assert.False(latch.ShouldWarn(1000, 3));
        Assert.True(latch.ShouldWarn(500, 3));
        Assert.True(latch.ShouldWarn(500, 5));

        //a boundary with nothing truncated resets the latch, so leaving truncation and re-entering it warns again
        Assert.False(latch.ShouldWarn(0, 0));
        Assert.True(latch.ShouldWarn(500, 5));
    }

    [Fact]
    public void ZeroLines_NeverWarns_EvenOnTheFirstSighting()
    {
        Assert.False(new Gatto.Cli.MemoryNagLatch().ShouldWarn(1000, 0));
    }
}
