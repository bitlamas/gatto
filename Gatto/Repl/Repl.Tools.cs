using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl;

//the /tools panel on the composer task, at rest and while a turn runs, on the slot the background panel takes
public sealed partial class Repl
{
    private static readonly FooterKey[] ToolsKeys = [new("Enter", "detail"), new("Esc", "close")];

    //a throw out of the panel is said on a system line between blocks, the editor's key catch would drop it silently
    private void OpenToolsOrSay(RichState s)
    {
        try { OpenTools(s); }
        catch (Exception ex)
        {
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(ex.Message, transient: true);
        }
    }

    //read-only, so the key row is the same mid-turn. a prompt holding the slot is the answer on screen, so the open does nothing then
    private void OpenTools(RichState s)
    {
        if (listTools is null)
        {
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(ToolsUnavailable, transient: true);
            return;
        }
        var tools = CommandText.Ordered(listTools());
        var policy = PolicyRows();
        //a policy line with no tool draws no row, so it says what nothing armed says
        if (tools.Count == 0)
        {
            lock (s.Gate) s.Renderer.CommitSystemBetweenBlocks(NoToolsArmed, transient: true);
            return;
        }
        var chrome = _chrome ?? new Gatto.Repl.Render.Chrome.ChromeHandle { Painter = s.Painter, Renderer = s.Renderer };
        if (chrome.Painter is not { Alive: true } || s.Pump is null) return;
        using var hold = TakePanelSlot(s, out var close);
        if (hold is null) return;
        var fields = new PanelFields("tools", ToolsKeys, CountUnit: "armed", Role: roleName, LabelInk: Theme.ToolName,
            DetailEndFirst: false, DetailOf: i => ToolsPanel.Detail(tools[i], policy));
        var spec = new SelectSpec([], null, ToolsPanel.Rows(tools), FooterHint: null, Numbered: false, Close: close.Token, Panel: fields);
        if (close.IsCancellationRequested) return;   //a prompt that took the slot before the first frame closes it as a re-entry would
        new SelectPrompt(s.Surface, s.Theme, new ConsoleKeySource(), s.Pump, chrome, s.Glyphs).ShowPanel(spec, wait: false);
    }
}
