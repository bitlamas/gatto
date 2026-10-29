using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//one picker row, where the live dot, the cursor start and the default model are three marks set independently
public sealed record PickerItem(string Id, string Label, bool Marked, bool Current,
    bool Default = false,
    IReadOnlyList<string>? Columns = null,   //a panel's cells after the name, the plain picker keeps drawing Label
    IReadOnlyList<string>? Levels = null,   //what the settings row steps through, null draws no row
    string? DefaultEffort = null,   //the level a switch to this row would land, one of Levels
    string? Sentence = null);   //the fact the sentence row states for this row

//a tool as the model sees it, plus the extension that armed it, null for a built-in
public sealed record ToolInfo(string Name, string Description, string? Origin);

//what a picker resolved, an answer that names a control key as well as a row, so the caller decides what to do
public abstract record PickOutcome
{
    //a closed hierarchy of exactly these three, so a caller can switch without an unreachable arm
    private protected PickOutcome() { }

    public sealed record Picked(int Index) : PickOutcome;

    //a declared control key pressed on a row, reachable only when the caller asked for that key
    public sealed record Control(char Key, int Cursor) : PickOutcome;

    public sealed record Cancelled : PickOutcome;

    //the chosen row, null on any other answer, named apart from Picked.Index since a record can't shadow it
    public int? PickedRow => this is Picked p ? p.Index : null;
}

//a panel pick: the rows, the panel's words and the keys that answer instead of choosing
public sealed record PanelSpec(IReadOnlyList<PickerItem> Items, PanelFields Fields,
    IReadOnlyList<char>? ControlKeys = null, int? OpenAt = null);

//what a panel resolved, with the settings row's value at the act and whether the user moved it there
public sealed record PanelOutcome(PickOutcome Pick, string? Setting = null, bool Moved = false);

//pick one of a list, answering with the chosen row, a declared control key or a cancel
public interface IListPicker
{
    //the pick drawn by the panel contract, the row's name as its label and its cells as columns
    PanelOutcome ShowPanel(PanelSpec spec);
}

//a thin binding onto SelectPrompt: it picks the words and options and owns no keypress loop, rendering or repaint
public sealed class RichListPicker(
    ITermSurface surface, Theme theme, IKeySource keys, InputPump? pump = null,
    ChromeHandle? chrome = null, Gatto.Core.WarningSink? warn = null,
    GlyphSet? glyphs = null) : IListPicker
{
    //this run's glyph set, handed down from the session so two painters can't disagree about the host
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //test seam for the widget call, so a test can see the spec the panel is shown
    internal Func<SelectSpec, SelectPrompt.PanelResult>? ShowPanelForTest;

    public PanelOutcome ShowPanel(PanelSpec panel)
    {
        var items = panel.Items;
        var options = new List<SelectOption>(items.Count);
        var cursor = -1;
        for (var i = 0; i < items.Count; i++)
        {
            //the loaded dot rides on the name in the role's tint, the default word follows the last cell dim
            var tail = items[i].Marked ? (IReadOnlyList<SelectRun>)[new(" "), new(_glyphs.Loaded, Theme.Accent)] : null;
            var trail = items[i].Default ? (IReadOnlyList<SelectRun>)[new("default", Theme.Dim)] : null;
            options.Add(new SelectOption(items[i].Id, Tail: tail, Columns: items[i].Columns, Trail: trail));
            if (cursor < 0 && items[i].Current) cursor = i;
        }
        var spec = new SelectSpec([], null, options, FooterHint: null,
            InitialCursor: panel.OpenAt ?? Math.Max(0, cursor), ControlKeys: panel.ControlKeys, Panel: panel.Fields);
        var result = ShowPanelForTest is { } showForTest
            ? showForTest(spec)
            : new SelectPrompt(surface, theme, keys, pump, chrome, _glyphs).ShowPanel(spec);
        PickOutcome pick = result.Outcome switch
        {
            SelectOutcome.Chosen chosen => new PickOutcome.Picked(chosen.Index),
            SelectOutcome.Control control => new PickOutcome.Control(control.Key, control.Cursor),
            _ => new PickOutcome.Cancelled(),
        };
        return new PanelOutcome(pick, result.Setting, result.Moved);
    }
}

//print the list and return a cancel, since a plain terminal cannot navigate it
public sealed class PlainListPicker(TextWriter output, GlyphSet? glyphs) : IListPicker
{
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //the panel's title and legend over the rows, and always a cancel
    public PanelOutcome ShowPanel(PanelSpec spec)
    {
        Print(spec.Fields.Title, spec.Items, spec.Fields.Legend);
        return new PanelOutcome(new PickOutcome.Cancelled());
    }

    private void Print(string title, IReadOnlyList<PickerItem> items, string? markLegend)
    {
        output.WriteLine(markLegend is null ? title : title + "   " + _glyphs.Loaded + " " + markLegend);
        foreach (var item in items)
            //the marks follow the name here too, so both paths read the same way round
            output.WriteLine((item.Current ? _glyphs.Prompt + "   " : "    ") + item.Label
                + (item.Marked ? "  " + _glyphs.Loaded : "") + (item.Default ? "  default" : ""));
    }
}
