using Gatto.Repl.Render;

namespace Gatto.Tests;

public class TableParseTests
{
    private static readonly string[] Basic =
    {
        "| a | b | c |",
        "| --- | :---: | ---: |",
        "| one | two | three |",
    };

    [Fact]
    public void ParsesHeadersAndAlignments()
    {
        Assert.True(TableParse.TryParse(Basic, 0, out var spec, out var end));
        Assert.Equal(new[] { "a", "b", "c" }, spec!.Headers);
        Assert.Equal(new[] { ColumnAlign.Left, ColumnAlign.Center, ColumnAlign.Right }, spec.Alignments);
        Assert.Equal(3, end);
    }

    [Fact]
    public void PadsShortRowsAndDropsExtraCells()
    {
        string[] ragged =
        {
            "| a | b | c |", "| --- | --- | --- |",
            "| only-one |", "| four | | six |", "| seven | eight | nine | ten |",
        };
        Assert.True(TableParse.TryParse(ragged, 0, out var spec, out _));
        Assert.Equal(new[] { "only-one", "", "" }, spec!.Rows[0]);
        Assert.Equal(new[] { "four", "", "six" }, spec.Rows[1]);
        Assert.Equal(new[] { "seven", "eight", "nine" }, spec.Rows[2]);
    }

    [Fact]
    public void DelimiterCellCountMustMatchHeader_ElseNotATable()
    {
        string[] mismatch = { "| a | b | c |", "| --- | --- |", "| one | two | three |" };
        Assert.False(TableParse.TryParse(mismatch, 0, out _, out _));
    }

    [Fact]
    public void LeadingAndTrailingPipesAreOptional()
    {
        string[] bare = { "a | b", "--- | ---", "one | two" };
        Assert.True(TableParse.TryParse(bare, 0, out var spec, out _));
        Assert.Equal(new[] { "a", "b" }, spec!.Headers);
    }

    [Fact]
    public void EscapedPipeIsCellText()
    {
        var cells = TableParse.SplitCells(@"| a \| b | c |");
        Assert.Equal(new[] { "a | b", "c" }, cells);
    }

    [Fact]
    public void RegionStopsAtBlankLineAndNonTableLine()
    {
        string[] lines =
        {
            "intro", "| a | b |", "| --- | --- |", "| 1 | 2 |", "", "after",
        };
        var regions = TableParse.Regions(lines);
        Assert.Single(regions);
        Assert.Equal((1, 4), regions[0]);
    }

    [Fact]
    public void HeaderOnlyTableIsValid()
    {
        string[] lines = { "| a | b |", "| --- | --- |" };
        Assert.True(TableParse.TryParse(lines, 0, out var spec, out var end));
        Assert.Empty(spec!.Rows);
        Assert.Equal(2, end);
    }
}
