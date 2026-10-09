using System.Runtime.InteropServices;
using System.Text.Json;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//what the wizard reads and how it holds the console, chosen once from the mouse setting. the double-click time is read here, beside the other console reads
internal sealed record WizardInput(IInputSource Source, ConsoleInputMode? Mode, bool QuietWheel, int DoubleClickMs)
{
    //with the mouse off the wheel would arrive as arrow keys, so the alt screen turns alternate scroll off
    internal static WizardInput For(bool mouse, Func<IInputSource> console, Func<IInputSource> keys, Func<ConsoleInputMode> mode,
        Func<int> doubleClickMs) =>
        mouse
            ? new WizardInput(console(), mode(), QuietWheel: false, doubleClickMs())
            : new WizardInput(keys(), null, QuietWheel: true, doubleClickMs());

    //the mouse key of gatto.json, true when the file, its JSON or the value cannot be read as a boolean. the wizard runs before the config loader and repairs a broken file, so nothing here may stop it
    internal static bool MouseOf(string home)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "gatto.json")));
            return doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("mouse", out var m)
                || m.ValueKind != JsonValueKind.False;
        }
        catch (Exception) { return true; }
    }

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    //the system's double-click time in ms, 500 when the call is not there to ask
    internal static int SystemDoubleClickMs()
    {
        try { return (int)GetDoubleClickTime(); }
        catch (Exception) { return 500; }
    }
}
