using Gatto.Repl.Input;

namespace Gatto.Tests;

public class HistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-hist").FullName;
    private string PathOf() => Path.Combine(_dir, "history.txt");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RoundTrips_MultilineEntries()
    {
        var h = new History(PathOf());
        h.Record("one\ntwo");
        var h2 = new History(PathOf());
        Assert.Equal(new[] { "one\ntwo" }, h2.Entries);
    }

    [Fact]
    public void SkipsEmpty_AndConsecutiveDuplicates()
    {
        var h = new History(PathOf());
        h.Record(""); h.Record("a"); h.Record("a"); h.Record("b"); h.Record("a");
        Assert.Equal(new[] { "a", "b", "a" }, h.Entries);
    }

    [Fact]
    public void CapsAt500_DroppingOldest()
    {
        var h = new History(PathOf());
        for (var i = 0; i < 510; i++) h.Record($"e{i}");
        Assert.Equal(500, h.Entries.Count);
        Assert.Equal("e10", h.Entries[0]);
    }

    [Fact]
    public void Walk_OlderThenNewer_RestoresDraft()
    {
        var h = new History(PathOf());
        h.Record("first"); h.Record("second");
        Assert.Equal("second", h.Older("my draft"));
        Assert.Equal("first", h.Older("ignored"));     //the draft is kept on the first Older call, and later calls ignore their argument
        Assert.Null(h.Older("ignored"));               //past the oldest entry there is nothing older.
        Assert.Equal("second", h.Newer());
        Assert.Equal("my draft", h.Newer());           //past the newest entry, Newer returns the saved draft
        Assert.Null(h.Newer());
    }

    [Fact]
    public void CorruptLines_AreSkipped()
    {
        File.WriteAllLines(PathOf(), new[] { "\"good\"", "not json {", "\"also good\"" });
        var h = new History(PathOf());
        Assert.Equal(new[] { "good", "also good" }, h.Entries);
    }

    [Fact]
    public void MissingFile_IsEmpty()
    {
        var h = new History(Path.Combine(_dir, "nope", "history.txt"));
        Assert.Empty(h.Entries);
        h.Record("works");                             //saving creates the missing folder instead of throwing.
        Assert.Single(h.Entries);
    }
}
