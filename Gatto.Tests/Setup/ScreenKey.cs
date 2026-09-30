using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//this switch reads the suite's own idea of a screen name, so a key assertion means something on its own
internal static class ScreenKey
{
    public static string Of(WizardScreen s) => s switch
    {
        WizardScreen.Choice c => c.Key,
        WizardScreen.Ask a => a.Key,
        WizardScreen.Info i => i.Key,
        WizardScreen.Progress p => p.Key,
        WizardScreen.Terminal t => t.Key,
        //the wildcard arm is the compiler's price, a new kind renders an empty key that fails every caller's comparison
        _ => "",
    };
}
