using Gatto.Core.Acquire;
using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the only thing in the wizard that touches a console (so /setup binds the REPL's widget seam). a test passes its own clock, since the watch resolves with no key
internal sealed class SetupFace(
    ITermSurface surface, Theme theme, IKeySource keys, TextWriter output,
    Func<IPollClock>? clock = null, Gatto.Terminal.GlyphSet? glyphs = null)
    : IWizardSurface
{
    private readonly Gatto.Terminal.GlyphSet _glyphs = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;

    //report the set the face was built with, SetupRunner hands this to the flow (the interface's default would say Unicode on an ASCII face)
    public Gatto.Terminal.GlyphSet Glyphs => _glyphs;

    //one transcript per face, since the collapse and the divider describe the whole run. a method that writes something other than a committed pair has to say so
    private readonly WizardTranscript _transcript = new(theme, glyphs);

    //the chosen option's key, or null when the user backed out. the check is taken without being drawn, AuditionTicker already writes its row into the transcript
    public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null, Func<FetchTick?>? tick = null,
        Func<CheckTick?>? check = null)
    {
        //the shelf renders as a table, so the keys, count and order don't move with the width (only the display follows it)
        var options = WizardPaint.Options(c, _glyphs);
        var shelf = c.Shelf is { } view ? ShelfBinding.For(view, options, theme, _glyphs) : default;

        var spec = new SelectSpec(
            TitleRows: [],
            //null when the body already asked, which leaves no empty row in its place
            Question: c.Question is { Length: > 0 } q ? new PromptQuestion(q) : null,
            //back is a row, and always the last one, so the numbers beside the real options never shift
            Options: options,
            //tone in, painted rows out, one mapping for both faces
            BodyRows: WizardPaint.Body(c.BodyRows),
            //just "Esc to leave" here, the write-at-the-end reassurance sits once in the fork's intro
            FooterHint: PrompterWizardSurface.LeaveHint,
            //keys left, legend right, and no legend off the shelf or on a shelf whose rows show no marks
            FooterLegend: shelf.Legend,
            //erase the block on the way out, the transcript writes question, answer and a rule to the next segment in its place
            EchoOnCompletion: true,
            EchoCompose: _transcript.Commit,
            //discard typing that arrived while the previous step was busy, it can't answer a screen that didn't exist yet
            DrainTypeAhead: true,
            //both are null off the shelf, so no other screen's labels or headings change
            LabelsAt: shelf.LabelsAt,
            HeadingsAt: shelf.HeadingsAt,
            //the shelf's models get no digits (arrows and Enter pick a row), every other screen keeps its numbering, moving with ShelfBinding's numbered flag
            Numbered: c.Shelf is null,
            //the shelf's four control keys, null off the shelf so no other screen can reach SelectOutcome.Control
            ControlKeys: c.Shelf is { } sv ? ShelfControls.Keys(sv) : null,
            //the cursor returns to the row the user was on, since a control press re-opens this prompt
            InitialCursor: _resumeCursor,
            NoInitialCursor: c.NoDefault,
            //the watch is chosen here, null on every other screen. a download is gatto working, so the row uses Cats.Face's default arm under no role
            Poll: c.Watching && watch is { } arrived
                ? new PollSpec(
                    Arrived: arrived,
                    //the empty label is the default arm on purpose (the wizard runs under no role). the fetch row is the widget's whole line, one composer for both faces
                    Row: ms => tick?.Invoke() is { } t
                        ? Widget.Line(t, _glyphs)
                        : ChromeTicker.PurrHead(Cats.Face(_glyphs), ms, PurrFrames.Short),
                    Clock: clock?.Invoke() ?? new ConsolePollClock(keys),
                    Interval: Watch.Interval,
                    //a file turning up is a discovery, so the permanent record must name it
                    ArrivedLabel: c.ArrivedLabel)
                : null);

        return new SelectPrompt(surface, theme, keys, pump: null, chrome: null, glyphs: _glyphs).Show(spec) switch
        {
            //the back row is appended last, so its index is the count of the real options
            SelectOutcome.Chosen ch when c.AllowBack && ch.Index == c.Options.Count => Answered(SetupFlow.BackKey),
            SelectOutcome.Chosen ch => Answered(c.Options[ch.Index].Key),
            SelectOutcome.Control ctl => Controlled(ctl),
            //the watch resolving on its own is an answer, so the transcript commits one entry for the whole wait
            SelectOutcome.Arrived => Answered(SetupFlow.Landed),
            _ => Answered(null),   //cancelled, or a shape this screen never asked for
        };
    }



    //one poll clock and one interval for both faces, since two would answer differently how long gatto waits

    //where a control press left the cursor. an answer clears it, so the next screen starts at the top
    private int _resumeCursor;

    //an answer leaves the screen, so the resume cursor returns to the top
    private string? Answered(string? key)
    {
        _resumeCursor = 0;
        return key;
    }

    //the widget erased its block and wrote nothing, so the transcript must forget its last height. keep Skip(), the next control screen may have a question
    private string? Controlled(SelectOutcome.Control ctl)
    {
        _transcript.Skip();
        _resumeCursor = ctl.Cursor;
        return ShelfControls.AnswerFor(ctl.Key);
    }

    //the key the flow compares against, and it never changes (the label reads the table and differs between glyph sets)
    internal const string BackOptionKey = "back";

    internal static string BackLabelOf(Gatto.Terminal.GlyphSet g) => $"{g.Left} back";

    //print rows the user reads and keeps
    public void Show(WizardScreen.Info i)
    {
        //these rows sit between two committed pairs, so the transcript must not reclaim its previous tail
        _transcript.Interrupt();
        foreach (var row in i.Rows) Write(row);
        output.WriteLine();
    }

    //wrap with the row's own indent from WizardRow.Hang, then paint each produced line separately (a highlight crossing a wrap just does not fire)
    private void Write(WizardRow row)
    {
        //the wrapping lives in WizardRows, so both faces answer where a wrapped line starts the same way
        foreach (var (lead, text) in WizardRows.Wrap(row, surface.Width, glyphs: _glyphs))
            output.WriteLine(lead + WizardPaint.Line(row with { Text = text }, theme));
    }

    //free text checked by the screen's own rule, re-asked until it passes or the user leaves, so prompt and check can't drift
    public string? Ask(WizardScreen.Ask a)
    {
        while (true)
        {
            var spec = new SelectSpec(
                TitleRows: [],
                //null label means the body already asked, so there is no question row at all
                Question: a.Label is { Length: > 0 } q ? new PromptQuestion(q) : null,
                //the default is a real row, since a keystroke contract can't be rendered. the offer comes first and back last, the same order as a Choice
                Options: [.. a.Offer is { } o ? new[] { new SelectOption(o.Label) } : [],
                    .. a.AllowBack
                        ? new[] { new SelectOption(BackLabelOf(_glyphs), Key: BackOptionKey) }
                        : []],
                BodyRows: WizardPaint.Body(a.BodyRows),
                FreeTextLabel: a.Placeholder ?? "type your answer",
                //the free-text row sits after the offer and before back, so back is last. do not add an InitialCursor override, a rendered number must still select its row
                FreeTextIndex: a.Offer is null ? 0 : 1,
                FooterHint: PrompterWizardSurface.LeaveHint,
                EchoOnCompletion: true,
                EchoCompose: _transcript.Commit,
                DrainTypeAhead: true);

            var answer = new SelectPrompt(surface, theme, keys, pump: null, chrome: null, glyphs: _glyphs).Show(spec) switch
            {
                //the offer goes back as its value, so downstream can't tell a picked offer from a typed one. back is the first option, index 1 with an offer and 0 without one
                SelectOutcome.Chosen ch when a.AllowBack && ch.Index == (a.Offer is null ? 0 : 1)
                    => SetupFlow.BackKey,
                SelectOutcome.Chosen when a.Offer is { } picked => picked.Value,
                SelectOutcome.FreeText t => t.Text,
                _ => null,   //cancelled, or a shape this screen never asked for
            };
            if (answer is null) return null;
            //back returns before validation, since a validator would reject it and re-ask forever
            if (answer == SetupFlow.BackKey) return answer;

            if (a.Validate(answer) is not { } complaint) return answer;
            //a complaint sits between two commits of the same question, so the transcript must not reclaim over it
            _transcript.Interrupt();
            output.WriteLine("  " + TermText.Sanitize(complaint));
        }
    }

    //the last screen always prints the next step, and a blank line after it so the shell prompt doesn't touch it
    public void End(WizardScreen.Terminal t)
    {
        _transcript.Interrupt();   //nothing follows the last screen, so nothing may reclaim into it
        //the separator belongs to the rows, so an empty row list prints no blank line
        foreach (var row in t.Rows) Write(row);
        //no blank after rows that already end blank, a screen with one empty row wants exactly one line of air
        if (t.Rows.Count > 0 && t.Rows[^1].Text.Length > 0) output.WriteLine();
        //a null NextStep means no closing sentence. the trailing blank stays on this face, it is air before the shell prompt comes back
        if (t.NextStep is { } next) Write(next);
        output.WriteLine();
    }
}
