using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//a line from the loop holds the middle dot, and each face draws the dot of the glyph set it knows
public sealed class SystemDotTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private const string Line = "turn truncated · 3 tool calls discarded, not run";

    private static string Visible(string row) => TermText.StripAnsiForWidth(row).TrimEnd();

    [Fact]
    public void A_SYSTEM_ROW_DRAWS_THE_ASCII_DOT_UNDER_THE_ASCII_SET()
    {
        var model = Visible(new SystemLineItem(Line, null).Render(80, T, GlyphSet.Ascii)[0]);
        Assert.Equal("# turn truncated . 3 tool calls discarded, not run", model);

        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate, glyphs: GlyphSet.Ascii)
            { Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Ascii), RoleForTint = "coder" };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null, glyphs: GlyphSet.Ascii);
        var tapped = new List<string>();
        r.CommitTap = rows => tapped.AddRange(rows);
        r.CommitSystem(Line);
        Assert.Contains(tapped, row => Visible(row) == "# turn truncated . 3 tool calls discarded, not run");
    }

    [Fact]
    public void THE_UNICODE_SET_KEEPS_THE_MIDDLE_DOT() =>
        Assert.Equal("♯ " + Line, Visible(new SystemLineItem(Line, null).Render(80, T, GlyphSet.Unicode)[0]));

    [Fact]
    public void THE_PLAIN_FACE_PRINTS_ITS_SETS_DOT()
    {
        var old = Console.Out;
        var w = new StringWriter();
        Console.SetOut(w);
        try { new PlainRenderer(glyphs: GlyphSet.Ascii).OnWarning(Line); }
        finally { Console.SetOut(old); }
        Assert.Contains("! turn truncated . 3 tool calls discarded, not run", w.ToString());
    }

    [Fact]
    public void A_LINE_WITHOUT_THE_DOT_DRAWS_AS_TODAY() =>
        Assert.Equal("# turn truncated", Visible(new SystemLineItem("turn truncated", null).Render(80, T, GlyphSet.Ascii)[0]));
}
