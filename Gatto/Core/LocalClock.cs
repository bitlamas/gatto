using System.Runtime.InteropServices;

namespace Gatto.Core;

//the time of day in the user's own Windows short-time format. invariant globalization hides that format from .NET, so Windows formats the time itself
internal static class LocalClock
{
    private const uint LocaleShortTime = 0x79;
    private const int BufferChars = 80;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetLocaleInfoEx(string? localeName, uint type, char[] data, int chars);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetTimeFormatEx(string? localeName, uint flags, ref SystemTime time, string? format, char[] output, int chars);

    //null when Windows cannot answer, so the turn line leaves the time out and never guesses a format
    public static string? Now() => Format(DateTime.Now, ShortTimePattern(null), null);

    //a null locale name means the user's own setting, and an empty one is the invariant locale that always reads HH:mm
    public static string? ShortTimePattern(string? localeName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var buffer = new char[BufferChars];
            var n = GetLocaleInfoEx(localeName, LocaleShortTime, buffer, buffer.Length);
            return n > 1 ? new string(buffer, 0, n - 1) : null;
        }
        catch (Exception) { return null; }
    }

    //the text goes into one committed terminal row, so a control character in a hand-edited pattern drops the time rather than break the row
    public static string? Format(DateTime local, string? pattern, string? localeName)
    {
        if (pattern is null || !OperatingSystem.IsWindows()) return null;
        try
        {
            var time = new SystemTime
            {
                Year = (ushort)local.Year, Month = (ushort)local.Month, DayOfWeek = (ushort)local.DayOfWeek, Day = (ushort)local.Day,
                Hour = (ushort)local.Hour, Minute = (ushort)local.Minute, Second = (ushort)local.Second, Milliseconds = (ushort)local.Millisecond,
            };
            var buffer = new char[BufferChars];
            var n = GetTimeFormatEx(localeName, 0, ref time, pattern, buffer, buffer.Length);
            if (n <= 1) return null;
            var text = new string(buffer, 0, n - 1).Trim();
            return text.Length == 0 || text.Any(char.IsControl) ? null : text;
        }
        catch (Exception) { return null; }
    }
}
