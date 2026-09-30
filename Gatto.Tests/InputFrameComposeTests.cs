using Gatto.Repl.Input;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class InputFrameComposeTests
{
    private static InputFrame Frame(RecordingSurface s) =>
        new(s, new Theme(TermCaps.Plain), "coder",
            new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode);

    [Fact]
    public void Compose_TotalRows_IsSumOfScreenRows_AndCursorInBounds()
    {
        var s = new RecordingSurface { Width = 40 };
        var layout = Frame(s).Compose(new EditorView(new List<string> { "hello world" }, 0, 5));
        Assert.Equal(layout.ScreenRows.Sum(), layout.TotalRows);
        Assert.True(layout.TotalRows >= 4);                       //the frame holds at least rule, editor, rule, and status.
        Assert.InRange(layout.CursorRowOffset, 0, layout.TotalRows - 1);
        Assert.Equal(2 + 5, layout.CursorCol);                    //the cursor column is the two-cell prompt prefix plus five typed cells.
    }

    //the composed frame is compared against golden rows listed here in full
    [Fact]
    public void Compose_KeepsTheTask7Shape()
    {
        var s = new RecordingSurface { Width = 40 };
        var view = new EditorView(new List<string> { "abc" }, 0, 3);

        var layout = Frame(s).Compose(view);
        var rows = layout.Rows.Select(StripCsi).ToList();
        Assert.Equal(new[]
        {
            new string('─', 23) + " gatto · coder ──",   //the top rule right-aligns its label, with four cells of overhead.
            "❯ abc",
            new string('─', 40),
            @"  C:\proj · qwen",                         //status shows folder and model, and the role label lives on the top rule.
        }, rows);
        Assert.Equal(new[] { 1, 1, 1, 1 }, layout.ScreenRows);
        Assert.Equal(4, layout.TotalRows);
        Assert.Equal(1, layout.CursorRowOffset);         //row one is the editor row.
        Assert.Equal(2 + 3, layout.CursorCol);           //the column counts the prompt prefix plus the typed cells.
    }

    private static string StripCsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[?0-9;]*[A-Za-z]", "");
}
