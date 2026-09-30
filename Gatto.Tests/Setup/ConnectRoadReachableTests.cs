using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Setup;

//this guards that the connect path stays reachable, no other test asks whether the user can get there. assert flow.Path rather than a screen key.
public class ConnectRoadReachableTests
{
    //run the flow through the real opening, a jump to the engine step proves nothing. every machine class gets its own row, the first screen differs for each.

    //the engine step opens on a different screen per machine class, and each must offer the connect entry. the Cuda class keeps its own row.
    [Theory]
    [InlineData(Machine.WithEngine)]
    [InlineData(Machine.NoEngine)]
    [InlineData(Machine.Unreadable)]
    [InlineData(Machine.Cuda)]
    public void A_WALK_CAN_REACH_THE_CONNECT_ROAD_FROM_THE_ENGINE_STEP(Machine machine)
    {
        var flow = new SetupFlow(Probes(machine));

        var entry = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());

        //the connect option must be on the screen the flow arrives at, whatever the entry is, otherwise the test only checks a remembered key.
        var door = Assert.Single(entry.Options, o => o.Key == SetupFlow.ForkConnect);

        flow.Answer(door.Key);

        Assert.Equal(SetupPath.Connect, flow.Path);
    }

    //arriving at the engine step is already the llama path, so a run that never answers the connect option stays off Connect.
    [Theory]
    [InlineData(Machine.WithEngine)]
    [InlineData(Machine.NoEngine)]
    [InlineData(Machine.Unreadable)]
    [InlineData(Machine.Cuda)]
    public void AND_A_WALK_THAT_NEVER_TOUCHES_THE_DOOR_DOES_NOT_LAND_THERE(Machine machine)
    {
        var flow = new SetupFlow(Probes(machine));

        flow.StartPastOpening();

        Assert.NotEqual(SetupPath.Connect, flow.Path);
    }

    //machines are enum rows, so adding a case is one row and not a second flag.
    public enum Machine { WithEngine, NoEngine, Unreadable, Cuda }

    private static WizardProbes Probes(Machine machine) => machine switch
    {
        Machine.WithEngine => new WizardProbes(),
        Machine.NoEngine => new WizardProbes
        {
            Llama = null,
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b10076\",
                new EnginePair(new EngineAsset("z.zip", "https://example.invalid/z", new string('a', 64), 1), null)),
        },
        //with no hardware gatto can't name a build, and that branch reaches the steering screen.
        Machine.Unreadable => new WizardProbes { Llama = null, Snapshot = null },
        //this fixture has a companion asset, and both screens it can reach hold the connect entry, which is the property under test.
        Machine.Cuda => new WizardProbes
        {
            Llama = null,
            Asset = new("llama-cuda.zip", "cudart.zip", "an NVIDIA card", "CUDA"),
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b10076\", new EnginePair(
                new EngineAsset("llama-cuda.zip", "https://example.invalid/s", new string('a', 64), 1),
                new EngineAsset("cudart.zip", "https://example.invalid/c", new string('b', 64), 1))),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(machine), machine, "no fixture"),
    };
}
