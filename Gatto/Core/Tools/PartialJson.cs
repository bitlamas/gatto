using System.Globalization;
using System.Text;

namespace Gatto.Core.Tools;

//reads one top-level string out of arguments that are still streaming, for display only
public static class PartialJson
{
    public static string? StringValue(string? json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var i = SkipWs(json, 0);
        if (i >= json.Length || json[i] != '{') return null;
        i++;
        while (true)
        {
            i = SkipWs(json, i);
            if (i >= json.Length || json[i] == '}') return null;
            if (json[i] == ',') { i++; continue; }
            if (json[i] != '"') return null;
            var (name, closed, next) = ReadString(json, i + 1);
            if (!closed) return null;
            i = SkipWs(json, next);
            if (i >= json.Length || json[i] != ':') return null;
            i = SkipWs(json, i + 1);
            if (i >= json.Length) return null;
            if (name == key) return json[i] == '"' ? ReadString(json, i + 1).Value : null;
            i = SkipValue(json, i);
            if (i < 0) return null;
        }
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    //decodes from just past an opening quote and stops at the closing quote, or before an escape the buffer has not finished
    private static (string Value, bool Closed, int Next) ReadString(string s, int i)
    {
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"') return (sb.ToString(), true, i + 1);
            if (c != '\\') { sb.Append(c); i++; continue; }
            if (i + 1 >= s.Length) break;
            var e = s[i + 1];
            if (e != 'u')
            {
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => e });
                i += 2;
                continue;
            }
            if (i + 6 > s.Length) break;
            if (!TryHex(s, i + 2, out var cp)) { i += 2; continue; }
            if (!char.IsHighSurrogate((char)cp)) { sb.Append((char)cp); i += 6; continue; }
            if (i + 12 > s.Length) break;
            if (s[i + 6] == '\\' && s[i + 7] == 'u' && TryHex(s, i + 8, out var lo) && char.IsLowSurrogate((char)lo))
            {
                sb.Append((char)cp).Append((char)lo);
                i += 12;
                continue;
            }
            i += 6;
        }
        return (sb.ToString(), false, s.Length);
    }

    private static bool TryHex(string s, int at, out int value) =>
        int.TryParse(s.AsSpan(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    //the index just past a value, or -1 while the value is still arriving
    private static int SkipValue(string s, int i)
    {
        if (s[i] == '"')
        {
            var (_, closed, next) = ReadString(s, i + 1);
            return closed ? next : -1;
        }
        if (s[i] is '{' or '[')
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"')
                {
                    var (_, closed, next) = ReadString(s, i + 1);
                    if (!closed) return -1;
                    i = next;
                    continue;
                }
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']' && --depth == 0) return i + 1;
                i++;
            }
            return -1;
        }
        while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
        return i < s.Length ? i : -1;
    }
}
