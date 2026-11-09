using System.Text;
using Gatto.Repl;

namespace Gatto.Tests.Repl;

//the archive mirrors an inbox under the nearest .gatto\mailbox above it, reading the path's structure and never its words, and the move keeps the letter whole and never overwrites
public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-archive-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void THE_ARCHIVE_MIRRORS_THE_INBOX_AT_WHATEVER_DEPTH_IT_HAS()
    {
        Assert.Equal(At(".gatto", "mailbox", "archive", "gatto", "m", "r"), Archive.FolderFor(At(".gatto", "mailbox", "gatto", "m", "r")));
        Assert.Equal(At(".gatto", "mailbox", "archive", "h", "s"), Archive.FolderFor(At(".gatto", "mailbox", "h", "s")));
        Assert.Equal(At(".gatto", "mailbox", "archive", "h", "n", "coder"), Archive.FolderFor(At(".gatto", "mailbox", "h", "n", "coder")));
    }

    [Fact]
    public void THE_NAMES_COMPARE_AS_THE_FILE_SYSTEM_DOES()
    {
        Assert.Equal(At(".gatto", "Mailbox", "archive", "gatto", "m", "r"), Archive.FolderFor(At(".gatto", "Mailbox", "gatto", "m", "r")));
        Assert.Null(Archive.FolderFor(At(".gatto", "mailbox", "Archive", "gatto", "m", "r")));
    }

    [Fact]
    public void A_ROLE_NAMED_MAILBOX_OR_ARCHIVE_MIRRORS_TO_ITS_OWN_PATH()
    {
        Assert.Equal(At(".gatto", "mailbox", "archive", "gatto", "m", "mailbox"), Archive.FolderFor(At(".gatto", "mailbox", "gatto", "m", "mailbox")));
        Assert.Equal(At(".gatto", "mailbox", "archive", "gatto", "m", "archive"), Archive.FolderFor(At(".gatto", "mailbox", "gatto", "m", "archive")));
    }

    [Fact]
    public void A_FOLDER_UNDER_THE_ARCHIVE_OR_WITH_NO_ROOT_ARCHIVES_NOTHING()
    {
        Assert.Null(Archive.FolderFor(At(".gatto", "mailbox", "archive", "h", "s")));
        Assert.Null(Archive.FolderFor(At("project", "inbox")));
        Assert.Null(Archive.FolderFor(At("mailbox", "proj", "inbox")));   //that mailbox has no .gatto parent
        Assert.Null(Archive.FolderFor(At(".gatto", "mailbox")));
    }

    private string Letter(string folder, string name, string body = "the letter")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void A_MOVED_LETTER_LEAVES_THE_INBOX_AND_ITS_READ_FILE_HOLDS_THREE_LINES()
    {
        var letter = Letter(At(".gatto", "mailbox", "gatto", "m", "r"), "a.md");
        var now = new DateTimeOffset(2026, 11, 9, 16, 16, 3, TimeSpan.Zero);

        Assert.Equal((true, (string?)null), Archive.Move(letter, "gatto/m/r/0123abcd", "0123abcd-0000-0000-0000-000000000000", now));

        var kept = At(".gatto", "mailbox", "archive", "gatto", "m", "r", "a.md");
        Assert.False(File.Exists(letter));
        Assert.Equal("the letter", File.ReadAllText(kept));
        Assert.Equal("*\n", File.ReadAllText(At(".gatto", "mailbox", "archive", "gatto", "m", "r", ".gitignore")));
        var bytes = File.ReadAllBytes(kept + ".read");
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', bytes);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("", lines[3]);
        Assert.Equal(["read-by", "session", "read"], lines[..3].Select(l => l[..l.IndexOf(": ", StringComparison.Ordinal)]));
        Assert.Equal("read-by: gatto/m/r/0123abcd", lines[0]);
        Assert.Equal("session: 0123abcd-0000-0000-0000-000000000000", lines[1]);
        Assert.Matches(@"^read: \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", lines[2]);
    }

    [Fact]
    public void A_LETTER_IN_A_FOLDER_WITH_NO_ROOT_STAYS_AND_NOTHING_IS_SAID()
    {
        var letter = Letter(At("project", "inbox"), "a.md");
        Assert.Equal((false, (string?)null), Archive.Move(letter, "r", "s", DateTimeOffset.Now));
        Assert.True(File.Exists(letter));
        Assert.False(Directory.Exists(At("project", "inbox", "archive")));
    }

    [Fact]
    public void A_NAME_ALREADY_IN_THE_ARCHIVE_KEEPS_THE_LETTER_IN_THE_INBOX_WITH_ONE_ROW()
    {
        var inbox = At(".gatto", "mailbox", "gatto", "m", "r");
        Archive.Move(Letter(inbox, "a.md", "first"), "r", "s", DateTimeOffset.Now);
        var second = Letter(inbox, "a.md", "second");

        Assert.Equal((false, (string?)"archive: a.md already there, letter kept in the inbox"), Archive.Move(second, "r", "s", DateTimeOffset.Now));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.Equal("first", File.ReadAllText(At(".gatto", "mailbox", "archive", "gatto", "m", "r", "a.md")));
    }

    [Fact]
    public void WITH_NO_SESSION_THE_LETTER_MOVES_AND_NO_READ_FILE_IS_WRITTEN()
    {
        var letter = Letter(At(".gatto", "mailbox", "gatto", "m", "r"), "a.md");
        Assert.Equal((true, (string?)null), Archive.Move(letter, null, null, DateTimeOffset.Now));
        Assert.True(File.Exists(At(".gatto", "mailbox", "archive", "gatto", "m", "r", "a.md")));
        Assert.False(File.Exists(At(".gatto", "mailbox", "archive", "gatto", "m", "r", "a.md.read")));
    }

    [Fact]
    public void A_READ_FILE_THAT_CANNOT_BE_WRITTEN_IS_ONE_ROW_AND_THE_MOVE_STANDS()
    {
        var letter = Letter(At(".gatto", "mailbox", "gatto", "m", "r"), "a.md");
        Directory.CreateDirectory(At(".gatto", "mailbox", "archive", "gatto", "m", "r", "a.md.read"));

        var (moved, row) = Archive.Move(letter, "r", "s", DateTimeOffset.Now);

        Assert.True(moved);
        Assert.StartsWith("archive: a.md moved, its read file failed: ", row);
        Assert.False(File.Exists(letter));
        Assert.True(File.Exists(At(".gatto", "mailbox", "archive", "gatto", "m", "r", "a.md")));
    }
}
