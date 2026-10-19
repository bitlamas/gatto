using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//this switch reads the suite's own idea of a screen name, so a key assertion means something on its own
internal static class ScreenKey
{
    public static string Of(SetupScreen s) => s switch
    {
        SetupScreen.Choice c => c.Key,
        SetupScreen.Ask a => a.Key,
        SetupScreen.Info i => i.Key,
        SetupScreen.Progress p => p.Key,
        SetupScreen.Terminal t => t.Key,
        //the wildcard arm is the compiler's price, a new kind renders an empty key that fails every caller's comparison
        _ => "",
    };
}
