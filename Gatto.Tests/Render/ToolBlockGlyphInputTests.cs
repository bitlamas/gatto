using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//a press on the triangle opens and focuses the block as the link's words do, and a press beside it selects
public sealed class ToolBlockGlyphInputTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly int[] Widths = { 60, 100, 160 };
    private const string Matches = "a.cs:1: one\nb.cs:2: two\nc.cs:3: three";

    private static (ChromePainter P, VtScreenSurface S, List<ToolBlockItem> Blocks) Committed(int width, bool prose, params ToolCall[] calls)
    {
        var s = new VtScreenSurface(width, 50);
        var gate = new object();
        var model = new TranscriptModel("coder");
        if (prose) model.Append(new AssistantBlockItem(new[] { "prose" }, "coder"));
        var p = new ChromePainter(s, T, gate, model, 3)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
        p.AltScreen.Enter();
        r.BeginTurn();
        foreach (var call in calls)
        {
            r.OnToolCallStart(call);
            r.OnToolResult(call, call.Name == "shell"
                ? new ToolResult("one\ntwo\nthree", Gloss: "exit 0")
                : new ToolResult(Matches, Gloss: "3 matches"));
        }
        r.EndTurn();
        p.Repaint();
        return (p, s, model.Items.OfType<ToolBlockItem>().ToList());
    }

    private static ToolCall Grep(int n) => new($"c{n}", "grep", JsonSerializer.Serialize(new { pattern = $"pattern{n}" }));

    private static ToolCall Shell(int n) => new($"s{n}", "shell", JsonSerializer.Serialize(new { command = $"echo run{n}" }));

    private static string Screen(VtScreenSurface s) => string.Join("\n", s.Viewport.Select(v => v.TrimEnd()));

    //the screen row of a block's result row, counted from its header since the layout's rows start there
    private static int ResultRowY(VtScreenSurface s, string header, ToolBlockItem b, int width)
    {
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(header, StringComparison.Ordinal));
        Assert.True(y >= 0, $"no row holds {header}:\n{Screen(s)}");
        return y + b.Layout(width, T, U).ResultRow;
    }

    private static void Press(ChromePainter p, int x, int y)
    {
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    private static void AssertTrianglePress(ChromePainter p, VtScreenSurface s, ToolBlockItem b, string header, int width)
    {
        Assert.False(b.LinkWords);
        var l = b.Layout(width, T, U);
        var y = ResultRowY(s, header, b, width);
        Assert.Equal(U.Triangle, s.Viewport[y].TrimEnd()[l.LinkStart..l.LinkEnd]);
        Press(p, l.LinkStart - 1, y);
        Assert.True(b.Collapsed, $"one cell before the triangle opened the block at {width}\n{Screen(s)}");
        Press(p, l.LinkStart, y);
        Assert.False(b.Collapsed, $"the triangle did not open the block at {width}\n{Screen(s)}");
        Assert.Equal(p.Model.Items.ToList().IndexOf(b), p.Focus.Focused);
    }

    [Fact]
    public void A_PRESS_ON_THE_TRIANGLE_OPENS_AND_FOCUSES()
    {
        foreach (var width in Widths)
        {
            var (p, s, blocks) = Committed(width, true, Grep(1), Grep(2), Grep(3));
            AssertTrianglePress(p, s, blocks[2], "pattern3", width);
        }
    }

    [Fact]
    public void A_PRESS_ONE_CELL_BEFORE_THE_TRIANGLE_SELECTS()
    {
        foreach (var width in Widths)
        {
            var (p, s, blocks) = Committed(width, true, Grep(1), Grep(2), Grep(3));
            var b = blocks[2];
            var l = b.Layout(width, T, U);
            Press(p, l.LinkStart - 1, ResultRowY(s, "pattern3", b, width));
            Assert.True(b.Collapsed);
            Assert.Null(p.Focus.Focused);
        }
    }

    [Fact]
    public void A_PRESS_ON_A_SHELL_BLOCKS_TRIANGLE_OPENS_IT()
    {
        foreach (var width in Widths)
        {
            var (p, s, blocks) = Committed(width, true, Shell(1), Shell(2), Shell(3));
            AssertTrianglePress(p, s, blocks[2], "run3", width);
        }
    }

    [Fact]
    public void A_PRESS_ON_THE_TRIANGLE_OF_A_FIRST_BLOCK_WITHOUT_A_LEADING_BLANK_ROW()
    {
        foreach (var width in Widths)
        {
            var (p, s, blocks) = Committed(width, false, Grep(1));
            var b = blocks[0];
            Assert.False(b.LeadingBlank);
            b.LinkWords = false;
            p.Repaint();
            AssertTrianglePress(p, s, b, "pattern1", width);
        }
    }

    [Fact]
    public void A_PRESS_ON_THE_TRIANGLE_OF_A_LATER_BLOCK_WITH_A_LEADING_BLANK_ROW()
    {
        foreach (var width in Widths)
        {
            var (p, s, blocks) = Committed(width, false, Grep(1), Grep(2), Grep(3), Grep(4));
            Assert.True(blocks[3].LeadingBlank);
            AssertTrianglePress(p, s, blocks[3], "pattern4", width);
        }
    }
}
