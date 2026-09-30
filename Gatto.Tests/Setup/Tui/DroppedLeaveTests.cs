using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//leaving the drop screen deletes the partial it named, since the armed row promised it and Resume is the way to keep the bytes
public class DroppedLeaveTests
{
    //leaving deletes what the screen offered, asserted on PartialsDeleted, whose only writer is the deed. drive it through MarkLeaving, the hook the chord reaches.
    [Fact]
    public void LEAVING_THE_DROP_SCREEN_DELETES_THE_PARTIAL_IT_NAMED()
    {
        var (flow, probes, _) = ModelFetchTests.Dropped();

        flow.MarkLeaving();

        Assert.Same(probes.HubOffer, Assert.Single(probes.PartialsDeleted));
    }

    //resuming deletes nothing, since that path exists to keep the bytes. it leaves after resuming, so nothing may collect on the promise later.
    [Fact]
    public void RESUMING_DELETES_NOTHING_AND_LEAVING_AFTERWARDS_STILL_DELETES_NOTHING()
    {
        var (flow, probes, _) = ModelFetchTests.Dropped();

        flow.Answer(SetupFlow.ModelResume);
        Assert.Empty(probes.PartialsDeleted);

        flow.MarkLeaving();

        Assert.Empty(probes.PartialsDeleted);
    }

    //picking another model deletes nothing either, since the armed row is gone and the promise belongs to the press that follows it
    [Fact]
    public void PICKING_ANOTHER_MODEL_DELETES_NOTHING()
    {
        var (flow, probes, _) = ModelFetchTests.Dropped();

        flow.Answer(SetupFlow.PickAnother);
        flow.MarkLeaving();

        Assert.Empty(probes.PartialsDeleted);
    }

    //in a session nothing offered the delete, so Esc leaves in one press and keeps the partial
    [Fact]
    public void LEAVING_THE_DROP_SCREEN_IN_A_SESSION_KEEPS_THE_PARTIAL()
    {
        var (flow, probes, _) = ModelFetchTests.Dropped(inSession: true);

        flow.MarkLeaving();

        Assert.Empty(probes.PartialsDeleted);
    }

    //the delete hangs off the armed drop row, so leaving a fetch that never dropped deletes nothing
    [Fact]
    public void LEAVING_A_WALK_THAT_NEVER_DROPPED_DELETES_NOTHING()
    {
        var (flow, probes, _) = ModelFetchTests.LiveFetch(inSession: false, blockingFetches: 0);

        flow.MarkLeaving();

        Assert.Empty(probes.PartialsDeleted);
    }
}
