using System.Text;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Repl;

//ask_user on the rich path as a wizard over SelectPrompt, a thin binding that decides words and steps and owns no key loop or rendering
public sealed class RichPrompter(   //nothing is committed to scrollback, and the answers reach the transcript through the tool result's gloss
    ITermSurface surface, Theme theme, IKeySource keys, InputPump? pump = null,   //the keys are read only when there is no pump, as in tests and any host outside the REPL
    ChromeHandle? chrome = null, TurnAbortHandle? abort = null,   //the abort is set on the rich REPL path only, and without it Esc cancels the call alone
    GlyphSet? glyphs = null) : IUserPrompter, IWizardPrompter
{
    //this run's glyph set comes from the session, a painter that read the environment itself could end up on a different set than its neighbour
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //one question, no wizard chrome, leaving returns null rather than throwing. never request an abort here, no turn is running to abort
    public WizardAnswer? AskOne(WizardAsk ask)
    {
        using var focus = pump?.PushFocus();
        var k = focus?.Keys ?? keys;

        var spec = new SelectSpec(
            TitleRows: Array.Empty<TitleRow>(),
            Question: new PromptQuestion(ask.Question),
            Options: [.. ask.Options],
            DetailRows: null,
            //options and free text can't both be asked here, no prompter-added row on a closed choice and no tabs. leaving Horizontal false is what makes Moved unreachable
            FreeTextLabel: ask.FreeTextLabel,
            FreeTextIndex: ask.FreeTextIndex,
            MultiSelect: false,
            FooterHint: ask.FooterHint,
            BodyRows: ask.BodyRows,
            //the shelf renders as a table here too, /setup inside a live session draws the same screen. null or false on every other ask
            LabelsAt: ask.LabelsAt,
            HeadingsAt: ask.HeadingsAt,
            FooterLegend: ask.FooterLegend,
            Numbered: ask.Numbered,
            ControlKeys: ask.ControlKeys,
            InitialCursor: ask.InitialCursor,
            NoInitialCursor: ask.NoInitialCursor);
        //don't arm a drain here, this path runs under the pump's modal focus which already stops type-ahead from answering a prompt

        return new SelectPrompt(surface, theme, k, pump: null, chrome, _glyphs).Show(spec) switch
        {
            SelectOutcome.Chosen c => new WizardAnswer(ask.Options[c.Index].Identity),
            SelectOutcome.FreeText t => new WizardAnswer(t.Text),
            SelectOutcome.Cancelled => null,
            //a control press changes the screen rather than answering it, so pass it back or the shared header strip draws keys that do nothing
            SelectOutcome.Control ctl => new WizardAnswer(Control: ctl.Key, Cursor: ctl.Cursor),
            //this spec never sets MultiSelect or Horizontal, so this arm must throw, a wizard bug must not become a session bug
            var other => throw new InvalidOperationException(
                $"a wizard question got an unexpected outcome: {other.GetType().Name}"),
        };
    }

    //the review step's heading and its two action rows, an explicit Cancel beside Submit so the way out is on screen
    private const string ReviewTitle = "Review your answers";
    private const string SubmitLabel = "Submit answers";
    private const string CancelLabel = "Cancel";
    //the dim readiness line between the reviewed answers and the action rows
    private const string ReadyNote = "Ready to submit your answers?";

    //what an unanswered question and a multi-select confirmed with nothing ticked both read as, the model gets the same empty Selected for either
    private const string SkippedText = "No answer. Skipped";

    //properties rather than consts, a const can't read the glyph set. a key glyph for the arrows isn't in the table yet
    private string QuestionFooter =>
        $"{_glyphs.Left} {_glyphs.Right} to move between questions " + _glyphs.Dot + " Esc to cancel";
    //what a one-question wizard shows instead, with nowhere to move the arrow hint would advertise a key that does nothing. only that hint is dropped
    private const string SingleQuestionFooter = "Esc to cancel";
    private string ReviewFooter =>
        "Enter to submit " + _glyphs.Dot + $" {_glyphs.Left} to go back " + _glyphs.Dot + " Esc to cancel";


    public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
    {
        //an empty ask returns an empty answer set without opening anything. the host API can hand this seam any list, and a modal to dismiss would be pure obstruction
        if (questions.Count == 0)
            return Task.FromResult<IReadOnlyList<AskAnswer>>(Array.Empty<AskAnswer>());

        //modal focus for the whole call, the widget's scope closes when Show returns and the pump would eat the next step's keys
        using var focus = pump?.PushFocus();   //one focus scope for every step, since a scope per step would let the composer drain the keys between two questions
        var k = focus?.Keys ?? keys;

        var n = questions.Count;
        //null means the question was arrowed past, an empty list means it was resolved to nothing. only auto-advance reads the difference
        var picked = new IReadOnlyList<string>?[n];
        //cursor and checkbox state per question, fed back on every re-open (a fresh Show() per step would otherwise reset them)
        var cursors = new int[n];
        var toggles = new bool[n][];
        for (var i = 0; i < n; i++) toggles[i] = new bool[questions[i].Options.Count];
        //free-text drafts per question, so typed text survives a move away and back, and a submitted answer shows on its row when revisited
        var drafts = new string?[n];

        var tabs = new List<string>(n);
        foreach (var q in questions) tabs.Add(q.Header);

        //step index, 0..n-1 are the questions and n is the review
        var step = 0;
        while (true)
        {
            //between steps only, a key read in flight can't be interrupted, so this is cooperative cancellation (the widget's reader blocks and takes no token)
            ct.ThrowIfCancellationRequested();

            if (step == n)
            {
                if (Review()) break;
                continue;
            }
            Question();
        }

        var answers = new List<AskAnswer>(n);
        for (var i = 0; i < n; i++)
            answers.Add(new AskAnswer(questions[i].Header, picked[i] ?? Array.Empty<string>()));
        return Task.FromResult<IReadOnlyList<AskAnswer>>(answers);

        //one question step, it sets step to wherever the outcome sends the user
        void Question()
        {
            //here is this question's own index, the arms below move step and writing toggles[step] afterwards would stamp one question's boxes onto another's
            var here = step;
            var q = questions[here];
            var options = new List<SelectOption>(q.Options.Count);
            for (var i = 0; i < q.Options.Count; i++)
            {
                var opt = q.Options[i];
                //the description and Recommended pass straight through, the widget owns first-recommended-wins and the dim child row
                options.Add(new SelectOption(
                    opt.Label, opt.Description, opt.Recommended, Marked: false, Selected: toggles[here][i]));
            }

            var outcome = Show(new SelectSpec(
                TitleRows: Array.Empty<TitleRow>(),
                Question: q.Question,
                Options: options,
                DetailRows: null,
                //a multi-select question gets no free-text row, the widget refuses the combination before it renders and a Multi outcome has nowhere to put text
                FreeTextLabel: q.MultiSelect ? null : PrompterCore.FreeTextLabelOf(_glyphs),
                MultiSelect: q.MultiSelect,
                FooterHint: n > 1 ? QuestionFooter : SingleQuestionFooter,
                InitialCursor: cursors[here],
                Tabs: tabs,
                ActiveTab: here,
                Horizontal: true,
                InitialDraft: drafts[here],
                Cancel: ct));

            switch (outcome)
            {
                case SelectOutcome.Chosen chosen:
                    //a Chosen index is always a real option, the widget resolves the free-text row as FreeText or backs out
                    picked[here] = new[] { q.Options[chosen.Index].Label };
                    //park the cursor on the pick so coming back to this question opens on what was chosen rather than a stale row
                    cursors[here] = chosen.Index;
                    Advance(here);
                    break;

                case SelectOutcome.FreeText free:
                    //raw text, the widget sanitized only the echo. the draft keeps the submitted answer so a revisit shows it on the row
                    picked[here] = new[] { free.Text };
                    drafts[here] = free.Text;
                    Advance(here);
                    break;

                case SelectOutcome.Multi multi:
                    Restore(multi.Indices);
                    var selected = new List<string>(multi.Indices.Count);
                    foreach (var i in multi.Indices) selected.Add(q.Options[i].Label);
                    picked[here] = selected;
                    Advance(here);
                    break;

                case SelectOutcome.Moved moved:
                    //leaving without answering, save the cursor, the toggles and the draft. the step index is clamped, it never wraps
                    cursors[here] = moved.Cursor;
                    Restore(moved.Selected);
                    drafts[here] = moved.Draft.Length > 0 ? moved.Draft : null;
                    step = Math.Clamp(here + moved.Delta, 0, n);
                    break;

                case SelectOutcome.Cancelled:
                    Cancel();
                    break;

                default:
                    //this default must stay, an unmatched outcome would leave step where it was and spin the loop forever re-rendering the same question
                    throw new InvalidOperationException(
                        $"ask_user question step got an unexpected outcome: {outcome.GetType().Name}");
            }

            void Restore(IReadOnlyList<int> indices)
            {
                Array.Clear(toggles[here]);
                foreach (var i in indices) toggles[here][i] = true;
            }
        }

        //the review step, true when the user submitted
        bool Review()
        {
            //the answers go in BodyRows, painted per kind, TitleRows would make them all Bright and bold. rows hold the full question text, the tab strip has the headers
            var body = new List<BodyRow>();
            for (var i = 0; i < n; i++)
            {
                body.Add(new BodyRow(questions[i].Question, BodyRowKind.Question));
                body.Add(picked[i] is { Count: > 0 } a
                    ? new BodyRow(string.Join(", ", a), BodyRowKind.Answer)
                    : new BodyRow(SkippedText, BodyRowKind.Skipped));
            }
            body.Add(new BodyRow(""));
            body.Add(new BodyRow(ReadyNote));

            var outcome = Show(new SelectSpec(
                TitleRows: [ReviewTitle],
                Question: null,
                Options: [new SelectOption(SubmitLabel), new SelectOption(CancelLabel)],
                DetailRows: null,
                FreeTextLabel: null,
                MultiSelect: false,
                FooterHint: ReviewFooter,
                InitialCursor: 0,
                Tabs: tabs,
                //no tab is live on the review, which sits past every question
                ActiveTab: -1,
                Horizontal: true,
                BodyRows: body,
                Cancel: ct));

            switch (outcome)
            {
                case SelectOutcome.Chosen chosen when chosen.Index == 0:
                    return true;

                //the explicit Cancel row performs the same withdrawal as Esc, offered so the way out is also visible
                case SelectOutcome.Chosen:
                    Cancel();
                    return false;   //unreachable, Cancel throws

                case SelectOutcome.Moved moved:
                    //a left press goes back into the questions, a right press hits the clamp since nothing sits past the review
                    step = Math.Clamp(n + moved.Delta, 0, n);
                    return false;

                case SelectOutcome.Cancelled:
                    Cancel();
                    return false;   //unreachable, Cancel throws

                case SelectOutcome.FreeText:
                case SelectOutcome.Multi:
                default:
                    //the spec asks for neither a free-text row nor checkboxes, so this arm means the widget's contract changed. throwing beats submitting answers nobody confirmed
                    throw new InvalidOperationException(
                        $"ask_user review step got an unexpected outcome: {outcome.GetType().Name}");
            }
        }

        SelectOutcome Show(SelectSpec spec) =>
            new SelectPrompt(surface, theme, k, pump: null, chrome, _glyphs).Show(spec);

        //forward only, after an answer go to the next question that has none yet and then to the review. a skipped question must never bounce the user back
        void Advance(int from)
        {
            for (var i = from + 1; i < n; i++)
                if (picked[i] is null) { step = i; return; }
            step = n;
        }

        //from any step Esc discards the partial answers, the throw leaves the seam like any tool error with only the pinned message
        void Cancel()
        {
            //request the abort before the throw, the loop only sees the cancelled token at its next round-top, after this throw became the IsError result
            abort?.RequestAbort();
            //reuse PermissionGate.CancelMessage, the one home for what the model is told when the user withdraws a call
            throw new InvalidOperationException(PermissionGate.CancelMessage);   //an ordinary exception the loop turns into an error result, since a cancellation would take the cancelled-token arm and lose the message
        }
    }

    //a line reader over pump keys, null when the pump is dead, the caller maps that to the cancellation contract ConsolePrompter uses for closed stdin
    internal static string? ReadLine(IKeySource keys, ITermSurface surface, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ConsoleKeyInfo k;
            try { k = keys.ReadKey(ct); }
            catch (InvalidOperationException) { return null; }
            if (k.Key == ConsoleKey.Enter) { surface.Write("\n"); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; surface.Write("\b \b"); }
                continue;
            }
            //the tab key passes with the printables, a pasted two-column answer must not collapse into one word and other C0 keys stay dropped
            if (k.KeyChar >= ' ' || k.KeyChar == '\t') { sb.Append(k.KeyChar); surface.Write(k.KeyChar.ToString()); }
        }
    }
}
