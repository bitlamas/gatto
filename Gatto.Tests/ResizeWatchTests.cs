using Gatto.Repl.Render;

namespace Gatto.Tests;

public class ResizeWatchTests
{
    [Fact]
    public void SteadyGeometry_NeverFires()
    {
        var w = new ResizeWatch(80, 30);
        Assert.False(w.Sample(80, 30));
        Assert.False(w.Sample(80, 30));
        Assert.False(w.Sample(80, 30));
    }

    [Fact]
    public void Change_DoesNotFireUntilStable()
    {
        //a drag reads stale intermediate sizes, so a change sample must not fire, only the first stable one does
        var w = new ResizeWatch(80, 30);
        Assert.False(w.Sample(100, 30));   //the changed sample holds without firing.
        Assert.True(w.Sample(100, 30));    //the first stable sample fires.
        Assert.False(w.Sample(100, 30));   //it does not fire a second time.
    }

    [Fact]
    public void DragBurst_CoalescesToOneFire()
    {
        var w = new ResizeWatch(80, 30);
        Assert.False(w.Sample(90, 30));
        Assert.False(w.Sample(100, 28));
        Assert.False(w.Sample(120, 26));   //another change, and the watch holds until the size settles
        Assert.True(w.Sample(120, 26));    //the settled size fires exactly once.
        Assert.False(w.Sample(120, 26));
    }

    [Fact]
    public void ChangeBackToOriginal_StillFires()
    {
        //a round-trip back to the original size still fires, the terminal may have reflowed content in between
        var w = new ResizeWatch(80, 30);
        Assert.False(w.Sample(100, 30));
        Assert.False(w.Sample(80, 30));    //the size is back at the start, but a second change happened.
        Assert.True(w.Sample(80, 30));
    }

    [Fact]
    public void HeightOnlyChange_Fires()
    {
        var w = new ResizeWatch(80, 30);
        Assert.False(w.Sample(80, 40));
        Assert.True(w.Sample(80, 40));
    }
}
