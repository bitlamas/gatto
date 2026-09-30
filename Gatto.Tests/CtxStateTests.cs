using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl.Term;

namespace Gatto.Tests;

public class CtxStateTests
{
    [Fact]
    public void Percent_NullUntilBothKnown()
    {
        var c = new CtxState();
        Assert.Null(c.Percent);
        c.RecordUsage(new Usage(100, 20));
        Assert.Null(c.Percent);                    //percent is null while no context window is known.
        c.ContextWindow = 1000;
        Assert.Equal(12, c.Percent);               //percent is prompt plus completion tokens divided by the window.
    }

    [Fact]
    public void Percent_WindowZero_IsNull()
    {
        var c = new CtxState { ContextWindow = 0 };
        c.RecordUsage(new Usage(1, 1));
        Assert.Null(c.Percent);
    }

    [Fact]
    public void Percent_PrefersEstimate_WhenBudgetSet()
    {
        var c = new CtxState { ContextWindow = 1000 };
        c.RecordUsage(new Usage(900, 0));           //the probe value differs on purpose, to expose which source wins.
        c.RecordEstimate(200, 1000);
        Assert.Equal(20, c.Percent);
    }

    [Fact]
    public void Percent_FallsBackToProbe_WhenBudgetNullOrZero()
    {
        var c = new CtxState { ContextWindow = 1000 };
        c.RecordUsage(new Usage(340, 0));
        c.RecordEstimate(999, null);
        Assert.Equal(34, c.Percent);
        c.RecordEstimate(999, 0);
        Assert.Equal(34, c.Percent);
    }

    [Fact]
    public void Percent_EstimateWithBudget_NoProbeNeeded()
    {
        var c = new CtxState();
        c.RecordEstimate(500, 2000);
        Assert.Equal(25, c.Percent);
    }

    [Fact]
    public void Percent_PrefersRealPromptTokens_OverEstimate()
    {
        var s = new CtxState { ContextWindow = 65_536 };
        s.RecordEstimate(estimateTokens: 55_000, budgetTokens: 65_536);   //the estimate deliberately differs from the server reading.
        s.RecordUsedTokens(62_000);                                       //server says 95%
        Assert.Equal(95, s.Percent);
    }

    [Fact]
    public void Percent_FallsBackToEstimate_WhenNoPromptReading()
    {
        var s = new CtxState();
        s.RecordEstimate(32_768, 65_536);
        Assert.Equal(50, s.Percent);
    }

    [Fact]
    public void Percent_PromptReading_UsesEstimateBudget_ThenWindow()
    {
        var s = new CtxState { ContextWindow = 100_000 };
        s.RecordUsedTokens(50_000);
        Assert.Equal(50, s.Percent);                                      //with no budget set, the denominator is the window.
        s.RecordEstimate(10, budgetTokens: 65_536);
        Assert.Equal(76, s.Percent);                                      //once a budget exists, it becomes the denominator.
    }

    //assert agreement between the footer and the trigger on identical state

    [Fact]
    public void The_footer_percent_and_the_auto_compact_trigger_read_the_same_figure()
    {
        const int budget = 32768;
        const double autoCompactAt = 0.8;   //the constant mirrors the config default.

        var convo = new Conversation("you are gatto");
        convo.AddUser("read the file and fix the bug");
        convo.AddAssistant("looking now");

        var usage = new ContextUsageState();
        usage.Record(promptTokens: 10_000, estimateAtRequest: 10_000, messageCount: convo.Count);

        //the fixture replays the reported state: a large tool result appended mid-turn.
        convo.AddToolResult("call_1", new ToolResult(new string('x', 70_000)));

        //the trigger's figure is the same expression the loop's checkpoint evaluates.
        var trigger = ContextBudget.UsedTokens(convo, usage);
        Assert.NotNull(trigger);
        Assert.True(trigger >= autoCompactAt * budget, "precondition: this state must fire the trigger");

        //the footer is wired exactly as production wires it.
        var ctx = new CtxState { ContextWindow = budget };
        ctx.RecordUsedTokens(trigger);
        ctx.RecordEstimate(ContextBudget.Estimate(convo.Messages), budget);

        //the footer must show the percent the trigger acted on
        Assert.Equal((int)Math.Round(100.0 * trigger.Value / budget), ctx.Percent);
        Assert.True(ctx.Percent >= 80, $"the footer must show the session is nearly full; got {ctx.Percent}");
    }

    [Fact]
    public void Tokens_is_the_numerator_behind_Percent_and_they_are_null_together()
    {
        //both figures come from one priority ladder, so the footer can never show a pair that disagrees
        var c = new CtxState();
        Assert.Null(c.Percent);
        Assert.Null(c.Tokens);

        c.ContextWindow = 262_144;
        c.RecordUsedTokens(35_750);
        Assert.Equal(14, c.Percent);
        Assert.Equal(35_750, c.Tokens);            //the division really does come out to 14 percent.
        Assert.Equal(c.Percent, (int)Math.Round(100.0 * c.Tokens!.Value / 262_144));

        //the same agreement is checked on the estimate branch.
        var e = new CtxState { ContextWindow = 262_144 };
        e.RecordUsedTokens(null);
        e.RecordEstimate(1_133, budgetTokens: 262_144);
        Assert.Equal(1_133, e.Tokens);
        Assert.Equal(e.Percent, (int)Math.Round(100.0 * e.Tokens!.Value / 262_144));
        Assert.Equal(0, e.Percent);                //a small share rounds to 0 percent, which is why the footer shows the token figure beside it
    }

    [Fact]
    public void Without_a_usage_echo_the_footer_still_falls_through_to_the_estimate()
    {
        const int budget = 32768;
        var convo = new Conversation("you are gatto");
        convo.AddUser("go");

        var ctx = new CtxState { ContextWindow = budget };
        ctx.RecordUsedTokens(null);
        ctx.RecordEstimate(ContextBudget.Estimate(convo.Messages), budget);

        Assert.Equal((int)Math.Round(100.0 * ContextBudget.Estimate(convo.Messages) / budget), ctx.Percent);
    }
}
