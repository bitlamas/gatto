namespace Gatto.Terminal;

//one key and what it does, words only. the shed rank says when a key gives up, 0 for never, a rank because the arrows key comes from the glyph table
public readonly record struct FooterKey(string Key, string Verb, int Shed = 0);

//what sits right of the keys. the footer draws both kinds the same way, whole or not at all
public enum LegendKind
{
    //glyphs with meanings, the GPU, RAM and too-big marks
    Marks,
    //a sentence, kept whole or dropped entirely, since an ellipsed half of it says nothing and still costs the room
    Sentence,
}

public readonly record struct Legend(LegendKind Kind, string Text);

//the footer is one row, always, so a hint key sheds instead of wrapping. the separator is an ASCII bar, the idiom the REPL footer already uses
public static class Footer
{
    //cells between one key's verb and the next key, one value wizard-wide. a per-screen gap is two visual languages
    public const int KeyGap = 3;

    private const string Indent = "  ";
    private const string Separator = "| ";

    //plain text of the row, built from Runs so the golden and the painted row can't drift apart
    public static string Compose(int width, IReadOnlyList<FooterKey> keys, Legend? legend = null) =>
        new PaintedRow(Runs(width, keys, legend)).Text;

    //the row with its ink: key bright, verb dim, because the eye scanning the footer hunts the key
    public static IReadOnlyList<Run> Runs(int width, IReadOnlyList<FooterKey> keys, Legend? legend = null)
    {
        //inset the width once, here. every fit choice below reads it, insetting again at the gap sheds at one width and draws at another
        width = Margins.Inside(width);
        var runs = KeyRuns(keys);

        if (legend is not { } leg) return runs;

        //when the legend doesn't fit, hint keys drop out lowest rank first, the legend goes last. rank lives on the keys, only as many drop as needed to stay one line
        var kept = keys;
        foreach (var rank in keys.Where(k => k.Shed > 0).Select(k => k.Shed).Distinct().Order())
        {
            if (Fits(kept, leg, width)) break;
            kept = [.. kept.Where(k => k.Shed != rank)];
        }

        var runsKept = kept.Count == keys.Count ? runs : KeyRuns(kept);
        var left = KeysRow(kept);
        var whole = Separator + leg.Text;

        if (Fits(kept, leg, width)) return Gap(runsKept, left, whole, width);

        //the legend shows whole or not at all, and shed keys stay gone (restoring them would make the narrow row wider than the wide one)
        return runsKept;
    }

    //the same arithmetic the drawing uses, read once so decision and drawing can't part
    private static bool Fits(IReadOnlyList<FooterKey> keys, Legend legend, int width) =>
        Cells(Separator + legend.Text) <= width - Cells(KeysRow(keys)) - 1;

    //the one spelling of a key set's runs, used before and after a shed so a shed row differs only in its keys
    private static List<Run> KeyRuns(IReadOnlyList<FooterKey> keys)
    {
        var runs = new List<Run> { new(Indent) };
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) runs.Add(new Run(new string(' ', KeyGap)));
            //a press on a key word acts as its key, so key and verb carry the key's own string. the arrows name a pair and take no press
            var tag = IsArrows(keys[i].Key) ? null : keys[i].Key;
            runs.Add(new Run(keys[i].Key, RunInk.Bright, tag));
            runs.Add(new Run(" " + keys[i].Verb, RunInk.Dim, tag));
        }
        return runs;
    }

    private static bool IsArrows(string key) => key == GlyphSet.Unicode.ArrowsKey || key == GlyphSet.Ascii.ArrowsKey;

    //the legend dim, pushed right. the gap comes from the same left string the plain row builds
    private static IReadOnlyList<Run> Gap(List<Run> runs, string left, string right, int width)
    {
        var gap = Math.Max(1, width - Cells(left) - Cells(right));
        runs.Add(new Run(new string(' ', gap)));
        runs.Add(new Run(right, RunInk.Dim));
        return runs;
    }

    //the keys row as text, the one string its width is ever measured from
    private static string KeysRow(IReadOnlyList<FooterKey> keys) =>
        Indent + string.Join(new string(' ', KeyGap), keys.Select(k => $"{k.Key} {k.Verb}"));

    //the armed warning takes the whole row, it mustn't sit beside the shortcuts it overrides
    public static string Armed(string warning) => Indent + warning;

    //the armed warning is ACCENT rather than dim, it's the one footer state asking the user to stop and read
    public static IReadOnlyList<Run> ArmedRuns(string warning) =>
        [new Run(Indent), new Run(warning, RunInk.Accent)];

    //the product's own width function, so fits-here and doesn't-wrap stay the same claim
    private static int Cells(string s) => UnicodeWidth.Of(s);
}
