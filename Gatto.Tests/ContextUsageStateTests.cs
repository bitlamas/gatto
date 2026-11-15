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

    //a head counted denser than the estimate, then a growth counted lighter, and the pair teaches the growth's own density
    [Fact]
    public void A_growth_pair_sets_the_growth_ratio_and_leaves_the_ratio_as_before()
    {
        var s = new ContextUsageState();
        s.Record(promptTokens: 2_800, estimateAtRequest: 2_000, messageCount: 1);
        s.Record(promptTokens: 2_800 + 3_600, estimateAtRequest: 2_000 + 4_000, messageCount: 4, comparable: true);
        Assert.Equal(0.9, s.GrowthRatio, precision: 6);
        Assert.Equal(6_400.0 / 6_000.0, s.Ratio, precision: 6);
    }

    //too little growth, a recut or failed request, no new message, a prompt that did not grow: each keeps the pair from teaching
    [Theory]
    [InlineData(511, true, 4, 100)]
    [InlineData(4_000, false, 4, 3_600)]
    [InlineData(4_000, true, 1, 3_600)]
    [InlineData(4_000, true, 4, 0)]
    public void A_pair_that_is_not_a_growth_teaches_nothing(int estimateGrowth, bool comparable, int messageCount, int promptGrowth)
    {
        var s = new ContextUsageState();
        s.Record(2_800, 2_000, 1);
        s.Record(2_800 + promptGrowth, 2_000 + estimateGrowth, messageCount, comparable);
        Assert.Equal(1.0, s.GrowthRatio);
        Assert.Equal(Math.Max(1.0, (2_800.0 + promptGrowth) / (2_000 + estimateGrowth)), s.Ratio, precision: 6);
    }

    [Theory]
    [InlineData(1_000, 4_000, 3.0)]
    [InlineData(1_000, 200, 0.75)]
    public void One_odd_pair_is_held_to_the_range(int estimateGrowth, int promptGrowth, double held)
    {
        var s = new ContextUsageState();
        s.Record(2_800, 2_000, 1);
        s.Record(2_800 + promptGrowth, 2_000 + estimateGrowth, 4, comparable: true);
        Assert.Equal(held, s.GrowthRatio);
    }

    [Fact]
    public void Clear_resets_the_growth_ratio_and_its_pair()
    {
        var s = new ContextUsageState();
        s.Record(2_800, 2_000, 1);
        s.Record(6_400, 6_000, 4, comparable: true);
        s.Clear();
        Assert.Equal(1.0, s.GrowthRatio);
        s.Record(10_000, 9_000, 6, comparable: true);   //the first reading after a clear has no partner
        Assert.Equal(1.0, s.GrowthRatio);
    }

    [Fact]
    public void A_seeded_session_learns_no_growth_from_its_first_reading()
    {
        var s = new ContextUsageState();
        s.Seed(5, 1.3);
        Assert.Equal(1.0, s.GrowthRatio);
        s.Record(10_000, 9_000, 6, comparable: true);
        Assert.Equal(1.0, s.GrowthRatio);
    }
}
