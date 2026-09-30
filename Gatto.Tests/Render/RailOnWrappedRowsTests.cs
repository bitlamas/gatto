using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Render;

public class RailOnWrappedRowsTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private const string Long = "Repl\\Render\\ChromePainter.cs:226:                n += ShellBlockRender.Layout(ShellBlockInput.Live(liveCommand, RoleForTint, null), ShellBlockState.Closed, width, _theme, _glyphs).Rows.Count";

    public static IEnumerable<object[]> GlyphSets() => [[GlyphSet.Unicode], [GlyphSet.Ascii]];

    private static void EveryRowHasTheRail(TranscriptItem item, int firstBodyRow, GlyphSet g)
    {
        var rail = $"{g.Box.Vertical}";
        for (var width = 30; width <= 120; width++)
        {
            var rows = item.Render(width, T, glyphs: g);
            var wraps = item.RowWraps(width, T, glyphs: g);
            Assert.True(wraps.Any(w => w.Continuation), $"precondition: a line wrapped at {width}");
            for (var i = firstBodyRow; i < rows.Count; i++)
                Assert.True(TermText.StripAnsiForWidth(rows[i]).StartsWith(rail, StringComparison.Ordinal),
                    $"row {i} at width {width} has no rail: [{TermText.StripAnsiForWidth(rows[i])}]");
        }
    }

    [Theory]
    [MemberData(nameof(GlyphSets))]
    public void Open_tool_block_keeps_the_rail_on_wrapped_rows(GlyphSet g)
    {
        var item = new ToolBlockItem("grep", "x", "ok", true, "coder") { Collapsed = false, View = ShellView.All, FullResult = Long + "\n" + Long };   //the window cuts a long row, show all wraps it
        //rows 0 and 1 are the header and the result row
        EveryRowHasTheRail(item, 2, g);
    }

    [Theory]
    [MemberData(nameof(GlyphSets))]
    public void Open_thought_block_keeps_the_rail_on_wrapped_rows(GlyphSet g)
    {
        var item = new ReasoningItem(new[] { Long, Long })
            { Streaming = false, Collapsed = false, Elapsed = System.TimeSpan.FromSeconds(3) };
        EveryRowHasTheRail(item, 1, g);
    }

    [Fact]
    public void A_wrapped_rail_row_reports_the_rail_as_its_prefix()
    {
        var item = new ToolBlockItem("grep", "x", "ok", true, "coder") { Collapsed = false, View = ShellView.All, FullResult = Long };
        var wraps = item.RowWraps(60, T, glyphs: GlyphSet.Unicode).Where(w => w.Continuation).ToList();
        Assert.NotEmpty(wraps);
        Assert.All(wraps, w => Assert.Equal(4, w.PrefixCells));
    }
}
