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
    bool SearchWhenLong = true);

//one tab of a panel, its name on the title row and its own list
public sealed record PanelTab(string Name, IReadOnlyList<SelectOption> Options);

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

//what the factory composed at, so the loop and the mouse act on the frame the user sees
public sealed record PanelLayout(int Width, int Room, int BodyRows, int Offset, IReadOnlyList<PanelHitSpan> Hits)
{
    //the target under a cell, null on a cell no press acts on
    public PanelHit? At(int row, int col)
    {
        foreach (var h in Hits)
            if (h.Row == row && col >= h.From && col < h.To) return h.Hit;
        return null;
    }
}
