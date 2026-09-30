using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core.Client;
using Gatto.Core.Home;

namespace Gatto.Roles;

public enum ThinkingLevel { None = 0, Low = 1, Medium = 2, High = 3, XHigh = 4, Max = 5 }

public static class Thinking
{
    public static ThinkingLevel Parse(string? s)
    {
        //null means None. an empty or blank string is a config mistake and throws with the other bad values
        if (s is null) return ThinkingLevel.None;

        return s.ToLowerInvariant() switch
        {
            "none" => ThinkingLevel.None,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            "max" => ThinkingLevel.Max,
            _ => throw new GattoConfigException(
                $"invalid thinking level '{s}' — valid values are: none, low, medium, high, xhigh, max"),
        };
    }

    public static ThinkingLevel Min(ThinkingLevel a, ThinkingLevel b) => a < b ? a : b;

    //the single on-level of a binary thinking map, null when it's missing, multi-level, or has no on-level (bad keys are skipped, ResolveMap reports them)
    public static ThinkingLevel? BinaryOnLevel(IReadOnlyDictionary<string, JsonElement?>? map)
    {
        if (map is null) return null;
        var hasNone = false;
        ThinkingLevel? only = null;
        foreach (var key in map.Keys)
        {
            ThinkingLevel level;
            try { level = Parse(key); } catch (GattoConfigException) { continue; }
            if (level == ThinkingLevel.None) { hasNone = true; continue; }
            if (only is not null) return null;
            only = level;
        }
        //binary means {none, X}, so the none entry is required too. a map with only an on-level toggles off to the server default (usually on) and the footer lies
        return hasNone ? only : null;
    }

    //how many on-levels a map declares, skipping bad keys like BinaryOnLevel does. only CapabilityOf asks, so it stays private
    private static int OnLevelCount(IReadOnlyDictionary<string, JsonElement?> map)
    {
        var count = 0;
        foreach (var key in map.Keys)
        {
            ThinkingLevel level;
            try { level = Parse(key); } catch (GattoConfigException) { continue; }
            if (level != ThinkingLevel.None) count++;
        }
        return count;
    }

    //the map decides the reasoning capability when it's there, one on-level plus none is a Toggle and two or more is Levels
    public static ThinkCapability CapabilityOf(IReadOnlyDictionary<string, JsonElement?>? map, ThinkCapability sniffed)
    {
        if (map is null) return sniffed;
        if (BinaryOnLevel(map) is not null) return ThinkCapability.Toggle;
        return OnLevelCount(map) >= 2 ? ThinkCapability.Levels : sniffed;
    }

    //an ON body that resolves to empty falls back to enable_thinking:true (a flipped server default would give a lying footer with an empty request)
    public static (JsonElement? Body, string? Suffix) ToggleBody(
        IReadOnlyDictionary<string, JsonElement?>? map, ThinkingLevel onLevel, bool on)
    {
        var (body, suffix) = ResolveEntry(map, on ? onLevel : ThinkingLevel.None);
        if (on && IsEmptyBody(body)) body = ThinkCapabilitySniff.EnableThinkingBody(true);
        return (body, suffix);
    }

    private static bool IsEmptyBody(JsonElement? b) =>
        b is not { } el || (el.ValueKind == JsonValueKind.Object && !el.EnumerateObject().Any());

    public static JsonElement? ResolveMap(IReadOnlyDictionary<string, JsonElement?>? map, ThinkingLevel level) =>
        TryResolveLevel(map, level, out _, out var value) ? value : null;

    //the level whose entry actually got used, since the loop can stop below the one asked for. report this, null is for the caller to read
    public static ThinkingLevel? LandedLevel(IReadOnlyDictionary<string, JsonElement?>? map, ThinkingLevel level) =>
        TryResolveLevel(map, level, out var landed, out _) ? landed : null;

    private static bool TryResolveLevel(
        IReadOnlyDictionary<string, JsonElement?>? map, ThinkingLevel level,
        out ThinkingLevel landed, out JsonElement? value)
    {
        landed = level;
        value = null;
        if (map is null || map.Count == 0) return false;

        var sorted = new SortedDictionary<ThinkingLevel, JsonElement?>();
        foreach (var (k, v) in map)
        {
            var parsed = Parse(k);
            if (!sorted.TryAdd(parsed, v))
                throw new GattoConfigException($"thinking map defines level '{k}' more than once");
        }

        for (var l = level; l >= ThinkingLevel.None; l--)
        {
            if (sorted.TryGetValue(l, out value)) { landed = l; return true; }
        }

        return false;
    }

    //a map entry may have a prompt_suffix key, a snippet appended to the user message. it's split out here, and a body of only that becomes null
    public static (JsonElement? Body, string? Suffix) ResolveEntry(
        IReadOnlyDictionary<string, JsonElement?>? map, ThinkingLevel level)
    {
        var resolved = ResolveMap(map, level);
        if (resolved is not { ValueKind: JsonValueKind.Object } body)
            return (resolved, null);   //no object, nothing to split

        if (!body.TryGetProperty("prompt_suffix", out var suffixEl))
            return (body, null);

        if (suffixEl.ValueKind != JsonValueKind.String)
            throw new GattoConfigException("thinking map 'prompt_suffix' must be a string");
        var suffix = suffixEl.GetString();

        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("prompt_suffix");
        if (node.Count == 0) return (null, suffix);

        using var doc = JsonDocument.Parse(node.ToJsonString());
        return (doc.RootElement.Clone(), suffix);
    }
}
