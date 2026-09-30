using System.Text.Json;

namespace Gatto.Core.Client;

//the reasoning switch a model's chat template exposes, sniffed from /props.chat_template as the fallback when the model has no thinking map
public enum ThinkCapability
{
    //the template names no reasoning switch
    None,
    //the template branches on enable_thinking, /effort none|on sends it per request even for a model with a map
    Toggle,
    //the template takes a reasoning_effort kwarg, so the /effort level pipeline is the switch
    Levels,
    //the generation prompt emits <think> with no switch to turn it off
    AlwaysOn,
}

public static class ThinkCapabilitySniff
{
    //derive the capability from the template alone, and in this order: enable_thinking wins over reasoning_effort, then <think>, then none
    public static ThinkCapability Of(string? chatTemplate)
    {
        if (string.IsNullOrEmpty(chatTemplate)) return ThinkCapability.None;
        if (chatTemplate.Contains("enable_thinking", StringComparison.Ordinal)) return ThinkCapability.Toggle;
        if (chatTemplate.Contains("reasoning_effort", StringComparison.Ordinal)) return ThinkCapability.Levels;
        if (chatTemplate.Contains("<think>", StringComparison.Ordinal)) return ThinkCapability.AlwaysOn;
        return ThinkCapability.None;
    }

    //the body a Toggle model needs to turn its reasoning on or off, sent as a body override per request
    public static JsonElement EnableThinkingBody(bool on)
    {
        using var doc = JsonDocument.Parse(
            $$"""{ "chat_template_kwargs": { "enable_thinking": {{(on ? "true" : "false")}} } }""");
        return doc.RootElement.Clone();
    }
}
