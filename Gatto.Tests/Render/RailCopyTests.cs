using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

public class RailCopyTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private const string First = "gamma.txt:2: RailWrap emitted a very long diagnostic payload that will not fit inside a sixty column window and must wrap onto continuation rows";
    private const string Second = "gamma.txt:8: RailWrap second long payload that also refuses to stay on one row in a narrow window";

    private static (ChromePainter P, VtScreenSurface S) Painted(int width, TranscriptItem item)
    {
        var s = new VtScreenSurface(width, 30);
        var gate = new object();
        var model = new TranscriptModel("coder");
        model.Append(item);
        var p = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        return (p, s);
    }

    private static int RowOf(VtScreenSurface s, string needle)
    {
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(needle, StringComparison.Ordinal));
        Assert.True(y >= 0, $"no row holds {needle}");
        return y;
    }

    //the drag starts on the first text cell, since a press on the rail closes a thought block
    private static string? Copy(ChromePainter p, int width, int y1, int y2)
    {
        p.Mouse.Handle(new MouseEvent(2, y1, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(width - 1, y2, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(width - 1, y2, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        return p.Selection.CopyText(p.Width, T, null, GlyphSet.Unicode)?.Replace("\r\n", "\n");
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(200)]
    public void Copy_across_the_lines_of_an_open_tool_block_holds_no_rail(int width)
    {
        var (p, s) = Painted(width, new ToolBlockItem("grep", "RailWrap", "ok", true, "coder") { Collapsed = false, View = ShellView.All, FullResult = First + "\n" + Second });
        var text = Copy(p, width, RowOf(s, "gamma.txt:2:"), RowOf(s, "gamma.txt:8:") + (width < 200 ? 1 : 0));
        Assert.Equal(First + "\n" + Second, text);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    public void Copy_across_the_lines_of_an_open_thought_block_holds_no_rail(int width)
    {
        var item = new ReasoningItem(new[] { First, Second }) { Streaming = false, Collapsed = false, Elapsed = TimeSpan.FromSeconds(3) };
        var (p, s) = Painted(width, item);
        var text = Copy(p, width, RowOf(s, "gamma.txt:2:"), RowOf(s, "gamma.txt:8:") + (width < 200 ? 1 : 0));
        Assert.Equal(First + "\n" + Second, text);
    }

    [Fact]
    public void A_drag_that_starts_inside_the_text_still_starts_there()
    {
        var (p, s) = Painted(200, new ToolBlockItem("grep", "RailWrap", "ok", true, "coder") { Collapsed = false, FullResult = First });
        var y = RowOf(s, "gamma.txt:2:");
        p.Mouse.Handle(new MouseEvent(14, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());   //ten cells into text that starts at column 4
        p.Mouse.Handle(new MouseEvent(199, y, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(199, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal(First[10..], p.Selection.CopyText(p.Width, T, null, GlyphSet.Unicode));
    }
}
