using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Tests;

public class ThinkCapabilityTests
{
    //the Qwen3.6 template names both enable_thinking and a literal think tag, and enable_thinking must win
    private const string QwenBranch =
        "{%- if enable_thinking is defined and enable_thinking is false %}{{- '<think>\\n\\n</think>\\n\\n' }}"
        + "{%- else %}{{- '<think>\\n' }}{%- endif %}";

    [Fact]
    public void Sniff_Qwen_EnableThinkingBranch_IsToggle() =>
        Assert.Equal(ThinkCapability.Toggle, ThinkCapabilitySniff.Of(QwenBranch));

    [Fact]
    public void Sniff_ReasoningEffort_IsLevels() =>
        Assert.Equal(ThinkCapability.Levels,
            ThinkCapabilitySniff.Of("{{- messages }} reasoning_effort applied here"));

    [Fact]
    public void Sniff_BareThinkTag_NoSwitch_IsAlwaysOn() =>
        Assert.Equal(ThinkCapability.AlwaysOn,
            ThinkCapabilitySniff.Of("{{- '<think>\\n' }} always reasons"));

    [Fact]
    public void Sniff_NoReasoningVocab_IsNone() =>
        Assert.Equal(ThinkCapability.None,
            ThinkCapabilitySniff.Of("{%- for m in messages %}{{ m.content }}{%- endfor %}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sniff_MissingTemplate_IsNone(string? tmpl) =>
        Assert.Equal(ThinkCapability.None, ThinkCapabilitySniff.Of(tmpl));

    [Fact]
    public void Sniff_EnableThinkingWinsOverReasoningEffort_WhenBothPresent() =>
        //the sniff is about the template alone, and a model's map overrides it when the map declares two or more on-levels
        Assert.Equal(ThinkCapability.Toggle,
            ThinkCapabilitySniff.Of("enable_thinking ... reasoning_effort"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnableThinkingBody_CarriesChatTemplateKwarg(bool on)
    {
        var body = ThinkCapabilitySniff.EnableThinkingBody(on);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        var kwargs = body.GetProperty("chat_template_kwargs");
        var flag = kwargs.GetProperty("enable_thinking");
        Assert.Equal(on ? JsonValueKind.True : JsonValueKind.False, flag.ValueKind);
        Assert.Equal(on, flag.GetBoolean());
    }
}
