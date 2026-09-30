using Gatto.Core.Acquire;

namespace Gatto.Tests;

//the shelf fills from the current tier downwards, and three tiers keep a guaranteed top row. the rule is selection only, so the rows here are bare ids.
public class TierFillTests
{
    private static ModelTier T(string repo) => QwenTier.Of(repo);

    private static TierFillResult<string> Fill(IReadOnlyList<string> rows, string pin, int budget) =>
        TierFill.Select(rows, T, T("Qwen" + pin), budget);

    //current-tier-only would show a single row, the fall-through fills the budget from the rows below
    [Fact]
    public void A_THIN_CURRENT_TIER_KEEPS_FILLING_FROM_THE_ONE_BELOW()
    {
        string[] rows =
        [
            "Qwen3-Coder-Next", "Qwen3-Next-80B-A3B-Instruct", "Qwen3-Next-80B-A3B-Thinking",
            "Qwen3.5-35B-A3B", "Qwen3.6-35B-A3B", "Qwen-AgentWorld-35B-A3B",
        ];

        //only one row here is 3.6, so a current-tier-only fill would show that row alone
        Assert.Single(rows, r => T(r).Label == "3.6");

        var filled = Fill(rows, "3.6", 6);
        Assert.Equal(6, filled.Rows.Count);
        Assert.Equal(0, filled.HiddenOlder);
    }

    //the pin's tier and the two versioned tiers below it each keep their top row. a greedy fill would spend the budget on the pin's tier alone
    [Fact]
    public void THE_TOP_ROW_OF_THREE_TIERS_IS_GUARANTEED_A_SLOT()
    {
        string[] rows =
        [
            "Qwen3.6-a", "Qwen3.6-b", "Qwen3.6-c",
            "Qwen3.5-a", "Qwen3-a", "Qwen2.5-a",
        ];

        var filled = Fill(rows, "3.6", 3);

        Assert.Equal(["Qwen3.6-a", "Qwen3.5-a", "Qwen3-a"], filled.Rows);
        //a greedy fill would have taken these two instead
        Assert.DoesNotContain("Qwen3.6-b", filled.Rows);
        Assert.DoesNotContain("Qwen3.6-c", filled.Rows);
    }

    //a guarantee that reached the unversioned bucket would spend a slot on it, so the fixture leaves room after the versioned tiers
    [Fact]
    public void THE_GUARANTEE_NEVER_REACHES_THE_UNVERSIONED_BUCKET()
    {
        string[] rows = ["Qwen3.6-a", "Qwen3.6-b", "Qwen3.6-c", "Qwen-AgentWorld"];

        var filled = Fill(rows, "3.6", 3);

        //the one versioned tier takes its guaranteed top row and then fills depth-first, so unversioned gets nothing here
        Assert.Equal(["Qwen3.6-a", "Qwen3.6-b", "Qwen3.6-c"], filled.Rows);
        Assert.DoesNotContain("Qwen-AgentWorld", filled.Rows);
    }

    //the guarantee covers three tiers, a fourth would take the slot the current generation's second row gets
    [Fact]
    public void THE_GUARANTEE_STOPS_AT_THREE_TIERS()
    {
        string[] rows = ["Qwen3.6-a", "Qwen3.6-b", "Qwen3.5-a", "Qwen3-a", "Qwen2.5-a"];

        var filled = Fill(rows, "3.6", 4);

        //three guaranteed tops, then the last slot goes depth-first to the current tier
        Assert.Equal(["Qwen3.6-a", "Qwen3.6-b", "Qwen3.5-a", "Qwen3-a"], filled.Rows);
        //a cap of four would spend that slot on the fourth tier
        Assert.DoesNotContain("Qwen2.5-a", filled.Rows);
    }

    //the unversioned bucket is reached last and only when the versioned tiers run out before the budget
    [Fact]
    public void UNVERSIONED_IS_LAST_AND_ONLY_IF_THERE_IS_ROOM()
    {
        string[] rows = ["Qwen3.6-a", "Qwen-AgentWorld", "Qwen3.5-a"];

        Assert.DoesNotContain("Qwen-AgentWorld", Fill(rows, "3.6", 2).Rows);
        Assert.Contains("Qwen-AgentWorld", Fill(rows, "3.6", 3).Rows);
    }

    //an empty current tier needs no special case, the fill takes the next tier down and never checked
    [Fact]
    public void NOTHING_IN_THE_CURRENT_TIER_LETS_THE_ONE_BELOW_OPEN_THE_SHELF()
    {
        string[] rows = ["Qwen3.5-a", "Qwen3.5-b", "Qwen3-a"];

        var filled = Fill(rows, "3.6", 3);

        Assert.Equal(rows, filled.Rows);
        Assert.DoesNotContain(rows, r => T(r).Label == "3.6");
    }

    //the selection keeps the order the rows arrived in, since the shelf is sorted by the user's chosen axis
    [Fact]
    public void THE_SELECTION_KEEPS_THE_AXIS_ORDER_IT_ARRIVED_IN()
    {
        string[] rows = ["Qwen3-big", "Qwen3.6-medium", "Qwen3.5-small"];

        var filled = Fill(rows, "3.6", 3);

        Assert.Equal(rows, filled.Rows);
        //tier order would put the 3.6 first, arrival order does not
        Assert.Equal("Qwen3-big", filled.Rows[0]);
    }

    //a tier newer than the pin is counted apart, since calling it older on the count line would be a lie
    [Fact]
    public void A_TIER_NEWER_THAN_THE_PIN_IS_NOT_COUNTED_AS_OLDER()
    {
        string[] rows = ["Qwen3.8-new", "Qwen3.6-a", "Qwen3.5-a"];

        var filled = Fill(rows, "3.6", 2);

        Assert.DoesNotContain("Qwen3.8-new", filled.Rows);
        Assert.Equal(1, filled.HiddenNewer);
        Assert.Equal(0, filled.HiddenOlder);
    }

    //every row that did not fit is counted in one of two buckets, and both buckets appear in this fixture
    [Fact]
    public void WHAT_DID_NOT_FIT_IS_COUNTED_BY_BUCKET()
    {
        string[] rows =
        [
            "Qwen3.8-new", "Qwen3.6-a", "Qwen3.5-a", "Qwen3-a", "Qwen2.5-a", "Qwen-AgentWorld",
        ];

        var filled = Fill(rows, "3.6", 2);

        Assert.Equal(2, filled.Rows.Count);
        Assert.Equal(1, filled.HiddenNewer);
        Assert.Equal(3, filled.HiddenOlder);
        Assert.Equal(rows.Length, filled.Rows.Count + filled.HiddenNewer + filled.HiddenOlder);
    }

    //a budget of 0 selects nothing, and an empty shelf is not a crash
    [Fact]
    public void AN_EMPTY_SHELF_AND_A_ZERO_BUDGET_ARE_BOTH_ANSWERS()
    {
        Assert.Empty(Fill([], "3.6", 10).Rows);
        Assert.Empty(Fill(["Qwen3.6-a"], "3.6", 0).Rows);
    }
}
