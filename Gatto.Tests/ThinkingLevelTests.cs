using Gatto.Core.Client;
using Gatto.Roles;
using System.Text.Json;

namespace Gatto.Tests;

public class ThinkingLevelTests
{
    private static IReadOnlyDictionary<string, JsonElement?> Map(params (string k, string? json)[] entries) =>
        entries.ToDictionary(e => e.k, e => e.json is null ? (JsonElement?)null : JsonDocument.Parse(e.json).RootElement.Clone());

    [Theory]
    [InlineData("none", ThinkingLevel.None)]
    [InlineData("XHIGH", ThinkingLevel.XHigh)]
    [InlineData("max", ThinkingLevel.Max)]
    [InlineData(null, ThinkingLevel.None)]
    public void Parse_Works(string? s, ThinkingLevel want) => Assert.Equal(want, Thinking.Parse(s));

    [Fact]
    public void Parse_Invalid_FriendlyError()
    {
        var ex = Assert.Throws<Gatto.Core.Home.GattoConfigException>(() => Thinking.Parse("turbo"));
        Assert.Contains("xhigh", ex.Message);   //the message lists the valid values
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        //an empty string is a config mistake and must throw like any other unrecognized value (null means None)
        var ex = Assert.Throws<Gatto.Core.Home.GattoConfigException>(() => Thinking.Parse(""));
        Assert.Contains("xhigh", ex.Message);
    }

    [Fact]
    public void Min_Orders() => Assert.Equal(ThinkingLevel.Low, Thinking.Min(ThinkingLevel.Max, ThinkingLevel.Low));

    //one non-none entry makes the map a binary toggle and gives back that level. more than one on-level, none at all, or no map gives null.
    [Fact]
    public void BinaryOnLevel_Mistral_none_high_is_High()
    {
        var map = Map(("none", """{"chat_template_kwargs":{"reasoning_effort":"none"}}"""),
                      ("high", """{"chat_template_kwargs":{"reasoning_effort":"high"}}"""));
        Assert.Equal(ThinkingLevel.High, Thinking.BinaryOnLevel(map));
    }

    [Fact]
    public void BinaryOnLevel_Qwen_none_low_is_Low()
    {
        var map = Map(("none", """{"chat_template_kwargs":{"enable_thinking":false}}"""),
                      ("low",  "{}"));
        Assert.Equal(ThinkingLevel.Low, Thinking.BinaryOnLevel(map));
    }

    [Fact]
    public void BinaryOnLevel_MultiLevel_gptoss_is_null()   //a genuine /effort model
    {
        var map = Map(("none", "{}"), ("low", "{}"), ("medium", "{}"), ("high", "{}"));
        Assert.Null(Thinking.BinaryOnLevel(map));
    }

    [Fact]
    public void BinaryOnLevel_OnlyNone_or_NoMap_is_null()
    {
        Assert.Null(Thinking.BinaryOnLevel(Map(("none", "{}"))));   //no on-level
        Assert.Null(Thinking.BinaryOnLevel(null));                  //no map
    }

    [Fact]
    public void BinaryOnLevel_SingleOnLevel_withoutExplicitNone_is_null()
    {
        //a map with no none entry is not binary, toggling off would send no body and the server default is usually on
        Assert.Null(Thinking.BinaryOnLevel(Map(("high", "{}"))));
    }

    //the model's map decides the capability, and the template sniff only covers no map or a degenerate one
    [Fact]
    public void CapabilityOf_MultiLevelMap_OverridesToggleSniff_IsLevels()
    {
        var map = Map(("none", "{}"), ("low", "{}"), ("medium", "{}"), ("high", "{}"));
        Assert.Equal(ThinkCapability.Levels, Thinking.CapabilityOf(map, ThinkCapability.Toggle));
    }

    [Fact]
    public void CapabilityOf_BinaryMap_OverridesLevelsSniff_IsToggle()
    {
        //a binary map overrides a Levels sniff
        var map = Map(("none", "{}"), ("high", "{}"));
        Assert.Equal(ThinkCapability.Toggle, Thinking.CapabilityOf(map, ThinkCapability.Levels));
    }

    [Theory]
    [InlineData(ThinkCapability.None)]
    [InlineData(ThinkCapability.Toggle)]
    [InlineData(ThinkCapability.Levels)]
    [InlineData(ThinkCapability.AlwaysOn)]
    public void CapabilityOf_NoMap_IsWhateverWasSniffed(ThinkCapability sniffed) =>
        Assert.Equal(sniffed, Thinking.CapabilityOf(null, sniffed));

    [Fact]
    public void CapabilityOf_MultiLevelMap_OverridesFailedProbe_IsLevels()
    {
        //a failed probe sniffs None, and a map with three on-levels still wins
        var map = Map(("none", "{}"), ("low", "{}"), ("medium", "{}"), ("xhigh", "{}"));
        Assert.Equal(ThinkCapability.Levels, Thinking.CapabilityOf(map, ThinkCapability.None));
    }

    //a map with one on-level and no none entry, or with no on-levels at all, falls to the sniff
    [Fact]
    public void CapabilityOf_DegenerateMap_SingleOnLevelWithoutNone_FallsToSniff()
    {
        Assert.Equal(ThinkCapability.Levels, Thinking.CapabilityOf(Map(("high", "{}")), ThinkCapability.Levels));
    }

    [Fact]
    public void CapabilityOf_EmptyMap_FallsToSniff() =>
        Assert.Equal(ThinkCapability.Toggle,
            Thinking.CapabilityOf(new Dictionary<string, JsonElement?>(), ThinkCapability.Toggle));

    //the on/off request body for a binary reasoning toggle, with the empty-on-body guard
    [Fact]
    public void ToggleBody_Mistral_on_sends_reasoning_effort_high()
    {
        var map = Map(("none", """{"chat_template_kwargs":{"reasoning_effort":"none"}}"""),
                      ("high", """{"chat_template_kwargs":{"reasoning_effort":"high"}}"""));
        var (body, _) = Thinking.ToggleBody(map, ThinkingLevel.High, on: true);
        Assert.Equal("high", body!.Value.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString());
        var (off, _) = Thinking.ToggleBody(map, ThinkingLevel.High, on: false);
        Assert.Equal("none", off!.Value.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ToggleBody_EmptyOnEntry_falls_back_to_explicit_enable_thinking_true()
    {
        //an empty on-body must become an explicit enable_thinking true so on never leans on the server default
        var map = Map(("none", """{"chat_template_kwargs":{"enable_thinking":false}}"""), ("low", "{}"));
        var (on, _) = Thinking.ToggleBody(map, ThinkingLevel.Low, on: true);
        Assert.True(on!.Value.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        var (off, _) = Thinking.ToggleBody(map, ThinkingLevel.Low, on: false);
        Assert.False(off!.Value.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ResolveMap_FallsBackDown_OnOffModel()
    {
        //in the on/off shape high and max resolve to the low entry
        var map = Map(("none", """{"chat_template_kwargs":{"enable_thinking":false}}"""),
                      ("low",  """{"chat_template_kwargs":{"enable_thinking":true}}"""));
        var resolved = Thinking.ResolveMap(map, ThinkingLevel.Max);
        Assert.True(resolved!.Value.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ResolveMap_ExplicitNull_SendsNothing()
    {
        var map = Map(("medium", null));
        Assert.Null(Thinking.ResolveMap(map, ThinkingLevel.Medium));
        Assert.Null(Thinking.ResolveMap(map, ThinkingLevel.High));   //falls down to the null entry
    }

    [Fact]
    public void ResolveMap_NoMap_Null() => Assert.Null(Thinking.ResolveMap(null, ThinkingLevel.High));

    [Fact]
    public void ResolveMap_CaseVariantDuplicateKeys_Throws()
    {
        //keys that differ only in case are the same level, so a duplicate must throw
        var map = Map(("Low", """{"a":1}"""), ("low", """{"a":2}"""));
        var ex = Assert.Throws<Gatto.Core.Home.GattoConfigException>(() => Thinking.ResolveMap(map, ThinkingLevel.Low));
        Assert.Contains("more than once", ex.Message);
    }

    //the level a request really stopped at, so setEffort can report that instead of the requested one
    [Fact]
    public void LandedLevel_FallsBackDown_ReturnsTheStoppingLevel()
    {
        var map = Map(("none", "{}"), ("medium", "{}"));
        Assert.Equal(ThinkingLevel.Medium, Thinking.LandedLevel(map, ThinkingLevel.High));
        Assert.Equal(ThinkingLevel.None, Thinking.LandedLevel(map, ThinkingLevel.Low));
    }

    [Fact]
    public void LandedLevel_RequestedLevelPresent_IsUnmoved()
    {
        var map = Map(("none", "{}"), ("medium", "{}"), ("xhigh", "{}"));
        Assert.Equal(ThinkingLevel.Medium, Thinking.LandedLevel(map, ThinkingLevel.Medium));
    }

    [Fact]
    public void LandedLevel_ExplicitNullEntry_StillLandsOnItsLevel()
    {
        //an entry whose value is an explicit null still names its level, ResolveMap can't tell that case from a missing entry
        var map = Map(("medium", null));
        Assert.Equal(ThinkingLevel.Medium, Thinking.LandedLevel(map, ThinkingLevel.High));
    }

    [Fact]
    public void LandedLevel_NothingAtOrBelowTheRequest_IsNull()
    {
        var map = Map(("high", "{}"));   //no entry at or below Medium
        Assert.Null(Thinking.LandedLevel(map, ThinkingLevel.Medium));
    }

    [Fact]
    public void LandedLevel_NoMap_IsNull() => Assert.Null(Thinking.LandedLevel(null, ThinkingLevel.High));

    [Fact]
    public void LandedLevel_EmptyMap_IsNull() =>
        Assert.Null(Thinking.LandedLevel(new Dictionary<string, JsonElement?>(), ThinkingLevel.High));

    //the prompt_suffix form of ResolveEntry

    [Fact]
    public void ResolveEntry_SplitsSuffixFromBody_SameFallbackDown()
    {
        //the fallback is the same as ResolveMap, and the low entry also holds a prompt_suffix
        var map = Map(("none", """{"chat_template_kwargs":{"enable_thinking":false}}"""),
                      ("low",  """{"chat_template_kwargs":{"enable_thinking":true},"prompt_suffix":"/think"}"""));

        var (body, suffix) = Thinking.ResolveEntry(map, ThinkingLevel.Max);   //falls down to the low entry

        Assert.Equal("/think", suffix);
        Assert.NotNull(body);
        Assert.True(body!.Value.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.False(body.Value.TryGetProperty("prompt_suffix", out _));   //the body no longer holds the prompt_suffix
    }

    [Fact]
    public void ResolveEntry_BodyBecomesEmptyAfterRemoval_IsNull()
    {
        //an entry with only a prompt_suffix gives a null body once the suffix is split out
        var map = Map(("low", """{"prompt_suffix":"/no_think"}"""));

        var (body, suffix) = Thinking.ResolveEntry(map, ThinkingLevel.Low);

        Assert.Null(body);
        Assert.Equal("/no_think", suffix);
    }

    [Fact]
    public void ResolveEntry_NoSuffixKey_BodyUnchangedAndSuffixNull()
    {
        var map = Map(("high", """{ "reasoning_effort": "high" }"""));

        var (body, suffix) = Thinking.ResolveEntry(map, ThinkingLevel.High);

        Assert.Null(suffix);
        Assert.Equal("high", body!.Value.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ResolveEntry_NoMap_NullBodyNullSuffix()
    {
        var (body, suffix) = Thinking.ResolveEntry(null, ThinkingLevel.High);
        Assert.Null(body);
        Assert.Null(suffix);
    }

    [Fact]
    public void ResolveEntry_ExplicitNullEntry_NullBodyNullSuffix()
    {
        var map = Map(("medium", null));
        var (body, suffix) = Thinking.ResolveEntry(map, ThinkingLevel.Medium);
        Assert.Null(body);
        Assert.Null(suffix);
    }
}
