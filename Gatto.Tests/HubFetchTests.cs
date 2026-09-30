using System.Net;
using System.Security.Cryptography;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//a dropped connection keeps the partial file, since a resume continues what arrived. a cancel deletes it, since the user declined the model
[Collection("e2e")]   //the clocks here are real, so this class runs apart from the rest of the suite (a busy thread pool reads as a stalled transfer)
public class HubFetchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-fetch-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Payload(int n, byte seed) => [.. Enumerable.Range(0, n).Select(i => (byte)(i + seed))];

    //the sink must append synchronously, Progress<T> posts to a sync context. a report can still be in flight, so a late assert sees a stale tick
    private sealed class Sink(List<FetchTick> into) : IProgress<FetchTick>
    {
        public void Report(FetchTick t) => into.Add(t);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    //infinite timeout, the fetch contract leaves the deadline to the caller's token (a multi-gigabyte download has no fixed timeout that makes sense)
    private static HttpClient Client(Fake f) => new(f) { Timeout = Timeout.InfiniteTimeSpan };

    private static HubQuant Set(params (string Name, byte[] Bytes)[] files) =>
        new(files[0].Name, files.Sum(f => f.Bytes.LongLength), null, files.Length,
            [.. files.Select(f => new HubFile(f.Name, f.Bytes.LongLength, Sha(f.Bytes)))]);

    //every file in the set has its own hash, so one call verifies each one
    [Fact]
    public async Task A_SET_ARRIVES_FILE_BY_FILE_EACH_VERIFIED()
    {
        var a = Payload(500, 1);
        var b = Payload(700, 9);
        var fake = new Fake { Files = { ["m-00001-of-00002.gguf"] = a, ["m-00002-of-00002.gguf"] = b } };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m",
            Set(("m-00001-of-00002.gguf", a), ("m-00002-of-00002.gguf", b)), _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_dir, "m-00001-of-00002.gguf")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(_dir, "m-00002-of-00002.gguf")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    //a quant in a folder comes from its repo path and is written under its bare name (the Hub answers 404 to the bare name)
    [Fact]
    public async Task A_SET_IN_A_FOLDER_IS_FETCHED_FROM_ITS_PATH_AND_SAVED_UNDER_ITS_NAME()
    {
        var a = Payload(300, 5);
        var b = Payload(300, 6);
        var fake = new Fake { ByPath = true, Files = { ["UD-IQ1_S/m-00001-of-00002.gguf"] = a, ["UD-IQ1_S/m-00002-of-00002.gguf"] = b } };
        var quant = new HubQuant("m-00001-of-00002.gguf", 600, null, 2,
            [new HubFile("m-00001-of-00002.gguf", 300, Sha(a), "UD-IQ1_S/m-00001-of-00002.gguf"),
             new HubFile("m-00002-of-00002.gguf", 300, Sha(b), "UD-IQ1_S/m-00002-of-00002.gguf")],
            Path: "UD-IQ1_S/m-00001-of-00002.gguf");

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", quant, _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_dir, "m-00001-of-00002.gguf")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(_dir, "m-00002-of-00002.gguf")));
    }

    //a tick names the file and its place, so the widget labels the right file
    [Fact]
    public async Task AND_THE_TICKS_NAME_EACH_FILE_AND_ITS_PLACE()
    {
        var a = Payload(400, 2);
        var b = Payload(400, 3);
        var fake = new Fake { Files = { ["m-00001-of-00002.gguf"] = a, ["m-00002-of-00002.gguf"] = b } };
        var ticks = new List<FetchTick>();

        await HubFetch.FetchAsync(Client(fake), "o/m",
            Set(("m-00001-of-00002.gguf", a), ("m-00002-of-00002.gguf", b)), _dir,
            new Progress<FetchTick>(ticks.Add), default);

        //yield once to join the posted progress reports before reading them, Progress<T> posts asynchronously
        await Task.Yield();
        Assert.Contains(ticks, t => t.FileName == "m-00001-of-00002.gguf" && t is { Index: 1, Count: 2 });
        Assert.Contains(ticks, t => t.FileName == "m-00002-of-00002.gguf" && t is { Index: 2, Count: 2 });
    }

    //the bytes that arrived are good, so a drop keeps the partial for a resume to continue
    [Fact]
    public async Task A_DROP_KEEPS_THE_PARTIAL()
    {
        var a = Payload(1000, 4);
        var fake = new Fake { Files = { ["m.gguf"] = a }, DropAfter = 400 };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", a)), _dir, null, default);

        Assert.Equal(HubFetchOutcome.Dropped, r.Outcome);
        var part = Path.Combine(_dir, "m.gguf.part");
        Assert.True(File.Exists(part), "a dropped connection must leave the partial for a resume");
        Assert.Equal(400, new FileInfo(part).Length);
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf")));
    }

    //a cancel deletes the partial, the user rejected the model and the bytes a drop keeps must not remain
    [Fact]
    public async Task AND_A_CANCEL_DELETES_IT()
    {
        var a = Payload(1000, 5);
        using var cts = new CancellationTokenSource();
        var fake = new Fake { Files = { ["m.gguf"] = a }, CancelAfter = (400, cts) };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", a)), _dir, null, cts.Token);

        Assert.Equal(HubFetchOutcome.Cancelled, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf.part")),
            "a cancel must not leave the bytes a drop would have kept");
    }

    //pause is its own ending: decided like a cancel, bytes kept like a drop
    [Fact]
    public async Task A_PAUSE_KEEPS_THE_PARTIAL_AND_IS_ITS_OWN_ENDING()
    {
        var a = Payload(1000, 7);
        using var pause = new CancellationTokenSource();
        var fake = new Fake { Files = { ["m.gguf"] = a }, CancelAfter = (400, pause) };

        var r = await HubFetch.FetchAsync(
            Client(fake), "o/m", Set(("m.gguf", a)), _dir, null, default, null, pause.Token);

        Assert.Equal(HubFetchOutcome.Paused, r.Outcome);
        Assert.Equal(a[..400], File.ReadAllBytes(Path.Combine(_dir, "m.gguf.part")));
    }

    //take the partial's path from disk rather than composing it, so the assert points at the file the fetch wrote
    [Fact]
    public async Task DELETE_PARTIAL_REMOVES_WHAT_THE_FETCH_WROTE()
    {
        var a = Payload(1000, 8);
        using var pause = new CancellationTokenSource();
        var fake = new Fake { Files = { ["m.gguf"] = a }, CancelAfter = (400, pause) };
        var quant = Set(("m.gguf", a));

        var r = await HubFetch.FetchAsync(
            Client(fake), "o/m", quant, _dir, null, default, null, pause.Token);
        Assert.Equal(HubFetchOutcome.Paused, r.Outcome);

        //take the name from disk, so the delete must match what the fetch wrote
        var wrote = Assert.Single(Directory.GetFiles(_dir, "*.part"));

        HubFetch.DeletePartial(quant, _dir);

        Assert.False(File.Exists(wrote), "the partial the fetch wrote is still on disk");
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    //a resumed tick reports the bytes already on disk, so compare Resumed with the length the first leg left there
    [Fact]
    public async Task A_RESUMED_FETCH_REPORTS_WHAT_WAS_ALREADY_ON_DISK()
    {
        var a = Payload(1000, 11);
        using var pause = new CancellationTokenSource();
        var first = new List<FetchTick>();
        var fake = new Fake { Files = { ["m.gguf"] = a }, CancelAfter = (400, pause) };
        var quant = Set(("m.gguf", a));

        var paused = await HubFetch.FetchAsync(Client(fake), "o/m", quant, _dir,
            new Sink(first), default, null, pause.Token);
        Assert.Equal(HubFetchOutcome.Paused, paused.Outcome);
        Assert.All(first, t => Assert.Equal(0, t.Resumed));

        var have = new FileInfo(Path.Combine(_dir, "m.gguf.part")).Length;
        var second = new List<FetchTick>();

        var done = await HubFetch.FetchAsync(Client(new Fake { Files = { ["m.gguf"] = a } }),
            "o/m", quant, _dir, new Sink(second), default);

        Assert.Equal(HubFetchOutcome.Arrived, done.Outcome);
        Assert.NotEmpty(second);
        Assert.All(second, t => Assert.Equal(have, t.Resumed));

        //the resumed bar counts from what is on disk, so the first tick's Done is past the partial length
        Assert.True(second[0].Done > have,
            $"the first resumed tick reported {second[0].Done} with {have} already on disk: "
            + "the bar restarted from zero");
        Assert.Equal(a.Length, second[^1].Done);
    }

    //the delete gets the whole quant, so it also reaches a member that already arrived (File.Move took that file off the .part name)
    [Fact]
    public async Task DELETE_PARTIAL_SPARES_A_MEMBER_THAT_ALREADY_ARRIVED()
    {
        var done = Payload(300, 3);
        var half = Payload(1000, 4);
        var fake = new Fake
        {
            Files = { ["done.gguf"] = done, ["half.gguf"] = half },
            CancelAfter = (400, new CancellationTokenSource()),
        };
        using var pause = fake.CancelAfter!.Value.Cts;
        var quant = Set(("done.gguf", done), ("half.gguf", half));

        var r = await HubFetch.FetchAsync(
            Client(fake), "o/m", quant, _dir, null, default, null, pause.Token);
        Assert.Equal(HubFetchOutcome.Paused, r.Outcome);

        //one file arrived and the other is partial, the state the delete must tell apart.
        Assert.True(File.Exists(Path.Combine(_dir, "done.gguf")), "the first file did not arrive");
        Assert.True(File.Exists(Path.Combine(_dir, "half.gguf.part")), "the second kept nothing");

        HubFetch.DeletePartial(quant, _dir);

        Assert.True(File.Exists(Path.Combine(_dir, "done.gguf")),
            "the delete took a file that had already arrived");
        Assert.Equal(done, File.ReadAllBytes(Path.Combine(_dir, "done.gguf")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    //a stop and a pause are separate tokens, one branch serving both would pass either test alone
    [Fact]
    public async Task A_STOP_DELETES_WHAT_A_PAUSE_WOULD_HAVE_KEPT()
    {
        var a = Payload(1000, 8);
        using var stop = new CancellationTokenSource();
        var fake = new Fake { Files = { ["m.gguf"] = a }, CancelAfter = (400, stop) };

        //pass the pause token unfired, so a branch that reads its existence instead of its firing fails here
        using var idlePause = new CancellationTokenSource();
        var r = await HubFetch.FetchAsync(
            Client(fake), "o/m", Set(("m.gguf", a)), _dir, null, stop.Token, null, idlePause.Token);

        Assert.Equal(HubFetchOutcome.Cancelled, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf.part")),
            "a stop must not leave the bytes a pause would have kept");
    }

    //a pause must leave the disk in the state a resume continues, the drop case does not prove that
    [Fact]
    public async Task THE_FETCH_AFTER_A_PAUSE_ASKS_FOR_THE_REST()
    {
        var a = Payload(1000, 9);
        var quant = Set(("m.gguf", a));

        using var pause = new CancellationTokenSource();
        await HubFetch.FetchAsync(
            Client(new Fake { Files = { ["m.gguf"] = a }, CancelAfter = (400, pause) }),
            "o/m", quant, _dir, null, default, null, pause.Token);

        var resuming = new Fake { Files = { ["m.gguf"] = a } };
        var r = await HubFetch.FetchAsync(Client(resuming), "o/m", quant, _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Equal("bytes=400-", Assert.Single(resuming.Ranges));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_dir, "m.gguf")));
    }

    //compare the whole payload, a wrong splice has the right length and wrong content
    [Fact]
    public async Task A_RESUME_ASKS_FOR_THE_REST_AND_LANDS_A_WHOLE_FILE()
    {
        var a = Payload(1000, 6);
        var quant = Set(("m.gguf", a));
        var dropping = new Fake { Files = { ["m.gguf"] = a }, DropAfter = 400 };
        await HubFetch.FetchAsync(Client(dropping), "o/m", quant, _dir, null, default);

        var resuming = new Fake { Files = { ["m.gguf"] = a } };
        var r = await HubFetch.FetchAsync(Client(resuming), "o/m", quant, _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Equal("bytes=400-", Assert.Single(resuming.Ranges));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_dir, "m.gguf")));
    }

    //a server that ignores the range answers 200 from byte zero, so the fetch restarts (appending would splice the head into its middle)
    [Fact]
    public async Task A_RANGE_IGNORING_SERVER_MAKES_THE_FETCH_START_OVER()
    {
        var a = Payload(1000, 7);
        var quant = Set(("m.gguf", a));
        await HubFetch.FetchAsync(
            Client(new Fake { Files = { ["m.gguf"] = a }, DropAfter = 400 }), "o/m", quant, _dir, null, default);

        var r = await HubFetch.FetchAsync(
            Client(new Fake { Files = { ["m.gguf"] = a }, IgnoreRange = true }), "o/m", quant, _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        //check the content, an append would also come out 1000 bytes long
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_dir, "m.gguf")));
    }

    //a mismatch deletes the file and reports both shas, so the screen can show expected and got
    [Fact]
    public async Task A_BAD_BYTE_DELETES_THE_FILE_AND_NAMES_BOTH_SHAS()
    {
        var honest = Payload(600, 8);
        var corrupt = (byte[])honest.Clone();
        corrupt[100] ^= 0xFF;
        var fake = new Fake { Files = { ["m.gguf"] = corrupt } };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", honest)), _dir, null, default);

        Assert.Equal(HubFetchOutcome.Mismatch, r.Outcome);
        Assert.Equal(Sha(honest), r.Expected);
        Assert.Equal(Sha(corrupt), r.Actual);
        Assert.NotEqual(r.Expected, r.Actual);
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf")));
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf.part")),
            "a file that failed its check must not be left for a resume to continue");
    }


    //the consent screen promises every file is checked, so serve the second corrupt and the first honest
    [Fact]
    public async Task AND_THE_SECOND_FILES_FINGERPRINT_IS_CHECKED_TOO()
    {
        var first = Payload(500, 1);
        var second = Payload(700, 9);
        var corrupt = (byte[])second.Clone();
        corrupt[13] ^= 0xFF;

        var fake = new Fake
        {
            Files =
            {
                ["m-00001-of-00002.gguf"] = first,
                ["m-00002-of-00002.gguf"] = corrupt,
            },
        };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m",
            Set(("m-00001-of-00002.gguf", first), ("m-00002-of-00002.gguf", second)),
            _dir, null, default);

        Assert.Equal(HubFetchOutcome.Mismatch, r.Outcome);
        Assert.Equal("m-00002-of-00002.gguf", r.FileName);
        Assert.Equal(Sha(second), r.Expected);
        Assert.Equal(Sha(corrupt), r.Actual);

        //a verified file stays on disk, deleting it would make a resume fetch it again
        Assert.True(File.Exists(Path.Combine(_dir, "m-00001-of-00002.gguf")),
            "a verified file was deleted because a later one failed");
        Assert.False(File.Exists(Path.Combine(_dir, "m-00002-of-00002.gguf")));
    }

    //a missing published digest refuses the file rather than skipping the check.
    [Fact]
    public async Task A_FILE_WITH_NO_PUBLISHED_DIGEST_IS_REFUSED()
    {
        var a = Payload(300, 10);
        var fake = new Fake { Files = { ["m.gguf"] = a } };
        var noSha = new HubQuant("m.gguf", a.LongLength, null, 1, [new HubFile("m.gguf", a.LongLength, null)]);

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", noSha, _dir, null, default);

        Assert.Equal(HubFetchOutcome.NoDigest, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf")));
    }

    //a file already present is left alone, re-verifying every run would hash tens of gigabytes again
    [Fact]
    public async Task A_FILE_ALREADY_HERE_IS_NOT_FETCHED_AGAIN()
    {
        var a = Payload(200, 11);
        File.WriteAllBytes(Path.Combine(_dir, "m.gguf"), a);
        var fake = new Fake { Files = { ["m.gguf"] = a } };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", a)), _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Empty(fake.Ranges);          //the empty range list proves no request left the client.
    }


    //no exception arrives for a wedge, so silence ends as a drop and the partial is kept
    [Fact]
    public async Task A_SILENT_TRANSFER_IS_DROPPED_AND_THE_PARTIAL_IS_KEPT()
    {
        var body = Payload(4096, 3);
        var fake = new Fake { Files = { ["m.gguf"] = body }, SilentAfter = 256 };

        //the test bounds the call, a broken deadline would hang and a hung test says nothing
        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", body)), _dir, null,
            default, idleDeadline: TimeSpan.FromMilliseconds(150))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HubFetchOutcome.Dropped, r.Outcome);
        var part = Path.Combine(_dir, "m.gguf.part");
        Assert.True(File.Exists(part), "the partial must be KEPT — a wedge is a drop, not a cancel");
        Assert.Equal(256, new FileInfo(part).Length);
    }


    //the arm before the loop only runs for a transfer silent from the first byte, the re-arm covers one that has sent bytes
    [Fact]
    public async Task A_TRANSFER_SILENT_FROM_THE_FIRST_BYTE_IS_STILL_DROPPED()
    {
        var body = Payload(4096, 11);
        var fake = new Fake { Files = { ["m.gguf"] = body }, SilentAfter = 0 };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", body)), _dir, null,
            default, idleDeadline: TimeSpan.FromMilliseconds(150))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HubFetchOutcome.Dropped, r.Outcome);
    }

    //the deadline measures silence, so a live link with gaps must complete (assert the elapsed beats it, or a fast host proves nothing)
    [Fact]
    public async Task A_SLOW_BUT_LIVE_TRANSFER_IS_NOT_KILLED()
    {
        var body = Payload(8192, 7);
        var fake = new Fake { Files = { ["m.gguf"] = body }, GapMs = 30 };
        var deadline = TimeSpan.FromMilliseconds(900);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", body)), _dir, null,
            default, idleDeadline: deadline);

        clock.Stop();
        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Equal(body, File.ReadAllBytes(Path.Combine(_dir, "m.gguf")));
        Assert.True(clock.Elapsed > deadline,
            $"the transfer took {clock.ElapsedMilliseconds} ms against a {deadline.TotalMilliseconds} ms "
            + "deadline, so it finished INSIDE the deadline and proves nothing about silence versus "
            + "duration - widen the body or the gap");
    }

    //a stop and a wedge throw the same exception, so the token that fired decides, else the idle arm keeps partials the user rejected
    [Fact]
    public async Task A_USER_STOP_IS_STILL_A_CANCEL_WITH_THE_DEADLINE_IN_PLACE()
    {
        var body = Payload(4096, 5);
        var cts = new CancellationTokenSource();
        var fake = new Fake { Files = { ["m.gguf"] = body }, CancelAfter = (256, cts) };

        var r = await HubFetch.FetchAsync(Client(fake), "o/m", Set(("m.gguf", body)), _dir, null,
            cts.Token, idleDeadline: TimeSpan.FromSeconds(30));

        Assert.Equal(HubFetchOutcome.Cancelled, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, "m.gguf.part")), "a cancel deletes the partial");
    }

    private sealed class Fake : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Ranges { get; } = [];
        public int DropAfter { get; init; } = -1;
        public (int After, CancellationTokenSource Cts)? CancelAfter { get; init; }
        public bool IgnoreRange { get; init; }

        //after this many bytes the body stops sending, the transport never reports that ending
        public int SilentAfter { get; init; } = -1;

        //a pause before every chunk simulates a slow but live link, which the deadline must not kill.
        public int GapMs { get; init; }

        //key the fake's files by their path in the repo, so a fetch that asks for the bare name gets 404
        public bool ByPath { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.AbsolutePath;
            var name = ByPath
                ? Uri.UnescapeDataString(url[(url.IndexOf("/resolve/main/", StringComparison.Ordinal) + "/resolve/main/".Length)..])
                : Uri.UnescapeDataString(req.RequestUri!.Segments[^1]);
            if (!Files.TryGetValue(name, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var from = 0L;
            if (req.Headers.Range is { } range)
            {
                Ranges.Add(range.ToString());
                if (!IgnoreRange && range.Ranges.FirstOrDefault()?.From is { } f) from = f;
            }

            var slice = body[(int)from..];
            var code = from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StreamContent(
                    new Trickle(slice, DropAfter, CancelAfter, SilentAfter, GapMs)),
            });
        }
    }

    //a body that stops early by failing or by cancelling the caller's token, so one stream models both endings
    private sealed class Trickle(byte[] body, int dropAfter, (int After, CancellationTokenSource Cts)? cancelAfter,
        int silentAfter = -1, int gapMs = 0)
        : Stream
    {
        private int _pos;

        //an awaited delay honours the token, a blocking wait on a pool thread cannot be cancelled
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (gapMs > 0) await Task.Delay(gapMs, ct).ConfigureAwait(false);
            if (silentAfter >= 0 && _pos >= silentAfter)
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 0;                                  //unreachable, the delay only ends when the token fires and then it throws
            }

            var take = buffer.Length;
            if (silentAfter >= 0) take = Math.Min(take, silentAfter - _pos);
            var scratch = new byte[Math.Max(1, take)];
            var n = Read(scratch, 0, take);
            scratch.AsMemory(0, n).CopyTo(buffer);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (cancelAfter is { } c && _pos >= c.After) { c.Cts.Cancel(); c.Cts.Token.ThrowIfCancellationRequested(); }
            if (dropAfter >= 0 && _pos >= dropAfter) throw new IOException("the connection dropped");
            if (_pos >= body.Length) return 0;

            var n = Math.Min(count, Math.Min(body.Length - _pos, 128));
            //clip the last chunk to the stop point, or the stream overshoots and the drop-point asserts become approximate
            if (dropAfter >= 0) n = Math.Min(n, dropAfter - _pos);
            if (cancelAfter is { } cap) n = Math.Min(n, cap.After - _pos);
            Array.Copy(body, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => body.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    //write the record before the first byte, a partial file alone has no repo id, members or total to resume from
    [Fact]
    public async Task A_PAUSED_FETCH_LEAVES_A_RECORD_THAT_NAMES_THE_REPO_AND_THE_TOTAL()
    {
        var a = Payload(500, 1);
        var fake = new Fake { Files = { ["a.gguf"] = a } };
        using var pause = new CancellationTokenSource();
        await pause.CancelAsync();   //cancel before the call, so no byte moves and the record still has to be written

        await HubFetch.FetchAsync(Client(fake), "unsloth/Qwen3.5-4B-GGUF",
            new HubQuant("a.gguf", a.LongLength, Sha(a)), _dir, null, default, pause: pause.Token);

        var record = HubFetch.ReadRecord(_dir);
        Assert.NotNull(record);
        Assert.Equal("unsloth/Qwen3.5-4B-GGUF", record!.RepoId);
        Assert.Equal(a.LongLength, record.Total);
        Assert.Equal("a.gguf", Assert.Single(record.Members).FileName);
    }

    //a stale resume row would fail when pressed, which is worse than no row
    [Fact]
    public async Task AND_THE_RECORD_IS_GONE_ONCE_THE_FETCH_ARRIVES()
    {
        var a = Payload(500, 1);
        var fake = new Fake { Files = { ["a.gguf"] = a } };

        var r = await HubFetch.FetchAsync(Client(fake), "unsloth/Qwen3.5-4B-GGUF",
            new HubQuant("a.gguf", a.LongLength, Sha(a)), _dir, null, default);

        Assert.Equal(HubFetchOutcome.Arrived, r.Outcome);
        Assert.Null(HubFetch.ReadRecord(_dir));
    }

    //a stop deletes the record with the partials, or it would advertise a resume whose bytes are gone
    [Fact]
    public async Task AND_THE_STOP_TAKES_THE_RECORD_WITH_THE_PARTIALS()
    {
        var a = Payload(500, 1);
        var quant = new HubQuant("a.gguf", a.LongLength, Sha(a));
        var fake = new Fake { Files = { ["a.gguf"] = a } };
        using var pause = new CancellationTokenSource();
        await pause.CancelAsync();
        await HubFetch.FetchAsync(Client(fake), "unsloth/Qwen3.5-4B-GGUF", quant, _dir,
            null, default, pause: pause.Token);
        Assert.NotNull(HubFetch.ReadRecord(_dir));   //the record must exist before the delete removes it, or the assert proves nothing

        HubFetch.DeletePartial(quant, _dir);

        Assert.Null(HubFetch.ReadRecord(_dir));
    }
}
