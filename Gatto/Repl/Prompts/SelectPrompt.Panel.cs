using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//the panel's own loop, beside the prompt's so a prompt draws, keys and routes golden for golden
public sealed partial class SelectPrompt
{
    //what a panel resolved, with the setting's value at the act, whether the user moved it, and the detail open at the end with its offset
    internal readonly record struct PanelResult(SelectOutcome Outcome, string? Setting, bool Moved,
        string? Detail = null, int DetailOffset = PanelState.End);

    //blocks like Show. the mouse route rides with the factory, so a prompt published over it inherits none
    internal PanelResult ShowPanel(SelectSpec spec, bool wait = true, string? detail = null, int detailOffset = PanelState.End)
    {
        var panel = spec.Panel!;
        //the contract has no watch row, so a panel's poll is a repaint key alone
        if (spec.Poll is { RenderRow: true })
            throw new ArgumentException("a panel's poll must set RenderRow false, a panel draws no watch row", nameof(spec));
        var state = new PanelState(panel, spec.Options, spec.Numbered, spec.InitialCursor, spec.ControlKeys, detail, detailOffset);
        if (state.Empty) return new PanelResult(new SelectOutcome.Cancelled(), null, false);

        var painter = chrome?.Painter is { Alive: true } p ? p : null;
        using var focus = pump?.PushFocus(wait, takesScrollKeys: true);
        var k = focus?.Keys ?? keys;
        var clock = spec.Poll is { } watching ? watching.ClockFor?.Invoke(focus) ?? watching.Clock : null;
        string? pollRow = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(spec.Cancel, spec.Close);
        var readToken = spec.Close.CanBeCanceled ? linked.Token : spec.Cancel;
        Func<MouseEvent, int, int, int, bool>? route = focus is null ? null
            : (e, row, w, _) => state.Mouse(e, row, w, () => focus.Inject(PanelState.Wake));
        Func<int, int, PanelContent>? published = null;
        var painted = 0;

        if (spec.DrainTypeAhead) while (k.KeyAvailable) k.ReadKey();
        if (painter is null)
        {
            surface.Write(Ansi.HideCursor);
            RenderInline();
        }

        try
        {
            if (painter is not null) Publish();
            while (true)
            {
                if (spec.Close.IsCancellationRequested) return Result(new SelectOutcome.Cancelled());
                if (spec.Poll is { } poll)
                {
                    var waited = WaitPoll(clock!, poll, spec.Close, now =>
                    {
                        if (poll.Row(now) is var fresh && fresh != pollRow) { pollRow = fresh; Repaint(); }
                    });
                    //rows that came back with another count are a re-entry's, as the arrival is
                    if (state.Stale) return Result(new SelectOutcome.Arrived(state.CursorIndex));
                    if (waited == PollStep.Closed) return Result(new SelectOutcome.Cancelled());
                    if (waited == PollStep.Arrived) return Result(new SelectOutcome.Arrived(state.CursorIndex));
                }
                ConsoleKeyInfo key;
                try { key = k.ReadKey(readToken); }
                catch (OperationCanceledException) when (spec.Close.IsCancellationRequested && !spec.Cancel.IsCancellationRequested)
                {
                    return Result(new SelectOutcome.Cancelled());
                }
                var step = state.Key(key);
                if (step.Repaint) Repaint();
                if (step.Outcome is { } outcome) return Result(outcome);
                if (state.Stale) return Result(new SelectOutcome.Arrived(state.CursorIndex));
            }
        }
        finally
        {
            painter?.ClearPanel(published);   //the factory and its route go together, and only this panel's own
            if (painter is null) surface.Write(Ansi.ShowCursor);
        }

        PanelResult Result(SelectOutcome outcome) => new(outcome, state.SettingValue, state.SettingMoved, state.Detail, state.DetailOffset);

        //a snapshot of the state, the paint thread composes from the copy and records the layout it composed at
        void Publish()
        {
            var view = state.View();
            var options = state.OptionsOf(view.Tab);
            published = painter!.SetPanelFactory((w, room) =>
            {
                var frame = PanelComposer.Compose(panel, options, spec.Numbered, view, w, room, theme, _glyphs);
                state.Record(frame.Layout, view.Tab);
                return new PanelContent(frame.Rows, null, spec.KeepsRoom);
            }, route);
        }

        //no painter, so the surface's height is the room and the panel windows here too
        void RenderInline()
        {
            var view = state.View();
            var room = surface.Height > 0 ? surface.Height : int.MaxValue;
            var frame = PanelComposer.Compose(panel, state.OptionsOf(view.Tab), spec.Numbered, view, surface.Width, room, theme, _glyphs);
            state.Record(frame.Layout, view.Tab);
            var lines = Math.Max(frame.Rows.Count, painted);
            for (var i = 0; i < lines; i++)
                surface.Write("\r" + Ansi.ClearToEol + (i < frame.Rows.Count ? frame.Rows[i] : "") + "\n");
            painted = lines;
        }

        void Repaint()
        {
            if (painter is null)
            {
                surface.Write(Ansi.Up(painted) + "\r");
                RenderInline();
            }
            else Publish();
        }
    }
}
