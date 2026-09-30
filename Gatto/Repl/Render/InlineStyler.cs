using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//a single-pass inline markdown tokenizer. a chip binds tightest, emphasis survives inside it, and an unmatched marker stays literal
public static class InlineStyler
{
    public static IReadOnlyList<StyledSpan> Parse(string text) => ParseInner(text, SpanFlags.None, null);

    private static List<StyledSpan> ParseInner(string text, SpanFlags inherited, string? url)
    {
        var chips = ChipRegions(text);   //an entry holds the open index, the content bounds and the close index
        var spans = new List<StyledSpan>();
        var lit = new StringBuilder();
        bool bold = false, italic = false, strike = false;
        char boldMarker = ' ', italicMarker = ' ';

        SpanFlags Flags() => inherited
            | (bold ? SpanFlags.Bold : 0) | (italic ? SpanFlags.Italic : 0) | (strike ? SpanFlags.Strike : 0);

        void EmitLit()
        {
            if (lit.Length == 0) return;
            spans.Add(new StyledSpan(lit.ToString(), Flags(), url));
            lit.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            if (chips.TryGetValue(i, out var ch))
            {
                EmitLit();
                spans.Add(new StyledSpan(text[ch.ContentStart..ch.ContentEnd], Flags() | SpanFlags.Chip, url));
                i = ch.Close + 1;
                continue;
            }
            var c = text[i];

            //two tildes toggle strike, which nests inside bold and italic
            if (IsPair(text, i, '~'))
            {
                if (strike) { EmitLit(); strike = false; }
                else if (HasPairAhead(text, i + 2, '~', chips)) { EmitLit(); strike = true; }
                else lit.Append('~', 2);
                i += 2;
                continue;
            }

            //two stars or two underscores toggle bold, and only the marker that opened it can close it
            if (IsPair(text, i, '*') || IsPair(text, i, '_'))
            {
                if (bold && boldMarker != c) { lit.Append(c, 2); }
                else if (bold)
                {
                    if (CloseBoundaryOk(text, i + 2, c)) { EmitLit(); bold = false; }
                    else lit.Append(c, 2);   //the close boundary is wrong here, so the marker is literal and bold stays open
                }
                else if ((c != '_' || BoundaryOk(text, i)) && HasPairAhead(text, i + 2, c, chips))
                {
                    EmitLit(); bold = true; boldMarker = c;
                }
                else lit.Append(c, 2);
                i += 2;
                continue;
            }

            //a single star or underscore toggles italic, guarded by what sits on either side of it
            if (c == '*' || c == '_')
            {
                if (italic && italicMarker == c)
                {
                    if (CloseBoundaryOk(text, i + 1, c)) { EmitLit(); italic = false; }
                    else lit.Append(c);      //the close boundary is wrong here, so the marker is literal and italic stays open
                    i++;
                    continue;
                }
                var prevOk = i == 0 || (!char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != c);
                var nextOk = i + 1 < text.Length && text[i + 1] != ' ' && text[i + 1] != c;
                if (!italic && prevOk && nextOk && HasSingleAhead(text, i + 1, c, chips))
                {
                    EmitLit(); italic = true; italicMarker = c;
                }
                else lit.Append(c);
                i++;
                continue;
            }

            //a link is one nested pass over its text, and a bracket inside a link stays literal
            if (c == '[' && url is null && TryParseLink(text, i, chips, out var inner, out var linkUrl, out var after))
            {
                EmitLit();
                spans.AddRange(ParseInner(inner, Flags() | SpanFlags.Link, linkUrl));
                i = after;
                continue;
            }

            lit.Append(c);
            i++;
        }
        EmitLit();
        return spans;
    }

    private static bool IsPair(string t, int i, char m) =>
        t[i] == m && i + 1 < t.Length && t[i + 1] == m;

    //an underscore marker must not sit right after a word char
    private static bool BoundaryOk(string t, int i) =>
        i == 0 || (!char.IsLetterOrDigit(t[i - 1]) && t[i - 1] != '_');

    //a closing underscore must not sit right before a word char, while a star may close anywhere
    private static bool CloseBoundaryOk(string t, int afterIdx, char m) =>
        m != '_' || afterIdx >= t.Length || (!char.IsLetterOrDigit(t[afterIdx]) && t[afterIdx] != '_');

    private static bool HasPairAhead(string t, int from, char m, Dictionary<int, ChipRegion> chips)
    {
        for (var i = from; i + 1 < t.Length; i++)
        {
            if (SkipChip(chips, ref i)) continue;
            if (t[i] == m && t[i + 1] == m && i > from && CloseBoundaryOk(t, i + 2, m)) return true;
        }
        return false;
    }

    private static bool HasSingleAhead(string t, int from, char m, Dictionary<int, ChipRegion> chips)
    {
        for (var i = from; i < t.Length; i++)
        {
            if (SkipChip(chips, ref i)) continue;
            if (t[i] == m
                && (i + 1 >= t.Length || t[i + 1] != m)   //this must not be the first marker of a pair
                && i > from
                && t[i - 1] != ' '
                && t[i - 1] != m                          //this must not be the second marker of a pair
                && CloseBoundaryOk(t, i + 1, m)) return true;
        }
        return false;
    }

    private static bool TryParseLink(string t, int open, Dictionary<int, ChipRegion> chips,
        out string inner, out string url, out int after)
    {
        inner = ""; url = ""; after = open;
        var rb = -1;
        for (var i = open + 1; i < t.Length; i++)
        {
            if (SkipChip(chips, ref i)) continue;
            if (t[i] == ']') { rb = i; break; }
            if (t[i] == '[') return false;
        }
        if (rb < 0 || rb == open + 1 || rb + 1 >= t.Length || t[rb + 1] != '(') return false;
        var rp = t.IndexOf(')', rb + 2);
        if (rp < 0) return false;
        var candidate = t[(rb + 2)..rp];
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace)) return false;
        inner = t[(open + 1)..rb];
        url = candidate;
        after = rp + 1;
        return true;
    }

    private readonly record struct ChipRegion(int ContentStart, int ContentEnd, int Close);

    //pair backticks left to right, where a close past the next char makes a chip and an empty or unmatched one stays literal
    private static Dictionary<int, ChipRegion> ChipRegions(string t)
    {
        var map = new Dictionary<int, ChipRegion>();
        var i = 0;
        while (i < t.Length)
        {
            if (t[i] == '`')
            {
                var close = t.IndexOf('`', i + 1);
                if (close > i + 1) { map[i] = new ChipRegion(i + 1, close, close); i = close + 1; continue; }
            }
            i++;
        }
        return map;
    }

    private static bool SkipChip(Dictionary<int, ChipRegion> chips, ref int i)
    {
        if (!chips.TryGetValue(i, out var ch)) return false;
        i = ch.Close;   //the caller's ++ moves one past the close
        return true;
    }
}
