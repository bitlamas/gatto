using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//where a screen is shown and an answer comes back. the /setup command binds the REPL's prompter, since a console reader would be a second reader on one stdin
internal interface IWizardSurface
{
    //the chosen option's key, or null when the user backed out. both tick and check arrive as parameters, so screens declare and the face owns the clock
    string? Choose(WizardScreen.Choice c, Func<bool>? watch = null, Func<FetchTick?>? tick = null,
        Func<CheckTick?>? check = null);

    //how many model rows this face can show, since the height is only known at the face. six is the count for a numbered list, and the TUI face overrides it
    int RowBudget => Gatto.Core.Acquire.HubSearch.DefaultRowBudget;

    //the glyph vocabulary this face speaks, a fact about the host it draws on. unicode by default
    Gatto.Terminal.GlyphSet Glyphs => Gatto.Terminal.GlyphSet.Unicode;

    //whether this face can bind m, which needs a key loop the numbered prompter has not. the default is the safe one, so an unbound key is never named
    bool CanSwitchSource => false;

    //whether this face draws a choice's body rows, since the closing narration must go only where they are drawn. the default keeps it
    bool ShowsChoiceBodyRows => false;

    //the typed text, or null when the user backed out
    string? Ask(WizardScreen.Ask a);

    void Show(WizardScreen.Info i);
    void End(WizardScreen.Terminal t);
}
