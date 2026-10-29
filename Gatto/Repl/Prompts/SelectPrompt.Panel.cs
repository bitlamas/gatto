using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//the panel's own loop, beside the prompt's so a prompt draws, keys and routes golden for golden
public sealed partial class SelectPrompt
{
    //what a panel resolved, with the setting's value at the act and whether the user moved it
    internal readonly record struct PanelResult(SelectOutcome Outcome, string? Setting, bool Moved);

    //blocks like Show. the scope takes the paging keys, and the mouse route rides with the factory so a prompt published over it inherits none
    internal PanelResult ShowPanel(SelectSpec spec, bool wait = true)
    {
        var panel = spec.Panel!;
        var state = new PanelState(panel, spec.Options, spec.Numbered, spec.InitialCursor, spec.ControlKeys);
        if (state.Empty) return new PanelResult(new SelectOutcome.Cancelled(), null, false);

        var painter = chrome?.Painter is { Alive: true } p ? p : null;
        using var focus = pump?.PushFocus(wait, takesScrollKeys: true);
        var k = focus?.Keys ?? keys;
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
                ConsoleKeyInfo key;
                try { key = k.ReadKey(readToken); }
                catch (OperationCanceledException) when (spec.Close.IsCancellationRequested && !spec.Cancel.IsCancellationRequested)
                {
                    return Result(new SelectOutcome.Cancelled());
                }
                var step = state.Key(key);
                if (step.Repaint) Repaint();
                if (step.Outcome is { } outcome) return Result(outcome);
            }
        }
        finally
        {
            painter?.ClearPanel(published);   //the factory and its route go together, and only this panel's own
            if (painter is null) surface.Write(Ansi.ShowCursor);
        }

        PanelResult Result(SelectOutcome outcome) => new(outcome, state.SettingValue, state.SettingMoved);

        //a snapshot of the state, the paint thread composes from the copy and records the layout it composed at
        void Publish()
        {
            var view = state.View();
            published = painter!.SetPanelFactory((w, room) =>
            {
                var frame = PanelComposer.Compose(panel, state.OptionsOf(view.Tab), spec.Numbered, view, w, room, theme, _glyphs);
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
