using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl;

//the /permissions panel on the composer task, at rest and while a turn runs, on the slot the background panel takes
public sealed partial class Repl
{
    //r is named only while no turn runs, the panel reads only while one does
    private static readonly FooterKey[] PermissionKeys = [new("r", "revoke"), new("Esc", "close")];
    private static readonly FooterKey[] PermissionCloseKeys = [new("Esc", "close")];

    //a throw out of the panel is said on a system line between blocks, the editor's key catch would drop it silently
    private void OpenPermissionsOrSay(RichState s)
    {
        try { OpenPermissions(s); }
        catch (Exception ex)
        {
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(ex.Message, transient: true);
        }
    }

    //the typed form's own lines for a store that is missing or empty, saved as the typed form saves them
    private void SayGrants(RichState s)
    {
        foreach (var line in HandlePermissions("").Split('\n'))
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(line);
    }

    //revokes under the lock every turn sets its token under, so the revoke ends before a turn's token exists or the press is spent. null when spent
    internal static PermissionEntry? RevokeAtRest(object turnLock, Func<bool> inFlight, PermissionStore store, int index)
    {
        lock (turnLock) return inFlight() ? null : store.RevokeAt(index);
    }

    //the grants as a list with r revoking the cursor row. a prompt holding the slot is the answer on screen, so the open does nothing then
    private void OpenPermissions(RichState s)
    {
        //an open on nothing answers before the slot, so it says its lines even under a prompt
        if (permissions is not { } store || store.ListEntries().Count == 0)
        {
            SayGrants(s);
            return;
        }
        var chrome = _chrome ?? new Gatto.Repl.Render.Chrome.ChromeHandle { Painter = s.Painter, Renderer = s.Renderer };
        if (chrome.Painter is not { Alive: true } || s.Pump is null) return;
        using var hold = TakePanelSlot(s, out var close);
        if (hold is null) return;

        bool InFlight() { lock (_ctsLock) return s.TurnCts is not null; }
        var cursor = 0;
        string? cursorKey = null;
        var g = s.Glyphs;
        while (!close.IsCancellationRequested)
        {
            var entries = store.ListEntries();
            if (entries.Count == 0)
            {
                //the last grant went under the open panel, so the transcript says what an open on nothing says
                SayGrants(s);
                return;
            }
            var options = PermissionsPanel.Rows(entries);
            //the cursor follows its row by key, and a row that left leaves it clamped where it was
            var keyed = cursorKey is null ? -1 : options.ToList().FindIndex(o => o.Key == cursorKey);
            cursor = keyed >= 0 ? keyed : Math.Clamp(cursor, 0, options.Count - 1);
            var wildShown = wild?.On == true;
            var fields = new PanelFields("permissions", PermissionKeys, Count: PermissionsPanel.Count(entries.Count),
                Subtitle: PermissionsPanel.Subtitle(wildShown, g), Header: PermissionsPanel.Header, LabelHeader: PermissionsPanel.LabelHeader,
                Role: roleName, SearchWhenLong: false, DigitsMove: true,
                KeysFor: _ => InFlight() ? PermissionCloseKeys : PermissionKeys);
            var spec = new SelectSpec([], null, options, FooterHint: null, InitialCursor: cursor, Numbered: true, ControlKeys: ['r'],
                //the turn's state repaints r on or off, and a wild toggle under the panel re-enters it so the subtitle follows the fact
                Poll: new PollSpec(Arrived: () => (wild?.On == true) != wildShown,
                    Row: _ => (InFlight() ? "turn" : "rest") + (wild?.On == true ? "|wild" : ""),
                    Clock: null, Interval: BackgroundPoll, RenderRow: false,
                    ClockFor: scope => new ScopePollClock(scope, close.Token)),
                Close: close.Token, Panel: fields);
            if (close.IsCancellationRequested) return;   //a prompt that took the slot before the first frame closes it as a re-entry would
            var result = new SelectPrompt(s.Surface, s.Theme, new ConsoleKeySource(), s.Pump, chrome, g).ShowPanel(spec, wait: false);
            switch (result.Outcome)
            {
                case SelectOutcome.Arrived a:
                    cursor = a.Cursor;
                    break;
                case SelectOutcome.Control { Key: 'r' } r:
                    //a prompt's Always cannot land between: HoldModalSlot closes this panel before it waits, and the slot frees only when this returns
                    if (RevokeAtRest(_ctsLock, () => s.TurnCts is not null, store, r.Cursor) is { } gone)
                        lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(PermissionsPanel.Revoked(gone));
                    cursor = r.Cursor;
                    break;
                default:
                    return;   //an escape, or a turn-thread prompt's close
            }
            cursorKey = cursor >= 0 && cursor < options.Count ? options[cursor].Key : null;
        }
    }
}
