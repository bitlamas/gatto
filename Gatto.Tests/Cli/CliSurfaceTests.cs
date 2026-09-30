using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Cli;

//the same calls give gatto's grammar on a terminal and plain text through a pipe. the surface decides that once, so no call site asks which one it has
public class CliSurfaceTests
{
    private const char Esc = '';

    private static (CliSurface S, StringWriter W) Plain()
    {
        var w = new StringWriter();
        return (new CliSurface(w, null, glyphs: GlyphSet.Unicode), w);
    }

    private static (CliSurface S, StringWriter W) Rich()
    {
        var w = new StringWriter();
        return (new CliSurface(w, new Theme(new TermCaps(true, true)), glyphs: GlyphSet.Unicode), w);
    }

    [Fact]
    public void A_NULL_THEME_PRODUCES_TEXT_WITH_NO_ESCAPES_AT_ALL()
    {
        //the theme is nullable so a caller never asks which surface it has. a pipe gets the same text with no escapes (one escape corrupts every script reading it)
        var (s, w) = Plain();
        s.Say("a sentence");
        s.Ok("lfm2.5-1.2b", "pid 31416");
        s.Row(("removed  ", CliInk.Plain), ("the exe", CliInk.Accent));

        Assert.DoesNotContain(Esc, w.ToString());
    }

    [Fact]
    public void THE_SAME_CALLS_SAY_THE_SAME_WORDS_on_both_surfaces()
    {
        //stripping the rich surface's paint must return the plain output exactly. the rich side must hold an escape, or a layer that printed nothing passes too
        var (plain, pw) = Plain();
        var (rich, rw) = Rich();
        foreach (var s in new[] { plain, rich })
        {
            s.Say("a sentence");
            s.Ok("lfm2.5-1.2b", "pid 31416");
        }

        Assert.Equal(pw.ToString(), TermText.StripAnsiForWidth(rw.ToString()));
        Assert.Contains(Esc, rw.ToString());
    }

    [Fact]
    public void SAY_WRITES_AT_COLUMN_ZERO()
    {
        //the body of a command belongs to the command. an indent claims a parent row, and under the header there is none to claim.
        var (s, w) = Plain();
        s.Say("a sentence");
        s.Ok("a subject");
        s.Row(("a composed run", CliInk.Plain));
        s.Live("a row that repaints");

        var rows = w.ToString().Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, rows.Length);
        Assert.All(rows, r =>
            Assert.False(r.StartsWith(' '), "a row indented itself: '" + r + "'"));
    }

    [Fact]
    public void UNDER_IS_THE_ONLY_TWO_SPACE_INDENT()
    {
        //two spaces have one meaning, which is that this row is subordinate to the row above it. every other member of the layer writes at the margin.
        var (s, w) = Plain();
        s.Say("llama-server exited before it became ready");
        s.Under("the quoted log tail");

        var rows = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.TrimEnd('\r')).ToList();
        Assert.Equal("llama-server exited before it became ready", rows[0]);
        Assert.Equal(CliSurface.Gutter + "the quoted log tail", rows[1]);
    }

    [Fact]
    public void CLOSE_WRITES_THE_FRAMES_LAST_BLANK_ROW()
    {
        //the frame ends with a blank row for the same reason it opens with one, a command appears in the middle of somebody's scrollback.
        var (s, w) = Plain();
        s.Say("the last body row");
        s.Close();

        Assert.EndsWith("the last body row" + Environment.NewLine + Environment.NewLine,
            w.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_MODEL_ID_TAKES_CODEINLINEFG()
    {
        //the palette already holds a token for an identifier drawn inline, and a model id is one. the standard adds no colour of its own.
        var theme = new Theme(new TermCaps(true, true));
        var w = new StringWriter();
        new CliSurface(w, theme, glyphs: GlyphSet.Unicode)
            .Row(("serving ", CliInk.Plain), ("qwen3.6-35b-a3b", CliInk.Model));

        Assert.Contains(theme.Paint("qwen3.6-35b-a3b", Theme.CodeInlineFg), w.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(theme.Paint("qwen3.6-35b-a3b", Theme.Accent), w.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_COMMAND_TAKES_ROLECODER_where_a_painter_exists_and_stays_PLAIN_where_none_does()
    {
        //a command a person can type takes one ink everywhere, with no quotes and no backticks. assert on the composed row, since the screen is the writer's half
        var theme = new Theme(new TermCaps(true, true));
        var (plain, pw) = Plain();
        var rw = new StringWriter();
        foreach (var s in new[] { plain, new CliSurface(rw, theme, glyphs: GlyphSet.Unicode) })
            s.Row(("the server stays up, ", CliInk.Plain), CliSurface.Command("gatto serve stop"),
                (" until you stop it.", CliInk.Plain));

        Assert.Equal("the server stays up, gatto serve stop until you stop it.",
            pw.ToString().TrimEnd());
        Assert.DoesNotContain(Esc, pw.ToString());
        Assert.Equal(pw.ToString(), TermText.StripAnsiForWidth(rw.ToString()));

        //the assertion pins the exact colour, since a row painted wholly in the body colour also strips back to plain.
        Assert.Contains(theme.Paint("gatto serve stop", Theme.RoleCoder), rw.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(theme.Paint("gatto serve stop", Theme.Accent), rw.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(theme.Paint("gatto serve stop", Theme.Bright), rw.ToString(), StringComparison.Ordinal);

        foreach (var s in new[] { pw.ToString(), rw.ToString() })
        {
            Assert.DoesNotContain("`gatto", s, StringComparison.Ordinal);
            Assert.DoesNotContain("'gatto", s, StringComparison.Ordinal);
        }
    }

    //a reset between two runs of one colour costs bytes and can break the row mid-colour, and no reader can see it.
    [Fact]
    public void ADJACENT_RUNS_OF_ONE_INK_ARE_PAINTED_ONCE()
    {
        var theme = new Theme(new TermCaps(true, true));
        var w = new StringWriter();
        new CliSurface(w, theme, glyphs: GlyphSet.Unicode)
            .Row(("a", CliInk.Dim), ("b", CliInk.Dim), ("c", CliInk.Accent), ("d", CliInk.Dim));
        var painted = w.ToString();

        //three colours in the row, so three painted runs and no more
        Assert.Equal(3, painted.Split(Esc + "[38").Length - 1);
        Assert.Contains(theme.Paint("ab", Theme.Dim), painted, StringComparison.Ordinal);
        //the merge changes the bytes and never the reading
        Assert.Equal("abcd", TermText.StripAnsiForWidth(painted).TrimEnd());
    }

    [Fact]
    public void MODEL_TEXT_CANNOT_DRIVE_THE_TERMINAL_THROUGH_THIS_LAYER()
    {
        //server-reported ids reach these lines, so the layer strips escapes before painting. stripping after would undo the colour it just added
        var (s, w) = Plain();
        s.Say(Esc + "[31mred" + Esc + "[0m");
        s.Ok(Esc + "[5mblink" + Esc + "[0m");
        s.Row((Esc + "[7minverse", CliInk.Accent));

        Assert.DoesNotContain(Esc, w.ToString());
        //the surviving word proves the test is not passing on an empty buffer.
        Assert.Contains("red", w.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_TICK_IS_THE_WIZARDS_OWN_GLYPH_and_not_a_second_one()
    {
        //the glyph table holds exactly one affirmative mark, and this surface borrows it, so a tick means the same thing everywhere.
        var (s, w) = Plain();
        s.Ok("done");

        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Ok, w.ToString(), StringComparison.Ordinal);
    }
}
