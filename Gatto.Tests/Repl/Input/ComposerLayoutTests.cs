using Gatto.Repl.Input;

namespace Gatto.Tests.Repl.Input;

public class ComposerLayoutTests
{
    //the round trip must return the same pair for every valid line and column, though the clamp cases below are unreachable this way

    public static IEnumerable<object[]> RoundTripBuffers()
    {
        var buffers = new (string Name, string[] Lines)[]
        {
            ("single line", new[] { "hello world, a short single line" }),
            ("wraps to 3+ rows", new[] { "the quick brown fox jumps over the lazy dog again and again and again" }),
            ("multi-line, some wrapping", new[]
            {
                "short first line",
                "a considerably longer second logical line that should wrap across several rows",
                "third",
                "and a fourth line here too",
            }),
            ("CJK mixed with ASCII", new[]
            {
                "日本語テキスト mixed with ascii words to force wrapping",
                "second 日本語テキスト line",
            }),
        };
        var widths = new[] { 20, 40, 80, 120 };

        foreach (var (name, lines) in buffers)
            foreach (var width in widths)
                yield return new object[] { name, lines, width };
    }

    [Theory]
    [MemberData(nameof(RoundTripBuffers))]
    public void PositionToCell_then_ComposerCellToPosition_is_identity_for_every_valid_position(
        string _, string[] lines, int width)
    {
        var layout = new ComposerLayout(lines, width);

        for (var line = 0; line < lines.Length; line++)
        {
            for (var col = 0; col <= lines[line].Length; col++)
            {
                var (row, cell) = layout.PositionToCell(line, col);
                var (backLine, backCol) = layout.ComposerCellToPosition(row, cell);
                Assert.True(backLine == line && backCol == col,
                    $"width {width}, line {line}, col {col}: forward->({row},{cell})->inverse gave ({backLine},{backCol})");
            }
        }
    }

    [Fact]
    public void Click_on_a_wide_glyphs_second_cell_snaps_to_the_glyphs_own_position()
    {
        //the fixture never wraps, so the wide glyph fills content cells zero and one, and content starts at visible cell two
        var layout = new ComposerLayout(new[] { "字a" }, width: 40);
        var (line, col) = layout.ComposerCellToPosition(regionRow: 0, cellCol: 3);
        Assert.Equal((0, 0), (line, col));
    }

    [Fact]
    public void Click_in_the_prefix_cells_of_a_head_row_maps_to_line_column_zero()
    {
        var layout = new ComposerLayout(new[] { "hello world" }, width: 40);
        Assert.Equal((0, 0), layout.ComposerCellToPosition(regionRow: 0, cellCol: 0));
        Assert.Equal((0, 0), layout.ComposerCellToPosition(regionRow: 0, cellCol: 1));
    }

    [Fact]
    public void Click_in_the_hang_cells_of_a_continuation_row_maps_to_that_segments_first_char()
    {
        //at width twenty the line wraps, and row one starts a new segment past the line's first character.
        const string line = "aaaa bbbb cccc dddd eeee ffff";
        var layout = new ComposerLayout(new[] { line }, width: 20);
        Assert.True(layout.RowCount >= 2, "test needs a line that wraps to 2+ rows");

        var segs = layout.SegsPerLine[0];
        var firstCharOfRow1 = segs[0].SourceChars;   //the first segment's length gives where row one starts in the logical line.

        Assert.Equal((0, firstCharOfRow1), layout.ComposerCellToPosition(regionRow: 1, cellCol: 0));
        Assert.Equal((0, firstCharOfRow1), layout.ComposerCellToPosition(regionRow: 1, cellCol: 1));
    }

    [Fact]
    public void Click_well_past_the_last_segments_content_maps_to_the_logical_lines_end()
    {
        var layout = new ComposerLayout(new[] { "short" }, width: 40);
        var (line, col) = layout.ComposerCellToPosition(regionRow: 0, cellCol: 999);
        Assert.Equal((0, "short".Length), (line, col));
    }

    [Fact]
    public void Click_past_a_middle_wrapped_segments_content_stays_on_that_segment_not_the_next_row()
    {
        //an unbroken word hard-breaks with no dropped space, so this case alone tells the below-boundary clamp apart from a no-op
        const string line = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789xyz";
        var layout = new ComposerLayout(new[] { line }, width: 20);   //at width twenty the budget hard-breaks every seventeen characters.
        Assert.True(layout.RowCount >= 3, "test needs a line that wraps to 3+ rows");

        var segs = layout.SegsPerLine[0];
        var middleRow = layout.RowCount - 2;   //pick a middle row, neither the first nor the last.
        Assert.True(middleRow > 0);
        Assert.Equal(segs[middleRow].Text.Length, segs[middleRow].SourceChars);   //precondition: the middle row dropped no space.

        var (line0, col) = layout.ComposerCellToPosition(regionRow: middleRow, cellCol: 999);

        //the clamped column stays strictly inside the middle segment, so the click cannot bounce to the next row
        var (backRow, _) = layout.PositionToCell(line0, col);
        Assert.Equal(0, line0);
        Assert.Equal(middleRow, backRow);
        var segStart = 0;
        for (var i = 0; i < middleRow; i++) segStart += segs[i].SourceChars;
        Assert.True(col < segStart + segs[middleRow].SourceChars,
            "clamped column must stay strictly inside the middle segment, never at its boundary");
    }

    [Fact]
    public void Click_below_the_last_composer_row_maps_to_the_buffer_end()
    {
        var lines = new[] { "first line", "second line here" };
        var layout = new ComposerLayout(lines, width: 40);

        var (line, col) = layout.ComposerCellToPosition(regionRow: layout.RowCount + 5, cellCol: 0);
        Assert.Equal((lines.Length - 1, lines[^1].Length), (line, col));
    }
}
