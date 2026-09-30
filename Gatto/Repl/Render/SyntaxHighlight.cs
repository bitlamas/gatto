using System.Text.Json;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

public readonly record struct RoleSpan(int Start, int Length, SpanRole Role);

//one home for which code is highlighted: the language names, the lexers behind them and the cache in front of them
public static class SyntaxHighlight
{
    private const int CacheLimit = 256;
    private static readonly Dictionary<(CodeLanguage, string), SpanRole[]> Cache = new();
    private static readonly object CacheGate = new();

    public static CodeLanguage LanguageOfTag(string? tag) => (tag ?? "").Trim().ToLowerInvariant() switch
    {
        "csharp" or "cs" or "c#" or "csx" => CodeLanguage.CSharp,
        "powershell" or "pwsh" or "ps1" or "ps" or "posh" => CodeLanguage.PowerShell,
        var other => FallbackLanguages.OfTag(other),
    };

    //the first word of a fence's info string names its language, so a title after it changes nothing
    public static CodeLanguage LanguageOfFence(string openingLine)
    {
        var info = openingLine.Trim();
        if (!info.StartsWith("```", StringComparison.Ordinal)) return CodeLanguage.None;
        info = info[3..].Trim();
        var space = info.IndexOfAny([' ', '\t']);
        return LanguageOfTag(space < 0 ? info : info[..space]);
    }

    public static CodeLanguage LanguageOfPath(string? path)
    {
        var text = path ?? "";
        var dot = text.LastIndexOf('.');
        return (dot < 0 ? "" : text[dot..].ToLowerInvariant()) switch
        {
            ".cs" or ".csx" => CodeLanguage.CSharp,
            ".ps1" or ".psm1" or ".psd1" => CodeLanguage.PowerShell,
            var other => FallbackLanguages.OfExtension(other),
        };
    }

    //only a write or an edit previews file text, and the language comes from the path the call names
    public static CodeLanguage LanguageOfToolCall(string tool, string argumentsJson)
    {
        if (tool is not ("write_file" or "edit_file")) return CodeLanguage.None;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson.Length > 0 ? argumentsJson : "{}");
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? LanguageOfPath(path.GetString()) : CodeLanguage.None;
        }
        catch (JsonException) { return CodeLanguage.None; }
    }

    //one role per UTF-16 char of the text, the array is cached and shared so callers must not write to it
    public static SpanRole[] Roles(CodeLanguage language, string text)
    {
        if (language == CodeLanguage.None || text.Length == 0) return new SpanRole[text.Length];
        lock (CacheGate)
            if (Cache.TryGetValue((language, text), out var hit)) return hit;

        var roles = new SpanRole[text.Length];
        try
        {
            var spans = language switch
            {
                CodeLanguage.CSharp => CSharpClassifier.Classify(text),
                CodeLanguage.PowerShell => PowerShellLexer.Classify(text),
                _ => FallbackLanguages.Of(language) is { } entry ? FallbackScanner.Classify(entry, text) : Array.Empty<RoleSpan>(),
            };
            foreach (var span in spans)
                for (var i = Math.Max(0, span.Start); i < Math.Min(text.Length, span.Start + span.Length); i++)
                    roles[i] = span.Role;
        }
        catch (Exception) { Array.Clear(roles); }   //a lexer defect shows plain code instead of taking the renderer down

        lock (CacheGate)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[(language, text)] = roles;
        }
        return roles;
    }

    //the runs of one role inside text from start for length chars. a char past the end of roles counts as no role
    public static List<(string Text, SpanRole Role)> Runs(string text, IReadOnlyList<SpanRole> roles, int start, int length)
    {
        SpanRole RoleAt(int i) => i < roles.Count ? roles[i] : SpanRole.None;
        var runs = new List<(string Text, SpanRole Role)>();
        var end = Math.Min(text.Length, start + length);
        var at = Math.Max(0, start);
        while (at < end)
        {
            var next = at + 1;
            while (next < end && RoleAt(next) == RoleAt(at)) next++;
            runs.Add((text[at..next], RoleAt(at)));
            at = next;
        }
        return runs;
    }
}
