using Gatto.Cli.Setup;
using Xunit;

namespace Gatto.Tests.Setup;

//flow-driven tests cross the opening screens here, from the single list in WalkOpening. to test a screen of the opening itself, call SetupFlow.Start directly
internal static class FlowStart
{
    //assert each screen before answering it, so a wrong run fails here instead of surfacing in a caller under another screen's name
    public static WizardScreen StartPastOpening(this SetupFlow flow) => flow.PastOpeningFrom(flow.Start());

    //use this only for a fixture with a working engine (a machine without one never reaches the found screen, so it stays on StartPastOpening)
    public static WizardScreen StartPastEngine(this SetupFlow flow)
    {
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        return flow.Answer(SetupFlow.FoundUse);
    }

    //the split lets a test hand PastOpeningFrom a wrong screen. the flow's Start always returns the welcome screen, so the mismatch assertion could never fire
    public static WizardScreen PastOpeningFrom(this SetupFlow flow, WizardScreen screen)
    {
        for (var i = 0; i < WalkOpening.Screens.Count; i++)
        {
            Assert.Equal(WalkOpening.Screens[i], ScreenKey.Of(screen));
            screen = flow.Answer(WalkOpening.Answers[i]!);
        }

        return screen;
    }
}
