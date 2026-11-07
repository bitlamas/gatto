namespace Gatto.Core;

//the operating system as the prompt names it, read from the runtime so a port changes nothing here
public static class OsWords
{
    public static string Name() => Name(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(),
        System.Runtime.InteropServices.RuntimeInformation.OSDescription);

    //the three gatto names, and the runtime's own description for anything else
    internal static string Name(bool windows, bool linux, bool mac, string description) =>
        windows ? "Windows" : linux ? "Linux" : mac ? "macOS" : description;
}
