using System.Net;
using System.Net.Http.Headers;

namespace Gatto.Core.Acquire;

//the four ways a fetch can end. a drop keeps the partial for a resume, a cancel deletes it since the user decided against the model
internal enum HubFetchOutcome
{
    //every file arrived and matched its published fingerprint
    Arrived,

    //the connection failed, the partial file stays and a resume continues it
    Dropped,

    //the file did not match its published sha, so it is deleted instead of offered as a choice
    Mismatch,

    //the user stopped it, so the partial file goes
    Cancelled,

    //keep this off the stop's token: a pause keeps the partial like a drop, and only the user's decision may be offered as a resume
    Paused,

    //the tree published no fingerprint for a file, so the fetch stops: an absent digest is not permission to skip the check
    NoDigest,
}

//fetch and check one member at a time, since a set has no hash of its own, and set an infinite Timeout on the client
internal static class HubFetch
{
    private const int BufferBytes = 1 << 20;

    //two minutes of complete silence ends the transfer, and every byte re-arms it. the Timeout is infinite and only a user stop cancels the caller's CTS
    public static readonly TimeSpan IdleDeadline = TimeSpan.FromMinutes(2);


    //every member needs a published hash, since the check is per file. the screen asks this before offering a fetch, so the offer and the refusal agree
    public static bool CanVerify(HubQuant quant) =>
        quant.Members.Count > 0 && quant.Members.All(m => m.Sha256 is { Length: > 0 });

    //fetch each file in order and stop at the first non-Arrived ending. pause must never be the stop's token: a stop deletes the partial and a pause keeps it
    public static async Task<HubFetchResult> FetchAsync(
        HttpClient http, string repoId, HubQuant quant, string destDir,
        IProgress<FetchTick>? progress, CancellationToken ct, TimeSpan? idleDeadline = null,
        CancellationToken pause = default)
    {
        Directory.CreateDirectory(destDir);
        //write the record before the first byte, since the repo id, the members and the total live nowhere on disk. without it a partial has no name or URL
        WriteRecord(destDir, new FetchRecord(repoId, quant.Members,
            quant.Members.Sum(m => m.Bytes)));

        var members = quant.Members;
        for (var i = 0; i < members.Count; i++)
        {
            var one = await OneAsync(http, repoId, members[i], destDir, i + 1, members.Count,
                    progress, ct, idleDeadline, pause)
                .ConfigureAwait(false);
            //stop at the first failure but keep the files already verified, which a resume would otherwise fetch again
            if (one.Outcome != HubFetchOutcome.Arrived) return one;
        }

        //delete the record on arrival, since one beside a finished model offers a resume with nothing left to fetch
        Delete(RecordPath(destDir));
        return new HubFetchResult(HubFetchOutcome.Arrived);
    }

    private static async Task<HubFetchResult> OneAsync(
        HttpClient http, string repoId, HubFile file, string destDir, int index, int count,
        IProgress<FetchTick>? progress, CancellationToken ct, TimeSpan? idleDeadline,
        CancellationToken pause)
    {
        //a file with no published digest stops the fetch instead of being fetched unchecked
        if (file.Sha256 is not { Length: > 0 } expected)
            return new HubFetchResult(HubFetchOutcome.NoDigest, file.FileName);

        var final = Path.Combine(destDir, file.FileName);
        //leave a file that is already here alone rather than re-hashing it, since the watch is where a file placed outside gatto gets checked
        if (File.Exists(final)) return new HubFetchResult(HubFetchOutcome.Arrived, file.FileName);

        var part = PartPath(destDir, file.FileName);
        var url = new Uri(HubUrl.Resolve(repoId, file.RepoPath));

        //the read and the write take the linked stop-or-pause token. pick the arm by asking whose token fired, since every throw looks the same
        using var stopOrPause = CancellationTokenSource.CreateLinkedTokenSource(ct, pause);
        var work = stopOrPause.Token;

        try
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

            using var res = await http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, work).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"fetching {file.FileName} answered {(int)res.StatusCode} {res.StatusCode}");

            //a 200 to a ranged request means the server ignored the range. truncate and start over instead of splicing the opening bytes into the middle
            var restart = have > 0 && res.StatusCode == HttpStatusCode.OK;
            if (restart) have = 0;

            var total = have + (res.Content.Headers.ContentLength ?? Math.Max(0, file.Bytes - have));
            var started = System.Diagnostics.Stopwatch.StartNew();

            await using (var dst = new FileStream(
                part, restart || have == 0 ? FileMode.Create : FileMode.Append,
                FileAccess.Write, FileShare.None, BufferBytes, useAsync: true))
            await using (var src = await res.Content.ReadAsStreamAsync(work).ConfigureAwait(false))
            {
                var buffer = new byte[BufferBytes];
                var done = have;
                //armed before the first read and re-armed on every byte, so the deadline measures silence and not the length of the download
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(work);
                var silence = idleDeadline ?? IdleDeadline;
                idle.CancelAfter(silence);

                while (true)
                {
                    work.ThrowIfCancellationRequested();
                    var n = await src.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    idle.CancelAfter(silence);          //bytes arrived, so re-arm the deadline

                    await dst.WriteAsync(buffer.AsMemory(0, n), work).ConfigureAwait(false);
                    done += n;
                    //report done once, so the bar, the rate and the remainder cannot disagree. have goes with it because done is cumulative while the stopwatch is per-sitting
                    progress?.Report(new FetchTick(
                        file.FileName, index, count, done, total, started.ElapsedMilliseconds,
                        Resumed: have));
                }
            }

            var check = await Checksum.Sha256Async(part, expected, null, work).ConfigureAwait(false);
            if (!check.Match)
            {
                //delete a file that failed its check, since keeping it would let a resume continue a corrupt file forever
                Delete(part);
                return new HubFetchResult(HubFetchOutcome.Mismatch, file.FileName, expected, check.ActualSha256);
            }

            File.Move(part, final, overwrite: true);
            return new HubFetchResult(HubFetchOutcome.Arrived, file.FileName);
        }
        //order matters: ask the pause token first, since a paused fetch satisfies the drop arm's guard too, and pick the arm by whose token fired
        catch (OperationCanceledException) when (pause.IsCancellationRequested)
        {
            return new HubFetchResult(HubFetchOutcome.Paused, file.FileName);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HubFetchResult(HubFetchOutcome.Dropped, file.FileName);
        }
        catch (OperationCanceledException)
        {
            //the user's own stop: delete the partial, which is what separates this from a drop
            Delete(part);
            return new HubFetchResult(HubFetchOutcome.Cancelled, file.FileName);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            //the network failed: keep the partial, since what arrived is good and a resume continues it
            return new HubFetchResult(HubFetchOutcome.Dropped, file.FileName);
        }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (Exception) { } //a partial we cannot remove is not a failure the user can act on
    }

    //the one home for the partial's name: a second spelling of ".part" lets the writer and the remover disagree about which file they mean
    internal static string PartPath(string destDir, string fileName) =>
        Path.Combine(destDir, fileName) + ".part";

    //the quant's total, since the row it feeds says N of M and M is what the user agreed to download
    public sealed record FetchRecord(string RepoId, IReadOnlyList<HubFile> Members, long Total);

    //the one home for the record's name, for the same reason as PartPath
    internal static string RecordPath(string destDir) =>
        Path.Combine(destDir, ".fetch.json");

    private static void WriteRecord(string destDir, FetchRecord record)
    {
        //a record gatto cannot write costs only the resume offer, so the fetch still runs and still arrives
        try
        {
            File.WriteAllText(RecordPath(destDir),
                System.Text.Json.JsonSerializer.Serialize(record));
        }
        catch (Exception) { } //swallow the write failure: the fetch is the deed and the record only helps the next run
    }

    //the record beside a paused fetch, or null when there is none or it cannot be read, since a half-written one would fail when pressed
    public static FetchRecord? ReadRecord(string destDir)
    {
        try
        {
            var path = RecordPath(destDir);
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<FetchRecord>(File.ReadAllText(path))
                : null;
        }
        catch (Exception) { return null; }
    }

    //delete every member's partial, since a shard set pauses with one partial and finished siblings the caller cannot tell apart
    public static void DeletePartial(HubQuant quant, string destDir)
    {
        foreach (var m in quant.Members) Delete(PartPath(destDir, m.FileName));
        //and the record with them: a record outliving the bytes advertises a resume it cannot serve
        Delete(RecordPath(destDir));
    }
}

//how a fetch ended, with Expected and Actual set only on Mismatch, since the mismatch screen shows both shas
internal sealed record HubFetchResult(
    HubFetchOutcome Outcome, string? FileName = null, string? Expected = null, string? Actual = null);
