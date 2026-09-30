using System.Text;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class SseReaderTests
{
    private static async Task<List<string>> ReadAll(string wire)
    {
        var stream = new DribbleStream(Encoding.UTF8.GetBytes(wire));
        var payloads = new List<string>();
        await foreach (var p in SseReader.ReadDataPayloads(stream, CancellationToken.None))
            payloads.Add(p);
        return payloads;
    }

    [Fact]
    public async Task Yields_each_data_payload()
    {
        var got = await ReadAll("data: {\"a\":1}\n\ndata: {\"b\":2}\n\ndata: [DONE]\n\n");
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}", "[DONE]" }, got);
    }

    [Fact]
    public async Task Handles_crlf_and_no_space_after_colon()
    {
        var got = await ReadAll("data:{\"a\":1}\r\n\r\n");
        Assert.Equal(new[] { "{\"a\":1}" }, got);
    }

    [Fact]
    public async Task Joins_multiline_data_and_ignores_comments_and_other_fields()
    {
        var got = await ReadAll(": comment\nevent: x\ndata: line1\ndata: line2\n\n");
        Assert.Equal(new[] { "line1\nline2" }, got);
    }

    [Fact]
    public async Task Flushes_trailing_event_without_final_blank_line()
    {
        var got = await ReadAll("data: tail");
        Assert.Equal(new[] { "tail" }, got);
    }
}
