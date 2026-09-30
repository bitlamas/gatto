using System.Runtime.InteropServices;

namespace Gatto.Terminal;

//read and write the STD_INPUT_HANDLE console mode, seamed so the mode logic is tested against a fake
public interface IConsoleModeControl { uint Get(); void Set(uint mode); }

//the real control over the input console mode
public sealed class Win32ConsoleModeControl : IConsoleModeControl
{
    private const int STD_INPUT_HANDLE = -10;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint h, uint mode);
    public uint Get() { GetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), out var m); return m; }
    public void Set(uint mode) => SetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), mode);
}

//owns the input-mode change for mouse capture, so the shell's mode comes back on every exit path. a leaked mode would break the user's own shell selection
public sealed class ConsoleInputMode(IConsoleModeControl ctl)
{
    public const uint EnableProcessedInput = 0x0001;
    public const uint EnableMouseInput     = 0x0010;
    public const uint EnableQuickEdit      = 0x0040;
    public const uint EnableExtendedFlags  = 0x0080;

    private uint _saved;
    private bool _entered, _restored;

    //wire Restore to ProcessExit and UnhandledException before Enter, so a crash during entry still puts the shell's mode back
    public void RegisterExitHooks()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
    }

    //save the current mode and turn on mouse capture, once (a second call changes nothing)
    public void Enter()
    {
        if (_entered) return;
        _entered = true;
        _saved = ctl.Get();
        //the QuickEdit clear only works when EXTENDED_FLAGS is set in the same call. the ProcessedInput clear makes Ctrl+C a key record instead of a signal
        ctl.Set((_saved | EnableMouseInput | EnableExtendedFlags) & ~EnableQuickEdit & ~EnableProcessedInput);
    }

    //write the saved mode back verbatim, once, and do nothing if Enter never ran
    public void Restore()
    {
        if (!_entered || _restored) return;
        _restored = true;
        ctl.Set(_saved);
    }
}
