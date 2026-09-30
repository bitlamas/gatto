using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

public class JumpHintGapTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private const int Lines = 30;

    private static (VtScreenSurface, ViewportCompositor) Build(int h, bool dense)
    {
        var s = new VtScreenSurface(40, h);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, T, glyphs: GlyphSet.Unicode);
        var lines = Enumerable.Range(0, Lines).Select(i => $"line {i}").ToArray();
        //dense is one open block with a row per line, the other has a blank row between items
        if (dense)
            model.Append(new ToolBlockItem("grep", "x", "ok", true, "generalist") { Collapsed = false, View = ShellView.All, FullResult = string.Join("\n", lines) });   //show all, so the block is as tall as its lines
        else
            foreach (var line in lines) model.Append(new AssistantBlockItem(new[] { line }, "generalist"));
        var comp = new ViewportCompositor(s, index, new object(), glyphs: GlyphSet.Unicode)
            { ComposeChrome = (_, _) => ChromeBlock.FromText(new[] { "> " }, caretRow: 0, caretCol: 2) };
        return (s, comp);
    }

    private static int HintRow(ViewportCompositor comp, int h) =>
        Enumerable.Range(0, h).FirstOrDefault(y => comp.HitTest(y) is JumpHintTarget, -1);

    [Theory]
    [InlineData(8, true)]
    [InlineData(14, true)]
    [InlineData(8, false)]
    [InlineData(14, false)]
    public void The_row_above_the_hint_is_blank_and_takes_no_click(int h, bool dense)
    {
        for (var offset = 1; offset <= 10; offset++)
        {
            var (s, comp) = Build(h, dense);
            comp.Paint(bottomOffset: offset, following: false);
            var y = HintRow(comp, h);
            Assert.True(y >= 2, $"precondition: the hint shows at offset {offset}");
            Assert.Equal("", s.Viewport[y - 1].Trim());
            Assert.IsType<NoTarget>(comp.HitTest(y - 1));
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    public void The_hint_counts_every_row_under_the_last_one_shown(int h)
    {
        for (var offset = 1; offset <= 10; offset++)
        {
            var (s, comp) = Build(h, dense: true);
            comp.Paint(bottomOffset: offset, following: false);
            var y = HintRow(comp, h);
            //the block ends on line 29 and the show all footer, so the rows below are the lines after the last one shown and the footer
            var shown = int.Parse(s.Viewport[y - 2].Trim().Split(' ')[^1]);
            Assert.Contains($" {Lines - shown} more ", s.Viewport[y]);
        }
    }

    [Fact]
    public void The_gap_does_not_move_the_top_row()
    {
        var (s, comp) = Build(14, dense: true);
        comp.Paint(bottomOffset: 5, following: false);
        //34 rows in all (blank, header, result, 30 lines, the footer). 12 of them show, ending 6 above the last
        Assert.EndsWith("line 13", s.Viewport[0].TrimEnd());
    }
}
