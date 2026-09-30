using Gatto.Cli.Setup;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//both wrapping rules go through the margin knob, so the plain face at margin 0 keeps its line breaks
public class WizardRowsTests
{
    private static string[] Wrap(string text, int width, int margin) =>
        [.. WizardRows.Wrap(new WizardRow(text), width, margin, Gatto.Terminal.GlyphSet.Unicode).Select(s => s.Lead + s.Text)];

    //the same sentence widows at margin 0 and not at the frame margin, so one fixture proves both halves
    [Fact]
    public void A_PARAGRAPH_NEVER_ENDS_ON_A_SINGLE_WORD_INSIDE_THE_FRAME()
    {
        const string text = "one two three four five six seven eight nine tenlongword";

        //the widow is there at margin 0, so the rule below has something to fix
        Assert.Equal("tenlongword", Wrap(text, 50, margin: 0)[^1].Trim());

        var framed = Wrap(text, 50, WizardRows.FrameMargin);
        Assert.Equal("nine tenlongword", framed[^1].Trim());

        //the same words in order, so the wrap lost and duplicated nothing
        Assert.Equal(text, string.Join(" ", framed.Select(l => l.Trim())));
    }

    //the last line is one word here, so matching the framed shape would mean the margin knob stopped isolating them
    [Fact]
    public void THE_TRANSCRIPT_FACE_IS_UNTOUCHED_BY_EITHER_RULE()
    {
        const string text = "one two three four five six seven eight nine tenlongword";

        var transcript = Wrap(text, 50, margin: 0);
        Assert.Single(transcript[^1].Trim().Split(' '));
        Assert.NotEqual(Wrap(text, 50, WizardRows.FrameMargin)[^1], transcript[^1]);
    }

    //taking a word from a one-word donor line would widow the line above, so the wrap declines instead
    [Fact]
    public void IT_DECLINES_WHEN_TAKING_A_WORD_WOULD_WIDOW_THE_LINE_ABOVE()
    {
        //two words nearly a full line each, then a short tail, so the donor line has nothing to give
        var wrapped = Wrap("aaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbb cc", 26, WizardRows.FrameMargin);

        Assert.Equal(3, wrapped.Length);
        Assert.Equal("cc", wrapped[^1].Trim());
    }

    //a merge wider than the frame would be cut, so the widow stands and no word is lost
    [Fact]
    public void IT_DECLINES_WHEN_THE_MERGED_LINE_WOULD_NOT_FIT()
    {
        const int width = 19;
        var wrapped = Wrap("wxyz 1234567890 abcdefghij", width, WizardRows.FrameMargin);

        //the widow stays, the merge would not fit the frame
        Assert.Equal("abcdefghij", wrapped[^1].Trim());
        Assert.All(wrapped, l => Assert.True(l.Length <= width,
            $"a de-widowed row reaches {l.Length} cells against a {width}-cell frame: [{l}]"));
    }

    //no width may produce a row wider than the frame, since the merge only overflows where the arithmetic lines up
    [Theory]
    [InlineData("wxyz 1234567890 abcdefghij")]
    [InlineData("one two three four five six seven eight nine tenlongword")]
    [InlineData("gatto asks before anything is downloaded or written, and Esc backs out at any point.")]
    public void NO_WIDTH_PRODUCES_A_ROW_WIDER_THAN_THE_FRAME(string text)
    {
        for (var width = 12; width <= 120; width++)
            Assert.All(Wrap(text, width, WizardRows.FrameMargin), l => Assert.True(l.Length <= width,
                $"at {width} columns a row reaches {l.Length} cells: [{l}]"));
    }

    //the frame keeps two cells free at the right edge, so the widest body row is w - 2
    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    public void WRAPPED_PROSE_STOPS_TWO_CELLS_SHORT_OF_THE_FRAME(int width)
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));

        var framed = Wrap(text, width, WizardRows.FrameMargin);
        Assert.All(framed, l => Assert.True(l.Length <= width - 2, $"row reaches {l.Length} of {width}"));
        Assert.Contains(framed, l => l.Length > width - 2 - 5);   //and one row really uses the budget, or a wrap that stopped early would still pass
    }

    //a status mark heads only the first line, so its continuation drops the text mark and the Glyph field. a kept Glyph would ink the first cell of the indent
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(78)]
    public void A_STATUS_MARK_LEADS_ONLY_THE_FIRST_LINE_OF_ITS_ROW(int width)
    {
        var row = new WizardRow(
            string.Join(" ", Enumerable.Repeat("word", 40)), Glyph: RowGlyph.Good);

        var lines = WizardRows.Toned([row], width, WizardRows.FrameMargin).ToList();

        Assert.True(lines.Count > 1, $"the fixture did not wrap at {width}, so nothing below is tested");
        Assert.StartsWith(Glyphs.Of(RowGlyph.Good, glyphs: GlyphSet.Unicode) + " ", lines[0].Text, StringComparison.Ordinal);
        Assert.Equal(RowGlyph.Good, lines[0].Glyph);

        foreach (var line in lines.Skip(1))
        {
            Assert.StartsWith("  ", line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Glyphs.Of(RowGlyph.Good, glyphs: GlyphSet.Unicode), line.Text, StringComparison.Ordinal);
            Assert.Null(line.Glyph);
        }
    }

    //the mark stays out of the text and takes the gutter's cells, so both rows start in the same column
    [Fact]
    public void A_MARKED_ROW_AND_A_PLAIN_ROW_START_THEIR_TEXT_IN_THE_SAME_COLUMN()
    {
        var marked = WizardRows.Toned([new WizardRow("hello", Glyph: RowGlyph.Good)], 100, 2).Single();
        var plain = WizardRows.Toned([new WizardRow("hello")], 100, 2).Single();

        Assert.Equal(plain.Text.IndexOf("hello", StringComparison.Ordinal),
            Gatto.Terminal.UnicodeWidth.Of(marked.Text[..marked.Text.IndexOf("hello", StringComparison.Ordinal)]));
    }

    //a wide row may spend the margin but not the width, or the row after it starts in a column nobody chose
    [Fact]
    public void A_WIDE_ROW_SPENDS_THE_MARGIN_AND_NEVER_THE_WIDTH()
    {
        var text = string.Join(' ', Enumerable.Repeat("alpha bravo charlie delta echo foxtrot", 6));
        var spent = false;

        for (var width = 40; width <= 120; width++)
        {
            var wide = WizardRows.Wrap(new WizardRow(text, Wide: true), width, WizardRows.FrameMargin, Gatto.Terminal.GlyphSet.Unicode)
                .Select(s => s.Lead + s.Text).ToList();
            var prose = WizardRows.Wrap(new WizardRow(text), width, WizardRows.FrameMargin, Gatto.Terminal.GlyphSet.Unicode)
                .Select(s => s.Lead + s.Text).ToList();

            foreach (var line in wide)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(line) <= width,
                    $"a wide row reached {Gatto.Terminal.UnicodeWidth.Of(line)} cells at {width}: [{line}]");

            //a break may fall in the same place either way, so the sweep only needs one width where the wide row renders wider
            if (wide.Max(l => Gatto.Terminal.UnicodeWidth.Of(l))
                > prose.Max(l => Gatto.Terminal.UnicodeWidth.Of(l)))
                spent = true;
        }

        Assert.True(spent, "no width in the sweep rendered a wide row any wider than a prose one, "
            + "so this guard would pass on a flag that does nothing");
    }
    //a highlighted span is atomic to the wrapper

    private static string[] Wrap(WizardRow row, int width, int margin) =>
        [.. WizardRows.Wrap(row, width, margin, Gatto.Terminal.GlyphSet.Unicode).Select(s => s.Lead + s.Text)];

    //a highlight is matched per line, so a span broken across a wrap never fires and reads as two commands
    [Fact]
    public void A_HIGHLIGHTED_COMMAND_IS_NEVER_BROKEN_ACROSS_A_WRAP()
    {
        const string cmd = "gatto audition gemma-4-26B-A4B-it";
        var row = new WizardRow($"The check couldn't run yet. Type {cmd} in a terminal to run it.",
            Highlight: [cmd]);

        for (var w = 40; w <= 120; w++)
            Assert.Contains(Wrap(row, w, 2), l => l.Contains(cmd, StringComparison.Ordinal));
    }

    //the same sentence with no highlight breaks in the sweep, which is what proves the guard above is about the highlight
    [Fact]
    public void AND_THE_SAME_WORDS_UNHIGHLIGHTED_STILL_BREAK()
    {
        const string cmd = "gatto audition gemma-4-26B-A4B-it";
        var plain = new WizardRow($"The check couldn't run yet. Type {cmd} in a terminal to run it.");

        Assert.Contains(Enumerable.Range(40, 81),
            w => !Wrap(plain, w, 2).Any(l => l.Contains(cmd, StringComparison.Ordinal)));
    }

    //the placeholder measures one cell and never reaches a terminal, checked on the rendered lines rather than the constant
    [Fact]
    public void THE_PLACEHOLDER_LEAVES_NO_TRACE_AND_COSTS_NO_CELLS()
    {
        const string cmd = "gatto serve start gemma-4-26B-A4B-it";
        var row = new WizardRow($"Type {cmd} when you want to run it.", Highlight: [cmd]);

        for (var w = 40; w <= 120; w++)
        {
            var lines = Wrap(row, w, 2);
            Assert.DoesNotContain(lines, l => l.Contains('\u2007'));
            //a placeholder that measured anything but one cell turns up here as a row over budget
            Assert.All(lines, l => Assert.True(Gatto.Terminal.UnicodeWidth.Of(l) <= w,
                $"a row ran to {Gatto.Terminal.UnicodeWidth.Of(l)} cells at width {w}: {l}"));
        }
    }

    //a span with no space is already atomic and a span absent from the text changes nothing, so neither touches the row
    [Fact]
    public void A_SPAN_WITH_NO_SPACE_OR_NO_MATCH_LEAVES_THE_ROW_ALONE()
    {
        const string text = "Run gatto from where it is, and have fun with your local model.";

        Assert.Equal(Wrap(new WizardRow(text), 60, 2),
            Wrap(new WizardRow(text, Highlight: ["gatto"]), 60, 2));
        Assert.Equal(Wrap(new WizardRow(text), 60, 2),
            Wrap(new WizardRow(text, Highlight: ["a command nobody wrote"]), 60, 2));
    }
}
