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

    //where a letter goes when its turn is kept, null when its inbox archives nothing
    public static string? PathFor(string letter) =>
        Path.GetDirectoryName(Path.GetFullPath(letter)) is { } inbox && FolderFor(inbox) is { } folder
            ? Path.Combine(folder, Path.GetFileName(letter)) : null;

    //moves the letter without overwrite and writes its read file beside it. the first value says the inbox no longer holds it, the second is the one line a failure says
    public static (bool Moved, string? Row) Move(string letter, string? readBy, string? session, DateTimeOffset now)
    {
        if (PathFor(letter) is not { } target) return (false, null);
        var name = Path.GetFileName(letter);
        try
        {
            Watch.CreateFolder(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) return (false, $"archive: {name} already there, letter kept in the inbox");
            File.Move(letter, target);
        }
        catch (Exception ex) { return (false, $"archive failed: {ex.Message}"); }
        if (readBy is null || session is null) return (true, null);
        try
        {
            using var file = new FileStream(target + ".read", FileMode.CreateNew, FileAccess.Write);
            file.Write(new UTF8Encoding(false).GetBytes($"read-by: {readBy}\nsession: {session}\nread: {SessionIdentity.TimeText(now)}\n"));
        }
        catch (Exception ex) { return (true, $"archive: {name} moved, its read file failed: {ex.Message}"); }
        return (true, null);
    }

    private static bool Named(string dir, string name) => Path.GetFileName(dir).Equals(name, StringComparison.OrdinalIgnoreCase);
}
