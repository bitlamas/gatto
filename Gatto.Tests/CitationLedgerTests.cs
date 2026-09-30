using System.Text.Json;
using Gatto.Core.Web;

namespace Gatto.Tests;

public class CitationLedgerTests
{
    [Fact]
    public void Record_WithContent_ComputesSha256AndStoresEntry()
    {
        var ledger = new CitationLedger(() => null);

        ledger.Record("http://example.com/a", content: "hello world");

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal("http://example.com/a", entry.Ref);
        Assert.Equal("hello world", entry.Content);
        Assert.False(entry.SearchOnly);
        //the expected value is the lowercase hex SHA-256 of the string hello world
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", entry.Sha256);
    }

    [Fact]
    public void Record_WithoutContent_Sha256AndContentAreNull()
    {
        var ledger = new CitationLedger(() => null);

        ledger.Record("http://example.com/b");

        var entry = Assert.Single(ledger.Entries);
        Assert.Null(entry.Content);
        Assert.Null(entry.Sha256);
        Assert.False(entry.SearchOnly);
    }

    [Fact]
    public void Record_SearchOnly_RoundTripsTrue()
    {
        var ledger = new CitationLedger(() => null);

        ledger.Record("http://example.com/c", searchOnly: true);

        var entry = Assert.Single(ledger.Entries);
        Assert.True(entry.SearchOnly);
    }

    [Fact]
    public void NullPathProvider_WritesNoFile_ButEntriesStillQueryable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-ledger-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            //the path provider returns null, as in a one-shot run with no session file.
            var ledger = new CitationLedger(() => null);

            ledger.Record("http://example.com/d", content: "x");
            ledger.Record("http://example.com/e", searchOnly: true);

            Assert.Equal(2, ledger.Entries.Count);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Record_AppendsOneParseableJsonObjectPerLine_ToProvidedPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-ledger-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "s1.ledger.jsonl");
        try
        {
            var ledger = new CitationLedger(() => path);

            ledger.Record("http://example.com/f", content: "body-one", searchOnly: false);
            ledger.Record("http://example.com/g", searchOnly: true);

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            using (var doc = JsonDocument.Parse(lines[0]))
            {
                var el = doc.RootElement;
                Assert.Equal("http://example.com/f", el.GetProperty("ref").GetString());
                Assert.Equal("body-one", el.GetProperty("content").GetString());
                Assert.False(el.GetProperty("searchOnly").GetBoolean());
                Assert.True(el.TryGetProperty("at", out _));
                Assert.Equal(JsonValueKind.String, el.GetProperty("sha256").ValueKind);
            }

            using (var doc = JsonDocument.Parse(lines[1]))
            {
                var el = doc.RootElement;
                Assert.Equal("http://example.com/g", el.GetProperty("ref").GetString());
                Assert.Equal(JsonValueKind.Null, el.GetProperty("content").ValueKind);
                Assert.Equal(JsonValueKind.Null, el.GetProperty("sha256").ValueKind);
                Assert.True(el.GetProperty("searchOnly").GetBoolean());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RestoreFrom_RoundTripsEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-ledger-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "s2.ledger.jsonl");
        try
        {
            var writer = new CitationLedger(() => path);
            writer.Record("http://example.com/h", content: "abc");
            writer.Record("http://example.com/i", searchOnly: true);

            var reader = new CitationLedger(() => null);
            reader.RestoreFrom(path);

            Assert.Equal(2, reader.Entries.Count);
            Assert.Equal("http://example.com/h", reader.Entries[0].Ref);
            Assert.Equal("abc", reader.Entries[0].Content);
            Assert.NotNull(reader.Entries[0].Sha256);
            Assert.False(reader.Entries[0].SearchOnly);

            Assert.Equal("http://example.com/i", reader.Entries[1].Ref);
            Assert.Null(reader.Entries[1].Content);
            Assert.Null(reader.Entries[1].Sha256);
            Assert.True(reader.Entries[1].SearchOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RestoreFrom_SkipsMalformedMiddleLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-ledger-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "s3.ledger.jsonl");
        try
        {
            var goodFirst = "{\"at\":\"2026-07-11T00:00:00+00:00\",\"ref\":\"http://example.com/first\",\"sha256\":null,\"content\":null,\"searchOnly\":false}";
            var corrupt = "{not valid json at all";
            var goodLast = "{\"at\":\"2026-07-11T00:01:00+00:00\",\"ref\":\"http://example.com/last\",\"sha256\":null,\"content\":null,\"searchOnly\":true}";
            File.WriteAllLines(path, new[] { goodFirst, corrupt, goodLast });

            var ledger = new CitationLedger(() => null);
            ledger.RestoreFrom(path);

            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal("http://example.com/first", ledger.Entries[0].Ref);
            Assert.Equal("http://example.com/last", ledger.Entries[1].Ref);
            Assert.True(ledger.Entries[1].SearchOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PathProvider_IsEvaluatedOnEveryAppend_SoSwitchingSessionsSwitchesFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-ledger-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var pathA = Path.Combine(dir, "a.ledger.jsonl");
        var pathB = Path.Combine(dir, "b.ledger.jsonl");
        try
        {
            var current = pathA;
            var ledger = new CitationLedger(() => current);

            ledger.Record("http://example.com/into-a");
            current = pathB;
            ledger.Record("http://example.com/into-b");

            Assert.Single(File.ReadAllLines(pathA));
            Assert.Single(File.ReadAllLines(pathB));
            Assert.Equal(2, ledger.Entries.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
