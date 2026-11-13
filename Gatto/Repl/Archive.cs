using System.Text;
using Gatto.Core;

namespace Gatto.Repl;

//where a handed-over letter is kept: the archive mirrors the inbox under the nearest .gatto\mailbox above it, read from the letter's own path and never from the live watch
internal static class Archive
{
    //the archive folder of an inbox, null when no .gatto\mailbox sits above it or the inbox is under the archive already. the path's structure decides, so a role named archive or mailbox mirrors too
    public static string? FolderFor(string inbox)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inbox));
        for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (!Named(dir, "mailbox") || Path.GetDirectoryName(dir) is not { } parent || !Named(parent, ".gatto")) continue;
            var relative = Path.GetRelativePath(dir, full);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return first.Equals("archive", StringComparison.OrdinalIgnoreCase) ? null : Path.Combine(dir, "archive", relative);
        }
        return null;
    }

    //where a letter goes when its turn is kept, null when its inbox archives nothing. the file system drops a name's trailing dots and spaces, so the archive name is written without them and its read file matches it
    public static string? PathFor(string letter) =>
        Path.GetDirectoryName(Path.GetFullPath(letter)) is { } inbox && FolderFor(inbox) is { } folder
            ? Path.Combine(folder, Path.GetFileName(letter).TrimEnd('.', ' ')) : null;

    //moves the letter without overwrite and writes its read file beside it. the first value says the inbox no longer holds it, the second is the one line it says
    public static (bool Moved, string? Row) Move(string letter, string? readBy, string? session, DateTimeOffset now)
    {
        if (PathFor(letter) is not { } target) return (false, null);
        var name = Path.GetFileName(letter);
        if (!File.Exists(letter)) return (true, $"archive: {name} had already left the inbox, no read file written");   //gatto cannot tell which letter its archive holds under that name, so it vouches for none
        string? row = null;
        try
        {
            Watch.CreateFolder(Path.GetDirectoryName(target)!);
            if (Free(target) is var free && free != target)
            {
                target = free;
                row = $"archive: {name} was taken, kept as {Path.GetFileName(free)}";
            }
            File.Move(letter, target);
        }
        catch (Exception ex) { return (false, $"archive failed: {ex.Message}"); }
        if (readBy is null || session is null) return (true, row);
        try
        {
            using var file = new FileStream(target + ".read", FileMode.CreateNew, FileAccess.Write);
            file.Write(new UTF8Encoding(false).GetBytes($"read-by: {readBy}\nsession: {session}\nread: {SessionIdentity.TimeText(now)}\n"));
        }
        catch (Exception ex) { return (true, $"archive: {Path.GetFileName(target)} moved, its read file failed: {ex.Message}"); }
        return (true, row);
    }

    //the first of name, name-2, name-3 and on that neither a letter nor a read file holds, so a second letter under a taken name is kept beside the first
    private static string Free(string target)
    {
        if (!File.Exists(target) && !File.Exists(target + ".read")) return target;
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var n = 2; ; n++)
        {
            var next = Path.Combine(dir, $"{stem}-{n}{ext}");
            if (!File.Exists(next) && !File.Exists(next + ".read")) return next;
        }
    }

    private static bool Named(string dir, string name) => Path.GetFileName(dir).Equals(name, StringComparison.OrdinalIgnoreCase);
}
