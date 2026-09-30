using Gatto.Core.Loop;
using Xunit;

namespace Gatto.Tests;

public sealed class ContextUsageStateTests
{
    [Fact]
    public void FreshState_NoReading_RatioOne()
    {
        var s = new ContextUsageState();
        Assert.Null(s.LastPromptTokens);
        Assert.Null(s.LastMessageCount);
        Assert.Equal(1.0, s.Ratio);
    }

    [Fact]
    public void Record_StoresPromptTokens_AndRatio()
    {
        var s = new ContextUsageState();
        s.Record(promptTokens: 69716, estimateAtRequest: 55000, messageCount: 12);
        Assert.Equal(69716, s.LastPromptTokens);
        Assert.Equal(12, s.LastMessageCount);
        Assert.Equal(69716.0 / 55000.0, s.Ratio, precision: 6);
    }

    [Fact]
    public void Ratio_FloorsAtOne()   //the ratio floors at one because the estimate is only ever corrected upward.
    {
        var s = new ContextUsageState();
        s.Record(promptTokens: 40000, estimateAtRequest: 55000, messageCount: 3);
        Assert.Equal(1.0, s.Ratio);
    }

    [Fact]
    public void Clear_ResetsReadingAndRatio()
    {
        var s = new ContextUsageState();
        s.Record(69716, 55000, 12);
        s.Clear();
        Assert.Null(s.LastPromptTokens);
        Assert.Null(s.LastMessageCount);   //the growth baseline describes a conversation that no longer exists, so Clear resets it too
        Assert.Equal(1.0, s.Ratio);
    }

    [Fact]
    public void Record_ZeroOrNegativeEstimate_KeepsRatioOne()   //the rule avoids dividing by an estimate of zero.
    {
        var s = new ContextUsageState();
        s.Record(1000, 0, 1);
        Assert.Equal(1.0, s.Ratio);
        Assert.Equal(1000, s.LastPromptTokens);
    }
}
