using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl;

//what runs between turns: the status line's facts, the background panel on the composer task, and the panel slot a turn-thread prompt takes from it
public sealed partial class Repl
{
    //how often the open panel asks the registry, the rate its runtimes may move at
    private static readonly TimeSpan BackgroundPoll = TimeSpan.FromSeconds(1);

    //the list's keys on a shell row and on the watch row, and a shell's detail's, each naming only what that row takes
    private static readonly FooterKey[] TaskKeys = [new("Enter", "detail"), new("x", "stop"), new("Esc", "close")];
    private static readonly FooterKey[] WatchKeys = [new("Enter", "detail"), new("Esc", "close")];
    private static readonly FooterKey[] ShellDetailKeys = [new("x", "stop"), new("Esc", "back")];

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
            //the composer-thread commit, so a line said between two streamed blocks never cuts the reply
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(ex.Message, transient: true);
        }
    }

    //takes the panel slot and registers the close a turn-thread prompt signals, null when a prompt already holds the slot
    private static IDisposable? TakePanelSlot(RichState s, out CancellationTokenSource close)
    {
        close = null!;
        if (!s.ModalSlot.Wait(0)) return null;
        close = new CancellationTokenSource();
        lock (s.Gate) s.BackgroundClose = close;
        return new PanelHold(s, close);
    }

    //the panel's release, once: the close is cleared only while it is still this panel's, then the slot goes back
    private sealed class PanelHold(RichState s, CancellationTokenSource close) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (s.Gate) { if (ReferenceEquals(s.BackgroundClose, close)) s.BackgroundClose = null; }
            close.Dispose();
            s.ModalSlot.Release();
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
        using var hold = TakePanelSlot(s, out var close);
        if (hold is null) return;
        var cursor = 0;
        string? cursorKey = null;
        string? detail = null;
        var detailOffset = PanelState.End;
        var g = s.Glyphs;
        while (!close.IsCancellationRequested)
        {
            var live = _tasks?.Live ?? [];
            Watch? watched;
            lock (s.Gate) watched = _watch;
            WatchView? WatchNow() => watched is null ? null
                : new WatchView(Shown(watched.Folder), watched.Paused, watched.Count, Watch.K, watched.Seen);
            var options = BackgroundPanel.Rows(live, WatchNow(), g);
            if (options.Count == 0)
            {
                //the list emptied under the open panel, so the transcript says what an open on nothing says
                lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(NoBackgroundTasks, transient: true);
                return;
            }
            //the cursor follows its row by key, and a row that left leaves it clamped where it was
            var keyed = cursorKey is null ? -1 : options.ToList().FindIndex(o => o.Key == cursorKey);
            cursor = keyed >= 0 ? keyed : Math.Clamp(cursor, 0, options.Count - 1);
            var numbers = live.Select(t => t.Number).ToList();
            var shells = live.Count;
            var fields = new PanelFields("tasks", TaskKeys, Count: BackgroundPanel.Count(shells, watched is not null, g),
                Role: roleName, SearchWhenLong: false, LabelWhole: true,
                KeysFor: i => i < shells ? TaskKeys : WatchKeys,
                DetailOf: i => i < shells ? BackgroundPanel.ShellDetail(live[i], g) : WatchNow() is { } w ? BackgroundPanel.WatchDetail(w, g) : null,
                DetailKeysFor: i => i < shells ? ShellDetailKeys : null,
                OptionsNow: () => BackgroundPanel.Rows(live, WatchNow(), g));
            var spec = new SelectSpec([], null, options, FooterHint: null, InitialCursor: cursor, Numbered: false, ControlKeys: ['x'],
                Poll: new PollSpec(
                    //a re-entry only when the set of tasks or the watch changed, every runtime, gloss and growth is a repaint that reads live
                    Arrived: () => !(_tasks?.Live ?? []).Select(t => t.Number).SequenceEqual(numbers) || !ReferenceEquals(_watch, watched),
                    Row: _ => BackgroundPanel.StateKey(live) + (watched is null ? "" : "|" + (watched.Paused ? "p" : "") + watched.Count),
                    Clock: null, Interval: BackgroundPoll, RenderRow: false,
                    ClockFor: scope => new ScopePollClock(scope, close.Token)),
                Close: close.Token, Panel: fields);
            var result = new SelectPrompt(s.Surface, s.Theme, new ConsoleKeySource(), s.Pump, chrome, g)
                .ShowPanel(spec, wait: false, detail, detailOffset);
            detail = result.Detail;
            detailOffset = result.DetailOffset;
            switch (result.Outcome)
            {
                case SelectOutcome.Arrived a:
                    cursor = a.Cursor;
                    break;
                case SelectOutcome.Control { Key: 'x' } x:
                    //the same act /tasks kill N does, and the row reads the kill's gloss at once off its handle
                    if (x.Cursor >= 0 && x.Cursor < live.Count) _tasks?.Kill(live[x.Cursor].Number, Gatto.Core.Tools.BackgroundTasks.ByTasks);
                    cursor = x.Cursor;
                    break;
                default:
                    return;   //an escape from the list, or a turn-thread prompt's close with or without a detail open
            }
            cursorKey = cursor >= 0 && cursor < options.Count ? options[cursor].Key : null;
        }
    }
}
