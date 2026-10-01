using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Tests;

public class UsageMeterTests
{
    private static JsonElement Usage(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Cost_is_the_sum_of_every_reported_cost()
    {
        //each request adds its own cost, so the footer shows what the whole session spent
        var meter = new UsageMeter();
        await meter.Record(Usage("""{"prompt_tokens":10,"cost":0.25}"""));
        await meter.Record(Usage("""{"prompt_tokens":10,"cost":0.17}"""));

        Assert.Equal(0.42m, meter.Read().Cost);
    }

    [Fact]
    public async Task Cost_stays_null_when_no_response_carried_one()
    {
        //a provider that reports no cost shows no money, never a made-up zero
        var meter = new UsageMeter();
        await meter.Record(Usage("""{"prompt_tokens":10,"completion_tokens":2}"""));

        Assert.Null(meter.Read().Cost);
    }

    [Fact]
    public async Task Quota_is_the_latest_reading_the_endpoint_parsed()
    {
        //the endpoint's reader turns the raw usage into a reading, and the newest one wins
        var meter = new UsageMeter(u => new QuotaReading(
            u.GetProperty("left").GetInt64(), "max"));
        await meter.Record(Usage("""{"left":19}"""));
        await meter.Record(Usage("""{"left":18}"""));

        Assert.Equal(new QuotaReading(18, "max"), meter.Read().Quota);
    }

    [Fact]
    public async Task A_null_reading_keeps_the_last_known_quota()
    {
        //a response without limits says nothing new, so the footer keeps the number it had
        var meter = new UsageMeter(u => u.TryGetProperty("left", out var l) ? new QuotaReading(l.GetInt64(), "lite") : null);
        await meter.Record(Usage("""{"left":7}"""));
        await meter.Record(Usage("""{"prompt_tokens":3}"""));

        Assert.Equal(7, meter.Read().Quota!.Remaining);
    }

    [Fact]
    public async Task A_throwing_reader_is_ignored_and_the_cost_still_counts()
    {
        //the reader is extension code, so its failure costs the quota part and nothing else
        var meter = new UsageMeter(_ => throw new InvalidOperationException("bad script"));
        await meter.Record(Usage("""{"cost":0.5}"""));

        Assert.Null(meter.Read().Quota);
        Assert.Equal(0.5m, meter.Read().Cost);
    }

    [Fact]
    public async Task A_label_with_a_control_character_is_refused()
    {
        //the label is drawn in the footer, so a newline or escape from a script never reaches the terminal
        var meter = new UsageMeter(_ => new QuotaReading(3, "max\u001b[31m"));
        await meter.Record(Usage("{}"));

        Assert.Null(meter.Read().Quota);
    }

    [Fact]
    public async Task A_long_label_is_cut_to_the_budget()
    {
        //one label must not crowd the row, so it is cut to a fixed number of characters
        var meter = new UsageMeter(_ => new QuotaReading(3, new string('a', 40)));
        await meter.Record(Usage("{}"));

        Assert.Equal(UsageMeter.MaxLabel, meter.Read().Quota!.Label.Length);
    }

    [Fact]
    public async Task Changed_fires_once_the_new_reading_is_readable()
    {
        //the footer repaints on this, so the reading it reads must already be the new one
        var meter = new UsageMeter(u => new QuotaReading(u.GetProperty("left").GetInt64(), "max"));
        long? seen = null;
        meter.Changed = () => seen = meter.Read().Quota?.Remaining;

        await meter.Record(Usage("""{"left":17}"""));

        Assert.Equal(17, seen);
    }

    [Fact]
    public async Task Changed_fires_for_a_cost_alone_and_a_throwing_handler_is_contained()
    {
        //an endpoint with no quota reader still repaints for its cost, and a failing repaint stays inside the meter
        var meter = new UsageMeter();
        var calls = 0;
        meter.Changed = () => { calls++; throw new InvalidOperationException("paint broke"); };

        await meter.Record(Usage("""{"cost":0.2}"""));

        Assert.Equal(1, calls);
        Assert.Equal(0.2m, meter.Read().Cost);
    }

    [Fact]
    public async Task A_reader_that_never_returns_does_not_hold_the_caller()
    {
        //the reader runs off the stream's thread, so a script that blocks costs a pool thread and not the turn
        var released = false;
        var meter = new UsageMeter(_ =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref released));
            return new QuotaReading(1, "max");
        });

        var pending = meter.Record(Usage("""{"cost":0.1}"""));

        Assert.Equal(0.1m, meter.Read().Cost);
        Assert.False(pending.IsCompleted);
        Volatile.Write(ref released, true);
        await pending;
        Assert.Equal(1, meter.Read().Quota!.Remaining);
    }
}
