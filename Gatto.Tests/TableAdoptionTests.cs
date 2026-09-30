using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

public class TableAdoptionTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    private static List<string> Aligned(TableSpec spec, int width) =>
        TableLayout.AlignedRows(spec, Plain, width).Select(r => TermText.StripAnsiForWidth(r.Text)).ToList();

    [Fact]
    public void AlignedFormHasNoBorderGlyphs()
    {
        var spec = new TableSpec(
            new[] { "tool", "description" },
            new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "read_file", "Read a file" } });
        var rows = Aligned(spec, 60);
        Assert.All(rows, r => Assert.DoesNotContain('│', r));
        Assert.All(rows, r => Assert.DoesNotContain('┌', r));
    }

    [Fact]
    public void AlignedColumnsLineUp()
    {
        var spec = new TableSpec(
            new[] { "tool", "origin" },
            new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "a", "x" }, new[] { "much-longer-name", "y" } });
        var rows = Aligned(spec, 60);
        Assert.Equal(rows[1].IndexOf('x'), rows[2].IndexOf('y'));
    }

    [Fact]
    public void ControlCharactersInAToolNameDoNotBreakAlignment()
    {
        //a real control character written as an escape, since a raw byte can vanish in an edit and leave the guard testing nothing
        const string evil = "evil\u0007name";
        var spec = new TableSpec(
            new[] { "tool", "origin" },
            new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { evil, "x" }, new[] { "plain", "y" } });
        var rows = Aligned(spec, 60);
        Assert.Equal(rows[1].IndexOf('x'), rows[2].IndexOf('y'));
        Assert.DoesNotContain(rows, r => r.Contains('\u0007'));
    }

    [Fact]
    public void AllEmptyHeadersEmitNoHeaderRow()
    {
        //the /permissions table has no header row, and the skip keeps Emit from leaving a blank one
        var spec = new TableSpec(
            new[] { "", "" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "1", "shell npm" } });
        Assert.StartsWith("1", Aligned(spec, 60)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsListIsTwoColumns_OriginTrailsInlineNotInAColumn()
    {
        //origin goes inline in parentheses, since a third column is as wide as the longest origin and detaches the value from its row
        var spec = Gatto.Repl.Repl.ToolsSpec(new[]
        {
            new ToolInfo("read_file", "Read a file from disk", null),
            new ToolInfo("ask_user", "Ask the user a question", "ask_user"),
        }, Array.Empty<(string, string)>(), glyphs: GlyphSet.Unicode);   //no policy rows here, since the columns are what this test checks
        Assert.Equal(2, spec.Headers.Count);
        Assert.DoesNotContain("origin", spec.Headers);
        Assert.EndsWith("(ask_user)", spec.Rows[1][1], StringComparison.Ordinal);
        Assert.DoesNotContain("(", spec.Rows[0][1], StringComparison.Ordinal);   //a built-in has no origin, so the description has no parentheses
    }

    [Fact]
    public void ListingItemReMeasuresAtANewWidth()
    {
        var spec = new TableSpec(
            new[] { "a", "b" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "one two three four five six", "x" } });
        var item = new ListingItem(spec, After: null);
        Assert.NotEqual(item.Render(80, Plain, glyphs: GlyphSet.Unicode).Count, item.Render(24, Plain, glyphs: GlyphSet.Unicode).Count);
    }

    [Fact]
    public void ListingItemRowWrapsMatchItsRenderedRowCount()
    {
        var spec = new TableSpec(
            new[] { "a", "b" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[] { new[] { "one two three four five six", "x" } });
        var item = new ListingItem(spec, After: null);
        Assert.Equal(item.Render(24, Plain, glyphs: GlyphSet.Unicode).Count, item.RowWraps(24, Plain, glyphs: GlyphSet.Unicode).Count);
    }

    //every column pads to its width including the last, so a row only ends clean because of the final TrimEnd
    [Fact]
    public void AlignedRowNeverEndsInWhitespace()
    {
        var spec = new TableSpec(
            new[] { "tool", "description" }, new[] { ColumnAlign.Left, ColumnAlign.Left },
            new[]
            {
                new[] { "a", "short" },
                new[] { "bbbbbbbbbbbb", "a description long enough to widen the column" },
                new[] { "", "policy: an empty first cell pads to the full column width" },
            });

        var rows = TableLayout.AlignedRows(spec, Plain, 100);

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(r.Text.TrimEnd(), r.Text));
    }
}
