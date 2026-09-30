using Gatto.Core.Client;

namespace Gatto.Tests;

//the one reader of the server's own timings, and reading prompt_ms for predicted_ms must fail here
public class ServerTimingsTests
{
    private static string Timings(double n, double predictedMs, double promptMs = 9_000) =>
        $$"""{"predicted_n": {{n}}, "predicted_ms": {{predictedMs}}, "prompt_ms": {{promptMs}}}""";

    //the rate is the decode time, and the fixture's prompt time is far larger so a wrong field gives a different answer
    [Fact]
    public void THE_RATE_IS_THE_DECODE_AND_NOT_THE_WHOLE_REQUEST()
    {
        var rate = ServerTimings.DecodeRate([Timings(100, 1_000, promptMs: 9_000)]);

        Assert.NotNull(rate);
        Assert.Equal(100.0, rate!.Value, 3);
    }

    //sum tokens and time across replies, an average per reply would weight a two-token reply like a two-hundred-token one
    [Fact]
    public void SEVERAL_REPLIES_ARE_ONE_MEASUREMENT()
    {
        var rate = ServerTimings.DecodeRate([Timings(10, 1_000), Timings(90, 1_000)]);

        Assert.Equal(50.0, rate!.Value, 3);
    }

    //a server that did not say leaves the rate absent, null and empty text and malformed json are one state
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"prompt_ms\": 12}")]
    public void A_SERVER_THAT_DID_NOT_SAY_PRODUCES_NO_RATE(string? timings) =>
        Assert.Null(ServerTimings.DecodeRate([timings]));

    //an unreadable reply must not discard the readable replies beside it, it counts as one that did not say
    [Fact]
    public void ONE_UNREADABLE_REPLY_DOES_NOT_LOSE_THE_OTHERS()
    {
        var rate = ServerTimings.DecodeRate(["{oh dear", Timings(50, 1_000)]);

        Assert.Equal(50.0, rate!.Value, 3);
    }
}
