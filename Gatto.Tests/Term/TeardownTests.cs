using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Gatto.Core.Client;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Term;

//teardown order (the compositor stops before the screen is restored), the degrade tail dump and the exit epilogue
public sealed class TeardownTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static StatusInfo Status(string role) => new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    //once armed, this surface throws on the next write, so a degrade can be aimed at one paint
    private sealed class ArmedThrowSurface : ITermSurface
    {
        public bool Armed;
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 24;
        public StringBuilder Output { get; } = new();
        public string Text => Output.ToString();
        public void Write(string s)
        {
            if (Armed) { Armed = false; throw new InvalidOperationException("paint boom"); }
            Output.Append(s);
        }
    }

    //teardown ordering: a late paint after Stop draws nothing

    [Fact]
    public void Teardown_stops_the_compositor_before_restoring_and_a_late_paint_is_a_no_op()
    {
        var s = new VtScreenSurface(40, 10);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model) { Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        model.Append(new AssistantBlockItem(new[] { "live line" }, "coder"));
        painter.AltScreen.Enter();
        painter.Repaint();
        Assert.True(s.AltScreen);
        Assert.Contains(s.Viewport, row => row.Contains("live line"));

        painter.Teardown();                       //the compositor stops first, the screen is restored after
        Assert.False(s.AltScreen);                //the main screen is restored

        //a late tick after teardown must not repaint onto the main screen
        var before = s.Viewport.ToList();
        painter.Repaint();
        painter.NotifyCommitted();
        Assert.Equal(before, s.Viewport.ToList());   //nothing more was drawn
        Assert.False(s.AltScreen);                   //the late paint did not re-enter the alt buffer
    }

    //a degrade restores the main screen and reprints the transcript tail

    [Fact]
    public void A_degrade_restores_the_main_screen_and_dumps_the_transcript_tail()
    {
        var s = new ArmedThrowSurface();
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model) { Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: model, convoTail: () => null);
        painter.AltScreen.Enter();                //records ESC[?1049h
        r.BeginTurn();
        r.CommitUser(new[] { "remember me" });    //commits into the model, then paints
        r.OnTextDelta("an answer line\n");        //more transcript to dump

        s.Armed = true;                           //the next paint throws and the renderer degrades
        r.OnTextDelta("this delta triggers the fault\n");

        Assert.True(IsDegraded(r));
        Assert.Contains(Ansi.AltScreenExit, s.Text, StringComparison.Ordinal);   //main screen restored
        //the degrade reprints the tail, otherwise the user is left on a blank screen
        Assert.Contains("remember me", StripAll(s.Text), StringComparison.Ordinal);
        Assert.Contains("an answer line", StripAll(s.Text), StringComparison.Ordinal);
    }

    //the exit epilogue, a full dump or a resume hint

    [Fact]
    public void ExitEpilogue_dumpOnExit_reprints_the_whole_transcript_plain()
    {
        var s = new RecordingSurface { Width = 60 };
        var model = new TranscriptModel("coder");
        model.Append(new UserEchoItem(new[] { "first question" }));
        model.Append(new AssistantBlockItem(new[] { "the whole answer" }, "coder"));

        Gatto.Repl.Repl.ExitEpilogue(s, model, T, dumpOnExit: true);

        var text = StripAll(s.Text);
        Assert.Contains("first question", text, StringComparison.Ordinal);
        Assert.Contains("the whole answer", text, StringComparison.Ordinal);
        //a reprinted transcript is already the record, so no resume line is offered after it
        Assert.False(Gatto.Repl.Repl.EndOf(degraded: false, dumpOnExit: true, hasSessions: true, inputDied: false).Resume);
    }

    //the exit writes no resume line, it only decides whether one is offered and the launcher prints it
    [Fact]
    public void A_SESSION_THAT_WAS_NOT_REPRINTED_IS_OFFERED_A_RESUME_AND_THE_EPILOGUE_WRITES_NONE()
    {
        var s = new RecordingSurface { Width = 60 };
        var model = new TranscriptModel("coder");
        model.Append(new UserEchoItem(new[] { "a question" }));

        Gatto.Repl.Repl.ExitEpilogue(s, model, T, dumpOnExit: false);

        Assert.Equal("", s.Text);
        Assert.True(Gatto.Repl.Repl.EndOf(degraded: false, dumpOnExit: false, hasSessions: true, inputDied: false).Resume);
    }

    //a dead input pump ends the session out loud

    [Fact]
    public void ExitEpilogue_says_why_when_the_input_pump_died()
    {
        var s = new RecordingSurface { Width = 60 };
        var model = new TranscriptModel("coder");
        model.Append(new UserEchoItem(new[] { "a question" }));

        Gatto.Repl.Repl.ExitEpilogue(s, model, T, dumpOnExit: false, inputDied: true);

        Assert.Contains("your session is saved", StripAll(s.Text), StringComparison.Ordinal);
        //the farewell is only for a quit the user chose, and a dead pump is not one
        var end = Gatto.Repl.Repl.EndOf(degraded: false, dumpOnExit: false, hasSessions: true, inputDied: true);
        Assert.True(end.Resume);
        Assert.False(end.Farewell);
    }

    [Fact]
    public void ExitEpilogue_is_silent_about_input_when_the_user_quit_normally()
    {
        var s = new RecordingSurface { Width = 60 };
        var model = new TranscriptModel("coder");

        Gatto.Repl.Repl.ExitEpilogue(s, model, T, dumpOnExit: false);

        //the input error is reported only when the pump died, otherwise the message would be a lie
        Assert.DoesNotContain("input error", StripAll(s.Text), StringComparison.Ordinal);
    }

    //no store to resume from, so no line that would point at an older session or at nothing
    [Fact]
    public void WITHOUT_A_SESSION_NO_RESUME_LINE_IS_OFFERED()
    {
        Assert.False(Gatto.Repl.Repl.EndOf(degraded: false, dumpOnExit: false, hasSessions: false, inputDied: false).Resume);
    }

    //the farewell is only for a quit the user chose
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void THE_FAREWELL_IS_FOR_A_QUIT_THE_USER_CHOSE(bool degraded, bool inputDied, bool farewell)
    {
        Assert.Equal(farewell,
            Gatto.Repl.Repl.EndOf(degraded, dumpOnExit: false, hasSessions: true, inputDied).Farewell);
    }

    private static string StripAll(string s) => TermText.StripAnsiForWidth(s);

    private static bool IsDegraded(StreamRenderer r) => (bool)typeof(StreamRenderer)
        .GetField("_degraded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(r)!;
}
