using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Gatto.Cli;

namespace Gatto.Tests;

//the client is untimed and the deadline is a stall: no bytes for N seconds, so a slow link finishes
public class UpdateDownloadTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-dl-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private static ReleaseAsset Asset(string name = "gatto-0.4.2-win-x64.zip", long size = 0) =>
        new(name, "https://example.invalid/" + name, null, size);

    //serves bytes in chunks with a delay, and with stopAfter it keeps the stream open and goes quiet
    private sealed class PausingHandler(int total, int chunk, TimeSpan delay, int? stopAfter = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = new DribbleStream(total, chunk, delay, stopAfter);
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            res.Content.Headers.ContentLength = total;
            return Task.FromResult(res);
        }
    }

    private sealed class DribbleStream(int total, int chunk, TimeSpan delay, int? stopAfter) : Stream
    {
        private int _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_sent >= total) return 0;
            if (stopAfter is { } stop && _sent >= stop)
            {
                //alive and producing nothing, only a stall budget ends this
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 0;
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
            var n = Math.Min(Math.Min(chunk, buffer.Length), total - _sent);
            buffer.Span[..n].Fill(0x41);
            _sent += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => total;
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    //the budget

    //assert on the client itself, an untimed one, or a constant refuses a slow link that would have finished
    [Fact]
    public void THE_CLIENT_IS_UNTIMED_because_a_total_budget_would_refuse_a_slow_link()
    {
        using var http = UpdateDownload.Client(TimeSpan.FromSeconds(5));
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Fact]
    public async Task A_SLOW_TRANSFER_COMPLETES_because_slow_is_not_dead()
    {
        //this transfer is far slower than the check's 3s budget and must still finish
        using var http = new HttpClient(new PausingHandler(640 * 1024, 64 * 1024, TimeSpan.FromMilliseconds(60)));

        var path = await UpdateDownload.FetchAsync(
            http, Asset(size: 640 * 1024), UpdateDownload.Dir(_home), null, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(640 * 1024, new FileInfo(path).Length);
        Assert.EndsWith("gatto-0.4.2-win-x64.zip", path);
    }

    //a killed transfer leaves no partial behind, the .part file must go with it
    [Fact]
    public async Task A_STALLED_TRANSFER_DIES_WITHIN_THE_BUDGET_and_leaves_no_partial()
    {
        using var http = new HttpClient(new PausingHandler(
            total: 4 * 1024 * 1024, chunk: 256 * 1024, delay: TimeSpan.FromMilliseconds(1),
            stopAfter: 1024 * 1024));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownload.FetchAsync(
            http, Asset(size: 4 * 1024 * 1024), UpdateDownload.Dir(_home), null, CancellationToken.None,
            stall: TimeSpan.FromMilliseconds(500)));

        Assert.Empty(Directory.GetFiles(UpdateDownload.Dir(_home)));
    }

    //a failing rename must still clean up, the method owes its cleanup guarantee at its own boundary
    [Fact]
    public async Task A_FAILING_RENAME_ALSO_LEAVES_NO_PARTIAL()
    {
        var dir = Directory.CreateDirectory(UpdateDownload.Dir(_home)).FullName;
        var final = Path.Combine(dir, "gatto-0.4.2-win-x64.zip");
        await File.WriteAllTextAsync(final, "a stale artifact from an earlier run");

        //no sharing, so the delete-then-move at the end of FetchAsync cannot succeed
        using var hold = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.None);
        using var http = new HttpClient(new PausingHandler(64 * 1024, 32 * 1024, TimeSpan.Zero));

        await Assert.ThrowsAnyAsync<Exception>(() => UpdateDownload.FetchAsync(
            http, Asset(size: 64 * 1024), UpdateDownload.Dir(_home), null, CancellationToken.None));

        Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PROGRESS_REPORTS_BYTES_not_a_percentage()
    {
        using var http = new HttpClient(new PausingHandler(256 * 1024, 64 * 1024, TimeSpan.Zero));
        var reports = new List<(long Done, long Total)>();

        await UpdateDownload.FetchAsync(http, Asset(size: 256 * 1024), UpdateDownload.Dir(_home),
            new Progress<(long, long)>(r => { lock (reports) reports.Add(r); }), CancellationToken.None);

        //the assertion is about what was reported, Progress<T> posts asynchronously so the last callback may still be queued
        await Task.Delay(50);
        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.All(reports, r => Assert.Equal(256 * 1024, r.Total));
            Assert.Contains(reports, r => r.Done == 256 * 1024);
        }
    }

    //verification

    [Fact]
    public async Task VERIFY_ACCEPTS_THE_DIGEST_OF_THE_BYTES_IT_HASHED()
    {
        var file = Path.Combine(_home, "payload.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5]);
        var real = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant();

        Assert.Null(await UpdateDownload.VerifyAsync(file, real, CancellationToken.None));
    }

    //an absent digest refuses as loudly as a wrong one, an unverifiable release must not install
    [Fact]
    public async Task VERIFY_REFUSES_A_MISMATCH_AND_AN_ABSENT_DIGEST_the_same_way()
    {
        var file = Path.Combine(_home, "payload.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5]);

        var mismatch = await UpdateDownload.VerifyAsync(file, new string('a', 64), CancellationToken.None);
        var absent = await UpdateDownload.VerifyAsync(file, null, CancellationToken.None);

        Assert.NotNull(mismatch);
        Assert.NotNull(absent);
        Assert.Contains("verify", absent, StringComparison.OrdinalIgnoreCase);
    }

    //extraction

    //builds the three-entry shape the release ships, an exe-only fixture would be green against the wrong archive
    private string ThreeEntryZip(bool hostile = false)
    {
        var zip = Path.Combine(_home, "release.zip");
        using (var fs = File.Create(zip))
        using (var ar = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "gatto.exe", "LICENSE", "README.md" })
                using (var w = new StreamWriter(ar.CreateEntry(name).Open())) w.Write("content of " + name);

            if (hostile)
            {
                //a traversal name and a same-named entry in a subfolder, so an exact-name lookup would find neither
                using (var w = new StreamWriter(ar.CreateEntry(@"..\evil.exe").Open())) w.Write("pwned");
                using (var w = new StreamWriter(ar.CreateEntry("sub/gatto.exe").Open())) w.Write("decoy");
            }
        }
        return zip;
    }

    [Fact]
    public void EXTRACT_TAKES_EXACTLY_ONE_NAMED_ENTRY_from_a_three_entry_zip()
    {
        var zip = ThreeEntryZip();
        using (var ar = ZipFile.OpenRead(zip)) Assert.Equal(3, ar.Entries.Count);   //the fixture itself must really have three entries
        var into = Directory.CreateDirectory(Path.Combine(_home, "into")).FullName;

        Assert.Null(UpdateDownload.ExtractExe(zip, into));

        Assert.Equal(["gatto.exe"], Directory.GetFiles(into).Select(f => Path.GetFileName(f)!).ToArray());
    }

    //a traversal that worked would write outside the target, so the oracle is the parent folder's list
    [Fact]
    public void A_HOSTILE_ENTRY_IS_NEVER_WRITTEN()
    {
        var zip = ThreeEntryZip(hostile: true);
        var into = Directory.CreateDirectory(Path.Combine(_home, "into")).FullName;
        var before = Directory.GetFiles(_home).OrderBy(x => x).ToArray();

        Assert.Null(UpdateDownload.ExtractExe(zip, into));

        Assert.Equal(["gatto.exe"], Directory.GetFiles(into).Select(f => Path.GetFileName(f)!).ToArray());
        Assert.Equal("content of gatto.exe", File.ReadAllText(Path.Combine(into, "gatto.exe")));
        Assert.Equal(before, Directory.GetFiles(_home).OrderBy(x => x).ToArray());
        Assert.False(File.Exists(Path.Combine(_home, "evil.exe")));
    }

    [Fact]
    public void EXTRACT_REFUSES_AN_ARCHIVE_WITH_NO_GATTO_EXE()
    {
        var zip = Path.Combine(_home, "empty.zip");
        using (var fs = File.Create(zip))
        using (var ar = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var w = new StreamWriter(ar.CreateEntry("LICENSE").Open())) w.Write("only this");
        var into = Directory.CreateDirectory(Path.Combine(_home, "into")).FullName;

        Assert.NotNull(UpdateDownload.ExtractExe(zip, into));
        Assert.Empty(Directory.GetFiles(into));
    }

    //the sweep

    //clears what a killed process left behind, and stays silent when there is no folder at all
    [Fact]
    public void SWEEP_CLEARS_THE_UPDATE_FOLDER_and_is_silent_when_absent()
    {
        var dir = Directory.CreateDirectory(UpdateDownload.Dir(_home)).FullName;
        File.WriteAllText(Path.Combine(dir, "gatto-0.4.2-win-x64.zip.part"), "half a download");
        File.WriteAllText(Path.Combine(dir, "gatto-0.4.2-win-x64.zip"), "a whole one");

        //a subfolder is staged under the update folder, so the sweep has to clear directories as well
        var staged = Directory.CreateDirectory(Path.Combine(dir, "staged")).FullName;
        File.WriteAllText(Path.Combine(staged, "gatto.exe"), "an extracted binary");

        UpdateDownload.Sweep(_home);

        //use FileSystemEntries, an assertion written with GetFiles shares the defect's blind spot
        Assert.Empty(Directory.GetFileSystemEntries(dir));

        UpdateDownload.Sweep(Path.Combine(_home, "no-such-home"));   //a folder that isn't there must not throw
    }
}
