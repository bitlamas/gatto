using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl;

//the /help panel on the composer task, at rest and while a turn runs: the commands, the keys and the extensions, one show with three tabs
public sealed partial class Repl
{
    private static readonly FooterKey HelpEsc = new("Esc", "close");
    private static readonly FooterKey[] HelpCommandKeys = [new("Enter", "compose"), HelpEsc];
    private static readonly FooterKey[] HelpCloseKeys = [HelpEsc];
    private static readonly FooterKey[] HelpToggleKeys = [new("Space", "on or off"), HelpEsc];

    //a throw out of the panel is said on a system line between blocks, and the composer keeps what it had
    private string? OpenHelpOrSay(RichState s)
    {
        try { return OpenHelp(s); }
        catch (Exception ex)
        {
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(ex.Message, transient: true);
            return null;
        }
    }

    //the text Enter puts in the composer, null when the panel closed on anything else. a prompt holding the slot is the answer on screen, so the open does nothing then
    private string? OpenHelp(RichState s)
    {
        var chrome = _chrome ?? new Gatto.Repl.Render.Chrome.ChromeHandle { Painter = s.Painter, Renderer = s.Renderer };
        if (chrome.Painter is not { Alive: true } || s.Pump is null) return null;
        using var hold = TakePanelSlot(s, out var close);
        if (hold is null) return null;

        bool InFlight() { lock (_ctsLock) return s.TurnCts is not null; }
        //the rows each tab asks at each view, kept so an act and the answer name the rows the user saw
        IReadOnlyList<SlashCommands.Cmd> known = KnownCommands();
        IReadOnlyList<ExtensionView> views = listExtensions?.Invoke() ?? [];
        IReadOnlyList<SelectOption> CommandRows() { known = KnownCommands(); return HelpPanel.Commands(known); }
        IReadOnlyList<SelectOption> ExtensionRows() { views = listExtensions?.Invoke() ?? []; return HelpPanel.Extensions(views, s.Glyphs); }
        //a write that failed is said under its row until that row's next clean toggle, since the session holds the flag and the file is the next launch's
        var failedWrites = new Dictionary<string, string>(StringComparer.Ordinal);
        var armedAtOpen = ArmedToolNames();

        var tabs = new PanelTab[]
        {
            new("commands", CommandRows(), OptionsNow: CommandRows),
            new("shortcuts", [], Keys: HelpCloseKeys, Rows: HelpPanel.Shortcuts(s.Glyphs)),
            //read-only while a turn runs, and the endpoint's file is never turned off, so Space is named on neither
            new("extensions", ExtensionRows(), Keys: HelpCloseKeys,
                KeysFor: i => setExtension is null || InFlight() || views[i].ServesEndpoint ? HelpCloseKeys : HelpToggleKeys,
                Under: new PanelUnder.Sentence(i => failedWrites.GetValueOrDefault(views[i].Name) ?? HelpPanel.Sentence(views[i])),
                OptionsNow: ExtensionRows,
                Rows: [new DetailRow(HelpPanel.EmptyLine, Dim: true)]),
        };
        var fields = new PanelFields("help", HelpCommandKeys, Tabs: tabs, Role: roleName,
            Act: (c, i) =>
            {
                if (c != ' ' || setExtension is null || InFlight() || views[i].ServesEndpoint) return false;
                var view = views[i];
                if (setExtension(view.Name, !view.On) is { } line) failedWrites[view.Name] = line;
                else failedWrites.Remove(view.Name);
                return true;
            });
        var spec = new SelectSpec([], null, tabs[0].Options, FooterHint: null, Numbered: false, ControlKeys: [' '],
            //the turn's state is the repaint key, so Space comes and goes on the row when a turn ends or starts under the panel
            Poll: new PollSpec(Arrived: () => false, Row: _ => InFlight() ? "turn" : "rest", Clock: null, Interval: BackgroundPoll,
                RenderRow: false, ClockFor: scope => new ScopePollClock(scope, close.Token)),
            Close: close.Token, Panel: fields);
        if (close.IsCancellationRequested) return null;   //a prompt that took the slot before the first frame closes it as a re-entry would
        var result = new SelectPrompt(s.Surface, s.Theme, new ConsoleKeySource(), s.Pump, chrome, s.Glyphs).ShowPanel(spec, wait: false);

        //a tool that left or joined the list moves the prefix, which a session that has talked pays for on its next request
        if (!ArmedToolNames().SequenceEqual(armedAtOpen, StringComparer.Ordinal) && convo.Messages.Any(m => m.Role != "system"))
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(EffortRereadsLine, transient: true);
        //a command that takes an argument gets the space it needs, and none is run from here
        return result.Outcome is SelectOutcome.Chosen(var index) && index >= 0 && index < known.Count
            ? known[index].Name + (known[index].Args.Length > 0 ? " " : "")
            : null;
    }

    private IReadOnlyList<string> ArmedToolNames() =>
        [.. (listTools?.Invoke() ?? []).Select(t => t.Name).Order(StringComparer.Ordinal)];
}
