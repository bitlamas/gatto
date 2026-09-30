using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Repl;

//one picker row, where the live dot, the cursor start and the default model are three marks set independently
public sealed record PickerItem(string Id, string Label, bool Marked, bool Current,
    bool Default = false);

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

//pick one of a list, answering with the chosen row, a declared control key or a cancel
public interface IListPicker
{
    //markLegend explains the dot, controlKeys answer with a key instead of choosing, and openAt sets the row the keys start on
    PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
        IReadOnlyList<char>? controlKeys = null, int? openAt = null);
}

//a thin binding onto SelectPrompt: it picks the words and options and owns no keypress loop, rendering or repaint
public sealed class RichListPicker(
    ITermSurface surface, Theme theme, IKeySource keys, InputPump? pump = null,
    ChromeHandle? chrome = null, Gatto.Core.WarningSink? warn = null,
    GlyphSet? glyphs = null) : IListPicker
{
    //this run's glyph set, handed down from the session so two painters can't disagree about the host
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //test seam for the widget call, since the fail-safe arm can't be reached through the real widget
    internal Func<SelectSpec, SelectOutcome>? ShowForTest;

    //the marks after a row's name, null when the row has neither so an all-plain list composes the same as before
    private static IReadOnlyList<SelectRun>? TailFor(PickerItem item, GlyphSet g)
    {
        if (!item.Marked && !item.Default) return null;
        var runs = new List<SelectRun>(3);
        //the spacing run has no ink, painting blanks would add escapes for nothing
        if (item.Marked) { runs.Add(new SelectRun("  ")); runs.Add(new SelectRun(g.Loaded, Theme.Accent)); }
        if (item.Default) runs.Add(new SelectRun("  default", Theme.Dim));
        return runs;
    }

    public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
        IReadOnlyList<char>? controlKeys = null, int? openAt = null)
    {
        //no empty-list guard here: SelectPrompt already refuses an option-less single-select as a cancel
        var options = new List<SelectOption>(items.Count);
        var cursor = -1;
        for (var i = 0; i < items.Count; i++)
        {
            options.Add(new SelectOption(items[i].Label, Marked: false, Tail: TailFor(items[i], _glyphs)));
            //the first Current item wins, and with none the cursor starts at the top
            if (cursor < 0 && items[i].Current) cursor = i;
        }

        //the spec sets no question, no detail rows and no free-text row, so Esc is the only way out
        var spec = new SelectSpec(
            TitleRows: [new TitleRow(title, MarkLegend: markLegend)],
            Question: null,
            Options: options,
            DetailRows: null,
            FreeTextLabel: null,
            MultiSelect: false,
            FooterHint: null,
            InitialCursor: openAt ?? (cursor < 0 ? 0 : cursor),
            ControlKeys: controlKeys);

        //the widget pushes its own focus scope, since a picker has a single read phase and needs no second one
        var outcome = ShowForTest is { } showForTest
            ? showForTest(spec)
            : new SelectPrompt(surface, theme, keys, pump, chrome, _glyphs).Show(spec);

        switch (outcome)
        {
            //the index matches items one for one, since every option is one item in order
            case SelectOutcome.Chosen chosen:
                return new PickOutcome.Picked(chosen.Index);

            //the key is reported and not acted on, with the cursor so the caller can re-open where the user was
            case SelectOutcome.Control control:
                return new PickOutcome.Control(control.Key, control.Cursor);

            //the pick was withdrawn with no turn semantics, a picker runs at rest between turns
            case SelectOutcome.Cancelled:
                return new PickOutcome.Cancelled();

            //the three unreachable cases are listed rather than left to default, since the hierarchy is closed and a caller must account for each
            case SelectOutcome.FreeText:
            case SelectOutcome.Multi:
            case SelectOutcome.Moved:
            default:
                //treat an unreadable outcome as a cancel and warn, since a guess at a row would be silent and a cancel is repeatable
                warn?.Warn($"list picker got an unrecognised {outcome.GetType().Name} response - cancelled");
                return new PickOutcome.Cancelled();
        }
    }
}

//print the list and return a cancel, since a plain terminal cannot navigate it
public sealed class PlainListPicker(TextWriter output, GlyphSet? glyphs) : IListPicker
{
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
        IReadOnlyList<char>? controlKeys = null, int? openAt = null)
    {
        output.WriteLine(markLegend is null ? title : title + "   " + _glyphs.Loaded + " " + markLegend);
        foreach (var item in items)
            //the marks follow the name here too, so both paths read the same way round
            output.WriteLine((item.Current ? _glyphs.Prompt + "   " : "    ") + item.Label
                + (item.Marked ? "  " + _glyphs.Loaded : "") + (item.Default ? "  default" : ""));
        //the answer is always a cancel, since a plain terminal cannot navigate the list it printed
        return new PickOutcome.Cancelled();
    }
}
