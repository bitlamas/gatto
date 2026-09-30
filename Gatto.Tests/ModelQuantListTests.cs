using Gatto.Terminal;

namespace Gatto.Tests;

//bare /model quant lists what the model holds, which nothing else shows. no profile, one file and several are three answers, and none may invent a file
public class ModelQuantListTests
{
    private static Func<IReadOnlyList<(string Quant, bool Active)>> Files(
        params (string Quant, bool Active)[] f) => () => f;

    //mark the active file and name it in words, a glyph alone leaves the reader guessing which row is special
    [Fact]
    public void THE_FILE_IN_USE_IS_MARKED_AND_NAMED()
    {
        var text = Gatto.Repl.Repl.QuantLines(Files(("Q4_K_M", true), ("Q6_K", false)), glyphs: GlyphSet.Unicode);

        Assert.Contains("● Q4_K_M   in use", text, StringComparison.Ordinal);
        Assert.Contains("Q6_K", text, StringComparison.Ordinal);
        //an idle row gets no mark, a glyph for the others would be a second vocabulary for the same fact
        Assert.DoesNotContain("● Q6_K", text, StringComparison.Ordinal);
    }

    //only show the hint when there is a second file, on a one-file model it promises a key that does nothing
    [Fact]
    public void THE_SWITCH_HINT_APPEARS_ONLY_WHEN_THERE_IS_A_CHOICE()
    {
        var several = Gatto.Repl.Repl.QuantLines(Files(("Q4_K_M", true), ("Q6_K", false)), glyphs: GlyphSet.Unicode);
        var one = Gatto.Repl.Repl.QuantLines(Files(("Q4_K_M", true)), glyphs: GlyphSet.Unicode);

        Assert.Contains("/model quant <name>", several, StringComparison.Ordinal);
        Assert.DoesNotContain("/model quant <name>", one, StringComparison.Ordinal);
        Assert.Contains("Q4_K_M", one, StringComparison.Ordinal);
    }

    //a connect endpoint gives no profile to read, so the reply says so rather than claiming the model names no files
    [Fact]
    public void A_SESSION_WITH_NO_PROFILE_SAYS_SO_RATHER_THAN_REPORTING_NOTHING()
    {
        var text = Gatto.Repl.Repl.QuantLines(null, glyphs: GlyphSet.Unicode);

        Assert.Contains("no model profile", text, StringComparison.Ordinal);
        Assert.DoesNotContain("names no files", text, StringComparison.Ordinal);
    }

    //a profile that exists but reads as empty gets its own sentence, merging it with the no-profile one hides the difference
    [Fact]
    public void AND_AN_EMPTY_LIST_IS_A_DIFFERENT_SENTENCE()
    {
        var text = Gatto.Repl.Repl.QuantLines(Files(), glyphs: GlyphSet.Unicode);

        Assert.Contains("names no files", text, StringComparison.Ordinal);
        Assert.NotEqual(Gatto.Repl.Repl.QuantLines(null, glyphs: GlyphSet.Unicode), text);
    }

    //the hint and the dispatch read one const, so the hint can't spell a verb the command rejects
    [Fact]
    public void THE_HINT_SPELLS_THE_VERB_THE_DISPATCH_LISTENS_FOR()
    {
        var text = Gatto.Repl.Repl.QuantLines(Files(("Q4_K_M", true), ("Q6_K", false)), glyphs: GlyphSet.Unicode);

        Assert.Contains("/model " + Gatto.Repl.Repl.QuantArg + " ", text, StringComparison.Ordinal);
    }
}
