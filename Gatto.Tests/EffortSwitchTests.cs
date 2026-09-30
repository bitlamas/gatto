using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

public class EffortSwitchTests
{
    [Fact]
    public void Uncapped_ResolvesVerbatim()
    {
        var (eff, name, msg) = EffortSwitch.Resolve("xhigh", ThinkingLevel.Max, null);
        Assert.Equal(ThinkingLevel.XHigh, eff);
        Assert.Equal("xhigh", name);
        Assert.Equal("effort: xhigh", msg);
    }

    [Fact]
    public void CapBites_AndSaysSo()
    {
        var (eff, name, msg) = EffortSwitch.Resolve("max", ThinkingLevel.High, "qwen3.6-35b");
        Assert.Equal(ThinkingLevel.High, eff);
        Assert.Equal("high", name);
        Assert.Equal("effort: high — requested max, capped by model 'qwen3.6-35b'", msg);
    }

    [Fact]
    public void JunkLevel_Throws() =>
        Assert.Throws<GattoConfigException>(() => EffortSwitch.Resolve("turbo", ThinkingLevel.Max, null));

    //these cases cover /effort on a Toggle-only model, which has no thinking levels
    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("off")]     //off is accepted as none here, so the case must never throw
    [InlineData("OFF")]
    [InlineData(" off ")]
    public void OnToggle_Off_ReportsNone(string level)
    {
        var (on, name) = EffortSwitch.OnToggle(level);
        Assert.False(on);
        Assert.Equal("none", name);          //the reported name is the picker's word none, whatever spelling the user typed
    }

    [Theory]
    [InlineData("high")]
    [InlineData("medium")]
    [InlineData("max")]
    public void OnToggle_RealLevel_ReportsOn(string level)
    {
        //any real level turns reasoning on and is never refused.
        var (on, name) = EffortSwitch.OnToggle(level);
        Assert.True(on);
        Assert.Equal("on", name);
    }

    //on is the picker's own word, so it must not reach Thinking.Parse and throw
    [Theory]
    [InlineData("on")]
    [InlineData("On")]
    [InlineData("ON")]
    [InlineData(" on ")]
    public void OnToggle_On_NeverThrows_AndReportsOn(string level)
    {
        var (on, name) = EffortSwitch.OnToggle(level);
        Assert.True(on);
        Assert.Equal("on", name);
    }

    [Fact]
    public void OnToggle_Junk_Throws() =>
        Assert.Throws<GattoConfigException>(() => EffortSwitch.OnToggle("turbo"));
}
