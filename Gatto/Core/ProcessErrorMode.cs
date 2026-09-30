using System.Runtime.InteropServices;

namespace Gatto.Core;

//a .NET process inherits its parent's error mode, so a missing DLL opens a modal dialog unless SEM_FAILCRITICALERRORS is set
internal static class ProcessErrorMode
{
    //the failing call returns instead of raising a modal hard-error dialog
    private const uint SemFailCriticalErrors = 0x0001;

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();

    //the inherited mode stays and SEM_FAILCRITICALERRORS is OR-ed in, but SEM_NOGPFAULTERRORBOX must never be added, it would suppress the crash dumps
    public static void FailFastOnMissingDll()
    {
        try { SetErrorMode(GetErrorMode() | SemFailCriticalErrors); }
        catch (Exception) { }
    }
}
