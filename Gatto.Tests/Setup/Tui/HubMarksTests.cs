using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a hub row matches the register by the oid its listing carries, file by file, and carries only the tested mark since it has no record here
public class HubMarksTests
{
    private static readonly GlyphSet G = GlyphSet.Unicode;
    private static readonly HardwareClass Machine =
        HardwareClassifier.Classify(new HardwareSnapshot(34359738368, 34093496320, GpuKind.Discrete, 8589934592));

    //one repo holding two files, the pick first, so a test can mark either one
    private static ModelRow Row(string? pickSha, string? otherSha = null)
    {
        var pick = new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, pickSha);
        var other = new HubQuant("m-Q6_K.gguf", 5_000_000_000, otherSha);
        return ShelfRows.Of("org/m-GGUF", "org", pick, FitRegime.FitsGpu, 32768, false, AllQuants: [pick, other]);
    }

    private static string Pane(ModelRow r, int width = 100) =>
        string.Join("\n", Gatto.Cli.Setup.Shelves.Pane.Rows(r, null, MachineShape.Discrete, width, glyphs: G).Select(x => x.Text));

    [Fact]
    public void A_PICK_WHOSE_OID_IS_SHIPPED_SHOWS_THE_TESTED_MARK_AND_WHAT_IT_FELL_SHORT_ON()
    {
        var shipped = ShippedRows.Any(fellShort: true);
        var pane = Pane(Row(shipped.Sha256));

        Assert.Contains($"tools     {G.Tested} {SearchRow.TestedWords}, fell short: {ShippedRows.Labels(shipped.FellShort, G)}",
            pane, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PICK_WITH_NO_OID_OR_AN_UNKNOWN_ONE_SHOWS_NOTHING()
    {
        Assert.DoesNotContain("tools ", Pane(Row(null)), StringComparison.Ordinal);
        Assert.DoesNotContain("tools ", Pane(Row(new string('0', 64))), StringComparison.Ordinal);
    }

    //a record filed under the same file name is the user's, and the hub shelf never reads it
    [Fact]
    public void A_HUB_ROW_NEVER_SHOWS_THE_MARKS_OF_THIS_MACHINE()
    {
        var shipped = ShippedRows.Any(fellShort: false);
        var probes = new FakeSetupProbes { Rows = [Row(shipped.Sha256)] };
        probes.Badges["m-Q4_K_M.gguf"] = new Badge("m-Q4_K_M.gguf", shipped.Sha256, true, [], new DateOnly(2026, 10, 1), "b", "defaults");

        var shelf = Assert.IsType<SetupScreen.Choice>(new SetupFlow(probes).StartPastEngine()).Shelf!;
        var row = Assert.Single(shelf.Rows);
        Assert.Null(row.Badge);
        Assert.NotNull(row.Tested);
        Assert.DoesNotContain(G.PassedHere, Pane(row), StringComparison.Ordinal);
    }

    //the tools line describes the pick, and the tested file beside it carries its glyph in the file list
    [Fact]
    public void AN_UNTESTED_PICK_BESIDE_A_TESTED_FILE_HAS_NO_TOOLS_LINE_AND_THE_FILE_CARRIES_THE_GLYPH()
    {
        var shipped = ShippedRows.Any(fellShort: false);
        var row = Row(null, shipped.Sha256);

        Assert.DoesNotContain("tools ", Pane(row), StringComparison.Ordinal);
        var files = SetupFlow.QuantsOf(row, Machine)!;
        Assert.True(files.Single(f => f.Quant == "Q6_K").Marks.Tested);
        Assert.False(files.Single(f => f.Quant == "Q4_K_M").Marks.Tested);
        Assert.True(SearchRow.ListedMarks(row, null).Tested);
    }

    //the mark is computed from the pick, so moving the pick moves it
    [Fact]
    public void A_REPICK_MOVES_THE_TOOLS_LINE_WITH_IT()
    {
        var shipped = ShippedRows.Any(fellShort: false);
        var row = Row(null, shipped.Sha256);
        Assert.Null(row.Tested);

        var repicked = row with { RowFile = new FileRef("org", "org/m-GGUF", "m-Q6_K.gguf") };
        Assert.Equal(shipped.Sha256, repicked.Tested!.Sha256);
        Assert.Contains($"{G.Tested} {SearchRow.TestedWords}", Pane(repicked), StringComparison.Ordinal);
    }

    //two marks wrap at the two-space seam, the second under the first glyph, and a clause too long for its line goes under its words
    [Fact]
    public void TWO_MARKS_WRAP_AT_THE_SEAM_UNDER_THE_FIRST_GLYPH_AT_80_COLUMNS()
    {
        var shipped = ShippedRows.Any(fellShort: true);
        var row = Row(shipped.Sha256) with
        {
            Badge = new Badge("m-Q4_K_M.gguf", shipped.Sha256, true, ["B5"], new DateOnly(2026, 10, 1), "b", "defaults"),
        };
        var lines = Gatto.Cli.Setup.Shelves.Pane.ToolsRows(row, "tools     ", 80, G).Select(x => x.Text).ToList();

        Assert.Equal(3, lines.Count);
        Assert.StartsWith($"tools     {G.Tested} {SearchRow.TestedWords}, fell short: ", lines[0], StringComparison.Ordinal);
        Assert.Equal($"          {G.PassedHere} {SearchRow.PassedHereWords}", lines[1]);
        Assert.Equal("            fell short: " + ShippedRows.Labels(["B5"], G), lines[2]);
        Assert.All(lines, l => Assert.True(UnicodeWidth.Of(l) <= 80, l));
    }

    //a pane narrower than the clause wraps it by word under the mark's words, and every word of every task stays on screen
    [Theory]
    [InlineData(44)]
    [InlineData(30)]
    public void A_CLAUSE_WIDER_THAN_THE_PANE_WRAPS_BY_WORD_AND_LOSES_NOTHING(int width)
    {
        var shipped = ShippedRows.Any(fellShort: true);
        var row = Row(shipped.Sha256) with
        {
            Badge = new Badge("m-Q4_K_M.gguf", shipped.Sha256, true, ["B5"], new DateOnly(2026, 10, 1), "b", "defaults"),
        };
        var lines = Gatto.Cli.Setup.Shelves.Pane.ToolsRows(row, "tools     ", width, G).Select(x => x.Text).ToList();

        Assert.All(lines, l => Assert.True(UnicodeWidth.Of(l) <= width, $"{UnicodeWidth.Of(l)} cells: {l}"));
        var said = string.Join(" ", lines.Select(l => l.Trim()));
        foreach (var word in (ShippedRows.Labels(shipped.FellShort, G) + " " + ShippedRows.Labels(["B5"], G)).Split(' '))
            Assert.Contains(word, said, StringComparison.Ordinal);
    }

    //a folder cut to its tail fits its cells in either set, since the ascii ellipsis is three cells and a line that overruns loses its last mark
    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void A_CUT_FOLDER_FITS_ITS_CELLS_IN_BOTH_SETS(int width)
    {
        const string folder = @"C:\Users\someone\weights-with-a-long-name\gemma-4-e4b-qat-folder";
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
            Assert.True(UnicodeWidth.Of(Gatto.Cli.Setup.Shelves.Pane.PathTail(folder, width, g)) <= width,
                Gatto.Cli.Setup.Shelves.Pane.PathTail(folder, width, g));
    }

    //the count row names each mark a listed file carries and none when no file carries one, and the footer keeps the fit marks alone
    [Fact]
    public void THE_COUNT_ROW_NAMES_A_MARK_ONLY_WHEN_A_LISTED_FILE_CARRIES_IT()
    {
        var shipped = ShippedRows.Any(fellShort: false);
        var shape = Gatto.Core.Hardware.MachineShape.Discrete;
        string Count(params ModelRow[] rows) =>
            string.Concat(Gatto.Cli.Setup.Shelves.Shelf.CountRuns(new ShelfView(rows, shape), G).Select(x => x.Text));

        var bare = Count(Row(null));
        Assert.DoesNotContain(SearchRow.TestedWords, bare, StringComparison.Ordinal);

        var listed = Count(Row(null, shipped.Sha256));
        Assert.EndsWith($" {G.Dot} {G.Tested} {SearchRow.TestedWords}", listed, StringComparison.Ordinal);

        var failed = Count(Row(shipped.Sha256) with { Badge = new Badge("m-Q4_K_M.gguf", shipped.Sha256, false, [], new DateOnly(2026, 10, 1), "b", "d") });
        Assert.EndsWith($"{G.Tested} {SearchRow.TestedWords} {G.Dot} {G.FailedHere} {SearchRow.FailedHereWords}", failed, StringComparison.Ordinal);
        Assert.DoesNotContain(SearchRow.PassedHereWords, failed, StringComparison.Ordinal);
        Assert.DoesNotContain(SearchRow.TestedWords, FitMarks.LegendFor(shape, G).Text, StringComparison.Ordinal);
    }

    //a tested file the pane would not list, under the size floor for the row's params, brings no words for a glyph no screen shows
    [Fact]
    public void A_TESTED_FILE_UNDER_THE_FLOOR_BRINGS_NO_MARK()
    {
        var shipped = ShippedRows.Any(fellShort: false);
        var pick = new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null);
        var tiny = new HubQuant("m-IQ1_S.gguf", 1_000_000, shipped.Sha256);
        var row = ShelfRows.Of("org/m-GGUF", "org", pick, FitRegime.FitsGpu, 32768, false, Params: 8_000_000_000, AllQuants: [pick, tiny]);

        Assert.DoesNotContain(HubSearch.Candidates(row.Publishers[0].Repos[0].Quants, row.Params), q => q.FileName == "m-IQ1_S.gguf");
        Assert.False(SearchRow.ListedMarks(row, null).Tested);
    }

    //a count row too wide for the frame moves the mark words to rows of their own, so every word stays on screen
    [Fact]
    public void A_NARROW_FRAME_PUTS_THE_MARK_WORDS_UNDER_THE_COUNT_AND_CUTS_NONE()
    {
        var shipped = ShippedRows.Any(fellShort: false);
        var row = Row(shipped.Sha256) with { Badge = new Badge("m-Q4_K_M.gguf", shipped.Sha256, false, [], new DateOnly(2026, 10, 1), "b", "d") };
        var view = new ShelfView([row], Gatto.Core.Hardware.MachineShape.Discrete);
        var frame = Gatto.Cli.Setup.Shelves.Shelf.Body(view, row: 0, chip: 0, file: -1, Region.List, 60, glyphs: G).Select(r => r.Text).ToList();

        Assert.All(frame, l => Assert.True(UnicodeWidth.Of(l) <= 60, $"{UnicodeWidth.Of(l)} cells: {l}"));
        var said = string.Join(" ", frame);
        Assert.Contains($"{G.Tested} {SearchRow.TestedWords}", said, StringComparison.Ordinal);
        Assert.Contains($"{G.FailedHere} {SearchRow.FailedHereWords}", said, StringComparison.Ordinal);
    }

    //two tasks fallen short on join with the shelf's dot, in the battery's own labels
    [Fact]
    public void TWO_TASKS_JOIN_WITH_THE_DOT()
    {
        Assert.Equal(", fell short: " + ShippedRows.Labels(["B3", "B4"], G), SearchRow.FellShort(["B3", "B4"], G));
        Assert.Contains($" {G.Dot} ", SearchRow.FellShort(["B3", "B4"], G), StringComparison.Ordinal);
        Assert.Equal("", SearchRow.FellShort([], G));
    }
}
