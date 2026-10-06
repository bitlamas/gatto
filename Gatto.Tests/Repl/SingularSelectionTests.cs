//at most one selection is live across the transcript, the chrome and the composer, pinned through the real painter, editor and pump
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Repl;

public class SingularSelectionTests
{
    //these tests enqueue gestures directly, so ReadKey is never called, and the throw says so instead of returning garbage
    private sealed class NeverReadKeySource : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("never driven by these tests");
    }

    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    private static (ChromePainter Painter, object Gate) BuildPainterWithComposerText(string text)
    {
        var theme = new Theme(new TermCaps(true, true));
        var surface = new VtScreenSurface(60, 20);
        var gate = new object();
        var painter = new ChromePainter(surface, theme, gate, new TranscriptModel("coder"))
        {
            Frame = new InputFrame(surface, theme, "coder",
                new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
        };
        painter.State.Composer = new EditorView(new List<string> { text }, 0, text.Length);
        painter.Repaint();   //a repaint fills the compositor's chrome row info, exactly as a real frame does.
        return (painter, gate);
    }

    private static void BeginATranscriptSpan(ChromePainter painter)
    {
        var rows = painter.ChromeRowInfo;
        var (head, headIdx) = rows.Select((r, i) => (r, i))
            .First(x => x.r.Region == ChromeRegion.Composer && x.r.RegionRow == 0);
        var start = head.Visible.IndexOf("hello", StringComparison.Ordinal);
        Assert.True(start >= 0, "precondition: the composer text rendered as expected");
        painter.Selection.BeginChromeDrag(new ChromeCell(headIdx, start), DragKind.Char, 60, rows);
        painter.Selection.ExtendChromeTo(new ChromeCell(headIdx, start + 4), 60, rows);
    }

    [Fact]
    public void The_hook_clears_a_live_transcript_selection_under_the_gate()
    {
        var (painter, gate) = BuildPainterWithComposerText("hello world");
        BeginATranscriptSpan(painter);
        Assert.True(painter.Selection.HasSelection);   //precondition: a transcript span is live.

        var hook = Gatto.Repl.Repl.BuildComposerSelectionSetHook(painter, gate, stopped: () => false);
        hook();

        Assert.False(painter.Selection.HasSelection);
    }

    [Fact]
    public void The_hook_is_a_noop_when_stopped_is_true()
    {
        //a hook that fires after teardown must not touch torn-down chrome, like every other callback in the same read call
        var (painter, gate) = BuildPainterWithComposerText("hello world");
        BeginATranscriptSpan(painter);
        Assert.True(painter.Selection.HasSelection);

        var hook = Gatto.Repl.Repl.BuildComposerSelectionSetHook(painter, gate, stopped: () => true);
        hook();

        Assert.True(painter.Selection.HasSelection);   //the stopped guard fires here, so the hook never clears the selection
    }

    [Fact]
    public void The_hook_is_harmless_when_no_transcript_selection_is_live()
    {
        var (painter, gate) = BuildPainterWithComposerText("hello world");
        Assert.False(painter.Selection.HasSelection);   //precondition: no selection is live.

        var hook = Gatto.Repl.Repl.BuildComposerSelectionSetHook(painter, gate, stopped: () => false);
        hook();   //the hook must not throw when there is nothing to clear.

        Assert.False(painter.Selection.HasSelection);
    }

    //both domains, the pump and the wiring are real, and the gesture is enqueued directly since the cross-domain invariant is the subject

    [Fact]
    public async Task A_composer_select_all_clears_a_live_transcript_span_end_to_end()
    {
        var (painter, gate) = BuildPainterWithComposerText("hello world");
        BeginATranscriptSpan(painter);
        Assert.True(painter.Selection.HasSelection);   //precondition: the transcript span is live.

        var dir = Directory.CreateTempSubdirectory("gatto-sst6");
        try
        {
            var history = new History(Path.Combine(dir.FullName, "history.txt"));
            var pump = new InputPump(new KeyInputSource(new NeverReadKeySource()));
            var editor = new LineEditor(pump.Composer, history);
            var hook = Gatto.Repl.Repl.BuildComposerSelectionSetHook(painter, gate, stopped: () => false);
            var readTask = Task.Run(() => editor.Read(_ => { }, onComposerSelectionSet: hook));

            //a mouse gesture and ctrl+a both reach the same BeginComposerSelect call, so driving the gesture covers the same path
            pump.EnqueueComposerGesture(new ComposerInput.Select(0, 0, ComposerGesture.Begin));

            Assert.True(SpinWait.SpinUntil(() => !painter.Selection.HasSelection, Bound),
                "Direction A: the transcript selection was not cleared by the composer selection-set hook");
            Assert.True(SpinWait.SpinUntil(() => editor.HasComposerSelectionForTest, Bound));
            //exactly one domain stays live, and here that is the composer side

            pump.InjectToComposer(Enter);   //the enter key lets editor.Read return, so the background task ends cleanly
            Assert.True(await Task.WhenAny(readTask, Task.Delay(Bound)) == readTask, "editor.Read did not return");
        }
        finally { try { dir.Delete(true); } catch { } }
    }

    [Fact]
    public async Task A_transcript_drag_begin_clears_a_live_composer_selection_end_to_end()
    {
        //a real drag through the production wiring, so the assertion shows the transcript side really holds the span after the cross-clear
        var theme = new Theme(new TermCaps(true, true));
        var s = new VtScreenSurface(40, 6);
        s.Write(Ansi.AltScreenEnter);
        var model = new TranscriptModel("generalist");
        var index = new LineIndex(model, theme, glyphs: GlyphSet.Unicode);
        var gate = new object();
        var scroll = new ScrollController(model, index);
        var comp = new ViewportCompositor(s, index, gate, glyphs: GlyphSet.Unicode) { ComposeChrome = (_, _) => ChromeBlock.FromText(new[] { "> " }, 0, 2) };
        var focus = new FocusController(model, index, scroll, comp);
        model.Append(new AssistantBlockItem(new[] { "hello world" }, "generalist") { LeadingBlank = false });
        var selection = new SelectionController(model);

        var dir = Directory.CreateTempSubdirectory("gatto-sst6b");
        try
        {
            var history = new History(Path.Combine(dir.FullName, "history.txt"));
            var pump = new InputPump(new KeyInputSource(new NeverReadKeySource()));
            var mc = new MouseController(comp, scroll, focus, model, wheelLines: 3, selection, theme,
                onComposerSelect: (line, col, kind) => pump.EnqueueComposerGesture(new ComposerInput.Select(line, col, kind)));
            var editor = new LineEditor(pump.Composer, history);
            var readTask = Task.Run(() => editor.Read(_ => { }));

            //first a composer selection goes live, mirroring the mouse begin or ctrl+a.
            pump.EnqueueComposerGesture(new ComposerInput.Select(0, 0, ComposerGesture.Begin));
            Assert.True(SpinWait.SpinUntil(() => editor.HasComposerSelectionForTest, Bound));
            Assert.False(selection.HasSelection);   //precondition: the transcript has no selection yet.

            comp.Paint(0, true);
            var y = Enumerable.Range(0, s.Height)
                .First(yy => comp.CellAt(0, yy) is { } c && c.ItemIndex == 0 && c.Rel == 0);
            mc.Handle(new MouseEvent(2, y, MouseKind.Press, MouseButton.Left, 0, 0), 40, 5);   //the mouse press starts a real transcript drag.

            Assert.True(SpinWait.SpinUntil(() => !editor.HasComposerSelectionForTest, Bound),
                "Direction B: the composer selection was not cleared by the transcript-drag Clear gesture");
            Assert.True(selection.HasSelection);   //the transcript domain must now genuinely hold a span.

            pump.InjectToComposer(Enter);
            Assert.True(await Task.WhenAny(readTask, Task.Delay(Bound)) == readTask, "editor.Read did not return");
        }
        finally { try { dir.Delete(true); } catch { } }
    }

    //the read runs on a pool task the suite's load can start late, and each wait ends on its event, so the bound only matters then
    private const int Bound = 30_000;
}
