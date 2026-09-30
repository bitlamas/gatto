using System.Security.Cryptography;

namespace Gatto.Core.Acquire;

//the outcome of verifying a downloaded file. the actual hash is always reported, since a mismatch is only actionable when the user sees it
internal sealed record ChecksumResult(bool Match, string ActualSha256);

//verify a download against the published hash. same origin, so corruption is covered and authenticity is not, and a shard set has no published hash
internal static class Checksum
{
    //1 MB reads, so a 40 GB model is not held in memory, and one chunk is the cancellation granularity
    private const int BufferBytes = 1 << 20;

    //hash the file and compare with the expected hex, case-insensitively. progress reports 0 to 1, and cancellation is honoured per chunk
    public static async Task<ChecksumResult> Sha256Async(
        string path, string expectedSha256, IProgress<double>? progress, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, useAsync: true);

        var total = fs.Length;
        var buffer = new byte[BufferBytes];
        long done = 0;

        while (true)
        {
            //checked before the read, so a cancel costs at most the chunk already in flight
            ct.ThrowIfCancellationRequested();
            var n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;

            hasher.AppendData(buffer, 0, n);
            done += n;
            progress?.Report(total > 0 ? (double)done / total : 1.0);
        }

        var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return new ChecksumResult(
            string.Equals(actual, expectedSha256?.Trim(), StringComparison.OrdinalIgnoreCase), actual);
    }
}
