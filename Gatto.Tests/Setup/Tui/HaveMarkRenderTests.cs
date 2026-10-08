using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//no golden draws a have-mark through the wizard's face, so these tests build the view and assert the marks on the screen
public class HaveMarkRenderTests
{
    //the unified shape the corpus frames were drawn on, which has no runs column, so the have column's width is readable
    private const MachineShape NoRunsColumn = MachineShape.UnifiedWithShare;

    private static ModelRow Row(string repo = "unsloth/gemma-4-26B-A4B-it",
        string file = "gemma-4-26B-A4B-it-Q4_K_M.gguf") =>
        ShelfRows.Of(repo, "unsloth", new HubQuant(file, 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 25_200_000_000);

    private static ShelfView Hub(MachineShape shape, params HaveMark[] marks) =>
        new([.. marks.Select((_, i) => Row($"unsloth/model-{i}", $"model-{i}-Q4_K_M.gguf"))],
            shape,
            Total: marks.Length,
            Facts: [.. marks.Select(m => new ModelFacts(Structure: "dense", Have: m))],
            Source: ShelfSource.Hub);

    private static ShelfView Local(MachineShape shape, params HaveMark[] marks) =>
        new([.. marks.Select((_, i) => Row($"unsloth/model-{i}", $"model-{i}-Q4_K_M.gguf"))],
            shape,
            Total: marks.Length,
            Facts: [.. marks.Select(m => new ModelFacts(
                Structure: "dense", LocalPath: @"C:\weights\model\", FilesHere: "1 file", Have: m))],
            Source: ShelfSource.Local);

    private static IReadOnlyList<string> TableText(ShelfView v) =>
        [.. Shelf.Table(v, 0, focused: false, glyphs: GlyphSet.Unicode).Select(r => r.Text)];

    //site 1: the Hub table's tail

    //in the Hub table the mark is a word in the row's tail, on the row gatto has and on no other
    [Fact]
    public void THE_HUB_TABLE_SAYS_THE_WORD_IN_THE_ROWS_TAIL()
    {
        var text = TableText(Hub(NoRunsColumn, HaveMark.Added, HaveMark.None));

        Assert.Contains("✓ added", text[1], StringComparison.Ordinal);
        //an unmarked row shows no glyph at all, which is where a code that marks everything fails
        Assert.DoesNotContain("✓", text[2], StringComparison.Ordinal);
        Assert.DoesNotContain("added", text[2], StringComparison.Ordinal);
    }

    //the have-mark outranks the fit mark, so the tail holds one column, and the fixture keeps a runs column so the two disagree
    [Fact]
    public void AND_THE_HAVE_MARK_OUTRANKS_THE_FIT_MARK_IN_THAT_TAIL()
    {
        var text = TableText(Hub(MachineShape.Discrete, HaveMark.Added));
        var fit = FitMarks.Of(FitRegime.FitsGpu, MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Contains("✓ added", text[1], StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(fit.Word), "the fixture needs a fit mark that has a word");
        Assert.DoesNotContain(fit.Word, text[1], StringComparison.Ordinal);
    }

    //the column widens the table by ten cells, and only when a row has a mark. a rule that always widened would cost the pane ten cells on every shelf
    [Fact]
    public void THE_COLUMN_COSTS_TEN_CELLS_AND_ONLY_WHEN_A_ROW_WEARS_A_MARK()
    {
        var without = Shelf.LeftWidth(Hub(NoRunsColumn, HaveMark.None), g: GlyphSet.Unicode);
        var with = Shelf.LeftWidth(Hub(NoRunsColumn, HaveMark.Added), g: GlyphSet.Unicode);

        Assert.Equal(10, with - without);
    }

    //on a machine that already has a runs column the width does not move, since the mark shares that column
    [Fact]
    public void BUT_A_SHELF_THAT_ALREADY_HAS_A_RUNS_COLUMN_DOES_NOT_GROW()
    {
        var without = Shelf.LeftWidth(Hub(MachineShape.Discrete, HaveMark.None), g: GlyphSet.Unicode);
        var with = Shelf.LeftWidth(Hub(MachineShape.Discrete, HaveMark.Added), g: GlyphSet.Unicode);

        Assert.Equal(without, with);
    }

    //the golden diffs plain text and the terminal shows the ink, so an assertion on the painted run is the only oracle
    [Fact]
    public void THE_GLYPH_CARRIES_THE_STATUS_INK_THAT_PLAIN_TEXT_CANNOT_SHOW()
    {
        var row = Shelf.Table(Hub(NoRunsColumn, HaveMark.Added), 0, focused: false, glyphs: GlyphSet.Unicode)[1];

        var glyph = Assert.Single(row.Runs, run => run.Text == "✓");
        Assert.Equal(RunInk.Ok, glyph.Ink);
    }

    //site 2: the local table's glyph column

    //on the local table the cell is one glyph wide, since a word-wide cell beside 24-cell file names starves the pane
    [Fact]
    public void THE_LOCAL_TABLE_SHOWS_ONE_GLYPH_AND_NEVER_THE_WORD()
    {
        var text = string.Join("\n", TableText(Local(NoRunsColumn, HaveMark.Added, HaveMark.Loaded)));

        Assert.Contains("✓", text, StringComparison.Ordinal);
        Assert.Contains("●", text, StringComparison.Ordinal);
        Assert.DoesNotContain("added", text, StringComparison.Ordinal);
        Assert.DoesNotContain("loaded", text, StringComparison.Ordinal);
    }

    //the local column costs three cells (two of separator and one of glyph), since the glyph sits one cell short of the divider
    [Fact]
    public void AND_THE_LOCAL_COLUMN_COSTS_ONLY_THE_GLYPH()
    {
        var without = Shelf.LeftWidth(Local(NoRunsColumn, HaveMark.None), g: GlyphSet.Unicode);
        var with = Shelf.LeftWidth(Local(NoRunsColumn, HaveMark.Loaded), g: GlyphSet.Unicode);

        Assert.Equal(3, with - without);
    }

    //a size and quant pair wider than its column pushes the pane rule onto the divider's cell, so the fixture runs a width sweep
    [Fact]
    public void A_WIDE_SIZE_NEVER_PUSHES_THE_PANE_RULE_ONTO_THE_ROW()
    {
        static ModelRow Wide(int i, string file, long bytes) =>
            ShelfRows.Of($"unsloth/model-{i}", "unsloth", new HubQuant(file, bytes, null),
                FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
                Params: 35_000_000_000);
        var v = new ShelfView(
            [Wide(0, "Qwen3.6-35B-A3B-UD-Q5_K_S.gguf", 25_560_000_000),
             Wide(1, "Qwen3.8-Flash-Next-Q4_K_XL.gguf", 102_440_000_000),
             Wide(2, "Big-Model-Q4_K_XL.gguf", 600_000_000_000)], NoRunsColumn, Total: 3,
            Facts: [new ModelFacts(Structure: "MoE A3B", LocalPath: @"C:\weights\a\", FilesHere: "1 file", Have: HaveMark.Loaded),
                    new ModelFacts(Structure: "MoE", LocalPath: @"C:\weights\b\", FilesHere: "1 file", Have: HaveMark.Added),
                    new ModelFacts(Structure: "dense", LocalPath: @"C:\weights\c\", FilesHere: "1 file", Have: HaveMark.None)],
            Source: ShelfSource.Local);
        var pairs = string.Join("\n", Shelf.Table(v, 0, focused: false, glyphs: GlyphSet.Unicode).Select(r => r.Text));
        Assert.Contains("23.8 GB  Q5_K_S", pairs, StringComparison.Ordinal);
        Assert.Contains("95.4 GB  Q4_K_XL", pairs, StringComparison.Ordinal);
        Assert.Contains("558.8 GB Q4_K_XL", pairs, StringComparison.Ordinal);
        var rule = GlyphSet.Unicode.Box.Vertical;
        var left = Shelf.LeftWidth(v, g: GlyphSet.Unicode);
        var joined = 0;

        foreach (var width in new[] { 80, 90, 100, 120, 140 })
            foreach (var text in Shelf.Body(v, row: 0, chip: -1, file: -1, Region.List, width,
                         glyphs: GlyphSet.Unicode).Select(r => r.Text))
            {
                var at = text.IndexOf(rule, StringComparison.Ordinal);
                if (at < 0) continue;
                joined++;
                Assert.True(at == left, $"at {width} the rule sits at column {at}, not {left}: [{text}]");
                if (text.Contains('●') || text.Contains('✓'))
                    Assert.True(text[at - 1] == ' ', $"at {width} the glyph row touches the rule: [{text}]");
            }

        //some width drew the pane, so the sweep reached the join
        Assert.True(joined > 0, "no width drew the pane, so nothing was checked");
    }

    //site 3: the count line's legend

    //the local shelf's count line holds the legend, since its cells are bare glyphs, and only the marks on screen are explained
    [Fact]
    public void THE_LOCAL_COUNT_LINE_EXPLAINS_EXACTLY_THE_MARKS_ON_SCREEN()
    {
        var both = Shelf.CountLine(Local(NoRunsColumn, HaveMark.Loaded, HaveMark.Added), glyphs: GlyphSet.Unicode);
        var addedOnly = Shelf.CountLine(Local(NoRunsColumn, HaveMark.Added), glyphs: GlyphSet.Unicode);

        Assert.Equal("1–2 of 2 · ● loaded · ✓ added", both);
        Assert.Contains("✓ added", addedOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("● loaded", addedOnly, StringComparison.Ordinal);
    }

    //a shelf with no marks gets no legend and no trailing separator, so a line that always appends them fails
    [Fact]
    public void AND_A_SHELF_WITH_NO_MARKS_CARRIES_NO_LEGEND()
    {
        var line = Shelf.CountLine(Local(NoRunsColumn, HaveMark.None), glyphs: GlyphSet.Unicode);

        Assert.Equal("1–1 of 1", line);
    }

    //only the local shelf's count line holds the legend, since a Hub row says its mark in words
    [Fact]
    public void THE_HUB_COUNT_LINE_HAS_NO_LEGEND_BECAUSE_ITS_ROWS_SAY_THE_WORDS()
    {
        var line = Shelf.CountLine(Hub(NoRunsColumn, HaveMark.Loaded, HaveMark.Added), glyphs: GlyphSet.Unicode);

        Assert.Equal("1–2 of 2", line);
    }

    //site 4: the pane's first fact

    //the pane names what you have as its first fact, compacting below 36 cells
    [Fact]
    public void THE_PANE_NAMES_WHAT_YOU_HAVE_AS_ITS_FIRST_FACT()
    {
        var rows = Pane.Rows(Row(), new ModelFacts(Have: HaveMark.Added), NoRunsColumn, 30)
            .Select(r => r.Text.TrimEnd()).ToList();

        Assert.Contains("✓ added · Q4_K_M", rows);
    }

    //the wide form names gatto's own id, and it renders on either shelf since HaveText does not branch on the source
    [Fact]
    public void THE_WIDE_FORM_NAMES_GATTOS_OWN_ID_ON_EITHER_SHELF()
    {
        var hub = new ModelFacts(Have: HaveMark.Added, HaveId: "gemma-4-e4b-it");
        var local = hub with { LocalPath = @"C:\weights\gemma\", FilesHere = "1 file" };

        var wide = Pane.Rows(Row(), hub, NoRunsColumn, 36).Select(r => r.Text.TrimEnd()).ToList();
        var narrow = Pane.Rows(Row(), hub, NoRunsColumn, 35).Select(r => r.Text.TrimEnd()).ToList();
        var onDisk = Pane.Rows(Row(), local, NoRunsColumn, 36).Select(r => r.Text.TrimEnd()).ToList();

        Assert.Contains("✓ added as gemma-4-e4b-it · Q4_K_M", wide);
        //the boundary, one cell narrower, so the threshold is told apart from a line that always names the id
        Assert.Contains("✓ added · Q4_K_M", narrow);
        Assert.DoesNotContain("✓ added as gemma-4-e4b-it · Q4_K_M", narrow);
        Assert.Contains("✓ added as gemma-4-e4b-it · Q4_K_M", onDisk);
    }

    //a producer with no id for an added model falls back to the compact form rather than a sentence with a hole in it
    [Fact]
    public void AND_AN_ADDED_MODEL_WITH_NO_ID_STAYS_COMPACT()
    {
        var rows = Pane.Rows(Row(), new ModelFacts(Have: HaveMark.Added), NoRunsColumn, 36)
            .Select(r => r.Text.TrimEnd()).ToList();

        Assert.Contains("✓ added · Q4_K_M", rows);
        Assert.DoesNotContain(rows, r => r.Contains(" as ", StringComparison.Ordinal));
    }

    //a row with nothing to say is left out, since a pane that reserves it shows a blank on every unmarked frame
    [Fact]
    public void AND_AN_UNMARKED_MODEL_ADDS_NO_ROW_AT_ALL()
    {
        var marked = Pane.Rows(Row(), new ModelFacts(Have: HaveMark.Added), NoRunsColumn, 30).Count;
        var bare = Pane.Rows(Row(), new ModelFacts(Have: HaveMark.None), NoRunsColumn, 30).Count;

        Assert.Equal(1, marked - bare);
    }

    //the sentence renders when the pane can hold it, measured against the sentence's own width rather than a pinned number
    [Fact]
    public void THE_LOADED_SENTENCE_RENDERS_WHEN_THE_PANE_CAN_HOLD_IT_AND_COMPACTS_WHEN_IT_CANNOT()
    {
        const string sentence = "● loaded · the model you're on";
        var fits = Gatto.Terminal.UnicodeWidth.Of(sentence);

        var wide = Pane.Rows(Row(), new ModelFacts(Have: HaveMark.Loaded), NoRunsColumn, fits)
            .Select(r => r.Text.TrimEnd()).ToList();
        var narrow = Pane.Rows(Row(), new ModelFacts(Have: HaveMark.Loaded), NoRunsColumn, fits - 1)
            .Select(r => r.Text.TrimEnd()).ToList();

        Assert.Contains(sentence, wide);
        Assert.DoesNotContain(sentence, narrow);
        Assert.Contains("● loaded", narrow);
    }

    //site 5: the pane's file list

    //the quant on disk is marked in the file list, where the have-mark outranks the fit mark as it does in the Hub tail
    [Fact]
    public void THE_FILE_LIST_MARKS_THE_QUANT_ON_DISK()
    {
        //two files, so the mark is tied to the quant on disk rather than to the whole list
        var facts = new ModelFacts(
            Have: HaveMark.Loaded,
            Publishers: [new PanePublisher("unsloth", [
                new PaneFile("Q4_K_M", 5_000_000_000, FitRegime.FitsGpu),
                new PaneFile("Q6_K", 7_100_000_000, FitRegime.FitsGpu)], null)]);

        //the publisher open, and its own line left out since it names no file
        var rows = Pane.Rows(Row(), facts, NoRunsColumn, 36, cursor: 1, open: 0)
            .Select(r => r.Text.TrimEnd()).Where(r => !r.Contains('▾')).ToList();

        var onDisk = Assert.Single(rows, r => r.Contains("Q4_K_M", StringComparison.Ordinal));
        var other = Assert.Single(rows, r => r.Contains("Q6_K", StringComparison.Ordinal));

        Assert.Contains("● loaded", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("● loaded", other, StringComparison.Ordinal);
        Assert.Contains(FitMarks.Of(FitRegime.FitsGpu, NoRunsColumn, glyphs: GlyphSet.Unicode).Word, other, StringComparison.Ordinal);
    }
}
