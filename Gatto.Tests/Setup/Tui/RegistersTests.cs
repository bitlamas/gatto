using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//assert the ink against the SGR bytes the theme actually emits, since a helper's idea of the escape is a second implementation
public class RegistersTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    //take only the prefix before the painted text, so the theme's real bytes are what the assertion compares
    private static string Sgr(RgbColor c)
    {
        var painted = T.Paint("x", c);
        return painted[..painted.IndexOf('x')];
    }

    [Fact]
    public void A_verdict_row_is_glyph_by_kind_then_a_PLAIN_word_then_a_DIM_tail()
    {
        var row = Registers.Verdict(T, VerdictKind.Ok, "passed", GlyphSet.Unicode, "5 of 5");

        Assert.Contains(T.Paint("✓", Theme.Ok), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("passed", Theme.Bright), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("5 of 5", Theme.Dim), row, StringComparison.Ordinal);
    }

    //the word takes no colour of its own, since a green passed reads as praise and a red struggled as blame
    [Theory]
    [InlineData(0, "passed")]
    [InlineData(1, "struggled")]
    [InlineData(2, "not run")]
    public void The_verdict_WORD_never_wears_the_glyphs_colour(int kind, string word)
    {
        var k = (VerdictKind)kind;
        var row = Registers.Verdict(T, k, word, glyphs: GlyphSet.Unicode);
        var colour = k switch
        {
            VerdictKind.Ok => Theme.Ok,
            VerdictKind.Failed => Theme.Err,
            _ => Theme.Warn,
        };
        Assert.DoesNotContain(T.Paint(word, colour), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint(word, Theme.Bright), row, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_kind_wears_its_ruled_glyph()
    {
        Assert.StartsWith(T.Paint("✓", Theme.Ok), Registers.Verdict(T, VerdictKind.Ok, "installed", glyphs: GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.StartsWith(T.Paint("✗", Theme.Err), Registers.Verdict(T, VerdictKind.Failed, "struggled", glyphs: GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.StartsWith(T.Paint("⚠", Theme.Warn), Registers.Verdict(T, VerdictKind.NotRun, "not run", glyphs: GlyphSet.Unicode), StringComparison.Ordinal);
    }

    //a command the user is about to type takes the accent colour, since it is the subject of their next action
    [Fact]
    public void A_command_in_a_sentence_is_accented()
    {
        var row = Registers.Command(T, "Once it stops, type gatto audition x in a terminal", "gatto audition x");
        Assert.Contains(T.Paint("gatto audition x", Theme.Accent), row, StringComparison.Ordinal);
    }

    //the occurrence argument picks which mention to accent, since PaintSpans paints only the first match
    [Fact]
    public void The_SECOND_occurrence_can_be_the_one_accented()
    {
        const string s = "gatto is installed; run gatto to start it";
        var row = Registers.Command(T, s, "gatto", occurrence: 1);

        var accented = T.Paint("gatto", Theme.Accent);
        Assert.Contains(accented, row, StringComparison.Ordinal);
        //the first gatto must stay dim, since it is a mention rather than the act
        Assert.StartsWith(T.Paint("gatto is installed; run ", Theme.Dim), row, StringComparison.Ordinal);
    }

    //paint the command as one token, since a line wrap must not split it into unmatched halves
    [Fact]
    public void The_command_is_a_single_unbroken_token()
    {
        var row = Registers.Command(T, "type gatto serve stop first", "gatto serve stop");
        var token = T.Paint("gatto serve stop", Theme.Accent);
        Assert.Contains(token, row, StringComparison.Ordinal);
        Assert.Equal(1, row.Split(token).Length - 1);
    }

    //a name that is only mentioned stays dim with its sentence, since the accent means an action the user should take
    [Fact]
    public void A_mentioned_name_carries_NO_accent()
    {
        var row = Registers.Mention(T, "gatto.json records update_check so this is asked once");
        Assert.DoesNotContain(Sgr(Theme.Accent), row, StringComparison.Ordinal);
        Assert.Contains(Sgr(Theme.Dim), row, StringComparison.Ordinal);
    }

    //the accent SGR must be findable when it is present, otherwise a green here only proves no accent is emitted at all
    [Fact]
    public void The_accent_SGR_is_detectable_when_present()
    {
        Assert.Contains(Sgr(Theme.Accent),
            Registers.Command(T, "run gatto doctor now", "gatto doctor"), StringComparison.Ordinal);
    }

    //a command missing from the sentence must not accent other words by mistake
    [Fact]
    public void A_command_that_is_not_in_the_sentence_leaves_it_dim()
    {
        var row = Registers.Command(T, "nothing to type here", "gatto audition");
        Assert.DoesNotContain(Sgr(Theme.Accent), row, StringComparison.Ordinal);
    }
}
