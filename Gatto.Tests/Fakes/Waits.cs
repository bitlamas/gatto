namespace Gatto.Tests.Fakes;

//the bound a test's waits take. each wait ends at its signal or its condition, so the bound only matters on a loaded machine
public static class Waits
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    //a condition with no event of its own, spun until it holds. the failure names what was read, so a miss says how far it got
    public static void Until(Func<bool> holds, Func<string> read) =>
        Assert.True(SpinWait.SpinUntil(holds, Bound), read());
}
