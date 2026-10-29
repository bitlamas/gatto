using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core;
using Gatto.Roles.Models;
using Gatto.Terminal;

namespace Gatto.Cli;

//the /model settings row and the /effort sentence row, read from a model's own map so a row never shows a level a switch would not send
internal static class ModelLevels
{
    internal static readonly IReadOnlyList<string> Toggle = ["none", "on"];

    internal const string NoneSentence = "none: no thinking tokens";
    internal const string OnSentence = "on: the model thinks before each answer";

    //the one shipped phrase, for an entry whose body and suffix both send nothing
    internal const string NothingSent = "nothing sent for this level";

    //the levels a row steps through and the one a switch would land. a map-less model has a toggle only when the server's sniff said so, else no row
    internal static (IReadOnlyList<string>? Levels, string? Default) For(
        IReadOnlyDictionary<string, JsonElement?>? map, ThinkingLevel? saved, bool knownToggle, bool thinkOnByDefault)
    {
        string ToggleDefault() => (saved is { } s ? s != ThinkingLevel.None : thinkOnByDefault) ? "on" : "none";
        try
        {
            if (map is null) return knownToggle ? (Toggle, ToggleDefault()) : (null, null);
            if (Thinking.BinaryOnLevel(map) is not null) return (Toggle, ToggleDefault());
            var levels = Ordered(map);
            if (levels.Count == 0) return (null, null);
            //the composition's own fallback, clamped onto the map as a switch clamps it
            var landed = Thinking.LandedLevel(map, saved ?? ThinkingLevel.Medium) ?? levels[0];
            return ([.. levels.Select(EffortSwitch.Name)], EffortSwitch.Name(landed));
        }
        catch (GattoConfigException) { return (null, null); }   //a map that will not resolve offers no row rather than a wrong one
    }

    //the map's levels in the enum's order, as /effort lists them, so a json file's key order never decides it
    internal static List<ThinkingLevel> Ordered(IReadOnlyDictionary<string, JsonElement?> map)
    {
        var levels = new List<ThinkingLevel>();
        foreach (var key in map.Keys)
        {
            try { levels.Add(Thinking.Parse(key)); }
            catch (GattoConfigException) { }   //a key that is no level is not a row
        }
        return [.. levels.Distinct().Order()];
    }

    //the level a row's name writes for that model's shape, on through its own map's on-level and Max with no map
    internal static ThinkingLevel Resolve(IReadOnlyDictionary<string, JsonElement?>? map, string name)
    {
        var onLevel = map is null ? null : Thinking.BinaryOnLevel(map);
        if (map is null || onLevel is not null)
        {
            var (on, _) = EffortSwitch.OnToggle(name);
            return on ? onLevel ?? ThinkingLevel.Max : ThinkingLevel.None;
        }
        return Thinking.Parse(name);
    }

    //the request a level sends, printed as it goes out: each body property as name and value, then the suffix the loop adds to every message
    internal static string Sentence(JsonElement? body, string? suffix, GlyphSet glyphs)
    {
        var parts = new List<string>();
        if (body is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var p in obj.EnumerateObject())
                parts.Add(p.Name + ": " + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : Compact(p.Value)));
        }
        else if (body is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } other)
            parts.Add(Compact(other));
        if (suffix is not null) parts.Add("prompt_suffix: " + suffix);
        return parts.Count == 0 ? NothingSent : string.Join($" {glyphs.Dot} ", parts);
    }

    private static string Compact(JsonElement e) => JsonNode.Parse(e.GetRawText())?.ToJsonString() ?? "null";
}
