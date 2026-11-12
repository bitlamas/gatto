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

    //a cold read is the count and the prompt time, and the decode time beside it is far larger so a wrong field gives a different answer
    [Fact]
    public void A_COLD_PROMPT_IS_ITS_COUNT_AND_ITS_PROMPT_TIME()
    {
        var read = ServerTimings.ColdPrompt("""{"cache_n":0,"prompt_n":8200,"prompt_ms":17000,"predicted_ms":90000}""");

        Assert.Equal(8200, read!.Value.Tokens);
        Assert.Equal(8200 / 17.0, read.Value.TokensPerSecond, 3);
    }

    //a reused head makes the read short and fast, and a server that does not say what it reused proves nothing cold
    [Theory]
    [InlineData("""{"cache_n":785,"prompt_n":36,"prompt_ms":365}""")]
    [InlineData("""{"prompt_n":821,"prompt_ms":2938}""")]
    [InlineData("""{"cache_n":0,"prompt_n":0,"prompt_ms":10}""")]
    [InlineData("""{"cache_n":0,"prompt_n":821,"prompt_ms":0}""")]
    [InlineData("not json at all")]
    [InlineData(null)]
    public void ONLY_A_READ_THAT_REUSED_NOTHING_IS_COLD(string? timings) =>
        Assert.Null(ServerTimings.ColdPrompt(timings));

    //the loss is the previous prompt less what the slot still held, so a reader that returns the previous count fails the second case
    [Theory]
    [InlineData(0, 22051)]
    [InlineData(1512, 20539)]
    public void THE_LOSS_IS_THE_PREVIOUS_PROMPT_LESS_THE_CACHED_COUNT(int cached, int lost) =>
        Assert.Equal(lost, ServerTimings.Lost(22051, $$"""{"cache_n":{{cached}},"prompt_n":22166,"prompt_ms":128000}"""));

    //a slot that held the whole previous prompt, or more since it also keeps the reply, lost nothing
    [Theory]
    [InlineData(22051)]
    [InlineData(22143)]
    public void A_SLOT_THAT_HELD_THE_PREVIOUS_PROMPT_LOST_NOTHING(int cached) =>
        Assert.Null(ServerTimings.Lost(22051, $$"""{"cache_n":{{cached}},"prompt_n":92,"prompt_ms":600}"""));

    //no cache_n is nothing measured, whatever else the timings hold
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"prompt_n":22166,"prompt_ms":128000}""")]
    [InlineData("""{"cache_n":"0","prompt_n":22166}""")]
    public void TIMINGS_WITHOUT_A_CACHED_COUNT_MEASURE_NO_LOSS(string? timings) =>
        Assert.Null(ServerTimings.Lost(22051, timings));

    [Fact]
    public void THE_CACHED_COUNT_IS_CACHE_N() =>
        Assert.Equal(7058, ServerTimings.Cached("""{"cache_n":7058,"prompt_n":52,"prompt_ms":4388}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"prompt_n":22166,"prompt_ms":128000}""")]
    [InlineData("""{"cache_n":"0","prompt_n":22166}""")]
    public void TIMINGS_WITHOUT_A_NUMBER_FOR_CACHE_N_HOLD_NO_COUNT(string? timings) =>
        Assert.Null(ServerTimings.Cached(timings));
}
