using System.Text.RegularExpressions;

namespace Gatto.Core.Session;

//what a sweep deletes: the transcripts last written before the date with every file of their stem, and the images no remaining file names
public sealed record SweepPlan(
    int Transcripts, IReadOnlyList<string> SessionFiles, long SessionBytes,
    IReadOnlyList<string> Blobs, long BlobBytes);

//the sweep reads and deletes, it never moves or rewrites a file. mail is never part of it
public static partial class SessionSweep
{
    //a blob is named by its sha256 and the extension of its media type, so its file name in a transcript is found without reading the record's shape
    [GeneratedRegex("^[0-9a-f]{64}[.][A-Za-z0-9]{1,8}$")]
    private static partial Regex BlobFile();

    public static SweepPlan Plan(string home, DateTime before)
    {
        var sessionsDir = Path.Combine(home, "sessions");
        var files = Directory.Exists(sessionsDir)
            ? new DirectoryInfo(sessionsDir).GetFiles("*", SearchOption.TopDirectoryOnly)
            : [];

        //a session's sidecars share its stem, so the stem before the first dot names every file of one session
        var swept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            if (IsTranscript(f.Name) && f.LastWriteTime < before)
                swept.Add(Stem(f.Name));

        var gone = new List<string>();
        var remaining = new List<string>();
        long sessionBytes = 0;
        foreach (var f in files)
        {
            if (swept.Contains(Stem(f.Name))) { gone.Add(f.FullName); sessionBytes += f.Length; }
            else remaining.Add(f.FullName);
        }

        var (blobs, blobBytes) = UnnamedBlobs(Path.Combine(home, "blobs"), remaining);
        return new SweepPlan(swept.Count, gone, sessionBytes, blobs, blobBytes);
    }

    //each file is deleted on its own, so one held file costs only itself, and every failure comes back with its reason
    public static IReadOnlyList<(string Path, string Why)> Delete(SweepPlan plan)
    {
        var failures = new List<(string, string)>();
        foreach (var path in plan.SessionFiles.Concat(plan.Blobs))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add((path, ex.Message)); }
        }
        return failures;
    }

    internal static bool IsTranscript(string name) =>
        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && !Gatto.Core.SessionFiles.IsSidecar(name);

    private static string Stem(string name) => name.IndexOf('.') is var dot and > 0 ? name[..dot] : name;

    //a blob stays when any file left in the sessions folder names it, a sidecar included, and a file in blobs that is not a blob name is never touched
    private static (IReadOnlyList<string> Blobs, long Bytes) UnnamedBlobs(string blobsDir, IReadOnlyList<string> remaining)
    {
        if (!Directory.Exists(blobsDir)) return ([], 0);
        var candidates = new DirectoryInfo(blobsDir).GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => BlobFile().IsMatch(f.Name))
            .ToList();
        if (candidates.Count == 0) return ([], 0);

        //one vectorised search for every candidate name, and the walk stops once each is found, since a home holds far fewer images than transcript bytes
        var names = candidates.Select(f => f.Name).ToArray();
        var search = System.Buffers.SearchValues.Create(names, StringComparison.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in remaining)
        {
            if (named.Count == names.Length) break;
            string text;
            //a live session is rewritten whole by rename, so a shared read sees the old file or the new one
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                //a file that cannot be read might name any blob, so none is called unnamed
                return ([], 0);
            }
            var rest = text.AsSpan();
            while (rest.IndexOfAny(search) is var at and >= 0)
            {
                var hit = rest[at..];
                foreach (var n in names)
                    if (hit.StartsWith(n, StringComparison.Ordinal)) { named.Add(n); break; }
                rest = rest[(at + 1)..];
            }
        }

        var unnamed = candidates.Where(f => !named.Contains(f.Name)).ToList();
        return (unnamed.Select(f => f.FullName).ToList(), unnamed.Sum(f => f.Length));
    }
}
