using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl;

//what runs between turns: the status line's facts, the background panel on the composer task, and the panel slot a turn-thread prompt takes from it
public sealed partial class Repl
{
    //how often the open panel asks the registry, the rate its runtimes may move at
    private static readonly TimeSpan BackgroundPoll = TimeSpan.FromSeconds(1);

    //how long a prompt waiting for the slot sleeps before it signals the close again, in case a panel opened after the first signal
    private const int SlotRetryMs = 50;

    //the cell's inputs, read under the gate at each paint. the selection counts only while the cell is on
    private BackgroundFacts BackgroundFactsNow(LineEditor? editor)
    {
        var running = _tasks?.Running.Count ?? 0;
        var watch = _watch;
        var on = running > 0 || watch is not null;
        return new BackgroundFacts(running, watch is not null, watch?.Paused == true, on && editor?.CellSelected == true);
    }

    //a turn-thread prompt's hold on the panel slot: the open panel is told to close, and the wait signals again until the slot is free. call with no gate held
    private static IDisposable HoldModalSlot(RichState s)
    {
        while (true)
        {
            CancellationTokenSource? close;
            lock (s.Gate) close = s.BackgroundClose;
            try { close?.Cancel(); }
            catch (ObjectDisposedException) { }   //the panel left between the read and the signal
            if (s.ModalSlot.Wait(SlotRetryMs)) return new SlotRelease(s.ModalSlot);
        }
    }

    private sealed class SlotRelease(SemaphoreSlim slot) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) slot.Release(); }
    }

    //a throw out of the panel is said on a system line and the composer comes back, the editor's key catch would drop it silently
    private void OpenBackgroundOrSay(RichState s)
    {
        try { OpenBackground(s); }
        catch (Exception ex)
        {
            lock (s.Gate) s.Renderer.CommitSystem(ex.Message);
        }
    }

    //the background panel, run on the composer task. a prompt holding the slot is the answer on screen, so the open does nothing then
    private void OpenBackground(RichState s)
    {
        Watch? armed;
        lock (s.Gate) armed = _watch;
        if ((_tasks?.Snapshot.Count ?? 0) == 0 && armed is null)
        {
            lock (s.Gate) s.Painter.SetInfoPanel((_, _) => ["  " + NoBackgroundTasks]);
            return;
        }
        //the session's own painter when no prompter shares a handle, so the panel always draws as chrome
        var chrome = _chrome ?? new Gatto.Repl.Render.Chrome.ChromeHandle { Painter = s.Painter, Renderer = s.Renderer };
        if (chrome.Painter is not { Alive: true } || s.Pump is null) return;
        if (!s.ModalSlot.Wait(0)) return;
        var close = new CancellationTokenSource();
        lock (s.Gate) s.BackgroundClose = close;
        try
        {
            var cursor = 0;
            int? detail = null;
            while (!close.IsCancellationRequested)
            {
                var live = _tasks?.Live ?? [];
                Watch? watch;
                lock (s.Gate) watch = _watch;
                var view = watch is null ? null : new WatchView(Shown(watch.Folder), watch.Paused, watch.Count, Watch.K, watch.Seen);
                var count = live.Count + (view is null ? 0 : 1);
                if (count == 0) return;
                cursor = Math.Clamp(cursor, 0, count - 1);
                if (detail >= count) detail = null;
                var numbers = live.Select(t => t.Number).ToList();
                var watched = watch;
                var grown = detail is int open && open < live.Count ? live[open].OutputSoFar() : default;
                var g = s.Glyphs;
                IReadOnlyList<DetailRow>? rows = null;
                Func<int, IReadOnlyList<DetailRow>>? rowsAt = null;
                if (detail is int d && d < live.Count)
                {
                    var t = live[d];
                    rows = BackgroundPanel.ShellDetail(t, int.MaxValue, g);
                    rowsAt = cap => BackgroundPanel.ShellDetail(t, cap, g);
                }
                else if (detail is not null && view is not null)
                    rows = BackgroundPanel.WatchDetail(view, g);
                var labels = BackgroundPanel.Labels(live, view, s.Surface.Width, g);
                var spec = new SelectSpec([], null, [.. labels.Select(l => new SelectOption(l))], rows,
                    FooterHint: null, InitialCursor: cursor, Numbered: false, ControlKeys: ['x'], DetailAt: rowsAt,
                    LabelsAt: w => BackgroundPanel.Labels(live, view, w, g),
                    Poll: new PollSpec(
                        //the rows change only at a re-entry, when the set of tasks or the watch changed, or the open detail's output grew
                        Arrived: () => !(_tasks?.Live ?? []).Select(t => t.Number).SequenceEqual(numbers)
                            || !ReferenceEquals(_watch, watched)
                            || (detail is int o && o < live.Count && live[o].OutputSoFar() != grown),
                        Row: _ => BackgroundPanel.StateKey(live) + (view is null ? "" : "|" + (watched!.Paused ? "p" : "") + watched.Count),
                        Clock: null, Interval: BackgroundPoll, RenderRow: false,
                        ClockFor: scope => new ScopePollClock(scope, close.Token)),
                    Close: close.Token);
                var outcome = new SelectPrompt(s.Surface, s.Theme, new ConsoleKeySource(), s.Pump, chrome, g).Show(spec, wait: false);
                switch (outcome)
                {
                    case SelectOutcome.Arrived a:
                        cursor = a.Cursor;
                        break;
                    case SelectOutcome.Chosen c:
                        detail = detail == c.Index ? null : c.Index;
                        cursor = c.Index;
                        break;
                    case SelectOutcome.Control { Key: 'x' } x:
                        //the same act /tasks kill N does, and the row reads the kill's gloss at once off its handle
                        if (x.Cursor >= 0 && x.Cursor < live.Count) _tasks?.Kill(live[x.Cursor].Number, Gatto.Core.Tools.BackgroundTasks.ByTasks);
                        cursor = x.Cursor;
                        break;
                    case SelectOutcome.Cancelled when detail is not null && !close.IsCancellationRequested:
                        detail = null;
                        break;
                    default:
                        return;
                }
            }
        }
        finally
        {
            lock (s.Gate) { if (ReferenceEquals(s.BackgroundClose, close)) s.BackgroundClose = null; }
            close.Dispose();
            s.ModalSlot.Release();
        }
    }
}
