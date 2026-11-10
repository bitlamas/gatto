using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Tests;

//the refresh bound is the header bound, one static, so the class joins the e2e collection and no other test sees a shortened value
[Collection("e2e")]
public class UsageMeterTests
{
    private static JsonElement Usage(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static UsageMeter Quota(Func<JsonElement, Allowance?> read) => new(new EndpointConfig(null, Quota: read));

    private static Allowance Left(long n, string label = "max") => new(label, AllowanceShape.Remaining, Remaining: n);

    private static UsageMeter Rows(Func<CancellationToken, Task<IReadOnlyList<Allowance>>> read, Func<JsonElement, Allowance?>? quota = null) =>
        new(new EndpointConfig(null, Quota: quota, Usage: read));

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
    public async Task Cost_comes_from_the_endpoints_reader_when_it_has_one()
    {
        //a provider that counts in its own unit hands core a reader, and the cost key is not read beside it
        var meter = new UsageMeter(new EndpointConfig(null,
            Cost: u => u.GetProperty("cost_in_usd_ticks").GetInt64() / 1e10m));
        await meter.Record(Usage("""{"cost_in_usd_ticks":11140000,"cost":5}"""));

        Assert.Equal(0.001114m, meter.Read().Cost);
    }

    [Fact]
    public async Task The_cost_reader_runs_on_the_pool()
    {
        //a reader that blocks costs a pool thread and never the stream that called Record
        using var gate = new ManualResetEventSlim();
        var meter = new UsageMeter(new EndpointConfig(null, Cost: _ => { gate.Wait(); return 1m; }));

        var pending = meter.Record(Usage("{}"));

        Assert.False(pending.IsCompleted);
        Assert.Null(meter.Read().Cost);
        gate.Set();
        await pending;
        Assert.Equal(1m, meter.Read().Cost);
    }

    [Fact]
    public async Task Quota_is_the_latest_reading_the_endpoint_parsed()
    {
        //the endpoint's reader turns the raw usage into a reading, and the newest one wins
        var meter = Quota(u => Left(u.GetProperty("left").GetInt64()));
        await meter.Record(Usage("""{"left":19}"""));
        await meter.Record(Usage("""{"left":18}"""));

        Assert.Equal(Left(18), meter.Read().Footer);
    }

    [Fact]
    public async Task A_null_reading_keeps_the_last_known_quota()
    {
        //a response without limits says nothing new, so the footer keeps the number it had
        var meter = Quota(u => u.TryGetProperty("left", out var l) ? Left(l.GetInt64(), "lite") : null);
        await meter.Record(Usage("""{"left":7}"""));
        await meter.Record(Usage("""{"prompt_tokens":3}"""));

        Assert.Equal(7, meter.Read().Footer!.Remaining);
    }

    [Fact]
    public async Task A_throwing_reader_is_ignored_and_the_cost_still_counts()
    {
        //the reader is extension code, so its failure costs the quota part and nothing else
        var meter = Quota(_ => throw new InvalidOperationException("bad script"));
        await meter.Record(Usage("""{"cost":0.5}"""));

        Assert.Null(meter.Read().Footer);
        Assert.Equal(0.5m, meter.Read().Cost);
    }

    [Fact]
    public async Task A_label_with_a_control_character_is_refused()
    {
        //the label is drawn in the footer, so a newline or escape from a script never reaches the terminal
        var meter = Quota(_ => Left(3, "max\u001b[31m"));
        await meter.Record(Usage("{}"));

        Assert.Null(meter.Read().Footer);
    }

    [Fact]
    public async Task A_long_label_is_stored_whole()
    {
        //the footer cuts its own copy, so /usage can draw the label the provider sent
        var meter = Quota(_ => Left(3, new string('a', 40)));
        await meter.Record(Usage("{}"));

        Assert.Equal(40, meter.Read().Footer!.Label.Length);
    }

    [Fact]
    public async Task A_negative_number_becomes_null_and_the_reading_stands()
    {
        //a null number is the grammar's own dash, so a bad number costs that number and not the row
        var meter = Quota(_ => new Allowance("week", AllowanceShape.UsedOfTotal, Used: -4, Total: 0, UsedPercent: -1, Remaining: -2));
        await meter.Record(Usage("{}"));

        var r = meter.Read().Footer!;
        Assert.Equal("week", r.Label);
        Assert.Null(r.Used);
        Assert.Null(r.Total);
        Assert.Null(r.UsedPercent);
        Assert.Null(r.Remaining);
    }

    [Fact]
    public async Task Changed_fires_once_the_new_reading_is_readable()
    {
        //the footer repaints on this, so the reading it reads must already be the new one
        var meter = Quota(u => Left(u.GetProperty("left").GetInt64()));
        long? seen = null;
        meter.Changed = () => seen = meter.Read().Footer?.Remaining;

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
        var meter = Quota(_ =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref released));
            return Left(1);
        });

        var pending = meter.Record(Usage("""{"cost":0.1}"""));

        Assert.Equal(0.1m, meter.Read().Cost);
        Assert.False(pending.IsCompleted);
        Volatile.Write(ref released, true);
        await pending;
        Assert.Equal(1, meter.Read().Footer!.Remaining);
    }

    [Fact]
    public async Task Refresh_stores_the_rows_and_the_time_and_the_first_row_is_the_footers()
    {
        //the reader's rows are /usage's whole list, its first is what the footer draws
        var week = new Allowance("week", AllowanceShape.Percent, UsedPercent: 42);
        var tier = new Allowance("tier one", AllowanceShape.Label);
        var meter = Rows(_ => Task.FromResult<IReadOnlyList<Allowance>>([week, tier]));
        var before = DateTimeOffset.Now;

        await meter.RefreshAsync(CancellationToken.None);

        var r = meter.Read();
        Assert.Equal(week, r.Footer);
        Assert.Equal([week, tier], r.Rows);
        Assert.True(r.ReadAt >= before);
        Assert.Null(r.LastFailure);
        Assert.True(r.HasReader);
        Assert.True(r.Refreshes);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("empty")]
    [InlineData("timeout")]
    public async Task A_refresh_that_throws_or_times_out_or_returns_nothing_keeps_the_last_rows_and_names_the_failure(string how)
    {
        //a failed read never blanks what was read before, and /usage says why the rows are old
        var week = new Allowance("week", AllowanceShape.Percent, UsedPercent: 42);
        var calls = 0;
        var meter = Rows(async ct =>
        {
            if (calls++ == 0) return [week];
            if (how == "throws") throw new InvalidOperationException("billing said no");
            if (how == "timeout") await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        await meter.RefreshAsync(CancellationToken.None);
        var firstRead = meter.Read().ReadAt;
        var saved = EndpointAuth.HeaderBound;
        try
        {
            EndpointAuth.HeaderBound = TimeSpan.FromMilliseconds(200);
            await meter.RefreshAsync(CancellationToken.None);
        }
        finally { EndpointAuth.HeaderBound = saved; }

        var r = meter.Read();
        Assert.Equal(week, r.Footer);
        Assert.Equal(firstRead, r.ReadAt);
        Assert.Equal(how switch
        {
            "throws" => "billing said no",
            "timeout" => "its extension did not answer in time",
            _ => "its extension returned no allowance",
        }, r.LastFailure);
    }

    [Fact]
    public async Task A_refresh_never_holds_the_caller()
    {
        //a reader that blocks before it returns a task still ends at the bound
        using var never = new ManualResetEventSlim();
        var meter = Rows(_ => { never.Wait(CancellationToken.None); return Task.FromResult<IReadOnlyList<Allowance>>([]); });
        var saved = EndpointAuth.HeaderBound;
        try
        {
            EndpointAuth.HeaderBound = TimeSpan.FromMilliseconds(200);
            var refresh = meter.RefreshAsync(CancellationToken.None);
            Assert.Same(refresh, await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(10))));
        }
        finally
        {
            EndpointAuth.HeaderBound = saved;
            never.Set();
        }
        Assert.Equal("its extension did not answer in time", meter.Read().LastFailure);
    }

    [Fact]
    public async Task Both_readers_at_once_the_usage_rows_first_row_is_the_footers_and_the_response_reading_is_one_more_row()
    {
        //the reader the provider built for limits wins the footer, the per-response bucket still shows in /usage
        var week = new Allowance("week", AllowanceShape.Percent, UsedPercent: 42);
        var meter = Rows(_ => Task.FromResult<IReadOnlyList<Allowance>>([week]), _ => Left(18));

        await meter.Record(Usage("{}"));
        await meter.RefreshAsync(CancellationToken.None);

        var r = meter.Read();
        Assert.Equal(week, r.Footer);
        Assert.Equal([week, Left(18)], r.Rows);
    }

    [Fact]
    public async Task A_label_shape_row_is_kept_as_the_reader_sent_it()
    {
        //a row with no number stays a row, the footer decides how it draws and that it never warns
        var tier = new Allowance("tier one", AllowanceShape.Label);
        var meter = Rows(_ => Task.FromResult<IReadOnlyList<Allowance>>([tier]));

        await meter.RefreshAsync(CancellationToken.None);

        Assert.Equal(tier, meter.Read().Footer);
    }

    [Fact]
    public async Task A_refresh_with_no_usage_reader_stores_nothing()
    {
        //a quota-only endpoint keeps its per-response reading, with no read time and no failure
        var meter = Quota(_ => Left(18));
        await meter.Record(Usage("{}"));

        await meter.RefreshAsync(CancellationToken.None);

        var r = meter.Read();
        Assert.Equal(Left(18), r.Footer);
        Assert.Null(r.ReadAt);
        Assert.Null(r.LastFailure);
        Assert.True(r.HasReader);
        Assert.False(r.Refreshes);
    }

    [Fact]
    public void An_endpoint_with_no_reader_says_so()
    {
        //the /usage sentence for an endpoint that reports no allowance reads this
        Assert.False(new UsageMeter(new EndpointConfig("https://cloud.example.test")).Read().HasReader);
    }
}
