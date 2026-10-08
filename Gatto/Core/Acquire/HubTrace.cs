using System.Diagnostics;
using System.Globalization;

namespace Gatto.Core.Acquire;

//a timed line per Hub read when GATTO_HUB_TRACE names a file, off by default, so a slow shelf is measured on the machine it is slow on
internal static class HubTrace
{
    internal const string Variable = "GATTO_HUB_TRACE";

    //the file the lines go to, null when the trace is off. a test points it at its own file
    internal static string? Target { get; set; } =
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;

    private static readonly object Gate = new();

    //one line: kind, where it came from (net, disk, memo), repo or subject, file, bytes, milliseconds. tab separated, since a repo id holds no tab
    internal static void Write(string kind, string from, string subject, string? file, long bytes, long ms)
    {
        if (Target is not { } target) return;
        var line = string.Join('\t', kind, from, subject, file ?? "-",
            bytes.ToString(CultureInfo.InvariantCulture), ms.ToString(CultureInfo.InvariantCulture));
        try
        {
            lock (Gate) File.AppendAllText(target, line + "\n");
        }
        //a trace that cannot be written must never fail a search
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    //a hit that cost no request, timed at zero
    internal static void Hit(string kind, string from, string subject, string? file = null) =>
        Write(kind, from, subject, file, 0, 0);

    //each ranged read of one file is a line of its own, so a table that grew to 32 MB shows every round trip
    internal static Gatto.Core.Models.RangeFetch Ranged(Gatto.Core.Models.RangeFetch fetch, string kind, string repoId, string file)
    {
        if (Target is null) return fetch;
        return async (offset, count, ct) =>
        {
            var clock = Stopwatch.StartNew();
            var got = await fetch(offset, count, ct).ConfigureAwait(false);
            Write(kind, "net", repoId, file, got.Length, clock.ElapsedMilliseconds);
            return got;
        };
    }
}
