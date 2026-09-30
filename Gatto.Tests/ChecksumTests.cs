using Gatto.Core.Acquire;

namespace Gatto.Tests;

//the expected digests are published SHA-256 values for abc and the empty string. deriving them from .NET's hash would prove only that SHA-256 equals itself
public class ChecksumTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files) { try { File.Delete(f); } catch { } }
    }

    private string TempFile(byte[] content)
    {
        var p = Path.Combine(Path.GetTempPath(), "gatto-sum-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(p, content);
        _files.Add(p);
        return p;
    }

    private const string ShaOfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string ShaOfEmpty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task A_known_vector_matches()
    {
        var r = await Checksum.Sha256Async(TempFile("abc"u8.ToArray()), ShaOfAbc, null, CancellationToken.None);
        Assert.True(r.Match);
        Assert.Equal(ShaOfAbc, r.ActualSha256);
    }

    [Fact]
    public async Task The_comparison_is_case_insensitive_and_tolerates_surrounding_space()
    {
        var r = await Checksum.Sha256Async(TempFile("abc"u8.ToArray()),
            "  " + ShaOfAbc.ToUpperInvariant() + "  ", null, CancellationToken.None);
        Assert.True(r.Match);
    }

    [Fact]
    public async Task An_empty_file_hashes_to_the_published_empty_vector()
    {
        var r = await Checksum.Sha256Async(TempFile([]), ShaOfEmpty, null, CancellationToken.None);
        Assert.True(r.Match);
    }

    [Fact]
    public async Task A_mismatch_reports_what_was_actually_there()
    {
        //report the digest actually found, so the user can act on it
        var r = await Checksum.Sha256Async(TempFile("abc"u8.ToArray()), new string('0', 64), null, CancellationToken.None);
        Assert.False(r.Match);
        Assert.Equal(ShaOfAbc, r.ActualSha256);
    }

    //record progress on the producer thread, otherwise Progress<T> posting could reorder the reports and fail a correct implementation
    [Fact]
    public async Task Progress_runs_monotonically_to_one()
    {
        var seen = new List<double>();
        await Checksum.Sha256Async(TempFile(new byte[4 << 20]), ShaOfEmpty,
            new Synchronously(seen.Add), CancellationToken.None);

        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(x => x), seen);          //the list is in report order, so comparing it with its sorted form checks monotonicity.
        Assert.Equal(1.0, seen[^1], 3);
    }

    //reports on the caller's thread, in the caller's order. the marshalling inside Progress<T> would sit between the assertion and its subject
    private sealed class Synchronously(Action<double> record) : IProgress<double>
    {
        public void Report(double value) => record(value);
    }

    [Fact]
    public async Task Cancellation_stops_the_hash_partway_through()
    {
        //the oracle is that the hash did not finish. an end-only token check would report progress 1.0 first, and a wall-clock bound would flake
        var path = TempFile(new byte[8 << 20]);               //the file spans 8 chunks of 1 MB.
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(_ => cts.Cancel());   //the cancel fires on the first reported chunk, so the hash stops partway

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Checksum.Sha256Async(path, ShaOfEmpty, progress, cts.Token));

        Assert.True(progress.Last > 0, "cancellation fired before any work — the test proves nothing");
        Assert.True(progress.Last < 0.5,
            $"the hash ran to {progress.Last:P0}; a per-chunk token check should stop far sooner");
    }

    //reports happen inline, async posting would let the hash finish before the cancel takes effect. the Last field records every report, so the check isn't vacuous
    private sealed class SynchronousProgress(Action<double> onFirst) : IProgress<double>
    {
        private int _fired;
        public double Last;

        public void Report(double value)
        {
            Last = value;
            if (Interlocked.Exchange(ref _fired, 1) == 0) onFirst(value);
        }
    }
}
