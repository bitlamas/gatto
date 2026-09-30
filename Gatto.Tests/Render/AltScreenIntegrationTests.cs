using System.Collections.Generic;
using System.Linq;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//drives a scripted turn through the real renderer, painter and compositor on an emulated alt screen, where a unit test covers one seam each
public sealed class AltScreenIntegrationTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static StatusInfo Status(string role) => new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    private const string Composer = "typing here";

    private static (StreamRenderer R, ChromePainter P, VtScreenSurface S) Wire(int w, int h, string role = "coder")
    {
        var s = new VtScreenSurface(w, h);
        var gate = new object();
        var model = new TranscriptModel(role);
        var painter = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, role, Status(role), glyphs: GlyphSet.Unicode),
            RoleForTint = role,
        };
        painter.State.Composer = new EditorView(new List<string> { Composer }, 0, Composer.Length);
        var ticker = new ChromeTicker(painter, gate);
        var r = new StreamRenderer(painter, s, T, role, ticker, gate, model: model, convoTail: () => null);
        painter.AltScreen.Enter();   //the compositor runs only on the alternate buffer.
        painter.Repaint();
        return (r, painter, s);
    }

    //the compositor pins the frame to the physical bottom, status on the last row and the composer third from the bottom
    private static void AssertComposerPinnedToBottom(VtScreenSurface s)
    {
        Assert.NotEqual("", s.Viewport[^1]);                                    //the bottom row always holds a chrome row.
        Assert.Equal(new string('─', s.Width), s.Viewport[^2]);                //the second row from the bottom is the frame's bottom rule.
        Assert.Contains("❯ " + Composer, s.Viewport[^3]);                      //the composer row sits one row above the rule.
    }

    [Fact]
    public void Reasoning_collapses_in_the_frame_and_expands_on_focus_toggle()
    {
        var (r, p, s) = Wire(60, 20);
        r.BeginTurn();
        r.OnReasoningDelta("thinking hard about the parser design here.\n");
        r.OnTextDelta("done");     //moving out of reasoning collapses that row by default.
        r.EndTurn();
        Assert.Contains(s.Viewport, row => row.Contains("thought for"));            //the collapsed form is a one-row summary.
        Assert.DoesNotContain(s.Viewport, row => row.Contains("thinking hard"));
        AssertComposerPinnedToBottom(s);

        p.Focus.Prev(p.Width, p.ViewportRows());          //the collapsed reasoning is the only collapsible row to focus here.
        p.Focus.ToggleFocused(p.Width, p.ViewportRows());
        p.Repaint();
        Assert.Contains(s.Viewport, row => row.Contains("thinking hard"));
        AssertComposerPinnedToBottom(s);
    }

    [Fact]
    public void A_stray_whitespace_prose_block_between_reasoning_and_a_tool_does_not_double_the_gap()
    {
        var (r, p, s) = Wire(60, 24);
        r.BeginTurn();
        r.OnReasoningDelta("brief thought\n");
        r.OnTextDelta("\n");   //the model's lone newline sits between reasoning and tool, and it must not add a second blank.
        var call = new ToolCall("id1", "web_search", "{}");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("ok", Gloss: "5 results"));
        r.EndTurn();

        var v = s.Viewport;
        var summary = Enumerable.Range(0, v.Count).First(i => v[i].Contains("thought for"));
        var tool = Enumerable.Range(0, v.Count).First(i => v[i].Contains("web_search"));
        Assert.Equal(2, tool - summary);   //a distance of two rows means exactly one blank between summary and tool bullet.
    }

    [Fact]
    public void Clicking_a_collapsed_reasoning_summary_expands_it_in_the_frame()
    {
        var (r, p, s) = Wire(60, 14);
        r.BeginTurn();
        r.OnReasoningDelta("thinking hard about the parser design here.\n");
        r.OnTextDelta("done");
        r.EndTurn();
        Assert.Contains(s.Viewport, row => row.Contains("thought for"));            //the visible row is the collapsed summary.
        Assert.DoesNotContain(s.Viewport, row => row.Contains("thinking hard"));

        //the thought for row is the summary's physical row, clicked the way the real sink clicks it
        var y = System.Array.FindIndex(s.Viewport.ToArray(), row => row.Contains("thought for"));
        Assert.True(y >= 0);
        p.Mouse.Handle(new Gatto.Terminal.MouseEvent(0, y, Gatto.Terminal.MouseKind.Press,
            Gatto.Terminal.MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();
        Assert.Contains(s.Viewport, row => row.Contains("thinking hard"));
        AssertComposerPinnedToBottom(s);
    }

    [Fact]
    public void Clicking_the_empty_composer_clears_the_focused_blocks_accent_bar()
    {
        //the accent bar marks the block that Ctrl+R and Ctrl+arrows act on, so a click on the composer takes it off
        var (r, p, s) = Wire(60, 14);
        r.BeginTurn();
        r.OnReasoningDelta("thinking hard about the parser design here.\n");
        r.OnTextDelta("done");
        r.EndTurn();
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.Focus.Prev(p.Width, p.ViewportRows());
        p.Repaint();
        var summary = System.Array.FindIndex(s.Viewport.ToArray(), row => row.Contains("thought for"));
        Assert.StartsWith($"{GlyphSet.Unicode.Box.Vertical}", s.Viewport[summary]);

        var composer = System.Array.FindLastIndex(s.Viewport.ToArray(), row => row.StartsWith($"{GlyphSet.Unicode.Prompt}", StringComparison.Ordinal));
        Assert.True(composer > summary);
        p.Mouse.Handle(new MouseEvent(4, composer, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();

        Assert.Null(p.Focus.Focused);
        Assert.StartsWith($"{GlyphSet.Unicode.Triangle}", s.Viewport[summary]);
    }

    [Fact]
    public void Wheel_up_detaches_follow_mode_through_Mouse_Handle()
    {
        var (r, p, s) = Wire(60, 8);
        for (var i = 0; i < 20; i++) { r.BeginTurn(); r.OnTextDelta($"answer line {i}"); r.EndTurn(); }
        Assert.True(p.Scroll.Following);
        p.Mouse.Handle(new Gatto.Terminal.MouseEvent(0, 0, Gatto.Terminal.MouseKind.Wheel,
            Gatto.Terminal.MouseButton.None, 120, 0), p.Width, p.ViewportRows());
        Assert.False(p.Scroll.Following);   //wheel-up into history must detach follow mode.
    }

    [Fact]
    public void Expanded_tool_output_appears_in_the_frame()
    {
        var (r, p, s) = Wire(60, 20);
        r.BeginTurn();
        var call = new ToolCall("c1", "read_file", "{\"path\":\"a.cs\"}");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("alpha line\nbeta line\ngamma line"));
        r.EndTurn();
        Assert.DoesNotContain(s.Viewport, row => row.Contains("alpha line"));       //the collapsed block shows the gloss only.

        p.Focus.Prev(p.Width, p.ViewportRows());
        p.Focus.ToggleFocused(p.Width, p.ViewportRows());
        p.Repaint();
        Assert.Contains(s.Viewport, row => row.Contains("alpha line"));
        AssertComposerPinnedToBottom(s);
    }

    [Fact]
    public void Capped_reasoning_shows_the_first_rows_not_later_ones()
    {
        var (r, p, s) = Wire(60, 20);   //the default wiring starts in collapsed mode.
        r.BeginTurn();
        for (var i = 0; i < 8; i++) r.OnReasoningDelta($"reasoning line {i}\n");   //eight lines are committed but the block is never closed.
        var text = string.Join("\n", s.Viewport);
        Assert.Contains("reasoning line 0", text);        //the capped preview shows the first line.
        Assert.DoesNotContain("reasoning line 7", text);  //lines past the cap must not render.
    }

    [Fact]
    public void Capped_reasoning_streaming_tail_is_suppressed_so_it_does_not_rotate_past_the_cap()
    {
        var (r, p, s) = Wire(60, 20);
        r.BeginTurn();
        for (var i = 0; i < 5; i++) r.OnReasoningDelta($"reasoning line {i}\n");   //five lines commit before the partial one.
        r.OnReasoningDelta("PARTIAL line still streaming");   //a delta without a newline stays the chrome's tail.
        var text = string.Join("\n", s.Viewport);
        Assert.DoesNotContain("PARTIAL line still streaming", text);   //the rotating tail must not grow reasoning past the cap.
    }

    [Fact]
    public void The_streaming_tail_is_hidden_while_scrolled_up()   //the tail must be hidden while scrolled up, so no stray line sits below the jump hint
    {
        var (r, p, s) = Wire(40, 12);
        r.BeginTurn();
        for (var i = 0; i < 30; i++) r.OnTextDelta($"answer line {i}\n");   //thirty lines overflow the viewport.
        r.OnTextDelta("PARTIAL streaming tail");                            //a delta without a newline becomes the chrome's tail.
        Assert.Contains(s.Viewport, row => row.Contains("PARTIAL streaming tail"));   //while following, the tail must show.

        p.Scroll.PageUp(p.Width, p.ViewportRows());   //scrolling up detaches follow mode.
        p.Repaint();
        Assert.DoesNotContain(s.Viewport, row => row.Contains("PARTIAL streaming tail"));   //once detached, the tail must hide.
        Assert.Contains(s.Viewport, row => row.Contains("Ctrl+End or click"));               //the jump hint stands in for the hidden tail.
    }

    [Fact]
    public void A_blank_row_sits_above_the_live_tool_bullet()
    {
        var (r, p, s) = Wire(60, 20);
        r.BeginTurn();
        r.OnTextDelta("here is the answer.\n");                              //a newline-terminated delta commits to the transcript.
        r.OnToolCallStart(new ToolCall("c1", "shell", "{\"cmd\":\"ls\"}"));  //the tool bullet renders as live chrome.
        var rows = s.Viewport.ToArray();
        var proseIdx = System.Array.FindIndex(rows, row => row.Contains("here is the answer"));
        var toolIdx = System.Array.FindIndex(rows, row => row.Contains("shell"));
        Assert.True(proseIdx >= 0 && toolIdx > proseIdx + 1, "the tool bullet must not sit directly against the transcript");
        for (var i = proseIdx + 1; i < toolIdx; i++) Assert.Equal("", rows[i].Trim());
    }

    [Fact]
    public void A_blank_row_sits_above_the_live_streaming_answer_tail()
    {
        //the separator row must already be there while the answer streams, otherwise the transcript shifts down when the turn commits
        var (r, p, s) = Wire(60, 20);
        r.BeginTurn();
        r.OnReasoningDelta("brief thought\n");                 //the reasoning delta commits and the block collapses.
        r.OnTextDelta("I'm doing well, thanks for asking");    //the reasoning closes, and the partial answer line streams as the chrome tail.
        //the turn stays open on purpose, so the check sees the live mid-answer state.

        var rows = s.Viewport.ToArray();
        var chipIdx = System.Array.FindIndex(rows, row => row.Contains("thought for"));
        var tailIdx = System.Array.FindIndex(rows, row => row.Contains("I'm doing well"));
        Assert.True(chipIdx >= 0 && tailIdx > chipIdx + 1,
            "the streaming answer tail must not sit directly against the collapsed reasoning summary");
        for (var i = chipIdx + 1; i < tailIdx; i++) Assert.Equal("", rows[i].Trim());
    }

    [Fact]
    public void Jump_to_bottom_hint_shows_when_scrolled_up_and_clears_at_the_bottom()
    {
        var (r, p, s) = Wire(40, 12);
        r.BeginTurn();
        r.CommitUser(new[] { "go" });
        for (var i = 0; i < 30; i++) r.OnTextDelta($"answer line {i}\n");   //thirty rows overflow the twelve-row viewport.
        r.EndTurn();
        Assert.DoesNotContain(s.Viewport, row => row.Contains("Ctrl+End or click"));   //following the bottom, the hint must not show.

        p.Scroll.PageUp(p.Width, p.ViewportRows());   //scrolling up detaches follow mode.
        p.Repaint();
        Assert.Contains(s.Viewport, row => row.Contains("Ctrl+End or click"));   //the hint must appear once detached.
        AssertComposerPinnedToBottom(s);                                        //the composer stays pinned while detached.

        p.Scroll.End();
        p.Repaint();
        Assert.DoesNotContain(s.Viewport, row => row.Contains("Ctrl+End or click"));
    }

    [Fact]
    public void Streaming_lines_appear_as_they_arrive_and_the_composer_stays_pinned()
    {
        var (r, _, s) = Wire(60, 20);
        AssertComposerPinnedToBottom(s);   //the composer is pinned before the turn even starts, on an empty transcript.

        r.BeginTurn();
        r.CommitUser(new[] { "do the thing" });
        Assert.Contains(s.Viewport, row => row.Contains("do the thing"));       //the user echo appears in the same call.
        AssertComposerPinnedToBottom(s);

        //each logical line must appear the moment its newline flushes it
        r.OnTextDelta("first line of the answer\n");
        Assert.Contains(s.Viewport, row => row.Contains("first line of the answer"));
        AssertComposerPinnedToBottom(s);

        r.OnTextDelta("second line of the answer\n");
        Assert.Contains(s.Viewport, row => row.Contains("first line of the answer"));
        Assert.Contains(s.Viewport, row => row.Contains("second line of the answer"));
        AssertComposerPinnedToBottom(s);

        //a tool call and result commit its bullet and gloss to the transcript.
        var call = new ToolCall("c1", "read_file", "{\"path\":\"a.md\"}");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("body", Gloss: "12 lines"));
        r.EndTurn();
        Assert.Contains(s.Viewport, row => row.Contains("○ read_file"));
        Assert.Contains(s.Viewport, row => row.Contains("12 lines"));
        AssertComposerPinnedToBottom(s);

        Assert.Empty(s.Scrollback);   //the alt screen owns its scrolling, so nothing may spill to native scrollback
        Assert.Equal(0, s.Scrolled);
    }

    [Fact]
    public void A_mid_stream_resize_reflows_the_transcript_and_keeps_the_composer_pinned()
    {
        var (r, painter, s) = Wire(80, 20);
        r.BeginTurn();
        r.CommitUser(new[] { "go" });
        //one long logical line wraps into few rows at width 80 and must reflow wider at width 34.
        var longLine = "This is a single long assistant line that will occupy noticeably more rows once the terminal is made much narrower.";
        r.OnTextDelta(longLine + "\n");

        var wideRows = s.Viewport.Count(row =>
            row.Contains("This is a single") || row.Contains("terminal is made much narrower"));
        Assert.Contains(s.Viewport, row => row.Contains("This is a single"));
        AssertComposerPinnedToBottom(s);

        //a resize re-renders the model at the new width, so the line reflows into more rows
        s.Resize(34, 20);
        painter.Repaint();

        AssertComposerPinnedToBottom(s);
        Assert.All(s.Viewport, row => Assert.True(UnicodeWidth.Of(row) <= 34));
        Assert.Contains(s.Viewport, row => row.Contains("This is a single"));
        var narrowRows = s.Viewport.Count(row =>
            row.Contains("This is a single") || row.Contains("narrower")
            || row.Contains("occupy") || row.Contains("terminal"));
        Assert.True(narrowRows > wideRows, "the long line must occupy more rows at the narrower width");

        Assert.Empty(s.Scrollback);
        Assert.Equal(0, s.Scrolled);
    }

    //a role swap builds a fresh renderer around the same painter and model, so post-swap turns still commit to the transcript
    [Fact]
    public void A_role_swap_renderer_still_commits_turns_to_the_transcript_model()
    {
        var s = new VtScreenSurface(60, 20);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var ticker = new ChromeTicker(painter, gate);
        var convo = new Conversation("sys");
        painter.AltScreen.Enter();
        System.Func<ChatMessage?> tail = () => convo.Messages.Count > 0 ? convo.Messages[^1] : null;

        var r1 = Gatto.Repl.Repl.BuildSessionRenderer(painter, s, T, "coder", ticker, gate, null, Gatto.Core.Home.ReasoningMode.Collapsed, tail, glyphs: GlyphSet.Unicode);
        r1.BeginTurn();
        r1.CommitUser(new[] { "first" });
        r1.OnTextDelta("before role switch\n");
        r1.EndTurn();

        var r2 = Gatto.Repl.Repl.BuildSessionRenderer(painter, s, T, "generalist", ticker, gate, null, Gatto.Core.Home.ReasoningMode.Collapsed, tail, glyphs: GlyphSet.Unicode);
        r2.BeginTurn();
        r2.CommitUser(new[] { "second" });
        r2.OnTextDelta("after role switch\n");
        r2.EndTurn();

        var rows = model.Items.SelectMany(i => i.Render(80, T, glyphs: GlyphSet.Unicode)).Select(TermText.StripAnsiForWidth).ToArray();
        Assert.Contains(rows, x => x.Contains("before role switch"));
        Assert.Contains(rows, x => x.Contains("after role switch"));
        Assert.Contains(rows, x => x.Contains("second"));
    }

    [Fact]
    public void Scrolling_up_reveals_earlier_transcript_without_touching_native_scrollback()
    {
        var (r, painter, s) = Wire(60, 16);
        r.BeginTurn();
        r.CommitUser(new[] { "long convo" });
        for (var i = 0; i < 20; i++) r.OnTextDelta($"answer line {i}\n");   //twenty rows overflow the sixteen-row viewport.
        r.EndTurn();

        //following the bottom shows the newest line and hides the oldest.
        Assert.Contains(s.Viewport, row => row.Contains("answer line 19"));
        Assert.DoesNotContain(s.Viewport, row => row.Contains("answer line 0"));
        AssertComposerPinnedToBottom(s);

        //home must reach the earliest content, so paging uses the transcript-area height, or the top rows stay unreachable
        var h = painter.ViewportRows();
        painter.Scroll.Home(s.Width, h);
        painter.Repaint();
        Assert.Contains(s.Viewport, row => row.Contains("answer line 0"));
        Assert.DoesNotContain(s.Viewport, row => row.Contains("answer line 19"));
        AssertComposerPinnedToBottom(s);

        //scrolling to the end must re-attach and follow the bottom again
        painter.Scroll.End();
        painter.Repaint();
        Assert.Contains(s.Viewport, row => row.Contains("answer line 19"));
        AssertComposerPinnedToBottom(s);

        Assert.Empty(s.Scrollback);
        Assert.Equal(0, s.Scrolled);
    }
}
