using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the shelf asks the engine for models over the lit families, and the landing pair is lit at open
public class FlowSearchesModelsTests
{
    [Fact]
    public void THE_FLOW_SEARCHES_MODELS()
    {
        var probes = new WizardProbes();
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();

        var asked = Assert.Single(probes.Requests);
        Assert.Equal(["gemma", "qwen"], asked.Lit.Order());
        Assert.False(asked.Lifted);
        Assert.Null(asked.Search);
    }
}
