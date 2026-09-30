using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gatto.Core.Tools;

public sealed record GrepMatch(string Path, int Line, string Text);

public sealed record GrepParse(IReadOnlyList<GrepMatch> Matches, IReadOnlyList<string> Notes)
{
    public int Files => Matches.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count();
}

//the grep tool's own text read back into matches, for display only, so the model's text never changes
public static class GrepRows
{
    private static readonly Regex Row = new(@"^(.+?):(\d+): (.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Note = new(@"^\[capped at .+\]$", RegexOptions.CultureInvariant);

    //null when any line is not the tool's, since a hook can rewrite the text and half a parse would mix two layouts
    public static GrepParse? Parse(string text)
    {
        if (text == Globbing.NoMatches) return new GrepParse(Array.Empty<GrepMatch>(), Array.Empty<string>());
        if (text.Length == 0) return null;
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var notes = new List<string>();
        while (lines.Count > 0 && Note.IsMatch(lines[^1])) { notes.Insert(0, lines[^1]); lines.RemoveAt(lines.Count - 1); }
        var matches = new List<GrepMatch>(lines.Count);
        foreach (var line in lines)
        {
            if (Row.Match(line) is not { Success: true } m
                || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return null;
            matches.Add(new GrepMatch(m.Groups[1].Value, n, m.Groups[3].Value));
        }
        return matches.Count == 0 ? null : new GrepParse(matches, notes);
    }

    //the pattern a grep call searched for, so the rows can mark what it matched
    public static string? PatternOf(string toolName, string argsJson)
    {
        if (toolName != "grep") return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
