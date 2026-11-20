using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop.Permissions;

namespace Gatto.Tests;

//the write_dir limit as a pure function: what it reads, the three sentences, and the walk over a fake disk
public class RoleWriteLimitTests
{
    private const string Cwd = @"C:\p";
    private const string Outside = "this role writes only under research";
    private static readonly string[] Research = ["research"];

    private static ToolCall Call(string path, string tool = "write_file") =>
        new("c1", tool, JsonSerializer.Serialize(new { path, content = "x" }));

    //a disk where only the named paths, relative to the working folder, are links, and everything else is absent
    private static Func<string, FileAttributes?> Links(params string[] relative)
    {
        var links = relative.Select(r => Path.Combine(Cwd, r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return p => links.Contains(p) ? FileAttributes.Directory | FileAttributes.ReparsePoint : null;
    }

    private static readonly Func<string, FileAttributes?> Empty = _ => null;

    private static string? Check(string path, string tool = "write_file", Func<string, FileAttributes?>? disk = null, string[]? dirs = null) =>
        RoleWriteLimit.Refusal(Cwd, dirs ?? Research, Call(path, tool), disk ?? Empty);

    [Theory]
    [InlineData(@"research\a.md")]
    [InlineData(@"RESEARCH\a.md")]
    [InlineData(@"research\deep\er\a.md")]
    [InlineData(@"C:\p\research\a.md")]
    public void A_PATH_UNDER_THE_FOLDER_PASSES(string path) => Assert.Null(Check(path));

    [Theory]
    [InlineData(@"research-notes\a.txt")]
    [InlineData("a.md")]
    [InlineData(@"research\..\a.md")]
    [InlineData(@"C:\q\a.md")]
    [InlineData(@"~\a.md")]
    public void A_PATH_OUTSIDE_THE_FOLDER_IS_REFUSED(string path) => Assert.Equal(Outside, Check(path));

    [Theory]
    [InlineData("research")]
    [InlineData(@"research\")]
    [InlineData(@"C:\p\research")]
    public void THE_FOLDER_ITSELF_IS_REFUSED_WITH_ITS_OWN_SENTENCE(string path) =>
        Assert.Equal("research is the folder this role writes under", Check(path));

    [Fact]
    public void EDIT_FILE_IS_LIMITED_AS_WRITE_FILE_IS()
    {
        Assert.Equal(Outside, Check("a.md", "edit_file"));
        Assert.Null(Check(@"research\a.md", "edit_file"));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("grep")]
    [InlineData("shell")]
    [InlineData("memory_write")]
    public void A_TOOL_THAT_DOES_NOT_WRITE_A_PATH_IS_NOT_READ(string tool) => Assert.Null(Check("a.md", tool));

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"content":"x"}""")]
    [InlineData("""{"path":5}""")]
    [InlineData("[1]")]
    public void ARGUMENTS_WITH_NO_STRING_PATH_ARE_LEFT_TO_THE_TOOL(string json) =>
        Assert.Null(RoleWriteLimit.Refusal(Cwd, Research, new ToolCall("c1", "write_file", json), Empty));

    [Fact]
    public void NO_FOLDER_MEANS_NO_LIMIT() => Assert.Null(RoleWriteLimit.Refusal(Cwd, [], Call("a.md"), Empty));

    [Theory]
    [InlineData("research", "research")]
    [InlineData(@"research\sub", @"research\sub")]
    [InlineData(@"research\sub\a.md", @"research\sub\a.md")]
    public void A_LINK_ON_THE_WAY_REFUSES_AND_NAMES_THAT_SEGMENT(string link, string named) =>
        Assert.Equal($"{named} is a junction or a link, and this role does not write through one", Check(@"research\sub\a.md", disk: Links(link)));

    //a stream name reads as absent on disk while the write opens the stream through the link, so the segment is asked without it
    [Fact]
    public void A_STREAM_ON_A_LINK_IS_ASKED_AS_THE_LINK()
    {
        var link = Path.Combine(Cwd, "research", "link");
        Func<string, FileAttributes?> disk = p => string.Equals(p, link, StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory | FileAttributes.ReparsePoint : null;

        Assert.Equal(@"research\link is a junction or a link, and this role does not write through one", Check(@"research\link:s", disk: disk));
    }

    [Fact]
    public void A_FAULT_OTHER_THAN_ABSENT_LEAVES_THE_FUNCTION_IN_THE_SYSTEMS_WORDS()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() => Check(@"research\a.md", disk: _ => throw new UnauthorizedAccessException("no access")));

        Assert.Equal("no access", ex.Message);
    }

    //a working folder reached through a junction is where the user chose to run, so it is never asked
    [Fact]
    public void THE_WORKING_FOLDER_ITSELF_IS_NEVER_ASKED()
    {
        var asked = new List<string>();
        FileAttributes? Disk(string p)
        {
            asked.Add(p);
            return string.Equals(p.TrimEnd('\\'), Cwd, StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory | FileAttributes.ReparsePoint : null;
        }

        Assert.Null(Check("a.md", disk: Disk, dirs: ["."]));
        Assert.Equal(new[] { Path.Combine(Cwd, "a.md") }, asked);
    }

    [Fact]
    public void TWO_FOLDERS_THAT_DO_NOT_NEST_REFUSE_EVERY_WRITE_NAMING_BOTH()
    {
        string[] both = ["a", "b"];

        Assert.Equal("a write must be inside both a and b", Check(@"a\x.md", dirs: both));
        Assert.Equal("a write must be inside both a and b", Check(@"b\x.md", dirs: both));
    }

    [Fact]
    public void TWO_FOLDERS_THAT_NEST_ALLOW_THE_INNER_ONE()
    {
        string[] nested = ["a", @"a\b"];

        Assert.Null(Check(@"a\b\x.md", dirs: nested));
        Assert.Equal("a write must be inside both a and a\\b", Check(@"a\x.md", dirs: nested));
    }

    [Fact]
    public void THE_ATTRIBUTE_TEST_READS_REPARSE_ALONE()
    {
        Assert.True(RoleWriteLimit.IsLink(FileAttributes.ReparsePoint));
        Assert.True(RoleWriteLimit.IsLink(FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.False(RoleWriteLimit.IsLink(FileAttributes.Directory));
        Assert.False(RoleWriteLimit.IsLink(FileAttributes.Normal));
    }

    //the production reader against a real junction, made with mklink, which needs no privilege
    [Fact]
    public void ON_DISK_SEES_A_JUNCTION_WHOSE_TARGET_DOES_NOT_EXIST()
    {
        var root = Directory.CreateTempSubdirectory("gatto-limit-").FullName;
        try
        {
            var link = Path.Combine(root, "dead");
            var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(root, "notyet")}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            made.WaitForExit();
            Assert.Equal(0, made.ExitCode);

            Assert.True(RoleWriteLimit.IsLink(RoleWriteLimit.OnDisk(link)!.Value));
            Assert.Null(RoleWriteLimit.OnDisk(link + ":s"));
            Assert.Null(RoleWriteLimit.OnDisk(Path.Combine(link, "a.md")));
            Assert.Null(RoleWriteLimit.OnDisk(Path.Combine(root, "absent.md")));
            Assert.Throws<IOException>(() => RoleWriteLimit.OnDisk(Path.Combine(root, "bad<name.md")));
        }
        finally
        {
            foreach (var d in Directory.GetDirectories(root)) Directory.Delete(d);
            Directory.Delete(root, recursive: true);
        }
    }
}
