using System.Collections.Generic;
using System.Linq;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//raw stderr writes corrupt the frame, so every diagnostic on the rich path goes through the transcript model
public sealed class CommandEchoTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static StatusInfo Status(string role) => new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    private static (StreamRenderer R, TranscriptModel M) Rich(string role = "coder")
    {
        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, role, Status(role), glyphs: GlyphSet.Unicode), RoleForTint = role };
        var model = painter.Model;
        var r = new StreamRenderer(painter, s, T, role, new ChromeTicker(painter, gate), gate, model: model, convoTail: () => null);
        return (r, model);
    }

    [Fact]
    public void WarningSink_defaults_to_its_initial_sink()
    {
        var seen = new List<string>();
        var sink = new WarningSink(seen.Add);
        sink.Warn("boom");
        Assert.Equal(new[] { "boom" }, seen);
    }

    [Fact]
    public void WarningSink_routes_then_restores_on_dispose()
    {
        var stderr = new List<string>();
        var routed = new List<string>();
        var sink = new WarningSink(stderr.Add);

        using (sink.Route(routed.Add))
        {
            sink.Warn("during");
            Assert.Equal(new[] { "during" }, routed);
            Assert.Empty(stderr);                    //while routed, the default sink receives nothing.
        }

        sink.Warn("after");
        Assert.Equal(new[] { "after" }, stderr);     //disposing the route restores the default sink.
        Assert.Equal(new[] { "during" }, routed);    //the routed sink sees nothing after dispose.
    }

    [Fact]
    public void WarningSink_nested_routes_restore_in_reverse_order()
    {
        var a = new List<string>();
        var b = new List<string>();
        var c = new List<string>();
        var sink = new WarningSink(a.Add);

        using (sink.Route(b.Add))
        {
            using (sink.Route(c.Add)) sink.Warn("inner");
            sink.Warn("middle");
        }
        sink.Warn("outer");

        Assert.Equal(new[] { "inner" }, c);
        Assert.Equal(new[] { "middle" }, b);
        Assert.Equal(new[] { "outer" }, a);
    }

    [Fact]
    public void Hold_buffers_until_release_then_emits_in_order_through_the_restored_sink()
    {
        var routed = new List<string>();
        var sink = new WarningSink(_ => Assert.Fail("the default sink must not see a held warning"));

        using (sink.Route(routed.Add))
        {
            using var held = sink.Hold();
            sink.Warn("first");
            sink.Warn("second");
            Assert.Empty(routed);          //the held buffer emits nothing, which covers the window where the display model resets.

            held.Release();
            Assert.Equal(new[] { "first", "second" }, routed);   //release replays the warnings in arrival order.
        }
    }

    [Fact]
    public void Hold_disposed_without_release_still_emits_so_a_throw_cannot_swallow_a_warning()
    {
        var routed = new List<string>();
        var sink = new WarningSink(_ => { });

        using (sink.Route(routed.Add))
        {
            try
            {
                using var held = sink.Hold();
                sink.Warn("raised before the throw");
                throw new InvalidOperationException("recompose blew up after warning");
            }
            catch (InvalidOperationException) { }
        }

        Assert.Equal(new[] { "raised before the throw" }, routed);
    }

    [Fact]
    public void Hold_release_is_idempotent_and_post_release_warnings_pass_straight_through()
    {
        var routed = new List<string>();
        var sink = new WarningSink(_ => { });

        using (sink.Route(routed.Add))
        {
            var held = sink.Hold();
            sink.Warn("held");
            held.Release();
            held.Release();                //release then dispose must not emit twice.
            held.Dispose();
            sink.Warn("after");            //after the release the held buffer is dead, so a warning goes to the routed sink
        }

        Assert.Equal(new[] { "held", "after" }, routed);
    }

    [Fact]
    public void A_diagnostic_routed_to_the_rich_renderer_becomes_a_system_line_item()
    {
        var (r, m) = Rich();
        var stderr = new List<string>();
        var sink = new WarningSink(stderr.Add);

        //the rich loop routes the sink to the renderer's warning line for its lifetime.
        using (sink.Route(msg => r.OnWarning(msg)))
        {
            r.BeginTurn();
            sink.Warn("hook error (tool_result): boom");   //the text mirrors a hook handler error raised mid-turn.
            r.EndTurn();
        }

        Assert.Empty(stderr);   //nothing may reach stderr, which would corrupt the alt-screen frame
        var sys = m.Items.OfType<SystemLineItem>().ToList();
        Assert.Contains(sys, it => it.Render(80, T, glyphs: GlyphSet.Unicode).Any(row => row.Contains("hook error (tool_result): boom")));
        //the item renders with the system-line marker.
        Assert.Contains(m.Items.SelectMany(i => i.Render(80, T, glyphs: GlyphSet.Unicode)), row =>
            TermText.StripAnsiForWidth(row).Contains("♯") &&
            TermText.StripAnsiForWidth(row).Contains("hook error"));
    }

    [Fact]
    public void After_the_route_scope_ends_diagnostics_fall_back_to_stderr()
    {
        var (r, m) = Rich();
        var stderr = new List<string>();
        var sink = new WarningSink(stderr.Add);

        using (sink.Route(msg => r.OnWarning(msg))) { } //the empty scope stands for a live rich session.
        sink.Warn("extension: post-session note");   //the warning fires after the route scope ends.

        Assert.Equal(new[] { "extension: post-session note" }, stderr);   //warnings return to stderr once the route scope ends
        Assert.DoesNotContain(m.Items.OfType<SystemLineItem>(),
            it => it.Render(80, T, glyphs: GlyphSet.Unicode).Any(row => row.Contains("post-session note")));
    }

    [Fact]
    public void CommitPlain_lands_a_command_echo_item_that_rerenders_at_a_new_width()
    {
        var (r, m) = Rich();
        r.CommitPlain("/help\nrow two of the table");

        var echo = Assert.Single(m.Items.OfType<CommandEchoItem>());
        //the echo item re-renders verbatim at any width the compositor asks for, so it survives a resize
        Assert.Contains(echo.Render(80, T, glyphs: GlyphSet.Unicode), row => row.Contains("/help"));
        Assert.Contains(echo.Render(40, T, glyphs: GlyphSet.Unicode), row => row.Contains("/help"));
    }

    [Fact]
    public void A_role_confirmation_commits_as_a_system_line()
    {
        var (r, m) = Rich();
        r.CommitSystem("role: coder");
        var sys = Assert.Single(m.Items.OfType<SystemLineItem>());
        Assert.Contains(sys.Render(80, T, glyphs: GlyphSet.Unicode), row => TermText.StripAnsiForWidth(row).Contains("♯"));
        Assert.Contains(sys.Render(80, T, glyphs: GlyphSet.Unicode), row => row.Contains("role: coder"));
    }
}
