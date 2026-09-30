using System.Runtime.InteropServices;
using System.Text;

namespace Gatto.Terminal;

public sealed record TermCaps(bool Rich, bool TrueColor)
{
    public static TermCaps Plain => new(false, false);

    public static TermCaps Detect() =>
        Detect(Environment.GetEnvironmentVariable, Console.IsOutputRedirected, Console.IsInputRedirected, TryEnableVt);

    public static TermCaps Detect(Func<string, string?> env, bool stdoutRedirected, bool stdinRedirected, Func<bool> tryEnableVt)
    {
        if (stdoutRedirected) return Plain;
        if (stdinRedirected) return Plain;     //raw-mode ReadKey needs a real console on stdin too
        if (env("NO_COLOR") is { Length: > 0 }) return Plain;   //any non-empty NO_COLOR disables color, even a value like 0
        if (!tryEnableVt()) return Plain;
        var trueColor = env("WT_SESSION") is { Length: > 0 }
            || (env("COLORTERM") ?? "").Contains("truecolor")
            || (env("COLORTERM") ?? "").Contains("24bit");
        return new TermCaps(Rich: true, TrueColor: trueColor);
    }

    //capabilities for a write-only command like gatto doctor, where stdin redirection doesn't matter because that check is for the REPL's raw-mode ReadKey
    public static TermCaps DetectForOutput() =>
        DetectForOutput(Environment.GetEnvironmentVariable, Console.IsOutputRedirected, TryEnableVt);

    public static TermCaps DetectForOutput(Func<string, string?> env, bool stdoutRedirected, Func<bool> tryEnableVt) =>
        Detect(env, stdoutRedirected, stdinRedirected: false, tryEnableVt);

    //best-effort OSC 11 background query for theme auto: reads the rgb reply within about 150 ms and returns it or null. typing during the wait is discarded
    public static string? QueryBackgroundColor()
    {
        try
        {
            if (Console.IsOutputRedirected || Console.IsInputRedirected) return null;
            Console.Out.Write("\x1b]11;?\x07");
            Console.Out.Flush();

            var sb = new StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(150);
            while (DateTime.UtcNow < deadline)
            {
                if (Console.KeyAvailable)
                {
                    var ch = Console.ReadKey(intercept: true).KeyChar;
                    if (ch == '\x1b' && sb.Length > 0)
                    {
                        //the ST terminator (ESC \) has a trailing backslash to drain, or it shows up as a stray character in the first prompt read
                        if (Console.KeyAvailable && Console.ReadKey(intercept: true).KeyChar != '\\')
                        { } //the second byte isn't the ST backslash, so it's junk from the reply
                        break;
                    }
                    if (ch is '\a' or '\x1b') continue;   //skip the BEL that ends a reply and a stray ESC that opens the next sequence
                    sb.Append(ch);
                }
                else if (sb.Length > 0) break;   //nothing more is waiting, so the reply is whole
                else System.Threading.Thread.Sleep(2);
            }
            var s = sb.ToString();
            return s.Contains("rgb:", StringComparison.Ordinal) ? s : null;
        }
        catch (Exception) { return null; }
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVtProcessing = 0x0004;

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint h, uint mode);

    private static bool TryEnableVt()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;  //POSIX terminals already speak VT
        try
        {
            var h = GetStdHandle(StdOutputHandle);
            if (h == 0 || h == -1) return false;
            if (!GetConsoleMode(h, out var mode)) return false;
            return (mode & EnableVtProcessing) != 0 || SetConsoleMode(h, mode | EnableVtProcessing);
        }
        catch (Exception) { return false; }
    }
}
