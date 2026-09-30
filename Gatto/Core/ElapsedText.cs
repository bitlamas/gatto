namespace Gatto.Core;

//one spelling for an elapsed time, in whole seconds with the remainder dropped, so 7435.6 s reads 2h 3m 55s
internal static class ElapsedText
{
    public static string Of(TimeSpan t)
    {
        var total = (long)Math.Floor(Math.Max(0, t.TotalSeconds));
        if (total == 0) return "<1s";
        var (h, m, s) = (total / 3600, total / 60 % 60, total % 60);
        return h > 0 ? $"{h}h {m}m {s}s" : m > 0 ? $"{m}m {s}s" : $"{s}s";
    }
}
