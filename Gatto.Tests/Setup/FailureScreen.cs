using Gatto.Cli.Setup;
using Xunit;

namespace Gatto.Tests.Setup;

//the failure sentence lives in the body rows of either failure screen. the helpers throw on any other screen, so a run that never reached one cannot pass.
internal static class FailureScreen
{
    //the failure sentence is the first body row on both failure screens.
    public static string Sentence(WizardScreen screen) => Rows(screen)[0].Text;

    //read the tested exe from its fact row by the tested label, and the row order is display copy that may change.
    public static string Tested(WizardScreen screen) =>
        Assert.Single(Rows(screen), r => r.Text.StartsWith("tested", StringComparison.Ordinal))
            .Text["tested".Length..].TrimStart();

    private static IReadOnlyList<WizardRow> Rows(WizardScreen screen) => screen switch
    {
        WizardScreen.Choice c when c.Key == SetupFlow.FailedKey && c.BodyRows is { Count: > 0 } cb => cb,
        WizardScreen.Ask a when a.Key == SetupFlow.LlamaPathKey && a.BodyRows is { Count: > 0 } ab => ab,
        _ => throw new Xunit.Sdk.XunitException(
            $"expected one of the failure screens, got {screen.GetType().Name} '{ScreenKey.Of(screen)}'"),
    };
}
