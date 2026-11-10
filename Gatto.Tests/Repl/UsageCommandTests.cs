using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//usage on the rich face, read fresh at the command and drawn as system rows, and the two sentences of an endpoint with nothing to read
[Collection("e2e")]
public class UsageCommandTests
{
    [Fact]
    public async Task USAGE_RE_READS_FIRST_THEN_DRAWS_THE_ROWS()
    {
        var fresh = new List<bool>();
        var h = new RichReplHarness(Path.GetTempPath(), allowanceEndpoint: "hosted",
            readAllowance: (f, _) =>
            {
                fresh.Add(f);
                return Task.FromResult(new AllowanceReport(0.5m, null,
                    [new Allowance("week", AllowanceShape.Percent, UsedPercent: fresh.Count)], DateTimeOffset.Now, null, true, Refreshes: true));
            });
        h.Keys.Line("/usage").Line("/quit");
        await h.RunAsync();

        Assert.Equal([false, true], fresh);
        Assert.True(h.Saw("week 2% used"), h.Written());
        Assert.True(h.Saw("read just now"), h.Written());
        Assert.True(h.Saw("cost $0.50 accounted by the provider"), h.Written());
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public async Task USAGE_ON_AN_ENDPOINT_WITH_NOTHING_TO_READ_SAYS_ITS_SENTENCE_AND_ASKS_THE_PROVIDER_NOTHING()
    {
        var fresh = 0;
        var h = new RichReplHarness(Path.GetTempPath(), noAllowanceLine: "endpoint hosted reports no allowance",
            readAllowance: (f, _) => { if (f) fresh++; return Task.FromResult(new AllowanceReport(null, null, [], null, null, false)); });
        h.Keys.Line("/usage").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("endpoint hosted reports no allowance"), h.Written());
        Assert.False(h.Saw("cost $"), h.Written());
        Assert.Equal(0, fresh);
    }

    [Fact]
    public async Task USAGE_SAYS_THE_READ_BEFORE_IT_WAITS_AND_ESC_ENDS_IT_ON_THE_ROWS_READ_BEFORE()
    {
        var stored = new AllowanceReport(null, null, [new Allowance("week", AllowanceShape.Percent, UsedPercent: 7)],
            DateTimeOffset.Now.AddMinutes(-5), null, true, Refreshes: true);
        var reading = new TaskCompletionSource();
        var h = new RichReplHarness(Path.GetTempPath(), allowanceEndpoint: "hosted",
            readAllowance: async (fresh, ct) =>
            {
                if (!fresh) return stored;
                reading.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }   //the meter stores nothing on a cancelled read and answers what it held
                return stored;
            });
        var run = h.RunAsync(60_000);
        h.Keys.Line("/usage");
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await h.UntilAsync(() => h.Saw("hosted: reading the allowance"), Waits.Bound), h.Written());

        _ = h.Keys.KeyAvailable;
        h.Keys.Key(new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false));
        Assert.True(await h.UntilAsync(() => h.Saw("week 7% used"), Waits.Bound), h.Written());
        //the scripted source waits on an availability sample after a key the cancel sink took, as the help panel's walk releases it
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
        await Task.Delay(100);
        _ = h.Keys.KeyAvailable;
        h.Keys.Line("/quit");
        await run;

        Assert.True(h.Saw("read 5 min ago"), h.Written());
    }

    [Fact]
    public async Task USAGE_ON_AN_ENDPOINT_WITH_NO_READER_STILL_SAYS_THE_COST_IT_SUMMED()
    {
        var h = new RichReplHarness(Path.GetTempPath(), noAllowanceLine: "endpoint hosted reports no allowance",
            readAllowance: (_, _) => Task.FromResult(new AllowanceReport(0.42m, null, [], null, null, false)));
        h.Keys.Line("/usage").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("endpoint hosted reports no allowance"), h.Written());
        Assert.True(h.Saw("cost $0.42 accounted by the provider"), h.Written());
    }

    [Fact]
    public async Task USAGE_ON_AN_ENDPOINT_WITH_NOTHING_TO_REFRESH_DRAWS_WHAT_IT_HOLDS_AND_SAYS_NO_READ()
    {
        //a reader of each response has nothing to ask the provider, so no read is announced
        var fresh = 0;
        var h = new RichReplHarness(Path.GetTempPath(), allowanceEndpoint: "hosted",
            readAllowance: (f, _) =>
            {
                if (f) fresh++;
                return Task.FromResult(new AllowanceReport(null, null, [new Allowance("max", AllowanceShape.Remaining, Remaining: 18)], null, null, true));
            });
        h.Keys.Line("/usage").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("max 18 left"), h.Written());
        Assert.False(h.Saw("reading the allowance"), h.Written());
        Assert.Equal(0, fresh);
    }

    [Fact]
    public async Task USAGE_WITH_NO_SEAM_IS_THE_LOCAL_SENTENCE()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/usage").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Gatto.Repl.Repl.LocalAllowance), h.Written());
    }
}
