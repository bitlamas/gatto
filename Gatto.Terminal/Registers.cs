
namespace Gatto.Terminal;

//what the verdict says about its subject, which picks glyph and colour. both Held and NotRun mean gatto didn't find out, neither blames the model
public enum VerdictKind
{
    //it happened, or it works
    Ok,
    //the ✗ row: it was tried and it didn't work
    Failed,
    //the warn row: it wasn't tried, or couldn't be. don't read it as a judgement on the model
    NotRun,
}

//accent paints only what the user is about to act on, a name merely mentioned stays dim. don't add a fourth span kind for convenience
public static class Registers
{
    //only the glyph takes a colour, the verdict word is plain and the tail dim. a green passed reads as praise
    public static string Verdict(Theme theme, VerdictKind kind, string word, GlyphSet glyphs,
        string? tail = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var (glyph, colour) = kind switch
        {
            VerdictKind.Ok => (g.Ok, Theme.Ok),
            VerdictKind.Failed => (g.Bad, Theme.Err),
            _ => (g.Warn, Theme.Warn),
        };
        var row = theme.Paint(glyph, colour) + " " + theme.Paint(word, Theme.Bright);
        return tail is { Length: > 0 } t ? row + " " + theme.Paint(t, Theme.Dim) : row;
    }

    //paint the nth occurrence, the first match isn't always the one meant. keep the command one token so a wrap can't split the accent
    public static string Command(Theme theme, string sentence, string command, int occurrence = 0)
    {
        if (command.Length == 0) return theme.Paint(sentence, Theme.Dim);

        var at = -1;
        for (var n = 0; n <= occurrence; n++)
        {
            at = sentence.IndexOf(command, at + 1, StringComparison.Ordinal);
            if (at < 0) return theme.Paint(sentence, Theme.Dim);   //the command isn't there: paint the whole sentence plain
        }

        return theme.Paint(sentence[..at], Theme.Dim)
             + theme.Paint(command, Theme.Accent)
             + theme.Paint(sentence[(at + command.Length)..], Theme.Dim);
    }

    //a name the sentence merely mentions. keep it dim, accenting would promise an action that isn't being offered
    public static string Mention(Theme theme, string sentence) => theme.Paint(sentence, Theme.Dim);
}
