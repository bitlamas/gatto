using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

public class TableLayoutAllocationTests
{
    [Fact]
    public void NaturalWidthIsWidestOfHeaderAndCells_InDisplayCells()
    {
        var spec = new TableSpec(
            new[] { "model", "名前" },
            new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "minimax", "ミニマックス" } });
        Assert.Equal(new[] { 7, 12 }, TableLayout.Natural(spec));   //4 cells for 名前 and 12 for ミニマックス
    }

    [Fact]
    public void FitsWithinBudget_EveryColumnKeepsNaturalWidth()
    {
        Assert.Equal(new[] { 10, 4 }, TableLayout.Allocate(new[] { 10, 4 }, 40));
    }

    [Fact]
    public void OverBudget_DeficitComesOffTheWidestColumns()
    {
        //17+12+7+4 is 40 into a 38 budget, so the widest columns cap at 15
        Assert.Equal(new[] { 15, 12, 7, 4 }, TableLayout.Allocate(new[] { 17, 12, 7, 4 }, 38));
    }

    [Fact]
    public void ResidualCellsGoLeftmostCappedFirst()
    {
        //20+20+3 is 43 into 40, and the leftover cell after the cap goes to the leftmost column
        Assert.Equal(new[] { 19, 18, 3 }, TableLayout.Allocate(new[] { 20, 20, 3 }, 40));
    }

    [Fact]
    public void ResidualNeverPushesAColumnPastItsNaturalWidth()
    {
        var w = TableLayout.Allocate(new[] { 5, 30 }, 40);
        Assert.Equal(5, w[0]);
        Assert.Equal(30, w[1]);
    }

    [Fact]
    public void EveryColumnGetsAtLeastOneCell()
    {
        Assert.All(TableLayout.Allocate(new[] { 40, 40, 40 }, 5), c => Assert.True(c >= 1));
    }

    [Fact]
    public void ChromeIsVerticalsPlusPadding()
    {
        Assert.Equal(13, TableLayout.Chrome(4));   //5 verticals and 8 padding cells for 4 columns
    }

    [Fact]
    public void AZeroNaturalColumnGetsZeroCells()
    {
        //only a column with a nonzero natural width gets a cell, since one for an empty column buys a stray space
        Assert.Equal(new[] { 10, 0 }, TableLayout.Allocate(new[] { 10, 0 }, 40));
    }
}

public class TableGridTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    private static IReadOnlyList<string> Render(TableSpec spec, int width) =>
        TableLayout.Rows(spec, Plain, width, glyphs: GlyphSet.Unicode).Select(r => r.Text).ToList();

    private static TableSpec Simple() => new(
        new[] { "tool", "origin" },
        new[] { ColumnAlign.Left, ColumnAlign.Left },
        new[] { new[] { "read_file", "" }, new[] { "ask_user", "bundled" } });

    [Fact]
    public void DrawsFullBoxWithARuleBetweenEveryRow()
    {
        var rows = Render(Simple(), 40);
        Assert.Equal("┌───────────┬─────────┐", rows[0]);
        Assert.Equal("├───────────┼─────────┤", rows[2]);
        Assert.Equal("├───────────┼─────────┤", rows[4]);   //the rule line appears between body rows as well as under the header
        Assert.Equal("└───────────┴─────────┘", rows[^1]);
    }

    [Fact]
    public void HeaderIsCentered_BodyIsLeftAligned()
    {
        var rows = Render(Simple(), 40);
        Assert.Equal("│   tool    │ origin  │", rows[1]);
        Assert.Equal("│ read_file │         │", rows[3]);
    }

    [Fact]
    public void OddPaddingPutsTheExtraCellOnTheRight()
    {
        var spec = new TableSpec(new[] { "ab" }, new[] { ColumnAlign.Left },
            new[] { new[] { "abcde" } });
        Assert.Equal("│  ab   │", Render(spec, 40)[1]);   //3 cells of padding, 1 left and 2 right
    }

    [Fact]
    public void AlignmentMarkersAreHonoured()
    {
        var spec = new TableSpec(
            new[] { "left", "center", "right" },
            new[] { ColumnAlign.Left, ColumnAlign.Center, ColumnAlign.Right },
            new[] { new[] { "x", "mid", "42" } });
        Assert.Equal("│ x    │  mid   │    42 │", Render(spec, 40)[3]);
    }

    [Fact]
    public void HeaderOnlyTableRendersHeaderBlock()
    {
        var spec = new TableSpec(new[] { "a", "b" },
            new[] { ColumnAlign.Left, ColumnAlign.Left }, Array.Empty<IReadOnlyList<string>>());
        Assert.Equal(3, Render(spec, 40).Count);   //top rule, header, bottom rule
    }

    [Fact]
    public void TableSizesToContentNotToTheTerminal()
    {
        Assert.All(Render(Simple(), 120), r => Assert.True(UnicodeWidth.Of(r) < 30));
    }

    [Fact]
    public void CellRowsReportLeftBorderAndPaddingAsPrefixChrome()
    {
        //prefix cells count leading chrome before content, so a rule row has none and this must not change either way
        var rows = TableLayout.Rows(Simple(), Plain, 40, glyphs: GlyphSet.Unicode);
        Assert.Equal(0, rows[0].PrefixCells);
        Assert.Equal(2, rows[1].PrefixCells);
        Assert.Equal(2, rows[3].PrefixCells);
    }

    [Fact]
    public void AnAllEmptyColumnRendersWithoutIncidentAtASqueezedWidth()
    {
        //an all-empty column renders as padding only, and every row still comes out one width
        var spec = new TableSpec(
            new[] { "a", "" },
            new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "one", "" }, new[] { "two", "" } });
        var rows = Render(spec, 14);
        Assert.NotEmpty(rows);
        Assert.Single(rows.Select(UnicodeWidth.Of).Distinct());
    }
}

public class TableWrapTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    private static IReadOnlyList<string> Render(TableSpec spec, int width) =>
        TableLayout.Rows(spec, Plain, width, glyphs: GlyphSet.Unicode).Select(r => r.Text).ToList();

    private static TableSpec OneCell(string text) => new(
        new[] { "v" }, new[] { ColumnAlign.Left }, new[] { new[] { text } });

    [Fact]
    public void MarkdownMarkersAreNotMeasured()
    {
        //the markers add no width, so **bold** is 4 cells
        Assert.Equal("┌──────┐", Render(OneCell("**bold**"), 40)[0]);
    }

    [Fact]
    public void CellWrapsAtSpaces()
    {
        var rows = Render(OneCell("alpha beta gamma delta"), 15);
        Assert.Contains(rows, r => r.Contains("alpha beta", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("gamma", StringComparison.Ordinal));
    }

    [Fact]
    public void UnbreakableTokenHardBreaksMidWord_NeverEllipsis()
    {
        var rows = Render(OneCell("minimax-m2.7-reap"), 12);
        Assert.DoesNotContain(rows, r => r.Contains('…'));
        Assert.Contains(rows, r => r.Contains("minimax", StringComparison.Ordinal));
    }

    [Fact]
    public void NoGridRenderEverContainsAnEllipsis()
    {
        //grid widths only, and the box is asserted first so the ellipsis check cannot pass on empty output
        var spec = new TableSpec(
            new[] { "a", "b" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "one two three four five", "short" } });
        foreach (var width in new[] { 40, 60, 80, 120 })
        {
            var rows = Render(spec, width);
            Assert.Contains(rows, r => r.Contains('┌'));
            Assert.DoesNotContain(rows, r => r.Contains('…'));
        }
    }

    [Fact]
    public void CjkAndEmojiKeepColumnsSquare()
    {
        //the emoji stays inside the ranges UnicodeWidth covers, so a rocket would break this and must not be added
        var spec = new TableSpec(
            new[] { "model", "名前", "mood" },
            new[] { ColumnAlign.Left, ColumnAlign.Left, ColumnAlign.Left },
            new[]
            {
                new[] { "kimi-k2", "月之暗面", "😴" },
                new[] { "qwen3", "通義千問", "😀" },
            });
        Assert.Single(Render(spec, 60).Select(UnicodeWidth.Of).Distinct());
    }

    [Fact]
    public void ARowIsAsTallAsItsTallestCell()
    {
        var spec = new TableSpec(
            new[] { "a", "b" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "one two three four", "x" } });
        var rows = Render(spec, 22);
        Assert.Equal(6, rows.Count);   //top rule, header, rule, the two body lines, bottom rule
        Assert.Contains("x", rows[3], StringComparison.Ordinal);
        Assert.DoesNotContain("x", rows[4], StringComparison.Ordinal);   //the cell is padded on the second line and the value is not repeated
    }
}

public class TableThresholdTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    //records are painted even under plain caps, so this strips the SGR while the grid goldens compare raw text
    private static IReadOnlyList<string> Render(TableSpec spec, int width) =>
        TableLayout.Rows(spec, Plain, width, glyphs: GlyphSet.Unicode).Select(r => TermText.StripAnsiForWidth(r.Text)).ToList();

    //width 12 gives a 4-cell column that fits one abcd per line, so the word count is the line count
    private const int CalibratedWidth = 12;

    private static TableSpec Rows(int words) => new(
        new[] { "k", "v" },
        new[] { ColumnAlign.Left, ColumnAlign.Left },
        new[] { new[] { "x", string.Join(' ', Enumerable.Repeat("abcd", words)) } });

    private static TableSpec TwoRecords() => new(
        new[] { "cmd", "note" },
        new[] { ColumnAlign.Left, ColumnAlign.Left },
        new[]
        {
            new[] { "start", string.Join(' ', Enumerable.Repeat("wwww", 12)) },
            new[] { "stop", "b" },
        });

    [Fact]
    public void FourLineRowStaysAGrid()
    {
        Assert.StartsWith("┌", Render(Rows(4), CalibratedWidth)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FiveLineRowDegradesToRecords()
    {
        var rows = Render(Rows(5), CalibratedWidth);
        Assert.DoesNotContain(rows, r => r.Contains('┌'));
        Assert.Contains(rows, r => r.StartsWith("k: ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeaderRowCountsTowardTheThreshold()
    {
        var spec = new TableSpec(
            new[] { "k", "a very long header that wraps past four lines in a narrow column" },
            new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "x", "y" } });
        Assert.DoesNotContain(Render(spec, 20), r => r.Contains('┌'));
    }

    [Fact]
    public void RecordsUseLabelColonValue_SeparatedByARuleWithNoneAfterTheLast()
    {
        var rows = Render(TwoRecords(), 24);
        Assert.Equal("cmd: start", rows[0]);
        Assert.StartsWith("note: ", rows[1], StringComparison.Ordinal);
        Assert.Contains(rows, r => r.Length > 0 && r.All(ch => ch == '─'));
        Assert.False(rows[^1].All(ch => ch == '─'));
    }

    [Fact]
    public void RecordSeparatorIsFullWidth()
    {
        var rows = Render(TwoRecords(), 24);
        var rule = rows.FirstOrDefault(r => r.Length > 0 && r.All(ch => ch == '─'));
        Assert.NotNull(rule);                      //this asserts rather than testing inside an if, so the check cannot be skipped
        Assert.Equal(24, UnicodeWidth.Of(rule!));  //the test would pass while checking nothing
    }

    [Fact]
    public void NoOutputRowEverContainsAnEllipsis_AtAnyWidth()
    {
        var spec = new TableSpec(
            new[] { "a", "b" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { new string('x', 200), "short" } });
        foreach (var width in new[] { 20, 40, 80, 120 })
        {
            var rows = Render(spec, width);
            Assert.NotEmpty(rows);
            Assert.DoesNotContain(rows, r => r.Contains('…'));
        }
    }
}
