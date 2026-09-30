using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the shelf as data for the face, passed on a Choice so the flow stays pure. the Rows list is the shown slice, with the cap already applied

//which source the shelf is showing, one discriminator rather than five flags that could disagree
internal enum ShelfSource { Hub, Local }

internal sealed record ShelfView(
    IReadOnlyList<ShelfRow> Rows, string? CuratedPublisher, MachineShape Shape,
    SearchOrder Axis = SearchOrder.MostDownloaded, bool Lift = false, int HiddenByFit = 0,
    bool CarriedOver = false, int HiddenByKind = 0,
    IReadOnlyList<string>? Families = null, string? Family = null,
    int Total = 0, int HiddenOlder = 0, int MoreBelow = 0, bool ShowAll = false,
    int HiddenNewer = 0,
    IReadOnlyList<string>? Empty = null,
    IReadOnlyList<Gatto.Cli.Setup.Tui.ModelFacts>? Facts = null,
    ShelfSource Source = ShelfSource.Hub,
    string? Folder = null,
    int HiddenByFamily = 0,
    string? Resume = null,
    bool Searched = false)
{
    //nothing was fetched at all, no rows and no family ladder. an empty search keeps its chips and keys, so the two states must stay apart
    public bool NothingFetched => Rows.Count == 0 && Families is not { Count: > 0 };

    //whether the chips row's right-hand slot is drawn, which decides both its Tab stop and its pick. the local shelf reuses the slot for its folder
    public bool HasPublisherSlot =>
        Source == ShelfSource.Hub && CuratedPublisher is { Length: > 0 };

    //the facts list must hold one entry per row, and it throws when it does not. assign Rows before Facts in a with, since an initializer runs in source order
    public IReadOnlyList<Gatto.Cli.Setup.Tui.ModelFacts>? Facts
    {
        get => _facts;
        init => _facts = value is null || value.Count == Rows.Count ? value
            : throw new ArgumentException(
                $"a shelf carries one ModelFacts per row: {Rows.Count} rows, {value.Count} facts",
                nameof(Facts));
    }

    private readonly IReadOnlyList<Gatto.Cli.Setup.Tui.ModelFacts>? _facts = Facts is null
        || Facts.Count == Rows.Count ? Facts
        : throw new ArgumentException(
            $"a shelf carries one ModelFacts per row: {Rows.Count} rows, {Facts.Count} facts",
            nameof(Facts));
}

//the one place that turns a ShelfView into the widget's labels and headings, called by both faces. a group starts at every non-continuation row

//the shelf's control keys, the one place the keyboard is named. the order here is the order the header renders, so the strip and the keys cannot drift
internal static class ShelfControls
{
    internal static readonly IReadOnlyList<(char Key, string Word, string Answer)> All =
    [
        //f opens the picker and stays, since the plain face has no slot beside the chips. f and the slot open the same screen
        ('f', "publisher", SetupFlow.CtlPublisher),
        ('s', "sort", SetupFlow.CtlSort),
        ('/', "search", SetupFlow.CtlSearch),
        ('a', "all sizes", SetupFlow.CtlLift),
    ];

    //the controls this shelf has, read by both the widget and the header strip so no key is advertised without a deed. every shelf offers all of them
    internal static IReadOnlyList<(char Key, string Word, string Answer)> For(ShelfView shelf) => All;

    internal static IReadOnlyList<char> Keys(ShelfView shelf) => [.. For(shelf).Select(c => c.Key)];

    //whether an answer is a control that stays on the shelf, so it pushes no back step. the typed row and the pick leave the screen and do push one
    internal static bool IsControl(string answer) =>
        //the publisher is the one control that leaves the screen, since its key opens the picker, so it must push a step
        (All.Any(c => c.Answer == answer) && answer != SetupFlow.CtlPublisher)
        || answer.StartsWith(SetupFlow.CtlFamily, StringComparison.Ordinal)
        || answer == SetupFlow.CtlSource;

    //the answer for one family chip, spelled once. the chips are a zone the keys enter rather than a key control
    internal static string FamilyAnswer(string family) => SetupFlow.CtlFamily + family;

    //m's answer, spelled here because the face knows the key and the flow knows the deed
    internal static string SourceAnswer() => SetupFlow.CtlSource;

    //the answer for Enter in the publisher slot, which opens the picker f opens. without it Enter fell through to the list cursor and picked a model
    internal static string PublisherAnswer() => SetupFlow.CtlPublisher;

    //a's answer, spelled here because the face knows the key and the flow knows the deed
    internal static string LiftAnswer() => SetupFlow.CtlLift;

    //whether this shelf can lift anything. an empty shelf offers nothing, but rows hidden by fit count even when none are shown
    internal static bool OffersLift(WizardScreen.Choice c) =>
        c.Shelf is { } v && (v.Rows.Count > 0 || v.HiddenByFit > 0 || v.ShowAll);

    //what the shelf's door answers with, the typed text wrapped the way a pick is. a typed 4096 and a row index are the same string
    internal static string TypedAnswer(string text) => SetupFlow.CtlTyped + text;

    //the text back out of a typed answer, or null when it is not one
    internal static string? Untyped(string answer) =>
        answer.StartsWith(SetupFlow.CtlTyped, StringComparison.Ordinal)
            ? answer[SetupFlow.CtlTyped.Length..]
            : null;

    //d searches the Hub and nothing else, so a local shelf or one that fetched nothing offers no d. the folder row on the local shelf stays a row
    internal static bool OffersD(WizardScreen.Choice c) =>
        c.Shelf is { NothingFetched: false, Source: ShelfSource.Hub };

    //whether the option at this index is the folder door, so the painter, the arrows and the digits agree on which index is skipped
    internal static bool IsFolderDoor(WizardScreen.Choice c, int i) =>
        OffersFolderDoor(c) && i >= 0 && i < c.Options.Count
        && string.Equals(c.Options[i].Key, SetupFlow.Elsewhere, StringComparison.Ordinal);

    //whether this screen offers a folder row, read off its own options so the footer and the key arm agree. the Hub shelf only
    internal static bool OffersFolderDoor(WizardScreen.Choice c) =>
        c.Shelf is { Source: ShelfSource.Hub }
        && c.Options.Any(o => string.Equals(o.Key, SetupFlow.Elsewhere, StringComparison.Ordinal));

    //the row's answer, wrapped with the file the user picked in the pane. the flow unwraps it, swaps the quant and handles the inner key
    internal static string PickAnswer(string key, string quant) =>
        SetupFlow.CtlPick + quant + ":" + key;

    //the file index and the row's key, or null when this is not a pick. one home for the spelling
    internal static (string Quant, string Key)? Unpick(string answer)
    {
        if (!answer.StartsWith(SetupFlow.CtlPick, StringComparison.Ordinal)) return null;
        var rest = answer[SetupFlow.CtlPick.Length..];
        var cut = rest.IndexOf(':');
        return cut > 0 ? (rest[..cut], rest[(cut + 1)..]) : null;
    }

    //the strip the header shows, composed from the same list the widget gets so a dead key stops being advertised in the same edit
    internal static string Strip(ShelfView shelf, GlyphSet g) =>
        string.Join($" {g.Dot} ", For(shelf).Select(c => $"{c.Key} {c.Word}"));

    internal static string? AnswerFor(char key) =>
        All.FirstOrDefault(c => c.Key == key) is { Answer: { } a } ? a : null;
}

internal static class ShelfBinding
{
    //the two seams, bound. options is the full list the screen shows, models first and escape rows after, one label per option
    internal static (Func<int, IReadOnlyList<string>> LabelsAt,
                     Func<int, IReadOnlyList<SelectHeading>> HeadingsAt,
                     string? Legend)
        For(ShelfView shelf, IReadOnlyList<SelectOption> options, Theme theme, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        //the column layout comes from the widget rather than being re-derived here. the shelf has no numbers, so this and SelectSpec.Numbered must move together
        var chrome = SelectPrompt.LabelColumn(options.Any(o => o.Marked), multiSelect: false, numbered: false);

        return (
            w => Labels(shelf, options, theme, Content(w, chrome), g),
            w => Headings(shelf, theme, Content(w, chrome), g),
            ShelfTable.Legend(shelf.Rows, g));
    }

    //width 0 means unknown all the way down, so it is passed through rather than turned into a negative budget
    private static int Content(int w, int chrome) => w <= 0 ? 0 : Math.Max(1, w - chrome);

    private static IReadOnlyList<string> Labels(
        ShelfView shelf, IReadOnlyList<SelectOption> options, Theme theme, int content, GlyphSet glyphs)
    {
        var groups = Grouped(shelf, theme, content, glyphs, out var lines);
        var labels = new List<string>(options.Count);

        //the resume row is option 0 and not a table row, so the alignment below starts one late. it is read off the field rather than passed as a count
        if (shelf.Resume is { Length: > 0 } resume)
        {
            var segs = SoftWrap.Wrap(resume, content, content);
            labels.Add(segs.Count == 0 ? resume : string.Join("\n", segs.Select(s => s.Text)));
        }

        //the lead is counted, not assumed to be 1, because a header row is emitted only when some column has a name
        var g = groups.Count - lines.Count(l => l.Row is not null);
        //labels may already hold the resume row, which is what makes the escape-row loop below start in the right place

        foreach (var line in lines)
        {
            //only a model becomes a label, its wrapped lines rejoined since the widget renders a newline as one selectable row
            if (line.Row is null) continue;
            labels.Add(string.Join("\n", groups[g]));
            g++;
        }

        //escape rows keep the flow's words but are wrapped with SoftWrap, since a raw row is cut by the widget's Fit
        for (var i = labels.Count; i < options.Count; i++)
        {
            var segs = SoftWrap.Wrap(options[i].Label, content, content);
            labels.Add(segs.Count == 0 ? options[i].Label : string.Join("\n", segs.Select(s => s.Text)));
        }
        return labels;
    }

    //how this shelf is ordered, in words. only most-downloaded needs a qualifier, since the other axes show their evidence on the row
    internal static string AxisWords(SearchOrder axis) => axis switch
    {
        //the four clauses share one grammar, and the qualifier is gone since the header's scope half carries that duty
        SearchOrder.RecentlyUpdated => "sorted by newest",
        SearchOrder.MostParams => "sorted by largest",
        SearchOrder.VerifiedFirst => "verified first",
        _ => "sorted by most downloaded",
    };

    //what this shelf is and how it is ordered, the header's left half
    internal static string StateSentence(ShelfView shelf, GlyphSet g) =>
        shelf.CuratedPublisher is { Length: > 0 } pub
            ? $"by {pub} {g.Dot} {AxisWords(shelf.Axis)}"
            : $"every approved publisher {g.Dot} {AxisWords(shelf.Axis)}";

    //it says what the filter hid, since does-not-fit is gatto's estimate that a overrules. it never says of N, because the count stops at the display cap
    internal static string CountLine(ShelfView shelf, GlyphSet g)
    {
        var parts = new List<string> { $"Showing {shelf.Rows.Count}" };

        //one tier means no separator, so the tier label appears here as the heading renders it rather than as a sentence
        var tiers = shelf.Rows.Select(r => r.Fit).Distinct().ToList();
        if (tiers.Count == 1) parts.Add(SearchRow.FitWords(tiers[0], shelf.Shape, g));

        //the lift's state, shown so a reader need not remember the keypress that set it
        if (shelf.Lift) parts.Add("including ones gatto thinks are too big");
        else if (shelf.HiddenByFit > 0)
            parts.Add($"{shelf.HiddenByFit} more hidden, press a to include them");

        //a shelf narrowed by kind says skipped and offers no key, since nothing brings those models back
        if (shelf.HiddenByKind > 0)
            parts.Add(shelf.HiddenByKind == 1
                ? "1 skipped, not a model you can talk to"
                : $"{shelf.HiddenByKind} skipped, not models you can talk to");

        return string.Join($" {g.Dot} ", parts);
    }

    private static IReadOnlyList<SelectHeading> Headings(ShelfView shelf, Theme theme, int content,
        GlyphSet glyphs)
    {
        var groups = Grouped(shelf, theme, content, glyphs, out var lines);
        var headings = new List<SelectHeading>();
        var g = 0;
        //headings anchor at an option index, so the models start at 1 while the resume row holds index 0
        var option = shelf.Resume is { Length: > 0 } ? 1 : 0;

        //the tier line is composed and painted here, since it left the table so it stops setting the model column's floor
        string TierLine(FitRegime fit) =>
            theme.Paint(ShelfTable.TierRuleOf(glyphs), Theme.Rule) + " "
            + theme.Paint(ShelfTable.TierLabel(fit, shelf.Shape, glyphs), Theme.Dim);

        //the header strip: the shelf's state on the left, its keys on the right. it wraps rather than dropping a key, since an unadvertised key does not exist
        {
            var state = StateSentence(shelf, glyphs);
            var strip = ShelfControls.Strip(shelf, glyphs);
            var gap = content - UnicodeWidth.Of(state) - UnicodeWidth.Of(strip);

            if (gap >= 2)
                headings.Add(new SelectHeading(0,
                    theme.Paint(state, Theme.Dim) + new string(' ', gap) + theme.Paint(strip, Theme.Dim)));
            else
            {
                foreach (var s in Wrapped(state, content)) headings.Add(new SelectHeading(0, theme.Paint(s, Theme.Dim)));
                foreach (var s in Wrapped(strip, content)) headings.Add(new SelectHeading(0, theme.Paint(s, Theme.Dim)));
            }
            headings.Add(new SelectHeading(0, ""));
        }

        //the header row is counted rather than assumed, and every line of it is emitted, since it wraps like any other row
        for (var lead = groups.Count - lines.Count(l => l.Row is not null); g < lead; g++)
            foreach (var text in groups[g]) headings.Add(new SelectHeading(0, text));

        foreach (var line in lines)
        {
            if (line.Row is null)
            {
                //air goes above a heading only, which binds it to what follows, and never before the first one
                if (headings.Count > 0) headings.Add(new SelectHeading(option, ""));
                headings.Add(new SelectHeading(option, TierLine(line.Tier)));
            }
            else
            {
                //only a model consumes a rendered group, since the tier lines are not in the table
                option++;
                g++;
            }
        }

        //the count line goes before the first escape row, between the table and the ways off it. an empty shelf renders its own screen instead
        if (shelf.Rows.Count > 0)
        {
            headings.Add(new SelectHeading(option, ""));
            foreach (var s in Wrapped(CountLine(shelf, glyphs), content))
                headings.Add(new SelectHeading(option, theme.Paint(s, Theme.Dim)));

            //an unmeasured shelf says so, since the register ships seeded and the check mark legend would otherwise explain a mark nothing has
            if (!shelf.Rows.Any(r => r.Badge is not null))
                foreach (var s in Wrapped(UnmeasuredShelf, content))
                    headings.Add(new SelectHeading(option, theme.Paint(s, Theme.Dim)));

            //beside the count line, where a reader already looks, and only when the fallback fired
            if (shelf.CarriedOver)
                foreach (var s in Wrapped(CarriedOverNote, content))
                    headings.Add(new SelectHeading(option, theme.Paint(s, Theme.Dim)));

            headings.Add(new SelectHeading(option, ""));
        }

        return headings;
    }

    //the sentence says the models have not been measured, since unverified would read as a verdict on them
    internal const string UnmeasuredShelf = "None of these has been tool-calling verified yet.";

    //the note says the search found none that fit this machine, since a sort that found nothing keeps the rows already on screen
    internal const string CarriedOverNote =
        "The search for other models found none that fit this machine, so these are the models "
        + "already shown.";

    //wrap rather than truncate, since the widget's Fit drops a tail that is one cell too wide
    private static IReadOnlyList<string> Wrapped(string text, int content)
    {
        if (content <= 0) return [text];
        var segs = SoftWrap.Wrap(text, content, content);
        return segs.Count == 0 ? [text] : [.. segs.Select(s => s.Text)];
    }

    //the rendered table cut back into its logical rows, one group per ShelfLine. a new group starts at every non-continuation row
    private static List<List<string>> Grouped(
        ShelfView shelf, Theme theme, int content, GlyphSet glyphs,
        out IReadOnlyList<ShelfTable.ShelfLine> lines)
    {
        lines = ShelfTable.Lines(shelf.Rows);
        var groups = new List<List<string>>();

        foreach (var row in ShelfTable.Render(shelf.Rows, theme, content, shelf.CuratedPublisher, shelf.Shape,
                     glyphs))
        {
            if (!row.Continuation || groups.Count == 0) groups.Add([]);
            groups[^1].Add(row.Text);
        }

        return groups;
    }
}
