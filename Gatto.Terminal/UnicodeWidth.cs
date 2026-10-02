using System.Text;

namespace Gatto.Terminal;

//best-effort display width in terminal cells, a heuristic covering the ranges gatto actually prints
public static class UnicodeWidth
{
    public static int Of(string s)
    {
        var w = 0;
        var prev = 0;
        foreach (var r in s.EnumerateRunes())
        {
            var rw = InContext(r, prev);
            w += rw;
            prev = rw;
        }
        return w;
    }

    //wrap-aware row count shared by the stream renderer and the input frame. a wide rune straddling the last column wraps early, an exactly-full line stays one row
    public static int Rows(string visibleText, int width)
    {
        if (width <= 0) return 1;
        var rows = 1;
        var col = 0;
        var prev = 0;
        foreach (var rune in visibleText.EnumerateRunes())
        {
            var rw = InContext(rune, prev);
            if (col + rw > width) { rows++; col = rw; }
            else { col += rw; }
            prev = rw;
        }
        return rows;
    }

    //the emoji selector adds the second cell to a narrow symbol and nothing to a symbol already two cells wide
    private static int InContext(Rune r, int previousWidth) =>
        r.Value == EmojiSelector && previousWidth == 2 ? 0 : OfRune(r);

    private const int EmojiSelector = 0xFE0F;

    public static int OfRune(Rune r)
    {
        var c = r.Value;
        //zero width: combining marks, zero-width chars
        if (c is >= 0x0300 and <= 0x036F) return 0;
        if (c is >= 0x1AB0 and <= 0x1AFF) return 0;
        if (c is >= 0x20D0 and <= 0x20FF) return 0;
        if (c is >= 0xFE20 and <= 0xFE2F) return 0;
        if (c == EmojiSelector) return 1;              //a terminal draws a narrow symbol with it as a two-cell emoji, so a walker one rune at a time gets the second cell here
        if (c is >= 0xFE00 and <= 0xFE0E) return 0;   //the other variation selectors
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
        if (c is >= 0x1F680 and <= 0x1F6FF) return InRanges(c, WideTransport) ? 2 : 1;
        if (c is >= 0x1F900 and <= 0x1F9FF) return 2;
        if (c is >= 0x1FA70 and <= 0x1FAFF) return 2;
        if (c is >= 0x2329 and <= 0x2B55 && InRanges(c, WideSymbols)) return 2;
        if (c is >= 0x1F004 and <= 0x1F7F0 && InRanges(c, WideMarks)) return 2;
        if (c is >= 0x20000 and <= 0x2FFFD) return 2;
        if (c is >= 0x30000 and <= 0x3FFFD) return 2;
        return 1;
    }

    //the symbols below the emoji planes that East Asian Width marks wide, each drawn as an emoji by default
    private static readonly (int Lo, int Hi)[] WideSymbols =
    [
        (0x2329, 0x232A), (0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2630, 0x2637), (0x2648, 0x2653), (0x267F, 0x267F), (0x268A, 0x268F), (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
        (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C),
        (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
    ];

    //the wide game pieces, enclosed marks and geometric shapes of the emoji plane outside the blocks above, the coloured circles and squares among them
    private static readonly (int Lo, int Hi)[] WideMarks =
    [
        (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F200, 0x1F202),
        (0x1F210, 0x1F23B), (0x1F240, 0x1F248), (0x1F250, 0x1F251), (0x1F260, 0x1F265), (0x1F7E0, 0x1F7EB),
        (0x1F7F0, 0x1F7F0),
    ];

    //the wide part of the transport and map block. the rest of it, the cloud mark among them, is narrow
    private static readonly (int Lo, int Hi)[] WideTransport =
    [
        (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7), (0x1F6DC, 0x1F6DF),
        (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC),
    ];

    private static bool InRanges(int c, (int Lo, int Hi)[] ranges)
    {
        foreach (var (lo, hi) in ranges)
            if (c >= lo && c <= hi) return true;
        return false;
    }
}
