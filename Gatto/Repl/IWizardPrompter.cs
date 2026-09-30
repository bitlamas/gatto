namespace Gatto.Repl;

//one modal question through the caller's own plumbing, null means the user left, and no implementation builds its own key source
public interface IWizardPrompter
{
    //ask one question and return the chosen label or the typed text, null means the user left rather than an exception
    WizardAnswer? AskOne(WizardAsk ask);
}

//a single wizard question, a free-text row (and a review step) only when the caller asks, plus the shelf's table fields passed through

//how a wizard question came back, so both faces can report a control, and Control is always null on the plain path
public sealed record WizardAnswer(string? Label = null, char? Control = null, int Cursor = 0);

public sealed record WizardAsk(
    string Question,
    IReadOnlyList<SelectOption> Options,
    string? FreeTextLabel = null,
    string FooterHint = "Esc to leave",
    IReadOnlyList<BodyRow>? BodyRows = null,
    Func<int, IReadOnlyList<string>>? LabelsAt = null,
    Func<int, IReadOnlyList<SelectHeading>>? HeadingsAt = null,
    string? FooterLegend = null,
    bool Numbered = true,
    IReadOnlyList<char>? ControlKeys = null,
    int InitialCursor = 0,
    //a screen with no default answer, passed across the prompter seam since the rich face builds its SelectSpec from this record
    bool NoInitialCursor = false,
    //where the free-text row sits among the options, null keeps it last, so a back row never sits above the input box
    int? FreeTextIndex = null);
