using System.Text;

namespace Gatto.Terminal;

//best-effort display width in terminal cells, a heuristic covering the ranges gatto actually prints
public static class UnicodeWidth
{
    public static int Of(string s)
    {
        var w = 0;
        foreach (var r in s.EnumerateRunes()) w += OfRune(r);
        return w;
    }

    //wrap-aware row count shared by the stream renderer and the input frame. a wide rune straddling the last column wraps early, an exactly-full line stays one row
    public static int Rows(string visibleText, int width)
    {
        if (width <= 0) return 1;
        var rows = 1;
        var col = 0;
        foreach (var rune in visibleText.EnumerateRunes())
        {
            var rw = OfRune(rune);
            if (col + rw > width) { rows++; col = rw; }
            else { col += rw; }
        }
        return rows;
    }

    public static int OfRune(Rune r)
    {
        var c = r.Value;
        //zero width: combining marks, zero-width chars
        if (c is >= 0x0300 and <= 0x036F) return 0;
        if (c is >= 0x1AB0 and <= 0x1AFF) return 0;
        if (c is >= 0x20D0 and <= 0x20FF) return 0;
        if (c is >= 0xFE20 and <= 0xFE2F) return 0;
        if (c is >= 0x200B and <= 0x200F) return 0;
        //wide: CJK & friends
        if (c is >= 0x1100 and <= 0x115F) return 2;   //hangul jamo
        if (c is >= 0x2E80 and <= 0x303E) return 2;   //CJK radicals and punctuation (incl 、)
        if (c is >= 0x3041 and <= 0x33FF) return 2;   //hiragana, katakana (incl ・), CJK compat
        if (c is >= 0x3400 and <= 0x4DBF) return 2;
        if (c is >= 0x4E00 and <= 0x9FFF) return 2;   //CJK unified
        if (c is >= 0xA000 and <= 0xA4CF) return 2;
        if (c is >= 0xAC00 and <= 0xD7A3) return 2;   //hangul syllables
        if (c is >= 0xF900 and <= 0xFAFF) return 2;
        if (c is >= 0xFE30 and <= 0xFE4F) return 2;
        if (c is >= 0xFF00 and <= 0xFF60) return 2;   //fullwidth forms (７ （ etc.)
        if (c is >= 0xFFE0 and <= 0xFFE6) return 2;
        if (c is >= 0x1F300 and <= 0x1F64F) return 2; //emoji
        if (c is >= 0x1F900 and <= 0x1F9FF) return 2;
        if (c is >= 0x20000 and <= 0x2FFFD) return 2;
        if (c is >= 0x30000 and <= 0x3FFFD) return 2;
        return 1;
    }
}
