using System.Runtime.InteropServices;

namespace Gatto.Cli;

//a fatal message at launch must stay readable when gatto owns the console, so the wait sits at the outermost return
internal static class FatalPause
{
    //an instruction rather than an apology, so the reader knows the console is closing and may stop it
    internal const string CloseLine = "press any key to close";

    //dependencies come in as arguments so a test drives every shape without a console. it always returns the exit code it was given, this waits and never decides
    internal static int Hold(int exit, Func<int> clients, Action<string> say, Action wait)
    {
        if (exit == 0) return exit;
        if (clients() != 1) return exit;

        say(CloseLine);
        wait();
        return exit;
    }

    internal static int Hold(int exit) => Hold(exit, ConsoleClients, Console.Error.WriteLine, WaitForKey);

    //0 when the count can't be read, which isn't 1 so nothing waits
    private static int ConsoleClients()
    {
        try
        {
            var buffer = new uint[8];
            return (int)GetConsoleProcessList(buffer, (uint)buffer.Length);
        }
        catch (Exception) { return 0; }
    }

    //there is no user to press a key when input is redirected, and ReadKey throws on it, so the check comes first
    private static void WaitForKey()
    {
        try
        {
            if (Console.IsInputRedirected) return;
            Console.ReadKey(intercept: true);
        }
        catch (Exception) { } //no console to wait on, the message was already printed
    }

    //a small buffer still returns the real count, and eight is enough to tell 1 from more
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);
}
