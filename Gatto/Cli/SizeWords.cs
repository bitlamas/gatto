namespace Gatto.Cli;

//the one home for bytes as gigabytes, and the round-to-zero guard lives here. a size below the resolution says so, because 0 GB reads as free
internal static class SizeWords
{
    //below this "0.#" would print a zero. the boundary is stated once, so the guard and its test can't disagree
    internal const double RoundsToZero = 0.05;

    //takes a double so long and ulong callers both reach it, without overloads that could resolve differently for one number
    internal static string Gb(double bytes, bool approx = false)
    {
        var gb = Math.Max(0, bytes) / (1024.0 * 1024 * 1024);
        return gb < RoundsToZero
            ? "<0.1 GB"
            : (approx ? "~" : "")
              //always one decimal, because these figures are read down a column and a bare "5 GB" moves the digits beside a "5.5 GB"
              + gb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }

    //a download's size in the unit it is measured in: whole megabytes below a gigabyte, tenths of a gigabyte above
    public static string Auto(long bytes)
    {
        var safe = Math.Max(0, bytes);
        const double Gib = 1024.0 * 1024 * 1024;
        return safe < Gib
            ? Math.Round(safe / (1024.0 * 1024)).ToString("0",
                System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : Gb(safe);
    }

    //a progress pair, one unit for both figures. the total chooses the unit, so half way through a download the figures can't switch under the reader
    public static (string Done, string Total) Pair(long done, long total)
    {
        const double Gib = 1024.0 * 1024 * 1024;
        var safeTotal = Math.Max(0, total);
        var safeDone = Math.Min(safeTotal, Math.Max(0, done));
        if (safeTotal < Gib)
            return (Figure(safeDone / (1024.0 * 1024), "0"), Auto(safeTotal));
        return (Figure(safeDone / Gib, "0.#"), Gb(safeTotal));

        static string Figure(double v, string format) =>
            Math.Round(v, format == "0" ? 0 : 1)
                .ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    //whole gigabytes, for the machine's memory and what a model can use of it (nobody thinks of their RAM in tenths)
    public static string WholeGb(ulong bytes) =>
        Math.Round(bytes / (1024.0 * 1024 * 1024)).ToString("0",
            System.Globalization.CultureInfo.InvariantCulture) + " GB";
}
