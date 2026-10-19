using Gatto.Cli.Setup;
using Xunit;

namespace Gatto.Tests.Setup;

//the failure sentence lives in the body rows of either failure screen. the helpers throw on any other screen, so a run that never reached one cannot pass.
internal static class FailureScreen
{
    //the failure sentence is the first body row on both failure screens.
    public static string Sentence(SetupScreen screen) => Rows(screen)[0].Text;

    //read the tested exe from its fact row by the tested label, and the row order is display copy that may change.
    public static string Tested(SetupScreen screen) =>
        Assert.Single(Rows(screen), r => r.Text.StartsWith("tested", StringComparison.Ordinal))
            .Text["tested".Length..].TrimStart();

    private static IReadOnlyList<SetupRow> Rows(SetupScreen screen) => screen switch
    {
        SetupScreen.Choice c when c.Key == SetupFlow.FailedKey && c.BodyRows is { Count: > 0 } cb => cb,
        SetupScreen.Ask a when a.Key == SetupFlow.LlamaPathKey && a.BodyRows is { Count: > 0 } ab => ab,
        _ => throw new Xunit.Sdk.XunitException(
            $"expected one of the failure screens, got {screen.GetType().Name} '{ScreenKey.Of(screen)}'"),
    };
}
