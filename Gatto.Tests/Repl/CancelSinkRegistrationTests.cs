//drives BuildCancelSink through a real InputPump (testing the whole live loop would cost more)
using System.Collections.Concurrent;
using System.Linq;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Repl;

public class CancelSinkRegistrationTests
{
    private sealed class ScriptSource : IInputSource
    {
        private readonly BlockingCollection<InputEvent> _q = new();
        public void Push(InputEvent e) => _q.Add(e);
        public InputEvent Read() => _q.Take();
        public bool KeyDownAvailable => _q.Count > 0;
    }

    private static readonly ConsoleKeyInfo Esc = new('\x1b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo CtrlC = new('\x03', ConsoleKey.C, false, false, true);

    private sealed class FakeClipboard : Gatto.Terminal.IClipboard
    {
        public string? Text;
        public bool TrySet(string text) { Text = text; return true; }
    }
    //the real BuildCancelSink closure runs end-to-end here, with only the clipboard faked
    [Fact]
    public void Ctrl_C_over_a_chrome_span_copies_the_rejoined_composer_line()
    {
        var theme = new Theme(new TermCaps(true, true));
        var surface = new VtScreenSurface(60, 20);
        var gate = new object();
        var painter = new ChromePainter(surface, theme, gate, new TranscriptModel("coder"))
        {
            Frame = new InputFrame(surface, theme, "coder",
                new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
        };
        painter.State.Composer = new EditorView(new List<string> { "hello world" }, 0, 11);
        painter.Repaint();   //a repaint fills the compositor's chrome row info, exactly as a real frame does.

        var rows = painter.ChromeRowInfo;
        var (head, headIdx) = rows.Select((r, i) => (r, i))
            .First(x => x.r.Region == ChromeRegion.Composer && x.r.RegionRow == 0);
        var start = head.Visible.IndexOf("hello", StringComparison.Ordinal);
        Assert.True(start >= 0, "precondition: the composer text rendered as expected");

        painter.Selection.BeginChromeDrag(new ChromeCell(headIdx, start), DragKind.Char, surface.Width, rows);
        painter.Selection.ExtendChromeTo(new ChromeCell(headIdx, start + 4), surface.Width, rows);   //start plus four cells is the whole word hello
        Assert.True(painter.Selection.HasSelection);

        var clip = new FakeClipboard();
        void Copy()   //mirrors the production copy closure, since its shape is part of what is under test
        {
            lock (gate)
            {
                var text = painter.Selection.CopyText(surface.Width, theme, painter.ChromeRowInfo, glyphs: GlyphSet.Unicode);
                if (text is { Length: > 0 }) clip.TrySet(text);
            }
        }

        var sink = Gatto.Repl.Repl.BuildCancelSink(painter, gate, turnCts: () => null, copy: Copy,
            composerEmpty: () => true);

        var src = new ScriptSource();
        var pump = new InputPump(src);
        pump.SetCancelSink(sink);
        pump.Start();
        src.Push(new KeyEvent(CtrlC));

        Assert.True(System.Threading.SpinWait.SpinUntil(() => clip.Text is not null, 5000));
        Assert.Equal("hello", clip.Text);
    }
}
