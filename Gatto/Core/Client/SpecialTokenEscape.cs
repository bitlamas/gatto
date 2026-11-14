using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core.Models;

namespace Gatto.Core.Client;

//the strings a served model's vocabulary reads as one token wherever they stand, broken on the way to the server and mended on the way back
public sealed class SpecialTokenEscape
{
    //invisible characters that leave the text readable, tried in order. the first one no token of the vocabulary holds is the break
    private static readonly char[] Breaks = [(char)0x200B, (char)0x2060, (char)0x2063];

    private static readonly ConcurrentDictionary<string, SpecialTokenEscape?> ByFile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<char, string[]> _byFirst;
    private readonly SearchValues<char> _firsts;
    private readonly string _breakText;
    private readonly string _breakJson;

    //the character put after a token's first character, which leaves the token unmatched
    public char Break { get; }

    private SpecialTokenEscape(Dictionary<char, string[]> byFirst, char brk)
    {
        _byFirst = byFirst;
        _firsts = SearchValues.Create(byFirst.Keys.ToArray());
        Break = brk;
        _breakText = brk.ToString();
        _breakJson = $"\\u{(int)brk:x4}";
    }

    //null when no token qualifies or every break is itself in the vocabulary, so a caller sends its text untouched
    public static SpecialTokenEscape? From(IEnumerable<string> tokens)
    {
        var all = tokens.Distinct(StringComparer.Ordinal).ToList();
        var free = Breaks.Where(b => !all.Any(t => t.Contains(b))).ToArray();
        if (free.Length == 0) return null;
        var byFirst = all.Where(Qualifies).GroupBy(t => t[0]).ToDictionary(g => g.Key, g => g.ToArray());
        return byFirst.Count == 0 ? null : new SpecialTokenEscape(byFirst, free[0]);
    }

    //the tokens of a model file, read once per file. null with unreadable false means the file holds none, and a file that could not be read is asked again at the next request
    public static SpecialTokenEscape? ForModelFile(string path, out bool unreadable)
    {
        unreadable = false;
        if (ByFile.TryGetValue(path, out var known)) return known;
        IReadOnlyList<string>? tokens = null;
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            tokens = GgufHeaderParser.SpecialTokens(s);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   //the flag below says it, a caller that stays silent would send special text unescaped with nothing said
        if (tokens is null)
        {
            unreadable = true;
            return null;
        }
        return ByFile[path] = From(tokens);
    }

    //a token opens and closes with punctuation, so a vocabulary that marks a plain word or a run of spaces breaks no prose. a surrogate cannot take a break after it
    private static bool Qualifies(string token) =>
        token.Length >= 2 && Edge(token[0]) && Edge(token[^1]) && !char.IsSurrogate(token[0]);

    private static bool Edge(char c) => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);

    //every place a token starts takes a break after its first character, so a token inside a longer one is broken too. text with no token comes back as the same instance
    public string Escape(string text)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            var next = text.AsSpan(i).IndexOfAny(_firsts);
            if (next < 0) break;
            i += next;
            if (StartsAt(text, i))
            {
                sb ??= new StringBuilder(text.Length + 8);
                sb.Append(text, copied, i + 1 - copied).Append(Break);
                copied = i + 1;
            }
            i++;
        }
        return sb is null ? text : sb.Append(text, copied, text.Length - copied).ToString();
    }

    //a break is taken out where, with the breaks gone, a token starts at the character before it. any other occurrence of the character stays
    public string Mend(string text)
    {
        if (!text.Contains(Break)) return text;
        var bare = text.Replace(_breakText, "", StringComparison.Ordinal);
        var sb = new StringBuilder(text.Length);
        var written = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Break)
            {
                sb.Append(text[i]);
                written++;
                continue;
            }
            var firstOfRun = i > 0 && text[i - 1] != Break;
            if (!(firstOfRun && StartsAt(bare, written - 1))) sb.Append(Break);
        }
        return sb.Length == text.Length ? text : sb.ToString();
    }

    //a JSON text as the server will render it: the server decodes the JSON first, so the keys and string values are escaped, never the JSON text
    public string EscapeJson(string json) =>
        json.AsSpan().IndexOfAny(_firsts) < 0 && !json.Contains('\\') ? json : MapStrings(json, Escape);

    //the arguments a model wrote after reading escaped text, with the breaks it copied taken out so a file is written as the text it holds
    public string MendJson(string json) =>
        json.Contains(Break) || json.Contains(_breakJson, StringComparison.OrdinalIgnoreCase) ? MapStrings(json, Mend) : json;

    private bool StartsAt(string text, int i)
    {
        if (!_byFirst.TryGetValue(text[i], out var tokens)) return false;
        foreach (var t in tokens)
            if (string.CompareOrdinal(text, i, t, 0, t.Length) == 0) return true;
        return false;
    }

    //the same text comes back when nothing changed or the JSON does not parse, so arguments nothing touched keep their bytes
    private static string MapStrings(string json, Func<string, string> map)
    {
        try
        {
            return JsonNode.Parse(json) is { } root && Rewrite(root, map) ? root.ToJsonString(Relaxed) : json;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return json; }
    }

    private static bool Rewrite(JsonNode node, Func<string, string> map)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToList())
            {
                if (Mapped(value, map) is { } text) { obj[key] = text; changed = true; }
                else if (value is not null) changed |= Rewrite(value, map);
                var renamed = map(key);
                if (renamed == key || obj.ContainsKey(renamed)) continue;
                var moved = obj[key];
                obj.Remove(key);
                obj[renamed] = moved;
                changed = true;
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (Mapped(array[i], map) is { } text) { array[i] = text; changed = true; }
                else if (array[i] is { } child) changed |= Rewrite(child, map);
            }
        }
        return changed;
    }

    //the new text of a string value the map changed, null for any other node
    private static string? Mapped(JsonNode? node, Func<string, string> map)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
        var before = value.GetValue<string>();
        var after = map(before);
        return before == after ? null : after;
    }
}
