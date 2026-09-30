using System.IO.Compression;

namespace Gatto.Cli;

//the client is untimed and the deadline is Stall, no bytes for that long kills it. downloads go in the home's update folder, swept on every launch
internal static class UpdateDownload
{
    public const string Folder = "update";

    //no bytes for this long and the transfer is dead. the number stays in code and never reaches a surface
    public static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    private const int ChunkBytes = 1 << 20;

    public static string Dir(string home) => Path.Combine(home, Folder);

    //clear everything under the update folder, recursively, on every launch, and let a file that is still held wait for the next one
    public static void Sweep(string home)
    {
        try
        {
            var dir = Dir(home);
            if (!Directory.Exists(dir)) return;

            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                try { File.Delete(f); } catch (Exception) { } //still held, the next launch gets it

            foreach (var d in Directory.GetDirectories(dir))
                try { Directory.Delete(d, recursive: true); } catch (Exception) { } //likewise, a held folder is removed on the next launch
        }
        catch (Exception) { } //an unreadable folder is not worth a word
    }

    //untimed on purpose, though the connect budget still applies since DNS and TCP failing is fast-failing
    public static HttpClient Client(TimeSpan connect) =>
        new(new SocketsHttpHandler { ConnectTimeout = connect }) { Timeout = Timeout.InfiniteTimeSpan };

    //stream the asset into the given folder, deleting the partial before any throw. the folder is a parameter so the engine step fetches llama.cpp by the same rules
    public static async Task<string> FetchAsync(HttpClient http, ReleaseAsset asset, string intoDir,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct, TimeSpan? stall = null)
    {
        var budget = stall ?? Stall;
        Directory.CreateDirectory(intoDir);

        var final = Path.Combine(intoDir, asset.Name);
        var part = final + ".part";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", "gatto/" + Gatto.Core.GattoVersion.String);

            using var res = await http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            var total = res.Content.Headers.ContentLength ?? asset.Size;

            //an inner scope so the write handle is closed before the rename below, or the rename fails for an unrelated reason
            {
                await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(
                    part, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true);

                var buffer = new byte[ChunkBytes];
                long done = 0;

                while (true)
                {
                    //the deadline is re-armed for every chunk, so a dribbling link keeps resetting it and a dead one doesn't
                    using var stallCts = new CancellationTokenSource(budget);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stallCts.Token);

                    var n = await src.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (n == 0) break;

                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    progress?.Report((done, total));
                }

                await dst.FlushAsync(ct).ConfigureAwait(false);
            }

            //inside the try, since a stale file held elsewhere makes this throw and the cleanup promise must hold on this path too
            if (File.Exists(final)) File.Delete(final);
            File.Move(part, final);
        }
        catch (Exception)
        {
            //every failing path leaves nothing behind, and the write handle is already released by the time this runs
            try { File.Delete(part); } catch (Exception) { } //the sweep gets it next launch
            throw;
        }

        return final;
    }

    //null when the bytes match, else the sentence to show, and an absent digest refuses too. page points at the release the caller is fetching from
    public static async Task<string?> VerifyAsync(string zipPath, string? expectedSha256,
        CancellationToken ct, string? page = null)
    {
        if (expectedSha256 is not { Length: > 0 })
            return "this release doesn't publish a digest, so gatto can't verify the download "
                 + "automatically. Download it yourself from " + (page ?? UpdateCheck.ReleasesPage)
                 + " instead.";

        var result = await Gatto.Core.Acquire.Checksum
            .Sha256Async(zipPath, expectedSha256, null, ct).ConfigureAwait(false);

        return result.Match
            ? null
            : $"the download didn't match the release's digest (got {result.ActualSha256}). Nothing was changed.";
    }

    //an exact-name lookup for gatto.exe, so a hostile traversal entry in the archive is never read
    public static string? ExtractExe(string zipPath, string intoDir)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            if (archive.GetEntry("gatto.exe") is not { } entry)
                return "the download doesn't contain gatto.exe. Nothing was changed.";

            Directory.CreateDirectory(intoDir);
            entry.ExtractToFile(Path.Combine(intoDir, "gatto.exe"), overwrite: true);
            return null;
        }
        catch (Exception ex) { return $"couldn't read the download: {ex.Message}"; }
    }
}
