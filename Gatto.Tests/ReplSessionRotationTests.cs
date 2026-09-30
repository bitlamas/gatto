//a warning raised before Reset dies with the old transcript, so the ordering and replay are pinned here
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class ReplSessionRotationTests
{
    //literal stand-ins for the handler's outcome rows, since only the replay order is under test
    private const string CarriedOutcome = "carried outcome line";
    private const string CarriedFailure = "carried failure line";

    private static readonly Theme T = new(new TermCaps(true, true));

    private static (StreamRenderer R, TranscriptModel M) Rich()
    {
        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(s, T, gate)
        {
            Frame = new InputFrame(s, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate,
            model: painter.Model, convoTail: () => null);
        return (r, painter.Model);
    }

    //mid-session diagnostics must reach the renderer's sharp row, so a warning that reaches stderr fails the test
    private static (WarningSink Sink, IDisposable Route) Routed(StreamRenderer r)
    {
        var sink = new WarningSink(m => Assert.Fail($"warning escaped to stderr: {m}"));
        return (sink, sink.Route(r.OnWarning));
    }

    private static string[] SystemLines(TranscriptModel m) =>
        m.Items.OfType<SystemLineItem>().Select(i => i.Text).ToArray();

    [Fact]
    public void RotateForNew_WarningRaisedByTheRecompose_SurvivesTheReset()
    {
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        m.Append(new UserEchoItem(new[] { "a turn from the old conversation" }));

        Conversation? adopted = null;
        Gatto.Repl.Repl.RotateForNew(
            recomposeSystem: () =>
            {
                //what the real recompose does on a malformed .gatto.json: keep the last good prefix and warn
                sink.Warn("context files not re-read — unknown key \"com\" in .gatto.json");
                return Composed.Text("the last prefix that composed cleanly");
            },
            held: sink.Hold(),
            adopt: fresh => adopted = fresh,
            model: m,
            renderer: r,
            lead: new CommandEchoItem(new[] { "banner" }, null) { Transient = true },
            line: Gatto.Repl.Repl.NewConversationLine,
            carry: Array.Empty<string>());

        Assert.NotNull(adopted);
        Assert.Equal("the last prefix that composed cleanly", adopted!.Messages[0].Content);
        //the reset is total, so screen, file and --continue cannot diverge
        Assert.DoesNotContain(m.Items, i => i is UserEchoItem);
        //the warning survives the reset and reaches the screen
        Assert.Contains(SystemLines(m), t => t.Contains("context files not re-read"));
    }

    [Fact]
    public void RotateForNew_TheWarningReadsAboveTheConfirmationLine_UnderTheBanner()
    {
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        Gatto.Repl.Repl.RotateForNew(
            recomposeSystem: () => { sink.Warn("memory index truncated — 3 lines dropped"); return Composed.Text("fresh"); },
            held: sink.Hold(),
            adopt: _ => { },
            model: m,
            renderer: r,
            lead: new CommandEchoItem(new[] { "banner" }, null) { Transient = true },
            line: Gatto.Repl.Repl.NewConversationLine,
            carry: Array.Empty<string>());

        var items = m.Items.ToList();
        var banner = items.FindIndex(i => i is CommandEchoItem);
        var warning = items.FindIndex(i => i is SystemLineItem s && s.Text.Contains("memory index truncated"));
        var confirmation = items.FindIndex(i => i is SystemLineItem s && s.Text == Gatto.Repl.Repl.NewConversationLine);

        Assert.True(banner >= 0 && warning > banner, $"banner {banner}, warning {warning}");
        //the recompose speaks first, so the warning sits above the confirmation line
        Assert.True(confirmation > warning, $"warning {warning}, confirmation {confirmation}");
    }

    [Fact]
    public void RotateForNew_SilentRecompose_LeavesTheRotationExactlyAsItWas()
    {
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        m.Append(new UserEchoItem(new[] { "old turn" }));
        Gatto.Repl.Repl.RotateForNew(
            recomposeSystem: () => Composed.Text("fresh"),
            held: sink.Hold(),
            adopt: _ => { },
            model: m,
            renderer: r,
            lead: new CommandEchoItem(new[] { "banner" }, null) { Transient = true },
            line: Gatto.Repl.Repl.NewConversationLine,
            carry: Array.Empty<string>());

        Assert.DoesNotContain(m.Items, i => i is UserEchoItem);
        Assert.Equal(new[] { Gatto.Repl.Repl.NewConversationLine }, SystemLines(m));   //a silent recompose adds no rows of its own
    }

    [Fact]
    public void RotateForNew_NoWarningSink_StillRotates()
    {
        //a null warn sink must not stop the rotation or throw
        var (r, m) = Rich();
        m.Append(new UserEchoItem(new[] { "old turn" }));

        Gatto.Repl.Repl.RotateForNew(
            recomposeSystem: () => Composed.Text("fresh"),
            held: null,
            adopt: _ => { },
            model: m,
            renderer: r,
            lead: new CommandEchoItem(new[] { "banner" }, null) { Transient = true },
            line: Gatto.Repl.Repl.NewConversationLine,
            carry: Array.Empty<string>());

        Assert.DoesNotContain(m.Items, i => i is UserEchoItem);
        Assert.Equal(new[] { Gatto.Repl.Repl.NewConversationLine }, SystemLines(m));
    }

    [Fact]
    public void RotateForNew_TakesAnAlreadyOpenHold_SoWarningsRaisedBeforeItSurvive()
    {
        //the hold has to be open before the warning fires, so the caller opens it and hands it in
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        using var held = sink.Hold();                              //the hold must be open before the warning below fires
        sink.Warn("hook error (session_summary): access to the path 'log.md' is denied");

        Gatto.Repl.Repl.RotateForNew(
            recomposeSystem: () => Composed.Text("fresh"),
            held: held,
            adopt: _ => { },
            model: m,
            renderer: r,
            lead: new CommandEchoItem(new[] { "banner" }, null) { Transient = true },
            line: Gatto.Repl.Repl.NewConversationLine,
            carry: new[] { CarriedOutcome });

        Assert.Equal(
            new[]
            {
                CarriedOutcome,
                "hook error (session_summary): access to the path 'log.md' is denied",
                Gatto.Repl.Repl.NewConversationLine,
            },
            SystemLines(m));
    }

    [Fact]
    public void RotateForNew_CarriedRowsSurviveTheReset_AndReadAboveTheHeldWarning()
    {
        //rows written straight to the renderer never reach the hold, so they must go through carry or the reset erases them
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        m.Append(new UserEchoItem(new[] { "a turn from the old conversation" }));

        Gatto.Repl.Repl.RotateForNew(
            recomposeSystem: () => { sink.Warn("context files not re-read — unknown key \"com\""); return Composed.Text("fresh"); },
            held: sink.Hold(),
            adopt: _ => { },
            model: m,
            renderer: r,
            lead: new CommandEchoItem(new[] { "banner" }, null) { Transient = true },
            line: Gatto.Repl.Repl.NewConversationLine,
            carry: new[] { "memory not updated — would exceed 64 KB", CarriedFailure });

        Assert.DoesNotContain(m.Items, i => i is UserEchoItem);          //no echo item survives, so the reset ran
        var lines = SystemLines(m);
        //the rows replay oldest first, and the confirmation closes the rotation
        Assert.Equal(
            new[]
            {
                "memory not updated — would exceed 64 KB",
                CarriedFailure,
                "context files not re-read — unknown key \"com\"",
                Gatto.Repl.Repl.NewConversationLine,
            },
            lines);
    }

    [Fact]
    public void RotateDisplay_CarriesTheBankFailure_PastTheCompactReset()
    {
        //past the 64 KB cap every bank call fails, and the compacted line reports silence, so this row is the user's only notice
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        m.Append(new UserEchoItem(new[] { "a pre-compact turn" }));
        using var held = sink.Hold();

        Gatto.Repl.Repl.RotateDisplay(m, r, new SessionLeadItem("summary", After: null), "compacted",
            held, new[] { "memory not updated — memory file INDEX.md would exceed 64 KB — compress or split topics" });

        Assert.DoesNotContain(m.Items, i => i is UserEchoItem);
        Assert.Equal(
            new[]
            {
                "memory not updated — memory file INDEX.md would exceed 64 KB — compress or split topics",
                "compacted",
            },
            SystemLines(m));
    }

    [Fact]
    public void RotateDisplay_CompactShape_KeepsTheSessionLead_AndTheHeldWarning()
    {
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        m.Append(new UserEchoItem(new[] { "a pre-compact turn" }));

        //the recompose sits inside CompactAsync, so the handler opens the hold around everything
        using var held = sink.Hold();
        sink.Warn("context files not re-read — unknown key \"com\" in .gatto.json");

        Gatto.Repl.Repl.RotateDisplay(m, r, new SessionLeadItem("what happened before", After: null), "compacted",
            held, Array.Empty<string>());

        Assert.DoesNotContain(m.Items, i => i is UserEchoItem);
        Assert.Contains(m.Items, i => i is SessionLeadItem s && s.Text == "what happened before");
        Assert.Contains(SystemLines(m), t => t.Contains("context files not re-read"));
        Assert.Contains(SystemLines(m), t => t == "compacted");
    }

    [Fact]
    public void RotateDisplay_NoLead_CommitsOnlyTheConfirmation()
    {
        var (r, m) = Rich();
        var (sink, route) = Routed(r);
        using var _ = route;

        using var held = sink.Hold();
        Gatto.Repl.Repl.RotateDisplay(m, r, lead: null, line: "compacted", held: held, carry: Array.Empty<string>());

        Assert.DoesNotContain(m.Items, i => i is SessionLeadItem);
        Assert.Equal(new[] { "compacted" }, SystemLines(m));
    }
}
