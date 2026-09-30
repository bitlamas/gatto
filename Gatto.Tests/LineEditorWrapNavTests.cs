using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class LineEditorWrapNavTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-lew").FullName;
    private History H() => new(Path.Combine(_dir, "history.txt"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    //the editor drains an IComposerSource, so this fake wraps each key in a ComposerInput.Key
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IComposerSource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ComposerInput Read() => new ComposerInput.Key(_q.Dequeue());
    }

    private static ConsoleKeyInfo K(char c) =>
        new(c, char.IsAsciiLetter(c) ? (ConsoleKey)char.ToUpperInvariant(c) : ConsoleKey.Spacebar, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);
    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    private static List<EditorView> Run(int width, History h, params ConsoleKeyInfo[] keys)
    {
        var editor = new LineEditor(new ScriptedKeys(keys), h, () => width);
        var views = new List<EditorView>();
        editor.Read(v => views.Add(v));
        return views;
    }

    private static ConsoleKeyInfo[] Type(string s, params ConsoleKeyInfo[] tail)
    {
        var k = new List<ConsoleKeyInfo>();
        foreach (var c in s) k.Add(K(c));
        k.AddRange(tail);
        return k.ToArray();
    }

    [Fact]
    public void Up_moves_between_display_rows_of_a_wrapped_line_instead_of_recalling_history()
    {
        var h = H();
        h.Record("PRIOR MESSAGE");   //a history entry is here for up to recall, and the display row above the caret must win

        //at width 20 the composer budget is 17, so this line wraps into several rows
        const string line = "the quick brown fox jumps over the lazy dog today";
        var views = Run(20, h, Type(line, Key(ConsoleKey.UpArrow), Enter));
        var afterUp = views[^1];   //enter submits without painting, so the last captured view is the one after up.

        Assert.Single(afterUp.Lines);
        Assert.Equal(line, afterUp.Lines[0]);                 //the buffer still holds the typed line
        Assert.True(afterUp.CursorCol < line.Length,          //a smaller column means the caret left the end of the line
            "caret should move up a display row, not stay at the end");

        //the caret really sits on an earlier display row
        var segs = SoftWrap.Wrap(line, 17, 17);
        var bottomRow = SoftWrap.MapCursor(segs, line, line.Length).Row;
        var afterRow = SoftWrap.MapCursor(segs, line, afterUp.CursorCol).Row;
        Assert.True(afterRow < bottomRow);
    }

    [Fact]
    public void Up_from_the_top_display_row_recalls_history()
    {
        var h = H();
        h.Record("previous message");
        //hi fits one display row, so its top row is the boundary and up recalls history there
        var editor = new LineEditor(new ScriptedKeys(Type("hi", Key(ConsoleKey.UpArrow), Enter)), h, () => 20);
        var result = editor.Read(_ => { });
        Assert.Equal("previous message", result);
    }

    [Fact]
    public void Walking_up_through_a_wrapped_line_reaches_history_only_at_the_top_row()
    {
        const string line = "alpha beta gamma delta epsilon zeta eta theta";   //this line wraps at width 20.
        var segs = SoftWrap.Wrap(line, 17, 17);
        var rows = segs.Count;
        Assert.True(rows >= 3, "test needs a line of 3+ display rows");
        var ups = Enumerable.Repeat(Key(ConsoleKey.UpArrow), rows - 1).ToArray();

        //rows-1 up presses stop at the top display row, and each run needs a fresh history because submitting the line records it
        var h1 = H(); h1.Record("OLDER");
        var views = Run(20, h1, Type(line, ups.Append(Enter).ToArray()));
        Assert.Equal(line, views[^1].Lines[0]);               //the buffer still holds the typed line
        Assert.Equal(0, SoftWrap.MapCursor(segs, line, views[^1].CursorCol).Row);   //row 0 of the mapped cursor, so the caret is on the top row

        //from the top row, the next up press recalls history.
        var h2 = H(); h2.Record("OLDER");
        var views2 = Run(20, h2, Type(line, ups.Append(Key(ConsoleKey.UpArrow)).Append(Enter).ToArray()));
        Assert.Equal("OLDER", string.Join("\n", views2[^1].Lines));
    }
}
