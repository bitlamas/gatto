using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Core.Loop;

//the four groups /context draws the window in
public enum ContextGroup { Prefix, Messages, Reasoning, ToolResults }

//one part of a request with the text the server would read for it, the detail drawn after the label behind the glyph set's dot
public sealed record ContextPartText(string Label, ContextGroup Group, string Text, string? Detail = null);

//one part with its count, exact from the server's tokenizer or estimated from its characters
public sealed record ContextPart(string Label, ContextGroup Group, int Tokens, string? Detail = null);

//the last request's prompt split into what the server reused and what it read fresh
public sealed record ContextCache(int FromCache, int Fresh);

//everything /context draws: fell back means an exact count was tried and a server call failed, before first request that nothing was sent yet
public sealed record ContextFigures(
    int? Window, int Total, bool Exact, bool FellBack, bool BeforeFirstRequest, double? AutoCompactAt,
    IReadOnlyList<ContextPart> Parts, ContextCache? Cache);

//llama-server's two counting routes, the exact path of /context
public interface ITokenCounter
{
    Task<int?> CountInputTokensAsync(ChatRequest request, CancellationToken ct);
    Task<int?> TokenizeCountAsync(string text, CancellationToken ct);
}

//what the figures are built from: the composed prefix, the tools by source, the request last sent and the usage the loop measured
public sealed record ContextInputs(
    string Model, string SystemText, IReadOnlyList<ContextPartText> SystemParts,
    IReadOnlyList<ToolSpec> BuiltinTools, IReadOnlyList<ToolSpec> ExtensionTools,
    RequestShape Shape, int? Window, double? AutoCompactAt, double Ratio, int? LastPromptTokens, string? LastTimings,
    Usage? LastUsage = null);

public static class ContextCount
{
    //the label of what the template adds around the parts, only the exact path can know it
    public const string TemplateMarkup = "template markup";

    //exact through the counter when there is one and every call answers, else the estimate. it reads only its inputs, never the live conversation
    public static async Task<ContextFigures> BuildAsync(ContextInputs inputs, ITokenCounter? counter, CancellationToken ct)
    {
        var parts = Parts(inputs);
        var before = inputs.Shape.LastSent is null;
        var cache = Cache(inputs.LastTimings) ?? Cache(inputs.LastUsage);

        if (counter is not null)
        {
            var messages = inputs.Shape.LastSent ?? (IReadOnlyList<ChatMessage>)[new ChatMessage("system", inputs.SystemText)];
            var request = new ChatRequest(inputs.Model, messages, inputs.Shape.Tools,
                inputs.Shape.SamplingOverrides, inputs.Shape.BodyOverrides);
            if (await counter.CountInputTokensAsync(request, ct) is int total)
            {
                var counted = new List<ContextPart>();
                foreach (var p in parts)
                {
                    if (p.Text.Length == 0) { counted.Add(new ContextPart(p.Label, p.Group, 0, p.Detail)); continue; }
                    if (await counter.TokenizeCountAsync(p.Text, ct) is not int n) { counted = null; break; }
                    counted.Add(new ContextPart(p.Label, p.Group, n, p.Detail));
                }
                if (counted is not null)
                {
                    var markup = Math.Max(0, total - counted.Sum(c => c.Tokens));
                    counted.Insert(PrefixEnd(counted), new ContextPart(TemplateMarkup, ContextGroup.Prefix, markup));
                    return new ContextFigures(inputs.Window, total, true, false, before, inputs.AutoCompactAt, counted, cache);
                }
            }
        }

        //chars over four scaled by the server's ratio, and the total is the server's own reading when there is one
        var estimated = parts.Select(p => new ContextPart(p.Label, p.Group,
            (int)Math.Round((p.Text.Length / 4.0) * inputs.Ratio), p.Detail)).ToList();
        var sum = estimated.Sum(e => e.Tokens);
        var totalEstimate = before ? sum : inputs.LastPromptTokens ?? sum;
        return new ContextFigures(inputs.Window, totalEstimate, false, counter is not null, before, inputs.AutoCompactAt,
            estimated, cache);
    }

    //the prefix part by part, then the conversation as last sent by role, reasoning apart and tool results with their count
    internal static List<ContextPartText> Parts(ContextInputs inputs)
    {
        var parts = new List<ContextPartText>(inputs.SystemParts);
        parts.Add(new ContextPartText("tools", ContextGroup.Prefix, SpecsJson(inputs.BuiltinTools), $"{inputs.BuiltinTools.Count} built-in"));
        if (inputs.ExtensionTools.Count > 0)
            parts.Add(new ContextPartText("tools", ContextGroup.Prefix, SpecsJson(inputs.ExtensionTools), $"{inputs.ExtensionTools.Count} extensions"));
        if (inputs.Shape.LastSent is not { } sent) return parts;

        var chat = sent.Where(m => m.Role != "system").ToList();
        var tools = chat.Where(m => m.Role == "tool").ToList();
        parts.Add(new ContextPartText("user", ContextGroup.Messages, Join(chat.Where(m => m.Role == "user").Select(m => m.Content))));
        parts.Add(new ContextPartText("assistant", ContextGroup.Messages, Join(chat.Where(m => m.Role == "assistant")
            .SelectMany(m => new[] { m.Content }.Concat(m.ToolCalls?.Select(c => c.Name + c.ArgumentsJson) ?? [])))));
        parts.Add(new ContextPartText("reasoning", ContextGroup.Reasoning, Join(chat.Select(m => m.ReasoningContent))));
        parts.Add(new ContextPartText("tool results", ContextGroup.ToolResults, Join(tools.Select(m => m.Content)), $"{tools.Count}"));
        return parts;
    }

    private static int PrefixEnd(List<ContextPart> parts)
    {
        var i = parts.FindIndex(p => p.Group != ContextGroup.Prefix);
        return i < 0 ? parts.Count : i;
    }

    private static string Join(IEnumerable<string?> texts) => string.Join("\n", texts.Where(t => !string.IsNullOrEmpty(t)));

    private static string SpecsJson(IReadOnlyList<ToolSpec> specs) =>
        specs.Count == 0 ? "" : JsonSerializer.Serialize(specs.Select(s => new { s.Name, s.Description, Parameters = s.ParametersSchema }));

    //a cloud server's usage names the cached part of the prompt, and the rest of the prompt was read fresh
    private static ContextCache? Cache(Usage? usage) =>
        usage is { CachedTokens: int cached } ? new ContextCache(cached, Math.Max(0, usage.PromptTokens - cached)) : null;

    //llama-server's timings carry cache_n and prompt_n, a server that reports neither gets no cache line
    private static ContextCache? Cache(string? timings)
    {
        if (timings is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(timings);
            var root = doc.RootElement;
            return root.TryGetProperty("cache_n", out var c) && c.TryGetInt32(out var cached)
                && root.TryGetProperty("prompt_n", out var p) && p.TryGetInt32(out var fresh)
                ? new ContextCache(cached, fresh) : null;
        }
        catch (JsonException) { return null; }
    }
}
