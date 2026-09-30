using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the 85% offer names what the loop will do next. each fixture configures one threshold and arms another, so a line read from the configuration fails.
[Collection("e2e")]
public class CompactOfferLineWiringTests
{
    //a handler the loop can hold but never call here. the harness loop has no budget and the fake client never overflows, so a call means the fixture is wrong.
    private sealed class NeverCalled : ICompactionHandler
    {
        public Task<CompactionResult?> CompactAsync(
            CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct) =>
            throw new InvalidOperationException("the fixture let a compaction run");
    }

    //one turn whose estimate crosses 85% of a small budget, so the offer fires after it.
    private static async Task<RichReplHarness> OfferAfterOneTurn(Action<AgentLoop> arm)
    {
        var h = new RichReplHarness(Path.GetTempPath(), width: 200, contextBudget: 50, autoCompact: 0.8);
        arm(h.Loop);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        h.Keys.Line(new string('x', 400));
        await h.RunUntilAsync(() => h.Saw("context 85% full"));
        return h;
    }

    //a threshold without a handler arms nothing (neither trigger runs without one)
    [Fact]
    public void A_THRESHOLD_WITHOUT_A_HANDLER_IS_NOT_ARMED()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Loop.EnableAutoCompact(null, new ContextUsageState(), 0.8);

        Assert.Null(h.Loop.ArmedAutoCompactAt);
    }

    [Fact]
    public async Task A_VETOED_SESSION_OFFERS_COMPACT_AS_OFF_and_never_quotes_the_configured_threshold()
    {
        var h = await OfferAfterOneTurn(loop => loop.EnableAutoCompact(null, null, null));

        Assert.Null(h.Loop.ArmedAutoCompactAt);
        Assert.True(h.Saw("(auto-compact is off)"), h.ScreenText());
        Assert.False(h.Saw("auto-compacts at"), h.ScreenText());
    }

    [Fact]
    public async Task AN_ARMED_SESSION_QUOTES_THE_THRESHOLD_THE_LOOP_HOLDS()
    {
        var h = await OfferAfterOneTurn(loop => loop.EnableAutoCompact(new NeverCalled(), new ContextUsageState(), 0.9));

        Assert.Equal(0.9, h.Loop.ArmedAutoCompactAt);
        Assert.True(h.Saw("auto-compacts at 90%"), h.ScreenText());
        Assert.False(h.Saw("auto-compacts at 80%"), h.ScreenText());
    }
}
