using System.Text;

namespace Gatto.Terminal;

//defensive text handling for anything model- or extension-controlled that reaches the terminal
public static class TermText
{
    //drop C0 bytes, DEL and C1, ESC, CR and LF included. a tab becomes spaces up to the next 4-column stop, since a raw tab jumps to the terminal's own stops
    public static string Sanitize(string s) => ExpandTabsAndFilter(s, keepNewline: false);

    //same as Sanitize except that '\n' survives, since the renderer's word wrap and row accounting need it
    public static string SanitizeProse(string s) => ExpandTabsAndFilter(s, keepNewline: true);

    //the ESC byte as a code point, so no raw control byte sits in this source file
    private static readonly char Esc = Convert.ToChar(0x1B);

    //cut to max visible cells, measure the ellipsis from the table since the ASCII mark is three cells wide, and copy escapes through whole
    public static string TruncateCells(string s, int max, GlyphSet? glyphs)
    {
        if (max <= 0) return "";
        if (UnicodeWidth.Of(StripAnsiForWidth(s)) <= max) return s;   //the visible width fits, so keep the escapes as they are
        var mark = (glyphs ?? GlyphSet.Unicode).Ellipsis;
        var markWidth = UnicodeWidth.Of(mark);
        if (markWidth >= max) { mark = ""; markWidth = 0; }
        var budget = max - markWidth;
        var sb = new StringBuilder();
        var w = 0;
        var i = 0;
        var truncated = false;
        while (i < s.Length)
        {
            if (s[i] == Esc)
            {
                var j = SkipEscape(s, i);
                sb.Append(s, i, j - i);
                i = j;
                continue;
            }
            if (truncated) { i++; continue; }   //past the cut, only escapes still copy through
            if (!System.Text.Rune.TryGetRuneAt(s, i, out var rune)) { i++; continue; }
            var rw = UnicodeWidth.OfRune(rune);
            if (w + rw > budget)
            {
                sb.Append(mark);
                truncated = true;
                i += rune.Utf16SequenceLength;
                continue;
            }
            sb.Append(rune.ToString());
            w += rw;
            i += rune.Utf16SequenceLength;
        }
        return sb.ToString();
    }

    //paint a selection background over visible cells start..end, re-emit bgOn after any SGR inside the range, and restore the background the row's escapes imply
    public static string HighlightCells(string s, int start, int end, string bgOn)
    {
        if (start < 0) start = 0;
        if (start >= end) return s;

        var sb = new StringBuilder(s.Length + bgOn.Length * 4);
        var ambient = Esc + "[49m";   //row-ground to start from, 49 is the terminal default
        var w = 0;
        var inRange = false;
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == Esc)
            {
                var j = SkipEscape(s, i);
                var esc = s.Substring(i, j - i);
                sb.Append(esc);
                if (IsSgr(esc))
                {
                    UpdateAmbientBg(esc, ref ambient);
                    if (inRange) sb.Append(bgOn);   //re-emit the highlight after a colour span, which doesn't cancel it
                }
                i = j;
                continue;
            }
            var shouldBeIn = w >= start && w < end;
            if (shouldBeIn && !inRange) { sb.Append(bgOn); inRange = true; }
            else if (!shouldBeIn && inRange) { sb.Append(ambient); inRange = false; }

            if (!System.Text.Rune.TryGetRuneAt(s, i, out var rune)) { sb.Append(s[i]); i++; continue; }
            sb.Append(rune.ToString());
            w += UnicodeWidth.OfRune(rune);
            i += rune.Utf16SequenceLength;
        }
        if (inRange) sb.Append(ambient);   //close the highlight before the row ends, or the leaked background tints the next clear
        return sb.ToString();
    }

    private static bool IsSgr(string esc) => esc.Length >= 3 && esc[1] == '[' && esc[^1] == 'm';

    //derives the background from one SGR escape, keeping a compound escape's background alone so its bold or colour is not replayed
    private static void UpdateAmbientBg(string esc, ref string ambient)
    {
        var body = esc[2..^1];   //the parameters between [ and m
        var parts = body.Split(';');
        for (var k = 0; k < parts.Length; k++)
        {
            var p = parts[k];
            if (p.Length == 0 || p == "49") { ambient = Esc + "[49m"; continue; }
            if (!int.TryParse(p, out var code)) continue;
            if (code == 0) { ambient = Esc + "[49m"; continue; }   //a reset, and a multi-digit 00 parses to the same 0
            if ((code >= 40 && code <= 47) || (code >= 100 && code <= 107)) { ambient = Esc + "[" + code + "m"; }
            else if (code == 48 && k + 1 < parts.Length)
            {
                if (parts[k + 1] == "5" && k + 2 < parts.Length) { ambient = Esc + "[48;5;" + parts[k + 2] + "m"; k += 2; }
                else if (parts[k + 1] == "2" && k + 4 < parts.Length) { ambient = Esc + "[48;2;" + parts[k + 2] + ";" + parts[k + 3] + ";" + parts[k + 4] + "m"; k += 4; }
            }
            //a foreground, bold or other parameter leaves the ambient background alone
        }
    }

    //drop CSI and OSC escapes and return the visible text, for rows a caller painted where only the width matters
    public static string StripAnsiForWidth(string s)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == Esc) { i = SkipEscape(s, i); continue; }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    //the index just past a CSI or OSC sequence starting at start, else one past the lone ESC byte
    private static int SkipEscape(string s, int start)
    {
        var j = start + 1;
        if (j >= s.Length) return j;
        if (s[j] == '[')   //a CSI sequence runs from here to a final byte in @-~
        {
            j++;
            while (j < s.Length && (s[j] < '@' || s[j] > '~')) j++;
            if (j < s.Length) j++;   //the final byte belongs to the sequence
            return j;
        }
        if (s[j] == ']')   //an OSC sequence, an OSC 8 hyperlink for instance, ends at ST or BEL
        {
            j++;
            while (j < s.Length && s[j] != (char)7 && !(s[j] == Esc && j + 1 < s.Length && s[j + 1] == '\\')) j++;
            return j < s.Length ? j + (s[j] == Esc ? 2 : 1) : j;   //skip the two bytes of ST or the one byte of BEL
        }
        return j;   //a lone or foreign ESC consumes one byte and nothing more
    }

    private const int TabStop = 4;

    //one line as the composer draws it, each tab widened to the next stop of TabStop cells, and toDrawn[i] the drawn index source char i starts at
    public static string ExpandTabs(string s, out int[] toDrawn)
    {
        toDrawn = new int[s.Length + 1];
        var sb = new StringBuilder(s.Length);
        var cells = 0;
        var i = 0;
        while (i < s.Length)
        {
            toDrawn[i] = sb.Length;
            if (s[i] == '\t')
            {
                var spaces = TabStop - (cells % TabStop);
                sb.Append(' ', spaces);
                cells += spaces;
                i++;
                continue;
            }
            Rune.DecodeFromUtf16(s.AsSpan(i), out var r, out var used);
            if (used == 2) toDrawn[i + 1] = sb.Length + 1;   //the low surrogate keeps its own index, a caret never rests there
            sb.Append(s, i, used);
            cells += UnicodeWidth.OfRune(r);
            i += used;
        }
        toDrawn[s.Length] = sb.Length;
        return sb.ToString();
    }

    //the filter behind Sanitize and SanitizeProse, tracking the output column, and keepNewline lets '\n' through and resets that column
    private static string ExpandTabsAndFilter(string s, bool keepNewline)
    {
        var sb = new StringBuilder(s.Length);
        var col = 0;
        foreach (var ch in s)
        {
            if (ch == '\t')
            {
                var spaces = TabStop - (col % TabStop);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else if (keepNewline && ch == '\n')
            {
                sb.Append(ch);
                col = 0;
            }
            else if (ch >= 0x20 && ch != 0x7F && (ch < 0x80 || ch > 0x9F))
            {
                sb.Append(ch);
                col++;
            }
            //a C0 or DEL byte is dropped here and leaves the column alone
        }
        return sb.ToString();
    }
}
