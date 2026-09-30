//a redraw throw must not look like a dead console, or the composer task ends the session quietly with exit code 0
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ReplComposerPaintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-rcp").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class OneKeyThenBlock : IComposerSource
    {
        private bool _sent;
        public bool KeyAvailable => false;
        public ComposerInput Read()
        {
            if (_sent) throw new InvalidOperationException("no more keys");   //the redraw throws before a second read can reach this line.
            _sent = true;
            return new ComposerInput.Key(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false));
        }
    }

    private sealed class DeadKeys : IComposerSource
    {
        public bool KeyAvailable => false;
        public ComposerInput Read() => throw new InvalidOperationException("console input redirected");
    }

    [Fact]
    public void Read_PaintThrow_SurfacesAsComposerPaintException_NotAsPumpDeath()
    {
        var editor = new LineEditor(new OneKeyThenBlock(), new History(Path.Combine(_dir, "h.txt")));
        var boom = new IOException("the handle is invalid");

        var ex = Assert.Throws<ComposerPaintException>(() => editor.Read(_ => throw boom));

        Assert.Same(boom, ex.InnerException);           //the wrapped exception must keep the real cause for the app backstop.
        Assert.Contains("the handle is invalid", ex.Message);
    }

    [Fact]
    public void Read_DeadKeySource_StillThrowsTheRawPumpDeathSignal_NotAPaintException()
    {
        //a dead key source must not report as a paint fault. only that signal lets the composer task end a session.
        var editor = new LineEditor(new DeadKeys(), new History(Path.Combine(_dir, "h.txt")));

        var ex = Assert.ThrowsAny<Exception>(() => editor.Read(_ => { }));

        Assert.IsNotType<ComposerPaintException>(ex);
        Assert.IsType<InvalidOperationException>(ex);
    }
}
