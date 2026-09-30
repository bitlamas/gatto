using Gatto.Core.Memory;

namespace Gatto.Tests;

public sealed class MemoryDirTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-mem").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void FindProjectRoot_IsCwd()
    {
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        Assert.Equal(bare, MemoryDir.FindProjectRoot(bare));
    }

    [Fact]
    public void FindProjectRoot_AncestorDotGattoIsIgnored()
    {
        //an ancestor folder holding .gatto must not capture the root, so the search never climbs
        var proj = Path.Combine(_root, "proj");
        Directory.CreateDirectory(Path.Combine(proj, ".gatto"));
        var deep = Path.Combine(proj, "src", "sub");
        Directory.CreateDirectory(deep);
        Assert.Equal(deep, MemoryDir.FindProjectRoot(deep));
    }

    [Theory]
    [InlineData("index"), InlineData("shell-gotchas"), InlineData("A-Topic")]  //mixed case is accepted and lowercased.
    public void ValidateTopic_Accepts(string t) => Assert.Equal(t.ToLowerInvariant(), MemoryDir.ValidateTopic(t));

    [Theory]
    [InlineData(".."), InlineData("a/b"), InlineData("a\\b"), InlineData("a.md"),
     InlineData(""), InlineData("x!"), InlineData("this-topic-name-is-way-way-way-too-long-over-forty")]
    public void ValidateTopic_Throws(string t) =>
        Assert.Throws<ArgumentException>(() => MemoryDir.ValidateTopic(t));

    //no filename is special, so index is treated as an ordinary slug
    [Fact]
    public void PathFor_NoLongerSpecialCasesIndex()
    {
        Assert.EndsWith(Path.Combine(".gatto", "memory", "index.md"), MemoryDir.PathFor(_root, "index"));
        Assert.EndsWith(Path.Combine(".gatto", "memory", "notes.md"), MemoryDir.PathFor(_root, "notes"));
    }

    [Fact]
    public void Write_CreatesFileAndDirs_EndsWithSingleNewline()
    {
        MemoryDir.Write(_root, "shell-gotchas", "- fact one");
        Assert.Equal("- fact one\n", File.ReadAllText(MemoryDir.PathFor(_root, "shell-gotchas")));
    }

    [Fact]
    public void Write_OverwritesWholeFile()
    {
        MemoryDir.Write(_root, "notes", "- a\n- b");
        MemoryDir.Write(_root, "notes", "- only this");
        Assert.Equal("- only this\n", File.ReadAllText(MemoryDir.PathFor(_root, "notes")));
    }

    //a fact file rewritten repeatedly must not grow blank lines or leak CRLF into the composed index.
    [Fact]
    public void Write_ContentIsOnlyNewlines_NormalizesToSingleNewline()
    {
        MemoryDir.Write(_root, "blank", "\n\n\n");
        Assert.Equal("\n", File.ReadAllText(MemoryDir.PathFor(_root, "blank")));
    }

    [Fact]
    public void Write_CrlfTerminatedContent_NormalizedToLf()
    {
        MemoryDir.Write(_root, "crlf", "- fact\r\n");
        Assert.Equal("- fact\n", File.ReadAllText(MemoryDir.PathFor(_root, "crlf")));
    }

    [Fact]
    public void Write_DoubleTrailingNewline_NoBlankGrowthAcrossRewrites()
    {
        MemoryDir.Write(_root, "notes", "- old\n\n");
        MemoryDir.Write(_root, "notes", "- new\n\n\n");
        Assert.Equal("- new\n", File.ReadAllText(MemoryDir.PathFor(_root, "notes")));
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        MemoryDir.Write(_root, "gone", "- doomed");
        MemoryDir.Delete(_root, "gone");
        Assert.False(File.Exists(MemoryDir.PathFor(_root, "gone")));
    }

    //a delete that silently succeeds on a missing name teaches the model that wrong slugs are fine. facts are addressed by slug, so the miss must throw.
    [Fact]
    public void Delete_MissingSlug_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MemoryDir.Delete(_root, "never-existed"));
        Assert.Equal("no such fact: never-existed", ex.Message);
    }

    [Fact]
    public void FactFiles_AreFilenameSorted_AndSkipNonMarkdown()
    {
        MemoryDir.Write(_root, "bravo", "- b");
        MemoryDir.Write(_root, "alpha", "- a");
        File.WriteAllText(Path.Combine(_root, ".gatto", "memory", "notes.txt"), "ignored");

        var names = MemoryDir.FactFiles(_root).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "alpha.md", "bravo.md" }, names);
    }

    [Fact]
    public void FactFiles_MissingDirectory_IsEmptyNotAThrow()
        => Assert.Empty(MemoryDir.FactFiles(Path.Combine(_root, "no-such-project")));

    [Fact]
    public void SizeCap_ThrowsWithCompressMessage()
    {
        var big = new string('x', MemoryDir.MaxFileBytes + 1);
        var ex = Assert.Throws<InvalidOperationException>(() => MemoryDir.Write(_root, "big", big));
        Assert.Contains("64 KB", ex.Message);
        Assert.Contains("compress", ex.Message);
    }
}
