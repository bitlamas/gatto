using Gatto.Core.Hardware;

namespace Gatto.Tests.Setup;

//a fixture named for a machine class has to classify as that class, or every comparison that reads it passes without checking anything
public class WalkFixtureClassTests
{
    [Fact]
    public void THE_UNIFIED_WALK_FIXTURE_IS_A_MACHINE_THE_CLASSIFIER_CALLS_UNIFIED()
    {
        var c = HardwareClassifier.Classify(RenderedWalkTests.Unified96);

        Assert.Equal(MemoryTopology.Unified, c.Topology);
        //the shape must be UnifiedWithShare, a unified machine with no graphics budget draws a different shelf
        Assert.Equal(MachineShape.UnifiedWithShare, c.Shape);
        Assert.True(c.GpuBudgetBytes > 0);
    }

    [Fact]
    public void THE_DISCRETE_WALK_FIXTURE_IS_A_MACHINE_THE_CLASSIFIER_CALLS_DISCRETE()
    {
        var c = HardwareClassifier.Classify(RenderedWalkTests.Discrete8);

        Assert.Equal(MemoryTopology.Discrete, c.Topology);
        Assert.Equal(MachineShape.Discrete, c.Shape);
    }

    //assert the two shapes differ, or the checks above pass against a classifier that answers one thing for everything
    [Fact]
    public void THE_TWO_WALK_FIXTURES_ARE_DIFFERENT_MACHINES()
    {
        Assert.NotEqual(
            HardwareClassifier.Classify(RenderedWalkTests.Unified96).Shape,
            HardwareClassifier.Classify(RenderedWalkTests.Discrete8).Shape);
    }

    //installed equal to visible must not classify as unified, so a fixture edited back to that fails here
    [Fact]
    public void A_ZERO_GAP_SNAPSHOT_IS_NOT_UNIFIED_WHATEVER_IT_IS_CALLED()
    {
        var zeroGap = new HardwareSnapshot(137438953472, 137438953472, GpuKind.Discrete, 103079215104);

        Assert.NotEqual(MemoryTopology.Unified, HardwareClassifier.Classify(zeroGap).Topology);
    }
}
