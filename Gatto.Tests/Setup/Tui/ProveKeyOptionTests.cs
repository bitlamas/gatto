using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//an answer must be one the screen actually drew, since several screens share the same prove key
public class ProveKeyOptionTests
{
    private static WizardProbes Connect(ProveOutcome prove) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Server = new ConnectProbe("http://127.0.0.1:1234", ["a-model"], 8192),
        Prove = prove,
    };

    //drives the connect path to wherever its check settles. the watch resolves the way the runner does, since stopping there asserts about a live frame
    private static SetupFlow Settled(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        var s = flow.ResumeAfterWrites(null);
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);
        if (s is WizardScreen.Choice { Watching: true })
        {
            Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
                "the check never settled");
            flow.Answer(SetupFlow.Finish);
        }
        return flow;
    }

    private static IReadOnlyList<string> Drew(SetupFlow flow) =>
        [.. Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]).Options.Select(o => o.Key)];

    //the no-stream arm withholds try again on purpose
    [Fact]
    public void THE_NO_STREAM_ARM_WITHHOLDS_TRY_AGAIN_AND_REFUSES_IT()
    {
        var flow = Settled(Connect(new ProveOutcome(false, "no frames", TimeSpan.Zero, ServerAnswered: 200)));

        Assert.Equal([SetupFlow.Finish], Drew(flow));
        var boom = Assert.Throws<InvalidOperationException>(() => flow.Answer(SetupFlow.Retry));
        Assert.Contains(SetupFlow.Retry, boom.Message, StringComparison.Ordinal);
    }

    //the guard must also accept a key the screen drew, since a check that only fires on the refusal proves half the rule
    [Fact]
    public void AND_THE_ARM_THAT_OFFERS_TRY_AGAIN_STILL_TAKES_IT()
    {
        var flow = Settled(Connect(new ProveOutcome(false, "connection refused", TimeSpan.Zero)));

        Assert.Contains(SetupFlow.Retry, Drew(flow));
        flow.Answer(SetupFlow.Retry);   //the drawn key is accepted
    }

    //the guard makes a stale fixture fail loudly when this screen's options change
    [Fact]
    public void THE_SUMMARY_REFUSES_AN_OPTION_IT_DOES_NOT_DRAW()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            Install = Gatto.Cli.InstallState.Installed,
            Audition = new AuditionCheck(
                AuditionOutcome.Passed,
                new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        });
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        flow.Answer(SetupFlow.FoundUse);
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        flow.Answer(SetupFlow.Landed);
        var next = flow.Answer(SetupFlow.AuditionPassedNext);
        while (flow.NeedsWritesApplied) next = flow.ResumeAfterWrites(null);

        Assert.Equal(SetupFlow.SummaryKey, ScreenKey.Of(next));
        Assert.Equal([SetupFlow.OpenRepl, SetupFlow.Finish], Drew(flow));
        Assert.Throws<InvalidOperationException>(() => flow.Answer(SetupFlow.Retry));
    }

    //a watched screen is exempt from the drawn-key guard, since the runner resolves it with the stop key alone
    [Fact]
    public void A_WATCHED_SCREEN_IS_ANSWERED_BY_RESOLVING_NOT_BY_ITS_DRAWN_OPTION()
    {
        var flow = new SetupFlow(Connect(new ProveOutcome(true, "hi", TimeSpan.FromSeconds(2))));
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        var s = flow.ResumeAfterWrites(null);
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);

        var watching = Assert.IsType<WizardScreen.Choice>(s);
        Assert.True(watching.Watching);
        Assert.Equal([SetupFlow.ConnectCheckStop], watching.Options.Select(o => o.Key));

        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the check never settled");
        flow.Answer(SetupFlow.Finish);   //the answer is not one of its options, which is how a watch resolves
    }
}
