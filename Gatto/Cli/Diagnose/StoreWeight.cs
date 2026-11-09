using System.Globalization;

namespace Gatto.Cli.Diagnose;

//what a store under the home or the project weighs: its items, every byte in its folder and the oldest item's last write. no threshold, a fixed number would be a rubric
internal static class StoreWeight
{
    internal sealed record Weight(int Items, long Bytes, DateTime? Oldest);

    //null when the folder does not exist. an item is a file the include test accepts, the size counts every file so sidecars weigh too
    internal static Weight? Of(string folder, Func<string, bool> isItem, bool recurse)
    {
        if (!Directory.Exists(folder)) return null;
        var items = 0;
        long bytes = 0;
        DateTime? oldest = null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            try
            {
                bytes += file.Length;
                if (!isItem(file.Name)) continue;
                items++;
                var written = file.LastWriteTime;
                if (oldest is null || written < oldest) oldest = written;
            }
            catch (IOException) { }   //a file removed between the listing and the read is not weighed
        }
        return new Weight(items, bytes, oldest);
    }

    //a transcript is a session jsonl, its ledger and trace sidecars weigh but are not counted
    internal static bool IsTranscript(string name) =>
        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && !Gatto.Core.SessionFiles.IsSidecar(name);

    internal static bool IsLetter(string name) => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    //the row reads the count, the size and the oldest date, and an empty store says it is empty
    internal static string Row(string store, string noun, Weight? weight, string shownPath, string dot)
    {
        if (weight is null) return $"{store}: none {dot} no {shownPath}";
        if (weight.Items == 0) return $"{store}: none {dot} {shownPath}";
        var oldest = weight.Oldest!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{store}: {Gatto.Core.Plural.Of(weight.Items, noun)}, {Size(weight.Bytes)}, oldest {oldest} {dot} {shownPath}";
    }

    //whole kilobytes below a megabyte, so a small store never reads as 0 MB, then the shared download form
    internal static string Size(long bytes)
    {
        var kb = Math.Ceiling(Math.Max(0, bytes) / 1024.0);
        return kb < 1024 ? kb.ToString("0", CultureInfo.InvariantCulture) + " KB" : SizeWords.Auto(bytes);
    }
}
