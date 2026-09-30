using Gatto.Cli.Setup;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the flow stamps the strip at both sites a screen leaves it. a test of Emit alone would pass with TakeNarration never stamping
public class StripStampTests
{
    private static string? Current(IReadOnlyList<StripSection> strip) =>
        strip.FirstOrDefault(s => s.State == StripState.Current).Name;

    //one walk covers both sites, the oracle is that they agree. the two strips are shown back to back, so a difference moves the row with nothing pressed
    [Fact]
    public void THE_STRIP_IS_STAMPED_AT_BOTH_SITES_A_SCREEN_LEAVES_THE_FLOW()
    {
        var flow = new SetupFlow(new WizardProbes());
        var fork = flow.StartPastOpening();

        //site 1, the screen SetupFlow.Emit returns
        Assert.Equal(WalkSection.Engine, Current(fork.Strip));

        //site 2, SetupFlow.TakeNarration, drained by SetupRunner just before the screen it precedes
        var narrated = flow.TakeNarration();
        Assert.NotEmpty(narrated);
        foreach (var marker in narrated)
            Assert.Equal(fork.Strip, marker.Strip);
    }

    //the welcome map shows the road instead of standing on it, and its golden draws the row with no cursor
    [Fact]
    public void THE_WELCOME_MAP_STANDS_ON_NO_SECTION()
    {
        var welcome = new SetupFlow(new WizardProbes()).Start();

        Assert.Equal(WalkSection.LlamaRoad.Length, welcome.Strip.Count);
        Assert.All(welcome.Strip, s => Assert.Equal(StripState.Pending, s.State));
    }

    //the state rule is positional, done behind, current on the step and dim ahead
    [Fact]
    public void EVERYTHING_BEHIND_THE_CURRENT_SECTION_IS_DONE_AND_EVERYTHING_AHEAD_IS_PENDING()
    {
        var strip = WalkSection.For("model.search", SetupPath.Llama);

        Assert.Equal(
            [
                new(WalkSection.Machine, StripState.Done),
                new(WalkSection.Engine, StripState.Done),
                new(WalkSection.Model, StripState.Current),
                new(WalkSection.Check, StripState.Pending),
                new(WalkSection.Done, StripState.Pending),
            ],
            strip);
    }

    //the connect road re-forms the strip to four sections, engine and model are gone. a user who already runs a server never had them to answer
    [Fact]
    public void THE_CONNECT_ROAD_SHOWS_ONE_SERVER_SECTION_INSTEAD_OF_ENGINE_AND_MODEL()
    {
        var strip = WalkSection.For("connect.confirm", SetupPath.Connect);

        Assert.Equal([WalkSection.Machine, WalkSection.Server, WalkSection.Check, WalkSection.Done],
            strip.Select(s => s.Name));
        Assert.Equal(WalkSection.Server, Current(strip));
    }

    //answering the fork is the only thing that sets Path, so this drives the flow rather than a fixture
    [Fact]
    public void ANSWERING_THE_FORK_FOR_A_SERVER_RE_FORMS_THE_STRIP()
    {
        var flow = new SetupFlow(new WizardProbes());
        flow.StartPastOpening();

        var before = flow.Strip.Select(s => s.Name).ToList();
        flow.Answer(SetupFlow.ForkConnect);

        Assert.Equal([.. WalkSection.LlamaRoad], before);
        Assert.Equal([.. WalkSection.ConnectRoad], flow.Strip.Select(s => s.Name));
    }

    //the strip on the screen must match what the flow reports, a screen carrying no section included
    [Fact]
    public void THE_SCREEN_CARRIES_WHAT_THE_FLOW_REPORTS()
    {
        var flow = new SetupFlow(new WizardProbes());

        var welcome = flow.Start();
        Assert.Equal(flow.Strip, welcome.Strip);

        //the screen after the welcome is the machine section, and this test needs one that carries a section while the welcome carries none
        var machine = flow.Answer(SetupFlow.WelcomeGo);
        Assert.Equal(flow.Strip, machine.Strip);
    }
}
