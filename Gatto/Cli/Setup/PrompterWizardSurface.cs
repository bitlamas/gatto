using Gatto.Core.Acquire;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//borrow the REPL's prompter inside a live session, a second key reader or a throwing cancel breaks the running conversation
internal sealed class PrompterWizardSurface(
    IWizardPrompter prompter, TextWriter output, Theme? theme = null,
    Action<TimeSpan>? pause = null, Gatto.Terminal.GlyphSet? glyphs = null) : IWizardSurface
{
    private readonly Gatto.Terminal.GlyphSet _glyphs = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;

    //this face reports the glyph set it was given rather than choosing one
    public Gatto.Terminal.GlyphSet Glyphs => _glyphs;

    private readonly Action<TimeSpan> _pause = pause ?? Thread.Sleep;

    //a null theme means no colour rather than no table, the plain caps keep the columns that hold the facts
    private readonly Theme _theme = theme ?? new Theme(TermCaps.Plain);

    //what every wizard screen's footer says, the write-at-the-end reassurance sits once in the fork screen's intro
    internal const string LeaveHint = "Esc to leave";

    //the cursor a control press left behind, cleared by any real answer so a fresh screen starts at the top
    private int _resumeCursor;

    //watch, tick and check are taken and not drawn, this face writes committed scrollback and cannot spare a second key reader
    public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null, Func<FetchTick?>? tick = null,
        Func<CheckTick?>? check = null)
    {
        //when only the watch can advance, waiting on the prompt would stall, so go to WaitOut
        if (c.OnlyTheWatchAdvances && watch is not null) return WaitOut(c, watch);

        while (true)
        {
            //compose the rows with the shared helper, so the labels match and engine text goes through one sanitize
            var options = WizardPaint.Options(c, _glyphs);
            var shelf = c.Shelf is { } view ? ShelfBinding.For(view, options, _theme, _glyphs) : default;

            var answer = prompter.AskOne(new WizardAsk(
                c.Question ?? "",
                options,
                //the typed door has to reach the face, it is the only way forward now that the row it replaced is gone
                FreeTextLabel: c.Door,
                FooterHint: LeaveHint,
                //the shelf's empty block reaches this face as body rows, it said nothing here before
                BodyRows: WizardPaint.Body(EmptyOrBody(c)),
                LabelsAt: shelf.LabelsAt,
                HeadingsAt: shelf.HeadingsAt,
                FooterLegend: shelf.Legend,
                Numbered: c.Shelf is null,
                //offer the same control keys the bare-console face does, the strip above them advertises keys that would otherwise do nothing
                ControlKeys: c.Shelf is { } sv ? ShelfControls.Keys(sv) : null,
                InitialCursor: _resumeCursor,
                NoInitialCursor: c.NoDefault));

            if (answer is null) return null;                       //backed out

            //a control press changes the screen and comes straight back, and only the cursor needs restoring here
            if (answer.Control is { } ctl)
            {
                _resumeCursor = answer.Cursor;
                return ShelfControls.AnswerFor(ctl);
            }

            _resumeCursor = 0;
            //the option's identity is what comes back, the back row's drawn label differs under each set
            var identity = answer.Label;
            if (c.AllowBack && identity == SetupFace.BackOptionKey) return SetupFlow.BackKey;
            //screen options set no key, so their label is their identity and only the back row is special
            if (c.Options.FirstOrDefault(o => o.Label == identity) is { } hit) return hit.Key;

            //text typed into the door is the answer, but only where the screen has one, otherwise ask again
            if (c.Door is not null && identity is { Length: > 0 }) return identity;

            //an answer that is not a label must not read as leaving, so the line below stays as a floor
            output.WriteLine("  pick one of the options above.");
        }
    }

    public string? Ask(WizardScreen.Ask a)
    {
        while (true)
        {
            //free text answers an Ask, and an offer adds one pickable row above it holding the default
            var answer = prompter.AskOne(new WizardAsk(
                a.Label ?? "",
                [.. a.Offer is { } o ? new[] { new SelectOption(o.Label) } : [],
                    .. a.AllowBack
                        ? new[] { new SelectOption(SetupFace.BackLabelOf(_glyphs),
                            Key: SetupFace.BackOptionKey) }
                        : []],
                FreeTextLabel: a.Placeholder ?? "type your answer",
                //set the input row to 0 with no offer and 1 with one, so back never sits above the box
                FreeTextIndex: a.Offer is null ? 0 : 1,
                FooterHint: LeaveHint,
                BodyRows: WizardPaint.Body(a.BodyRows)));

            if (answer is null) return null;
            //an Ask has no control keys, so anything that arrives as one is not an answer
            if (answer.Label is not { } answered) return null;
            //read back before the offer mapping and before validation, a validator would re-ask the screen forever
            if (a.AllowBack && answered == SetupFace.BackOptionKey) return SetupFlow.BackKey;
            //map the picked label back to the offer's value, the prompter only speaks in labels
            var typed = a.Offer is { } picked && answered == picked.Label ? picked.Value : answered;

            if (a.Validate(typed) is not { } complaint) return typed;
            output.WriteLine("  " + TermText.Sanitize(complaint));
        }
    }

    //leave these rows unpainted, a caller-painted line could meet a second sanitize on the commit sink

    //drop empty rows at this face, a blank line is a record here that renders as a bare gutter mark
    public void Show(WizardScreen.Info i) => WriteRows(i.Rows);

    //the one writer for transcript rows on this face, the waiting screen goes through it too
    private void WriteRows(IReadOnlyList<WizardRow>? rows)
    {
        foreach (var row in (rows ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Text)))
            output.WriteLine("  " + TermText.Sanitize(row.Text));
    }
    //only the watch moves this screen on, so it waits with no cancel (polling keys would add a second console reader)
    private string WaitOut(WizardScreen.Choice c, Func<bool> watch)
    {
        //write the screen's own rows through the same writer Show uses, a second composition would drift unseen
        if (c.Question is { Length: > 0 } question)
            output.WriteLine("  " + TermText.Sanitize(question));
        WriteRows(c.BodyRows);

        while (!watch()) _pause(Watch.Interval);   //ends on any resolution of the watch, a drop included, and only the fetch's idle deadline bounds it
        return SetupFlow.Landed;
    }


    //no bare gutter marks on the last screen, every separator here is tied to a row that is written
    public void End(WizardScreen.Terminal t)
    {
        //drop the empty rows here too, the transcript already separates its own entries
        foreach (var row in t.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Text)))
            output.WriteLine("  " + TermText.Sanitize(row.Text));
        if (t.NextStep is { } next) output.WriteLine("  " + TermText.Sanitize(next.Text));
    }

    //the shelf's empty block is the body here, the two are never drawn together. blank rows are dropped from the block, an empty row on this face is a record
    private static IReadOnlyList<WizardRow>? EmptyOrBody(WizardScreen.Choice c) =>
        c.Shelf?.Empty is { Count: > 0 } block
            ? [.. block.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => new WizardRow(l))]
            : c.BodyRows;
}
