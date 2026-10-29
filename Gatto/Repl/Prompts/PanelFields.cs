using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//what makes a select spec a panel: the title block, the rows under the list and the key row. a spec without it is a prompt and draws as it always did
public sealed record PanelFields(
    string Title,
    IReadOnlyList<FooterKey> Keys,
    string? CountUnit = null,   //the word after the count in the title, null shows no count
    IReadOnlyList<PanelTab>? Tabs = null,
    string? Subtitle = null,
    IReadOnlyList<string>? Header = null,   //the names over the columns, drawn only when an option carries columns
    string? Role = null,   //the role whose tint inks the cursor, the marks and the caret, null keeps the accent
    IReadOnlyList<FooterKey>? BoxKeys = null,   //the keys while the search box holds them, null is Enter list and Esc back
    string? Legend = null,   //the words after the dot on the key row's right edge
    PanelUnder? Under = null,
    bool SearchWhenLong = true,
    string? Count = null,   //the words after the title's dot as the caller composes them, winning over CountUnit
    Func<int, IReadOnlyList<FooterKey>>? KeysFor = null,   //the cursor row's keys by its original index, winning over Keys
    Func<int, IReadOnlyList<DetailRow>?>? DetailOf = null,   //a row's detail by its original index, null for a row Enter answers on
    Func<int, IReadOnlyList<FooterKey>>? DetailKeysFor = null,   //an open detail's keys, null is Esc back
    Func<IReadOnlyList<SelectOption>>? OptionsNow = null,   //the rows asked again at each view, for a screen whose rows read live
    bool LabelWhole = false,   //the label column at its widest label, so the columns take the cut
    RgbColor? LabelInk = null,   //the ink of a label off the cursor row, null is bright
    bool DetailEndFirst = true,   //a detail opens at its end, a tail read from its newest line, and false opens it at its top
    Func<char, int, bool>? Act = null)   //a taken control key acted on inside the show with the row's original index, true asks the rows again
{
    //the tab's own row under the list, or the panel's when the tab sets none
    internal PanelUnder? UnderAt(int tab) => Tabs is { Count: > 0 } tabs && tab < tabs.Count ? tabs[tab].Under ?? Under : Under;
}

//one tab of a panel, its name on the title row and its own list. each field it leaves null falls back to the panel's own, and a tab with no option row draws its rows as text
public sealed record PanelTab(string Name, IReadOnlyList<SelectOption> Options,
    IReadOnlyList<FooterKey>? Keys = null,
    Func<int, IReadOnlyList<FooterKey>>? KeysFor = null,
    PanelUnder? Under = null,
    Func<IReadOnlyList<SelectOption>>? OptionsNow = null,
    IReadOnlyList<DetailRow>? Rows = null);

//the one row under the list, a sentence or a setting, each a function of the option's original index
public abstract record PanelUnder
{
    private protected PanelUnder() { }

    //a fact of the cursor row, dim. a null sentence draws no row
    public sealed record Sentence(Func<int, string?> Text) : PanelUnder;

    //a value the arrows step through in place, null levels or a null value draw no row and promise no dead key
    public sealed record Setting(string Name, Func<int, IReadOnlyList<string>?> Levels, Func<int, string?> Value) : PanelUnder;
}

//what a press on a run of a panel row acts as
public abstract record PanelHit
{
    private protected PanelHit() { }

    public sealed record Option(int Index) : PanelHit;   //the option's original index, so a filter never moves what a press names
    public sealed record Key(string Word) : PanelHit;
    public sealed record Tab(int Index) : PanelHit;
    public sealed record Box : PanelHit;
}

//one press target on a composed row, columns from the row's left edge, end exclusive
public readonly record struct PanelHitSpan(int Row, int From, int To, PanelHit Hit);

//what the factory composed at, so the loop and the mouse act on the frame the user sees. the detail is the open row's key, null for the list
public sealed record PanelLayout(int Width, int Room, int BodyRows, int Offset, IReadOnlyList<PanelHitSpan> Hits,
    string? Detail = null, int BodyStart = 0, int BodyCount = 0,   //the rows a detail's text sits on, marks excluded
    int BodyTotal = 0)   //the physical rows a detail's window slides over, wrapped lines counted
{
    //the target under a cell, null on a cell no press acts on
    public PanelHit? At(int row, int col)
    {
        foreach (var h in Hits)
            if (h.Row == row && col >= h.From && col < h.To) return h.Hit;
        return null;
    }
}
