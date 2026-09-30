using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the structure read of a gguf's first 16 kb, one retry to 256 kb, and the composer that drives it
public class RangedStructureReadTests
{
    private static RangeFetch FetcherOver(byte[] file, List<(long off, int count)> log) =>
        (off, count, ct) =>
        {
            log.Add((off, count));
            var take = (int)Math.Max(0, Math.Min(count, file.Length - off));
            return Task.FromResult(file.AsSpan((int)off, take).ToArray());
        };

    private static Task<GgufHeader> Read(byte[] file, List<(long, int)> log) =>
        RemoteGgufHeader.ReadAsync(
            FetcherOver(file, log), HeaderWindow.Structure, ModelStructure.Answered,
            CancellationToken.None);

    //the common case the 16 kb window was measured for, structure keys in the metadata block settle the column in one request
    [Fact]
    public async Task KEYS_IN_THE_PREFIX_COST_EXACTLY_ONE_REQUEST()
    {
        var log = new List<(long, int)>();
        var h = await Read(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B"), log);

        Assert.Equal("MoE A4B", ModelStructure.Cell(h));
        Assert.Single(log);
        Assert.Equal((0L, 16 << 10), log[0]);
    }

    //the loop must stop on the structure predicate rather than the fit terms, so this fixture puts expert keys early and omits the fit terms
    [Fact]
    public async Task THE_STRUCTURE_READ_STOPS_ON_THE_STRUCTURE_NOT_ON_THE_FIT_TERMS()
    {
        var log = new List<(long, int)>();
        var file = GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B",
            fitTerms: false, trailingPaddingBytes: 512 << 10);
        var h = await Read(file, log);

        Assert.True(ModelStructure.Answered(h));
        Assert.False(h.HasAllFitTerms);

        Assert.Equal("MoE A4B", ModelStructure.Cell(h));
        Assert.Single(log);                       //one request proves the loop stopped on the structure.
    }

    //the second fetch is a delta at offset 16 kb, so assert the offset (a refetching loop would pass a bare count of two)
    [Fact]
    public async Task KEYS_PAST_THE_PREFIX_TRIGGER_ONE_RETRY_TO_256KB()
    {
        var log = new List<(long, int)>();
        var h = await Read(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B", paddingBytes: 64 << 10), log);

        Assert.Equal("MoE A4B", ModelStructure.Cell(h));
        Assert.Equal(2, log.Count);
        Assert.Equal((0L, 16 << 10), log[0]);
        Assert.Equal(((long)(16 << 10), (256 << 10) - (16 << 10)), log[1]);
    }

    //a dense verdict from absence alone is a plausible lie, so the read must fetch the second window anyway
    [Fact]
    public async Task A_DENSE_VERDICT_FROM_ABSENCE_STILL_PAYS_FOR_THE_SECOND_WINDOW()
    {
        var log = new List<(long, int)>();
        var file = GgufTestBytes.WithStructure(sizeLabel: "27B", trailingPaddingBytes: 512 << 10);
        var h = await Read(file, log);

        Assert.Equal("dense", ModelStructure.Cell(h));
        Assert.Equal(2, log.Count);       //two requests prove the loop did not stop on the inference.

        //assert the first window parses dense but is not answered, otherwise a predicate that never stops passes this test
        var firstWindow = GgufHeaderParser.Parse(new MemoryStream(file, 0, 16 << 10));
        Assert.Equal("dense", ModelStructure.Cell(firstWindow));
        Assert.False(ModelStructure.Answered(firstWindow));
    }

    //a file that states its expert count must answer in one window, otherwise the guard above passes against a predicate that never stops
    [Theory]
    [InlineData(0, "dense")]
    [InlineData(128, "MoE")]
    public async Task A_STATED_EXPERT_COUNT_ANSWERS_IN_ONE_WINDOW(int experts, string expected)
    {
        var log = new List<(long, int)>();
        var h = await Read(GgufTestBytes.WithStructure(
            expertCount: experts, trailingPaddingBytes: 512 << 10), log);

        Assert.Equal(expected, ModelStructure.Cell(h));
        Assert.Single(log);
    }

    //the read gives up after two requests (it runs per row while the user waits) and the model stays with an empty column
    [Fact]
    public async Task PAST_THE_CAP_THE_COLUMN_IS_EMPTY_AND_THE_READ_STOPS()
    {
        var log = new List<(long, int)>();
        var h = await Read(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B", paddingBytes: 512 << 10), log);

        Assert.Null(ModelStructure.Cell(h));
        Assert.Equal(2, log.Count);
        Assert.Equal(GgufOutcome.Truncated, h.Outcome);
    }

    //one loop serves both ladders, and the fit read's first window must stay a megabyte
    [Fact]
    public async Task THE_FIT_READ_STILL_WALKS_ITS_OWN_LADDER()
    {
        var log = new List<(long, int)>();
        var h = await RemoteGgufHeader.ReadAsync(
            FetcherOver(GgufTestBytes.Rich(), log), CancellationToken.None);

        Assert.True(h.HasAllFitTerms);
        Assert.Equal((0L, 1 << 20), log[0]);
    }

    //a stub that honours Range over a byte array and records what was asked for
    private sealed class RangeHandler(byte[] file) : HttpMessageHandler
    {
        public readonly List<string> Ranges = [];
        //recording the urls as well is the only way to pin the url the client builds
        public readonly List<string> Urls = [];
        public HttpStatusCode Status = HttpStatusCode.PartialContent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Ranges.Add(r.Headers.Range?.ToString() ?? "(none)");
            Urls.Add(r.RequestUri!.ToString());
            if (Status != HttpStatusCode.PartialContent)
                return Task.FromResult(new HttpResponseMessage(Status));

            var from = (int)(r.Headers.Range!.Ranges.First().From ?? 0);
            var to = (int)(r.Headers.Range!.Ranges.First().To ?? file.Length - 1);
            var take = Math.Max(0, Math.Min(to - from + 1, file.Length - from));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(file.AsSpan(from, take).ToArray()) });
        }
    }

    private static HubClient ClientOver(RangeHandler h) =>
        new(new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan });

    [Fact]
    public async Task THE_COMPOSER_READS_A_STRUCTURE_OVER_HTTP()
    {
        var handler = new RangeHandler(GgufTestBytes.WithStructure(
            expertCount: 512, expertUsed: 10, sizeLabel: "512x2.5B"));

        var h = await ClientOver(handler).StructureHeaderAsync(
            "qwen/Qwen3-Coder-Next-GGUF", "model.gguf", CancellationToken.None);

        Assert.NotNull(h);
        Assert.Equal("MoE", ModelStructure.Cell(h));
        Assert.Equal((512L, 10L), ModelStructure.Experts(h));
        Assert.Equal("bytes=0-16383", Assert.Single(handler.Ranges));
        //the request must go to the resolve endpoint, since ?download=true asks for a human download rather than a header read
        Assert.Equal(
            "https://huggingface.co/qwen/Qwen3-Coder-Next-GGUF/resolve/main/model.gguf",
            Assert.Single(handler.Urls));
    }

    //a failed header read leaves the model with an empty structure cell, like a file that states no structure
    [Fact]
    public async Task A_HUB_FAILURE_YIELDS_NO_STRUCTURE_RATHER_THAN_AN_EXCEPTION()
    {
        var handler = new RangeHandler(GgufTestBytes.WithStructure(expertCount: 128))
        { Status = HttpStatusCode.InternalServerError };

        var h = await ClientOver(handler).StructureHeaderAsync(
            "qwen/Q-GGUF", "model.gguf", CancellationToken.None);

        Assert.Null(h);
        Assert.Single(handler.Ranges);       //one recorded range proves the call was really attempted.
    }

    //a malformed repo id must never reach the wire, and typed user input can arrive here so the usual validation applies
    [Theory]
    [InlineData("not-a-repo-id")]
    [InlineData("https://huggingface.co/qwen/Q-GGUF")]
    [InlineData("../../etc/passwd")]
    public async Task A_MALFORMED_ID_MAKES_NO_REQUEST(string repoId)
    {
        var handler = new RangeHandler(GgufTestBytes.WithStructure(expertCount: 128));

        Assert.Null(await ClientOver(handler).StructureHeaderAsync(
            repoId, "model.gguf", CancellationToken.None));
        Assert.Empty(handler.Ranges);
    }

    //a server that ignores Range must still have its body capped by the 16 kb window, and a hopeless header is not retried
    [Fact]
    public async Task A_RANGE_IGNORING_ENDLESS_SERVER_COSTS_THE_COLUMN_AND_NOTHING_MORE()
    {
        var stream = new EndlessStream();
        var handler = new IgnoringHandler(() => stream);

        var h = await new HubClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
            .StructureHeaderAsync("qwen/Q-GGUF", "model.gguf", CancellationToken.None);

        Assert.Null(ModelStructure.Cell(h!));                          //the hostile server costs the structure cell only.
        Assert.InRange(stream.BytesRead, 16 << 10, (16 << 10) + 200);  //the 16 kb window bounds the bytes read from the endless body
        Assert.Equal(1, handler.Calls);                                //a header that is not gguf is hopeless, so there is no second call
    }

    //a 200 answer at a non-zero offset must be refused, the bytes would be read as extension bytes and corrupt the buffer
    [Fact]
    public async Task A_RANGE_IGNORING_SERVER_IS_REFUSED_ON_THE_EXTENSION_ROUND()
    {
        var file = GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B", paddingBytes: 64 << 10);
        var handler = new IgnoringHandler(() => new MemoryStream(file));

        var h = await new HubClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
            .StructureHeaderAsync("qwen/Q-GGUF", "model.gguf", CancellationToken.None);

        Assert.Null(h);                     //the refusal surfaces as an exception and is caught into a no-structure result.
        Assert.Equal(2, handler.Calls);     //the two calls are the parsed round and the refused round.
    }

    //a stub that answers 200 with the whole body and ignores Range. the body is a factory since StreamContent disposes what it is handed
    private sealed class IgnoringHandler(Func<Stream> body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(body()) });
        }
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead;
        public override int Read(byte[] buffer, int offset, int count)
        { var n = Math.Min(count, 100); BytesRead += n; return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    //a file name is untrusted input that arrives in the request path, so the escaping belongs to HubUrl
    [Fact]
    public async Task A_FILE_NAME_IS_ESCAPED_INTO_THE_URL()
    {
        var url = HubUrl.Resolve("qwen/Q-GGUF", "a model?x=1#frag.gguf");

        Assert.Equal(
            "https://huggingface.co/qwen/Q-GGUF/resolve/main/a%20model%3Fx%3D1%23frag.gguf", url);
        Assert.DoesNotContain("?download=true", url, StringComparison.Ordinal);
    }
}
