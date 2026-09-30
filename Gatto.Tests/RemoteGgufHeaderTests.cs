using System.Net;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the fetch-extend loop that stops when satisfied, driven through a delegate fetcher and a stub handler with no network
public class RemoteGgufHeaderTests
{
    private static RangeFetch FetcherOver(byte[] file, List<(long off, int count)> log) =>
        (off, count, ct) =>
        {
            log.Add((off, count));
            var take = (int)Math.Max(0, Math.Min(count, file.Length - off));
            return Task.FromResult(file.AsSpan((int)off, take).ToArray());
        };

    [Fact]
    public async Task Satisfied_in_the_first_window_costs_exactly_one_fetch()
    {
        var file = GgufTestBytes.Rich();                       //the fixture keeps every fit term inside a few kb.
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(file, log), CancellationToken.None);
        Assert.True(h.HasAllFitTerms);
        Assert.Single(log);                                    //one fetch pins the stop-when-satisfied rule.
        Assert.Equal((0L, 1 << 20), log[0]);
    }

    [Fact]
    public async Task Terms_past_the_window_extend_by_deltas_up_to_the_cap()
    {
        //the fixture hides the keys past 5 mb of padding, so the first two windows come back truncated and the third finds the terms
        var file = GgufTestBytes.RichWithLeadingPadding(paddingBytes: 5 << 20);
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(file, log), CancellationToken.None);
        Assert.True(h.HasAllFitTerms);
        Assert.Equal(3, log.Count);
        Assert.Equal((0L, 1 << 20), log[0]);
        Assert.Equal(((long)(1 << 20), 3 << 20), log[1]);      //the second fetch is the delta up to 4 mb.
        Assert.Equal(((long)(4 << 20), 12 << 20), log[2]);     //the third fetch is the delta up to the 16 mb cap.
    }

    [Fact]
    public async Task Past_the_cap_the_missing_terms_stay_missing_and_the_loop_stops()
    {
        var file = GgufTestBytes.RichWithLeadingPadding(paddingBytes: 20 << 20);   //the padding pushes the terms past the 16 mb cap.
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(file, log), CancellationToken.None);
        Assert.False(h.HasAllFitTerms);                        //the terms past the cap stay unknown, and FitArithmetic.Judge prices them with its widened margin
        Assert.Equal(3, log.Count);                            //three is the ceiling, so a fourth fetch never happens.
    }

    [Fact]
    public async Task A_file_smaller_than_the_window_stops_at_its_real_end()
    {
        //the fixture is built to be at least 4 kb, so its slice is smaller than the window
        var file = GgufTestBytes.Rich()[..2000];               //the slice stands in for a remote file cut short.
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(file, log), CancellationToken.None);
        Assert.Single(log);                                    //a short return signals end of file, so the loop must not grow the window.
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
    }

    [Fact]
    public async Task Malformed_bytes_stop_immediately_more_bytes_cannot_unlie_them()
    {
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(new byte[64], log), CancellationToken.None);
        Assert.Equal(GgufOutcome.Malformed, h.Outcome);
        Assert.Single(log);
    }

    [Fact]
    public async Task Cancellation_between_iterations_stops_before_the_next_fetch()
    {
        //the fetcher cancels after it returns its bytes, so the check at the top of the loop must fire before fetch two
        var file = GgufTestBytes.RichWithLeadingPadding(paddingBytes: 5 << 20);   //the padding makes the file want three fetches, so a cancel arrives between iterations.
        using var cts = new CancellationTokenSource();
        var log = new List<(long, int)>();
        var inner = FetcherOver(file, log);
        RangeFetch cancelling = async (o, c, ct) => { var b = await inner(o, c, ct); cts.Cancel(); return b; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RemoteGgufHeader.ReadAsync(cancelling, cts.Token));
        Assert.Single(log);
    }

    [Fact]
    public async Task Satisfied_but_truncated_still_stops_at_one_fetch()
    {
        //the stop comes from finding the terms, with trailing padding keeping eof false so nothing else can end the loop
        var file = GgufTestBytes.RichWithTrailingPadding(paddingBytes: 5 << 20);
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(file, log), CancellationToken.None);
        Assert.True(h.HasAllFitTerms);
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Single(log);
    }

    [Fact]
    public async Task A_small_satisfied_file_also_stops_at_one_fetch()
    {
        //this small file stops on eof rather than on the found terms, so it stays a case of its own
        var file = GgufTestBytes.RichWithOverstatedKvCount();
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(FetcherOver(file, log), CancellationToken.None);
        Assert.True(h.HasAllFitTerms);
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
        Assert.Single(log);
    }
}

public class HttpRangeFetchTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { LastRequest = r; return Task.FromResult(respond(r)); }
    }

    private static HttpClient Client(StubHandler h) =>
        new(h) { Timeout = Timeout.InfiniteTimeSpan };          //every test uses this client shape with no timeout.

    [Fact]
    public async Task Sends_the_range_header_and_returns_206_bodies()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(body) });
        var fetch = HttpRangeFetch.Create(Client(handler), new Uri("http://x/m.gguf"));

        var got = await fetch(1024, 5, CancellationToken.None);

        Assert.Equal(body, got);
        Assert.Equal("bytes=1024-1028", handler.LastRequest!.Headers.Range!.ToString());
    }

    //a 200 answer means Range was ignored, and the stream never ends and returns at most 100 bytes per read like a socket
    private sealed class ThrottledEndlessStream : Stream
    {
        public long BytesRead;
        public override int Read(byte[] buffer, int offset, int count)
        { var n = Math.Min(count, 100); BytesRead += n; return n; }   //this stream never reaches eof and returns zeros.
        public override bool CanRead => true;
        public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_server_that_ignores_range_is_read_at_most_count_bytes()
    {
        var stream = new ThrottledEndlessStream();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) });
        var fetch = HttpRangeFetch.Create(Client(handler), new Uri("http://x/m.gguf"));

        var got = await fetch(0, 4096, CancellationToken.None);

        Assert.Equal(4096, got.Length);   //the result comes from about 41 partial reads, so a single-shot read fails here.
        //the read never goes meaningfully past count, and the 200-byte slack covers one final 100-byte read
        Assert.InRange(stream.BytesRead, 4096, 4096 + 200);
    }

    [Fact]
    public async Task A_range_ignoring_server_is_an_error_on_an_EXTENSION_round()
    {
        //a 200 body starts at byte zero, so only a zero offset matches the ask and any other offset would corrupt the buffer
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[4096]) });
        var fetch = HttpRangeFetch.Create(Client(handler), new Uri("http://x/m.gguf"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => fetch(1 << 20, 4096, CancellationToken.None));
        Assert.Contains("ignored by the server", ex.Message);
    }

    [Fact]
    public async Task Status_416_returns_empty()                 //status 416 means the offset was past the end of the file.
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        var fetch = HttpRangeFetch.Create(Client(handler), new Uri("http://x/m.gguf"));
        Assert.Empty(await fetch(999_999_999, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task Other_statuses_throw_HttpRequestException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var fetch = HttpRangeFetch.Create(Client(handler), new Uri("http://x/m.gguf"));
        await Assert.ThrowsAsync<HttpRequestException>(() => fetch(0, 1024, CancellationToken.None));
    }
}
