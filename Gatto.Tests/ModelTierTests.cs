using Gatto.Core.Acquire;

namespace Gatto.Tests;

//the repo names come from the approved fixture, and an invented one is here to break a rule rather than to pad coverage
public class ModelTierTests
{
    //each matched version is its own tier, and the label keeps the written text (a minor the publisher did not write invents precision)
    [Theory]
    [InlineData("unsloth/Qwen3.6-35B-A3B", "3.6", 3, 6)]
    [InlineData("unsloth/Qwen3.5-35B-A3B-MTP", "3.5", 3, 5)]
    [InlineData("unsloth/Qwen3-32B", "3", 3, 0)]
    [InlineData("unsloth/Qwen2.5-7B-Instruct", "2.5", 2, 5)]
    [InlineData("unsloth/Qwen3-Coder-30B-A3B-Instruct", "3", 3, 0)]
    public void A_VERSION_IN_THE_NAME_IS_THE_TIER(string repo, string label, int major, int minor)
    {
        var t = QwenTier.Of(repo);
        Assert.Equal(label, t.Label);
        Assert.Equal(major, t.Major);
        Assert.Equal(minor, t.Minor);
        Assert.True(t.IsVersioned);
    }

    //a -Next suffix must not promote the model, a suffix table would break as the publisher markets a new one
    [Theory]
    [InlineData("unsloth/Qwen3-Next-80B-A3B-Instruct")]
    [InlineData("unsloth/Qwen3-Next-80B-A3B-Thinking")]
    [InlineData("unsloth/Qwen3-Coder-Next")]
    public void NEXT_IS_NOT_A_PROMOTION(string repo)
    {
        var t = QwenTier.Of(repo);
        Assert.Equal("3", t.Label);
        Assert.Equal(3, t.Major);
        Assert.Equal(0, t.Minor);
    }

    //a name with no version is unversioned and never current, guessing from siblings would sort a model by them rather than itself
    [Theory]
    [InlineData("unsloth/Qwen-AgentWorld-35B-A3B")]
    [InlineData("unsloth/QwenLong-L1-32B")]
    [InlineData("google/gemma-4-26B-A4B-it")]
    [InlineData("")]
    [InlineData(null)]
    public void A_NAME_WITH_NO_VERSION_IS_UNVERSIONED(string? repo)
    {
        var t = QwenTier.Of(repo);
        Assert.Equal(ModelTier.Unversioned, t);
        Assert.False(t.IsVersioned);
    }

    //3.10 is the tenth minor, so a decimal reading of the labels would sort it below 3.6
    [Fact]
    public void A_TENTH_MINOR_SORTS_ABOVE_A_SIXTH_NOT_BELOW_IT()
    {
        var ten = QwenTier.Of("unsloth/Qwen3.10-35B-A3B");
        var six = QwenTier.Of("unsloth/Qwen3.6-35B-A3B");

        Assert.Equal("3.10", ten.Label);
        Assert.Equal(10, ten.Minor);
        Assert.True(ModelTier.Compare(ten, six) < 0, "3.10 must sort NEWER than 3.6");

        //the mirrored comparison pins the other direction, which a decimal reading would flip
        Assert.True(ModelTier.Compare(six, ten) > 0);
    }

    //newest first, with unversioned below every versioned tier, even one from an old major
    [Fact]
    public void TIERS_SORT_NEWEST_FIRST_WITH_UNVERSIONED_LAST()
    {
        var sorted = new[]
            {
                QwenTier.Of("x/Qwen-AgentWorld-35B"), QwenTier.Of("x/Qwen2.5-7B"),
                QwenTier.Of("x/Qwen3.6-35B"), QwenTier.Of("x/Qwen3-32B"), QwenTier.Of("x/Qwen3.5-35B"),
            }
            .Order(Comparer<ModelTier>.Create(ModelTier.Compare))
            .Select(t => t.Label)
            .ToList();

        Assert.Equal(["3.6", "3.5", "3", "2.5", "unversioned"], sorted);
    }

    //only a tier-pinned family answers a tier question, an arch-keyed family returns null rather than an empty answer
    [Fact]
    public void ONLY_A_TIERED_FAMILY_ANSWERS_A_TIER()
    {
        var f = Families.Load();

        Assert.Equal("3.6", f.TierOf("qwen", "unsloth/Qwen3.6-35B-A3B")?.Label);
        Assert.Null(f.TierOf("gemma", "unsloth/gemma-4-26B-A4B-it"));
        Assert.Null(f.TierOf("mistral", "unsloth/Mistral-Small-3.2-24B"));
    }

    //current means the tier the file pins, and a versioned tier that is not the pin must answer false too
    [Fact]
    public void CURRENT_IS_THE_DATED_PIN_AND_UNVERSIONED_IS_NEVER_IT()
    {
        var f = Families.Load();

        Assert.True(f.IsCurrent("qwen", QwenTier.Of("x/Qwen3.8-35B")));
        Assert.False(f.IsCurrent("qwen", QwenTier.Of("x/Qwen3.6-35B")));
        Assert.False(f.IsCurrent("qwen", QwenTier.Of("x/Qwen3-32B")));
        Assert.False(f.IsCurrent("qwen", ModelTier.Unversioned));

        //a family with no pin has no current tier at all
        Assert.False(f.IsCurrent("gemma", QwenTier.Of("x/Qwen3.6-35B")));
    }

    //a pin that does not parse becomes unversioned, and without the IsVersioned clause a demoted tier reads as current
    [Fact]
    public void A_PIN_THAT_DOES_NOT_PARSE_MAKES_NOTHING_CURRENT()
    {
        var broken = new Families("2026-08-30", "b10076", ["qwen", "all"],
            new Dictionary<string, IReadOnlyDictionary<string, ArchRole>>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["qwen"] = "latest" },
            new Dictionary<string, string>());

        Assert.False(broken.IsCurrent("qwen", ModelTier.Unversioned),
            "a pin that parses to `unversioned` must not promote the tier the ruling demoted");

        //a broken pin selects nothing, every tier answers false
        Assert.False(broken.IsCurrent("qwen", QwenTier.Of("x/Qwen3.6-35B")));
    }

    //the publisher prefix is dropped, and the parse is anchored at the last segment, so a name merely containing Qwen3.6 does not match
    [Fact]
    public void THE_PARSE_IS_ANCHORED_AT_THE_LAST_SEGMENTS_START()
    {
        Assert.Equal("3.6", QwenTier.Of("anyone/Qwen3.6-35B-A3B").Label);
        Assert.Equal("3.6", QwenTier.Of("Qwen3.6-35B-A3B").Label);
        Assert.Equal(ModelTier.Unversioned, QwenTier.Of("bartowski/Something-Qwen3.6-merge"));
        Assert.Equal(ModelTier.Unversioned, QwenTier.Of("Qwen3.6-35B/nested-Qwen2.5"));
    }
}
