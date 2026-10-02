using System.Text;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Repl;

//one painted piece of an option's Tail, a null Ink is unpainted text so the spacing between marks gets no escape codes
public sealed record SelectRun(string Text, RgbColor? Ink = null);

//one option row, a Disabled row can never be chosen and shows where a feature is going. the LabelCode style shows only on an unselected row
public sealed record SelectOption(
    string Label, string? Description = null, bool Recommended = false,
    bool Marked = false, bool Selected = false,
    string? LabelCode = null, string LabelAfter = "", bool Disabled = false,
    //what the option is when the label says something else, null otherwise so the identity is the label. a label that changes with the glyph set can't be an identity
    string? Key = null,
    //marks that follow the label, appended outside the row paint so their inks survive the cursor row's reset. leave Marked false when you use this
    IReadOnlyList<SelectRun>? Tail = null,
    //what the parenthesised word says when Recommended is the wrong one. only that word changes, Recommended still decides whether the suffix is drawn
    string? MarkWord = null)
{
    //what a caller matching this option compares against, the key when there is one and the label otherwise. one definition, so both prompters agree
    public string Identity => Key ?? Label;

    public static implicit operator SelectOption(string label) => new(label);
}

//the question row, Code paints one span in the inline-code style like the title does. implicit from string so plain callers stay terse
public sealed record PromptQuestion(string Text, string? Code = null, string After = "")
{
    public static implicit operator PromptQuestion(string text) => new(text);
}

//one title row, Text paints bright and bold, Code in the inline-code style and MarkLegend an accent dot with dim text. the widget owns every paint
public sealed record TitleRow(string Text, string? Code = null, string? MarkLegend = null)
{
    public static implicit operator TitleRow(string text) => new(text);
}

//a row of an edit's change in the prompt, drawn with a sign after the gutter and, when it changed, on its ground
public enum DetailKind { None, Same, Added, Removed }

//one detail row, Gutter renders dim ahead of the text, Dim dims the whole row and Warn paints it in the warn ink. the row stands for its Lines of source, more than one on a count row
public sealed record DetailRow(string Text, string? Gutter = null, bool Dim = false, IReadOnlyList<SpanRole>? Roles = null,
    CodeLanguage Language = CodeLanguage.None, DetailKind Kind = DetailKind.None, bool Warn = false, int Lines = 1)
{
    public static implicit operator DetailRow(string text) => new(text);
}

//how one BodyRow renders, the widget owns the glyphs and the paints so a caller names only the kind
public enum BodyRowKind
{
    //dim plain text with no glyph, used for the review's readiness line
    Note,
    //a dim bullet with plain text, for a reviewed question
    Question,
    //an indented accent arrow and accent text, the answer under its question
    Answer,
    //the same indented arrow, all dim, for a question that was skipped
    Skipped,
    //accent text with no glyph, for the one thing on the screen the user is looking for, such as a file name
    Accent,
    //undimmed body text with no glyph, for a row that must read as primary rather than as a footnote
    Plain,
}

//one row of the body above the options, plain text the widget sanitizes and paints, and an empty text is a spacer row
public sealed record BodyRow(string Text, BodyRowKind Kind = BodyRowKind.Note,   //the indent applies to every wrapped line, and the hang adds cells to continuation lines only
    IReadOnlyList<string>? Highlight = null, int Indent = 0, int Hang = 0);   //a highlight span is literal text, painted undimmed on a note row and in accent on a plain row, and only inside one wrapped line

//the caller's rows replace the erased block verbatim, the composer owns its own wrapping. pass ReclaimAbove only when the composer wrote those rows itself
public sealed record EchoTail(IReadOnlyList<string> Rows, int ReclaimAbove = 0);

//the composer is told the question, the answer, the live width and whether a body block sits above. read Identity when the answer drives a decision
public sealed record EchoInput(string? Question, string Answer, int Width, bool HasBody,
    string? Identity = null);

//a heading is not an option, the cursor can't reach it and it takes no digit. an out-of-range BeforeOption is refused and the text goes out verbatim
public sealed record SelectHeading(int BeforeOption, string Text);

//the user never presses, gatto watches, so this screen's successor is itself. the arrival sentence comes from ArrivedLabel, a null one commits nothing
public sealed record PollSpec(
    Func<bool> Arrived, Func<long, string> Row, IPollClock Clock, TimeSpan Interval,
    string? ArrivedLabel = null);

//the watch's clock sits on the poll spec rather than the widget, so other screens are unchanged. it is not a general clock, other timing sites keep Stopwatch
public interface IPollClock
{
    //wait up to budget for a keystroke, true means one is waiting and false means the budget expired with nothing typed
    bool WaitForKey(TimeSpan budget);

    //milliseconds since the wait began, the row composer's only input so the user reads what this clock says
    long ElapsedMs { get; }
}

//what the widget renders, and it sanitizes every string itself, so its own chrome is the only source of escape bytes
public sealed record SelectSpec(
    IReadOnlyList<TitleRow> TitleRows,
    PromptQuestion? Question,
    IReadOnlyList<SelectOption> Options,
    IReadOnlyList<DetailRow>? DetailRows = null,
    string? FreeTextLabel = null,   //null means no free-text row, and combining it with MultiSelect throws
    bool MultiSelect = false,
    string? FooterHint = "Esc to cancel",
    int InitialCursor = 0,
    IReadOnlyList<string>? Tabs = null,   //a chip strip as the first row, painted by the widget since the chips are model text
    int ActiveTab = 0,   //out of range, -1 included, means no chip is active, as on the review step
    bool Horizontal = false,   //opt-in, so a consumer written before Moved never meets it, and the outcome keeps the cursor and toggles
    IReadOnlyList<BodyRow>? BodyRows = null,
    string? InitialDraft = null,
    int? FreeTextIndex = null,   //moves the free-text row among the options without changing option indices, and null keeps it last
    bool EchoOnCompletion = false,
    bool DrainTypeAhead = false,
    Func<EchoInput, EchoTail>? EchoCompose = null,
    Func<int, IReadOnlyList<string>>? LabelsAt = null,   //display labels asked again at each width. the supplier sanitizes them, and the echo still reads Label
    Func<int, IReadOnlyList<SelectHeading>>? HeadingsAt = null,   //keyed on the width like the labels, since a table's header row changes with it
    bool Numbered = true,   //false drops the digits, their column, the digit key and the overflow hint together
    string? FooterLegend = null,   //the glyph legend, right-aligned on the footer or on a row of its own, and kept when the width narrows
    IReadOnlyList<char>? ControlKeys = null,   //keys that change the screen, matched exactly and composing no tail. a rendered digit and the free-text row both win over them
    PollSpec? Poll = null,
    //no row starts highlighted and Enter before a choice does nothing, this question must not nudge. keep this parameter last, those above it have positional callers
    bool NoInitialCursor = false,
    //the turn token a key read waits on, so ctrl+break ends a prompt that is waiting for a key
    CancellationToken Cancel = default,
    //the detail at a row cap, asked when the panel is taller than its room, DetailRows holds it at the full cap
    Func<int, IReadOnlyList<DetailRow>>? DetailAt = null);

//how a SelectPrompt resolved, the widget gives no meaning to any outcome, the callers do
public abstract record SelectOutcome
{
    //closed hierarchy, exactly these cases, nested and same-assembly so a caller can write an exhaustive switch with no catch-all
    private protected SelectOutcome() { }

    public sealed record Chosen(int Index) : SelectOutcome;
    public sealed record FreeText(string Text) : SelectOutcome;
    public sealed record Multi(IReadOnlyList<int> Indices) : SelectOutcome;
    public sealed record Cancelled : SelectOutcome;

    //the user left this step without answering, so it is reachable only on a Horizontal prompt. the cursor, the boxes and the draft return for the caller to restore
    public sealed record Moved(int Delta, int Cursor, IReadOnlyList<int> Selected, string Draft = "") : SelectOutcome;

    //a control key changed the screen without answering it, the caller re-opens the prompt at Cursor. no tail is composed, so a control press commits nothing
    public sealed record Control(char Key, int Cursor) : SelectOutcome;

    //the thing being watched for turned up with nobody pressing a key, the only outcome the widget reaches on its own
    public sealed record Arrived : SelectOutcome;
}

//how a panel line read ended, three outcomes. escape backs out to whatever opened the panel, so it must not read as a dead pump or a cancel
internal enum PanelLineStatus { Submitted, BackedOut, Dead }

//the text is the raw buffer, meaningful only on a Submitted status (the echo is sanitized, this is not)
internal readonly record struct PanelLine(PanelLineStatus Status, string Text)
{
    public static PanelLine Submitted(string text) => new(PanelLineStatus.Submitted, text);
    public static readonly PanelLine BackedOut = new(PanelLineStatus.BackedOut, "");
    public static readonly PanelLine Dead = new(PanelLineStatus.Dead, "");
}

//the one modal selector the prompts and pickers bind onto, holding focus for its whole life so no key reaches the composer
public sealed class SelectPrompt(   //single-select, multi-select and the free-text row share one loop, and Esc clears a draft before it cancels
    ITermSurface surface, Theme theme, IKeySource keys, InputPump? pump = null,
    ChromeHandle? chrome = null, GlyphSet? glyphs = null)
{
    //this run's glyph set comes from the session, a painter that read the environment itself could end up on a different set than its neighbour
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //nine, a number promises one keystroke and there is no key for 10. rows past this stay in the list, arrow-reachable, and get no digit
    internal const int NumberedRows = 9;

    //the cursor glyph is the one the composer prompt uses. the chevron glyph stays the collapsed-reasoning marker, don't use it here

    //a property rather than a const, a const can't read a set. it stays one cell wide so the columns measured around it don't move
    private string CursorGlyph => _glyphs.Prompt + " ";

    //two cells, so a non-cursor row lines up under a cursor row and every non-option region shares one left edge
    private const string Indent = "  ";

    //the chrome cells before an option's label, caret, number and any dot and checkbox columns. a caller laying out a table needs this number

    //numbered has no default on purpose, a caller that measures numbered while rendering unnumbered gets a table three cells narrower than its screen
    internal static int LabelColumn(bool anyMarked, bool multiSelect, bool numbered) =>
        Indent.Length + (numbered ? NumberWidth + 2 : 0)
        + (anyMarked ? MarkPad.Length : 0) + (multiSelect ? BoxOff.Length : 0);

    //nothing renders a number past nine, so the column is never wider than one digit
    private const int NumberWidth = 1;

    //how often a watching screen asks its clock, the rate the live row may change at. the repaint is gated on the row changing, a ceiling on writes
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(140);

    //two spaces between chips, one space reads as a single header on a no-color terminal
    private const string TabGap = "  ";

    //the dot reads the glyph set and the pad beside it stays a literal, it is whitespace matching the glyph's width
    private string MarkDot => _glyphs.Loaded + " ";
    private const string MarkPad = "  ";
    private const string RecommendedSuffix = " (Recommended)";

    //the suffix for one option, its own word when it has one and the shipped literal otherwise
    private static string SuffixFor(SelectOption o) =>
        o.MarkWord is { Length: > 0 } w ? " (" + w + ")" : RecommendedSuffix;

    //multi-select checkboxes and the widget's own confirm row, which is chrome and never a caller option
    private const string BoxOff = "[ ] ";
    private const string BoxOn = "[x] ";
    private const string NextLabel = "Next";

    //the delete byte comes from its code point 0x7F, so no raw control byte sits in this source file, as TermText does
    private const char Del = (char)0x7F;

    //blocks until the prompt resolves, modal focus held for the whole call. rows go inside the composer frame when a painter is armed, inline otherwise
    public SelectOutcome Show(SelectSpec spec)
    {
        var options = spec.Options;
        var multi = spec.MultiSelect;
        var freeText = spec.FreeTextLabel;

        //a caller error, refused before a key is read, the two modes disagree about what Enter does on that one row
        if (multi && freeText is not null)
            throw new ArgumentException(
                "SelectSpec cannot be MultiSelect and carry a FreeTextLabel: a Multi outcome has nowhere to put the text",
                nameof(spec));

        //labels that do not line up with the options would slide onto the wrong models. check here, Rows() runs on the paint thread where a throw kills the frame
        if (spec.LabelsAt is { } labelsAt && labelsAt(surface.Width).Count != options.Count)
            throw new ArgumentException(
                $"SelectSpec.LabelsAt must return one label per option ({options.Count})", nameof(spec));

        //a heading naming an option that does not exist would render nowhere, and the row dropped is a table's header
        if (spec.HeadingsAt is { } headingsAt
            && headingsAt(surface.Width).Any(h => h.BeforeOption < 0 || h.BeforeOption >= options.Count))
            throw new ArgumentException(
                $"SelectSpec.HeadingsAt names an option outside 0..{options.Count - 1}", nameof(spec));

        //the free-text row counts as one more numbered option in the terminal position, until the moment it is confirmed
        var count = options.Count + (freeText is null ? 0 : 1);
        //multi-select adds one more reachable row past the last option, the Next row at index count
        var lastRow = multi ? count : count - 1;
        //nothing to pick resolves as a cancel, while a zero-option multi-select still renders Next alone and resolves as Multi([])
        if (lastRow < 0) return Done(new SelectOutcome.Cancelled());

        //the initial toggles come from the caller's Selected values, this array is the live state
        var selected = new bool[options.Count];
        for (var i = 0; i < options.Count; i++) selected[i] = options[i].Selected;

        //the free-text row edits in place, the draft replaces its label and leaves on a Moved outcome for the caller to re-seed
        var freeBuf = new StringBuilder(freeText is null ? "" : spec.InitialDraft ?? "");
        //the free-text row's position is the caller's, absent it stays last so a caller that never asked renders as before
        var freeIdx = freeText is null ? -1 : Math.Clamp(spec.FreeTextIndex ?? options.Count, 0, options.Count);

        //rows and options are no longer the same sequence, index options only through OptionAt. ask IsFreeText rather than comparing a row with freeIdx
        bool IsFreeText(int rowOrIndex) => freeText is not null && rowOrIndex == freeIdx;
        int OptionAt(int row) => freeIdx < 0 || row < freeIdx ? row : row - 1;
        int RowOf(int option) => freeIdx < 0 || option < freeIdx ? option : option + 1;

        //a torn-down painter counts as unarmed, a dead panel would take the keys and render nothing
        var painter = chrome?.Painter is { Alive: true } p ? p : null;   //a torn-down painter would take the keys and render nowhere

        //this push must stay above the first render, the first write after it is the focus-is-armed signal a pump test polls on
        using var focus = pump?.PushFocus();
        var k = focus?.Keys ?? keys;

        //the cursor goes to the nearest row that accepts it, so an InitialCursor pointing at a disabled row never parks the user there
        bool Landable(int i) => IsFreeText(i) || OptionAt(i) >= options.Count || !options[OptionAt(i)].Disabled;
        int Nearest(int from, int step)
        {
            for (var i = from; i >= 0 && i <= lastRow; i += step)
                if (Landable(i)) return i;
            for (var i = from; i >= 0 && i <= lastRow; i -= step)
                if (Landable(i)) return i;
            return from;
        }
        //no row is selected at -1, the render needs no branch for it and only the key guards below pay for the shape
        const int NoCursor = -1;
        var cursor = spec.NoInitialCursor ? NoCursor : Nearest(Math.Clamp(spec.InitialCursor, 0, lastRow), 1);
        var painted = 0;   //inline path: rows the last render occupied, which is the cursor-up distance
        //how this prompt resolved, for the echo. null while the loop runs and after an aborted read, which must not echo as an answer
        SelectOutcome? resolved = null;

        //the inline path hides the hardware cursor and restores it in the finally. drain type-ahead, those keys answer a question the user has not seen yet
        if (spec.DrainTypeAhead) while (k.KeyAvailable) k.ReadKey();

        //the watch's live row, null on every screen that does not watch so those screens render as before
        string? pollRow = null;

        if (painter is null)
        {
            surface.Write(Ansi.HideCursor);
            RenderInline(RowsLive());
        }

        try
        {
            if (painter is not null) PublishFactory();   //one gated step, mutate then publish

            while (true)
            {
                //the clock is asked at animation cadence and Arrived only on the poll's own interval, which hits the disk. the repaint is gated on the composed row changing
                if (spec.Poll is { } poll)
                {
                    var arrived = false;
                    //armed so the first tick checks, the file may already be there and a wasted interval would be the screen doing nothing
                    var lastCheckMs = -(long)poll.Interval.TotalMilliseconds;

                    while (!poll.Clock.WaitForKey(FrameInterval))
                    {
                        var now = poll.Clock.ElapsedMs;
                        if (poll.Row(now) is var fresh && fresh != pollRow)
                        {
                            pollRow = fresh;
                            Repaint();
                        }

                        if (now - lastCheckMs < poll.Interval.TotalMilliseconds) continue;
                        lastCheckMs = now;
                        if (poll.Arrived()) { arrived = true; break; }
                    }

                    if (arrived) return Done(new SelectOutcome.Arrived());
                }

                var key = k.ReadKey(spec.Cancel);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        //the step clamps rather than wraps and skips disabled rows. from NoCursor up takes Nearest(lastRow, -1), Step would start at -2 and give back -1
                        cursor = cursor < 0 ? Nearest(lastRow, -1) : Step(cursor, -1);
                        Repaint();
                        continue;
                    case ConsoleKey.DownArrow:
                        //down needs no NoCursor arm, Step(-1, +1) begins at 0 and finds the first row that accepts the cursor
                        cursor = Step(cursor, +1);
                        Repaint();
                        continue;
                    case ConsoleKey.Enter:
                        //with nothing selected Enter does nothing, a prompt that starts unhighlighted and still submits row 0 has the nudge back
                        if (cursor < 0) continue;
                        //on a multi-select Enter toggles an option and only confirms on the Next row, so a stray key cannot submit a half-built set
                        if (multi)
                        {
                            if (cursor == count) return Done(new SelectOutcome.Multi(Toggled()));
                            selected[cursor] = !selected[cursor];
                            Repaint();
                            continue;
                        }
                        //a blank draft clears and stays, an empty answer must not submit as nothing. the test trims the draft, the return is raw
                        if (IsFreeText(cursor))
                        {
                            if (freeBuf.ToString().Trim().Length == 0)
                            {
                                //on a horizontal prompt a blank Enter skips like the right arrow with the draft intact, elsewhere the draft clears so the placeholder returns
                                if (spec.Horizontal)
                                    return Done(new SelectOutcome.Moved(1, cursor, Toggled(), freeBuf.ToString()));
                                freeBuf.Clear();
                                Repaint();
                                continue;
                            }
                            return Done(new SelectOutcome.FreeText(freeBuf.ToString()));
                        }
                        return Done(new SelectOutcome.Chosen(OptionAt(cursor)));
                    case ConsoleKey.Escape:
                        //escape clears a non-empty draft first, and with nothing left it cancels like everywhere else
                        if (IsFreeText(cursor) && freeBuf.Length > 0) { freeBuf.Clear(); Repaint(); continue; }
                        return Done(new SelectOutcome.Cancelled());
                    //draft editing only, elsewhere Backspace stays dead since its key char is a control byte
                    case ConsoleKey.Backspace:
                        if (IsFreeText(cursor) && freeBuf.Length > 0) { freeBuf.Length--; Repaint(); }
                        continue;
                    //opt-in through the when guard, an older consumer would deny a Moved it has no arm for. the cursor and toggles go along or the step comes back blank
                    case ConsoleKey.LeftArrow when spec.Horizontal:
                        return Done(new SelectOutcome.Moved(-1, cursor, Toggled(), freeBuf.ToString()));
                    case ConsoleKey.RightArrow when spec.Horizontal:
                        return Done(new SelectOutcome.Moved(1, cursor, Toggled(), freeBuf.ToString()));
                    //space toggles only on an option of a multi-select, the Next row must not confirm. cursor >= 0 matters, -1 < count is true
                    case ConsoleKey.Spacebar when multi && cursor >= 0 && cursor < count:
                        selected[cursor] = !selected[cursor];
                        Repaint();
                        continue;
                }

                //a digit selects and confirms at once, options past nine stay arrow-reachable. a digit with Alt or Ctrl must not confirm, a chord typed early would answer
                var c = key.KeyChar;
                var chorded = (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0;

                //on the free-text row every printable is a character of the draft, so a digit types instead of confirming
                if (IsFreeText(cursor) && !chorded && ((c >= ' ' && c != Del) || c == '\t'))
                {
                    freeBuf.Append(c);
                    Repaint();
                    continue;
                }

                //the digit of a disabled row is dead and numbering stays positional, so the digits never shift. when no numbers are rendered no digit may fire
                if (spec.Numbered && !chorded && c >= '1' && c <= '9' && c - '1' < count && Landable(c - '1'))
                {
                    var index = c - '1';
                    //on a multi-select the digit only toggles, it never confirms, and the Next row is out of reach by digit
                    if (multi)
                    {
                        selected[index] = !selected[index];
                        Repaint();
                        continue;
                    }
                    //a digit naming the free-text row parks the cursor there to type, that row wants input before it means anything
                    if (IsFreeText(index)) { cursor = freeIdx; Repaint(); continue; }

                    //on a no-default prompt the first digit lights its row and answers nothing, the next digit or Enter submits
                    if (cursor == NoCursor) { cursor = index; Repaint(); continue; }
                    //move the cursor onto the pick and repaint, the frame left behind must show the outcome the user got
                    cursor = index;
                    Repaint();
                    return Done(new SelectOutcome.Chosen(OptionAt(index)));
                }

                //reached only after the digit branch, a rendered number is a promise about a key. no cursor guard is needed, the typing branch already took the printables
                if (!chorded && spec.ControlKeys is { Count: > 0 } controls && controls.Contains(c))
                    return Done(new SelectOutcome.Control(c, cursor));

                //everything else is dead and the widget keeps waiting, including the space bar and the old y/a/n letters
            }
        }
        finally
        {
            painter?.SetPanel(null);   //unconditional, an aborted read must not pin a dead panel
            if (painter is null)
            {
                if (spec.EchoOnCompletion) EraseAndEcho();
                surface.Write(Ansi.ShowCursor);   //show the cursor again, the inline path hides it for the prompt's lifetime
            }
        }

        //the inline path erases its own block, only the widget knows the row count. one dim question row with an accent answer replaces it, a cancel echoes nothing
        void EraseAndEcho()
        {
            if (painted == 0) return;   //nothing was rendered, the empty-spec guard already returned

            var echo = resolved switch
            {
                SelectOutcome.Chosen c => options[c.Index].Label,
                SelectOutcome.FreeText t => t.Text,
                SelectOutcome.Multi m => string.Join(", ", m.Indices.Select(i => options[i].Label)),
                //a resolved wait exits like the three above, so its label belongs here, and a null label leaves nothing behind
                SelectOutcome.Arrived when spec.Poll?.ArrivedLabel is { Length: > 0 } landed => landed,
                //cancelled, control, an arrival with no label or an exception on the way out, so nothing is echoed
                _ => null,
            };

            var rows = new List<string>();
            var reclaim = 0;
            if (echo is not null)
            {
                //the body stays after the answer, it is what the screen was saying, while the options, cursor and footer go
                var hasBody = spec.BodyRows is { Count: > 0 };
                if (hasBody)
                {
                    rows.AddRange(BodyBlock(surface.Width));
                    rows.Add("");
                }

                //a caller that owns a transcript across prompts composes its own tail, but the erase stays here, only this file knows painted
                if (spec.EchoCompose is { } compose)
                {
                    var identity = resolved is SelectOutcome.Chosen ch ? options[ch.Index].Identity : null;
                    var tail = compose(new EchoInput(spec.Question?.Text, echo, surface.Width, hasBody,
                        identity));
                    if (tail.Rows.Count == 0)
                    {
                        //the tail is empty, nothing is written and the screen renders again. the body goes too, writing it per answer stacks a line per keypress
                        rows.Clear();
                        reclaim = 0;
                    }
                    else
                    {
                        rows.AddRange(tail.Rows);
                        reclaim = Math.Max(0, tail.ReclaimAbove);
                    }
                }
                else
                {
                    var q = spec.Question is { } question ? TermText.Sanitize(question.Text) + " " : "";
                    var answer = TermText.Sanitize(echo);
                    var w = surface.Width;

                    //wrap the echo, it is committed text and never repainted, so a truncated answer would stay cut
                    if (w <= 0)
                    {
                        //no width, so no wrapping, guessing a budget could break a line that would have fitted
                        rows.Add(Indent + theme.Paint(q, Theme.Dim) + theme.Paint(answer, Theme.Accent));
                    }
                    else
                    {
                        var first = Math.Max(1, w - UnicodeWidth.Of(Indent + q));
                        var cont = Math.Max(1, w - Indent.Length);
                        var segs = SoftWrap.Wrap(answer, first, cont);
                        for (var i = 0; i < segs.Count; i++)
                            rows.Add(i == 0
                                ? Indent + theme.Paint(q, Theme.Dim) + theme.Paint(segs[i].Text, Theme.Accent)
                                : Indent + theme.Paint(segs[i].Text, Theme.Accent));
                    }
                    rows.Add("");
                }
            }

            //leftover rows are overwritten and the cursor returns under the echo. reclaim is zero unless a composer asked for it
            surface.Write(Ansi.Up(painted + reclaim) + "\r");
            var lines = Math.Max(rows.Count, painted + reclaim);
            for (var i = 0; i < lines; i++)
                surface.Write(ClearThenRow(i < rows.Count ? rows[i] : ""));
            surface.Write(Ansi.Up(lines - rows.Count) + "\r");
            painted = 0;
        }

        //records how the prompt resolved so the finally can echo it, every outcome return goes through here
        SelectOutcome Done(SelectOutcome outcome)
        {
            resolved = outcome;
            return outcome;
        }

        //ascending, the outcome is a set and the order the user pressed the keys must not show in it
        List<int> Toggled()
        {
            var picked = new List<int>();
            for (var i = 0; i < selected.Length; i++)
                if (selected[i]) picked.Add(i);
            return picked;
        }

        //armed, republish a snapshot factory so a resize recomposes at the live width. without a painter, rewrite the block in place or the cursor moves unseen
        void Repaint()
        {
            if (painter is null)
            {
                surface.Write(Ansi.Up(painted) + "\r");
                RenderInline(RowsLive());
            }
            else PublishFactory();
        }

        //publish a closure over a snapshot of the live state, the painter re-invokes it at every width. capturing the live locals instead would race the paint thread
        void PublishFactory()
        {
            var atCursor = cursor;
            var picked = (bool[])selected.Clone();
            var draft = freeBuf.ToString();
            painter!.SetPanelFactory((w, room) =>
                new PanelContent(Rows(w, room, atCursor, picked, draft, out var inputCaret), inputCaret));
        }

        void RenderInline(List<string> rows)
        {
            //rewrite at least as many lines as the last frame took, this block's height varies with the width so a shrink leaves orphan rows
            var lines = Math.Max(rows.Count, painted);
            for (var i = 0; i < lines; i++)
                surface.Write(ClearThenRow(i < rows.Count ? rows[i] : ""));
            painted = lines;
        }

        //compose at the live width and live state, the inline path's render on every keystroke
        List<string> RowsLive() => Rows(surface.Width, int.MaxValue, cursor, selected, freeBuf.ToString(), out _);   //the width is read on every render, so a terminal that shrinks gets rows that fit at the next repaint

        //the body region painted, the completion echo re-emits it so the kept copy is the same rendering
        List<string> BodyBlock(int w)
        {
            var block = new List<string>();
            foreach (var b in spec.BodyRows ?? [])
            {
                if (b.Text.Trim().Length == 0) { block.Add(""); continue; }
                var sanitized = TermText.Sanitize(b.Text);
                var prefixCells = b.Kind switch
                {
                    BodyRowKind.Question => 2,                        //the question bullet and its space, two cells
                    BodyRowKind.Answer or BodyRowKind.Skipped => 4,   //the indented arrow, four cells
                    _ => 0,
                };
                //the row's own indent on top of the region's chrome, leading spaces in Text would be lost the moment it wraps. the Hang field is what continuations take
                var pad = Indent.Length + prefixCells + Math.Max(0, b.Indent);
                var contPad = pad + Math.Max(0, b.Hang);
                var bodyBudget = w <= 0 ? 0 : Math.Max(1, w - pad);
                var contBudget = w <= 0 ? 0 : Math.Max(1, w - contPad);
                var segs = SoftWrap.Wrap(sanitized, bodyBudget, contBudget);
                for (var s = 0; s < segs.Count; s++)
                {
                    //own is added on every arm so pad stays the true width of what was written. the lead and the pad must come from the same sum
                    var own = new string(' ', Math.Max(0, b.Indent));
                    var lead = s > 0 ? new string(' ', contPad) : b.Kind switch
                    {
                        BodyRowKind.Question => Indent + own + theme.Paint(_glyphs.Loaded, Theme.Dim) + " ",
                        BodyRowKind.Answer => Indent + own + "  " + theme.Paint($"{_glyphs.Right}", Theme.Accent) + " ",
                        BodyRowKind.Skipped => Indent + own + "  " + theme.Paint($"{_glyphs.Right}", Theme.Dim) + " ",
                        _ => Indent + own,
                    };
                    var text = b.Kind switch
                    {
                        BodyRowKind.Answer or BodyRowKind.Accent => theme.Paint(segs[s].Text, Theme.Accent),
                        //on a dim row the span stands out by being the one part not dimmed
                        BodyRowKind.Skipped or BodyRowKind.Note =>
                            PaintSpans(segs[s].Text, b.Highlight, Theme.Dim, null),
                        //on a plain row there is nothing to un-dim, so the span takes the accent instead
                        _ => PaintSpans(segs[s].Text, b.Highlight, null, Theme.Accent),
                    };
                    block.Add(Fit(lead + text, w));
                }
            }
            return block;
        }

        //a row painted in baseFg with its spans picked out in spanFg, the rule lives in Theme.PaintSpans so both faces share it
        string PaintSpans(string line, IReadOnlyList<string>? spans, RgbColor? baseFg, RgbColor? spanFg) =>
            theme.PaintSpans(line, spans, baseFg, spanFg);

        //one cursor move, skipping disabled rows and clamping at both ends, and a move that finds nothing leaves the cursor put
        int Step(int from, int step)
        {
            for (var i = from + step; i >= 0 && i <= lastRow; i += step)
                if (Landable(i)) return i;
            return from;
        }

        //the detail takes the room the rest leaves, fewer rows until the panel fits, and none when no cap fits, so the title, the question and the options stay
        List<string> Rows(int w, int room, int atCursor, bool[] picked, string draft, out (int Row, int Col)? inputCaret)
        {
            var rows = Compose(w, spec.DetailRows, atCursor, picked, draft, out inputCaret);
            if (rows.Count <= room || spec.DetailAt is not { } at || spec.DetailRows is not { Count: > 0 } full) return rows;
            for (var cap = full.Count - 1; cap >= 1; cap--)
            {
                var fitted = Compose(w, at(cap), atCursor, picked, draft, out var caret);
                if (fitted.Count > room) continue;
                inputCaret = caret;
                return fitted;
            }
            return Compose(w, null, atCursor, picked, draft, out inputCaret);
        }

        //pure in its arguments, it can't read the live cursor, toggles, draft or width. inputCaret is set only while the cursor sits on the free-text row
        List<string> Compose(int w, IReadOnlyList<DetailRow>? details, int atCursor, bool[] picked, string draft, out (int Row, int Col)? inputCaret)
        {
            inputCaret = null;
            var rows = new List<string>();

            //the strip is painted here so its chips go through Sanitize. no row here may soft-wrap, the inline path rewinds over the rows it wrote
            if (spec.Tabs is { Count: > 0 } tabs)
            {
                var budget = w <= 0 ? 0 : Math.Max(1, w - Indent.Length);
                var strip = new StringBuilder();
                var stripCells = 0;

                void FlushStrip()
                {
                    if (strip.Length == 0) return;
                    rows.Add(Fit(Indent + strip, w));
                    strip.Clear();
                    stripCells = 0;
                }

                for (var i = 0; i < tabs.Count; i++)
                {
                    var chip = TermText.Sanitize(tabs[i]);
                    var ink = i == spec.ActiveTab ? Theme.Accent : Theme.Dim;
                    var chipCells = UnicodeWidth.Of(chip);

                    if (budget > 0 && stripCells > 0 && stripCells + TabGap.Length + chipCells > budget)
                        FlushStrip();

                    //a header wider than the whole strip has no row to flow to, so it wraps onto rows of its own. paint each piece separately so no colour run is torn
                    if (budget > 0 && chipCells > budget)
                    {
                        FlushStrip();
                        foreach (var seg in SoftWrap.Wrap(chip, budget, budget))
                        {
                            strip.Append(theme.Paint(seg.Text, ink));
                            stripCells = UnicodeWidth.Of(seg.Text);
                            FlushStrip();
                        }
                        continue;
                    }

                    if (stripCells > 0) { strip.Append(TabGap); stripCells += TabGap.Length; }
                    strip.Append(theme.Paint(chip, ink));
                    stripCells += chipCells;
                }
                FlushStrip();
            }

            foreach (var title in spec.TitleRows)
            {
                //compose the title row here rather than in the caller, so every text goes through Sanitize
                var titleRow = Indent + theme.Paint(TermText.Sanitize(title.Text), Theme.Bright, bold: true);
                if (title.Code is { } code) titleRow += theme.Chip(TermText.Sanitize(code));
                if (title.MarkLegend is { } legend)
                    titleRow += "   " + theme.Paint(_glyphs.Loaded, Theme.Accent) + " "
                        + theme.Paint(TermText.Sanitize(legend), Theme.Dim);
                rows.Add(Fit(titleRow, w));
            }

            //the body texts are model-controlled, so they're sanitized and painted here, where the widget's chrome is the only source of escapes
            if (spec.BodyRows is { Count: > 0 })
            {
                if (rows.Count > 0) rows.Add("");
                rows.AddRange(BodyBlock(w));
            }

            //the detail reads before the choice, so it sits above the question. frame it with rules only when a row has a gutter, blank lines otherwise
            var hasDetails = details is { Count: > 0 };
            var ruledDetails = hasDetails && details!.Any(d => d.Gutter is not null);
            if (hasDetails)
            {
                if (ruledDetails) rows.Add(Rule(w));
                else if (rows.Count > 0) rows.Add("");
                //the details arrive as raw lines and wrap at this paint's width, since a clipped end would be the part under review
                foreach (var detail in details!)
                {
                    var gutter = detail.Gutter is null ? "" : TermText.Sanitize(detail.Gutter);
                    var gutterCells = UnicodeWidth.Of(gutter);
                    //the sign column of a change's row, and the ground a changed row stands on from its gutter to the panel's edge
                    var sign = detail.Kind switch
                    {
                        DetailKind.Added => theme.Paint("+", Theme.Ok) + "  ",
                        DetailKind.Removed => theme.Paint("-", Theme.Err) + "  ",
                        DetailKind.Same => "   ",
                        _ => "",
                    };
                    var signCells = detail.Kind == DetailKind.None ? 0 : 3;
                    RgbColor? ground = detail.Kind switch { DetailKind.Added => Theme.DiffAddedBg, DetailKind.Removed => Theme.DiffRemovedBg, _ => null };
                    var pad = Indent.Length + gutterCells + signCells;
                    var detailBudget = w <= 0 ? 0 : Math.Max(1, w - pad);
                    var detailPlain = TermText.Sanitize(detail.Text);
                    var segs = SoftWrap.Wrap(detailPlain, detailBudget, detailBudget);
                    if (segs.Count == 0 && detail.Kind != DetailKind.None) segs = new[] { new WrapSeg("", 0) };
                    if (segs.Count == 0)
                    {
                        if (gutterCells > 0) rows.Add(Fit(Indent + theme.Paint(gutter, Theme.Dim), w));
                        continue;
                    }
                    var detailCoded = !detail.Dim && detail.Roles is not null && detail.Roles.Count == detailPlain.Length;
                    var detailStart = 0;
                    for (var s = 0; s < segs.Count; s++)
                    {
                        var lead = s == 0
                            ? (gutterCells > 0 ? theme.Paint(gutter, Theme.Dim) : "") + sign
                            : new string(' ', gutterCells + signCells);
                        var text = detail.Dim ? theme.Paint(segs[s].Text, Theme.Dim)
                            : detail.Warn ? theme.Paint(segs[s].Text, Theme.Warn)
                            : detailCoded && string.CompareOrdinal(detailPlain, detailStart, segs[s].Text, 0, segs[s].Text.Length) == 0
                                ? theme.PaintRuns(SyntaxHighlight.Runs(detailPlain, detail.Roles!, detailStart, segs[s].Text.Length), null, detail.Language)
                                : detail.Kind == DetailKind.None ? segs[s].Text : theme.Paint(segs[s].Text, Theme.CodeBlockFg);
                        rows.Add(Fit(Indent + (ground is { } bg ? theme.Ground(lead + text, bg) : lead + text), w));
                        detailStart += segs[s].SourceChars;
                    }
                }
                if (ruledDetails) rows.Add(Rule(w));
            }

            if (spec.Question is { } question)
            {
                //the question hugs the closing rule under ruled details, otherwise a blank keeps what's above from gluing to it
                if (rows.Count > 0 && !ruledDetails) rows.Add("");
                //a question must not truncate. the three parts wrap as one string, so a word on the Text/Code seam breaks where it should
                var qHead = TermText.Sanitize(question.Text);
                var qCode = question.Code is { } code ? TermText.Sanitize(code) : "";
                var qPlain = qHead + qCode + TermText.Sanitize(question.After);
                var qBudget = w <= 0 ? 0 : Math.Max(1, w - Indent.Length);
                var qAt = 0;
                foreach (var seg in SoftWrap.Wrap(qPlain, qBudget, qBudget))
                {
                    rows.Add(Fit(Indent + Chipped(seg.Text, qAt, qHead.Length, qCode.Length), w));
                    qAt += seg.SourceChars;
                }
                //one blank between the question and its options, and it belongs here since every prompt in gatto goes through this widget
                if (count > 0) rows.Add("");
            }
            //with no question, one blank separates the content above from the options. skip it when the last row above is already blank, two blanks read as a hole
            else if (count > 0 && rows.Count > 0
                     && (spec.BodyRows is { Count: > 0 } bodyTail
                         ? bodyTail[^1].Text.Trim().Length > 0
                         : spec.TitleRows.Count == 0 || spec.TitleRows[^1].Text.Trim().Length > 0))
                rows.Add("");

            //reserve the dot column only when some option is marked, a permission prompt shouldn't pay two columns for a picker it doesn't use
            var anyMarked = false;
            var recommended = -1;
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i].Marked) anyMarked = true;
                //the suffix goes on the first recommended option only, extra ones get no suffix rather than an error
                if (recommended < 0 && options[i].Recommended) recommended = i;
            }
            //the recommended option as a row, since the loop below iterates rows and the two only match while the free-text row is terminal
            var recommendedRow = recommended < 0 ? -1 : RowOf(recommended);

            //the display labels for this width are asked once per render. the caller's layout may be a whole table, so the row loop must not re-run it per option
            var supplied = spec.LabelsAt?.Invoke(w);

            //headings hang at the label column, which stays constant for the render. a caller laying a table out to width minus that column lines up with its cells
            var headings = spec.HeadingsAt?.Invoke(w);
            var labelCol = LabelColumn(anyMarked, multi, spec.Numbered);

            for (var i = 0; i < count; i++)
            {
                var opt = IsFreeText(i) || OptionAt(i) >= options.Count
                    ? null                       //null marks the free-text row or multi-select's Next row, the rows with no option behind them
                    : options[OptionAt(i)];
                //headings add rows without disturbing the option counts, and an index matching no option renders nowhere rather than throwing
                if (headings is not null && opt is not null)
                    foreach (var h in headings)
                        if (h.BeforeOption == OptionAt(i))
                            //an empty heading text is a spacer, emitted as a blank line rather than labelCol spaces so a frame has no trailing whitespace
                            rows.Add(h.Text.Length == 0 ? "" : Fit(new string(' ', labelCol) + h.Text, w));

                var caret = i == atCursor ? CursorGlyph : Indent;
                //a disabled row or one past NumberedRows loses the digit but keeps its slot. with Numbered false the column is absent so a caller's table keeps those cells
                var disabled = opt is { Disabled: true };
                var number = !spec.Numbered
                    ? ""
                    : disabled || i >= NumberedRows
                        ? new string(' ', NumberWidth + 2)
                        : (i + 1).ToString().PadLeft(NumberWidth) + ". ";
                //once any option is marked the dot column is reserved on every row, the free-text row included so its label lines up
                var dot = !anyMarked ? "" : opt is { Marked: true } ? MarkDot : MarkPad;
                //multi-select only, and the guard in Show makes it exclusive with the free-text row so picked[i] is always in range
                var box = !multi ? "" : picked[i] ? BoxOn : BoxOff;
                //once anything is typed the draft replaces the label on the row itself. it stays visible and plain while the cursor is elsewhere
                var isFree = opt is null;
                var draftShown = isFree && draft.Length > 0;
                //the label is the full visible text, so the caret math and Fit see one string. the code span is a chip off the cursor and joins the accent run on it
                var labelText = TermText.Sanitize(draftShown ? draft : opt?.Label ?? freeText!);
                var codeText = opt?.LabelCode is { } lc ? TermText.Sanitize(lc) : null;
                var afterText = opt is null ? "" : TermText.Sanitize(opt.LabelAfter);
                var label = labelText + codeText + afterText;
                var styledLabel = codeText is null
                    ? label : labelText + theme.Chip(codeText) + afterText;

                //budget the label against the width minus the chrome and the tail, so Fit never cuts the marks. the cut is ANSI-aware, so a painted chip span survives it
                var tailRuns = opt?.Tail ?? [];
                var tailCells = 0;
                foreach (var run in tailRuns) tailCells += UnicodeWidth.Of(run.Text);
                if (tailCells > 0 && w > 0)
                {
                    var room = Math.Max(0, w - UnicodeWidth.Of(caret + number + dot + box) - tailCells);
                    label = TermText.TruncateCells(label, room, glyphs: _glyphs);
                    styledLabel = TermText.TruncateCells(styledLabel, room, glyphs: _glyphs);
                }

                //a supplied label goes in as it stands, its supplier owns the sanitize and the paint. an index past the list uses the option's label rather than throwing
                var passLines = !isFree && OptionAt(i) < (supplied?.Count ?? 0)
                    ? supplied![OptionAt(i)].Split('\n')
                    : null;
                var chrome = caret + number + dot + box;

                //a draft soft-wraps under the label column instead of truncating, since cutting what the user is typing hides their own keystrokes
                IReadOnlyList<string> draftLines = [];
                if (draftShown && w > 0)
                {
                    //at least one cell of room whatever the width, a zero budget would put every character on its own row
                    var room = Math.Max(1, w - UnicodeWidth.Of(chrome));
                    draftLines = [.. SoftWrap.Wrap(label, room, room).Select(s => s.Text)];
                    if (draftLines.Count > 0)
                    {
                        label = draftLines[0];
                        styledLabel = draftLines[0];
                    }
                }

                if (isFree && i == atCursor)
                {
                    //the caret goes at the end of the draft, on its last row when the draft wraps. both parts depend on the width, so the factory composes the caret with the rows
                    var prefixCells = UnicodeWidth.Of(caret + number + dot + box);
                    var lastLine = draftLines.Count > 0 ? draftLines[^1] : label;
                    inputCaret = (rows.Count + Math.Max(0, draftLines.Count - 1),
                        prefixCells + (draftShown ? UnicodeWidth.Of(lastLine) : 0));
                }

                string row;
                if (passLines is not null)
                {
                    //only the chrome is painted here, since a label run nested inside the accent paint drops that accent at its first reset
                    row = i == atCursor ? theme.Paint(chrome, Theme.Accent) + passLines[0]
                        : opt is { Marked: true }
                            ? caret + number + theme.Paint(_glyphs.Loaded, Theme.Accent) + " " + box + passLines[0]
                        : disabled ? theme.Paint(chrome, Theme.Dim) + passLines[0]
                        : chrome + passLines[0];
                }
                else if (i == atCursor)
                    //an empty free-text row under the cursor is a placeholder, accent chrome around a dim label
                    row = isFree && !draftShown
                        ? theme.Paint(caret + number + dot + box, Theme.Accent) + theme.Paint(label, Theme.Dim)
                        : theme.Paint(caret + number + dot + box + label, Theme.Accent);
                else if (isFree && !draftShown)
                    //an empty free-text row is a placeholder off the cursor too, with only the label dimmed and the chrome left ordinary
                    row = caret + number + dot + box + theme.Paint(label, Theme.Dim);
                else if (opt is { Marked: true })
                    //a marked row off the cursor paints only the dot, its text like every other row. the dot takes the accent, since it's the panel's one status glyph
                    row = caret + number + theme.Paint(_glyphs.Loaded, Theme.Accent) + " " + box + styledLabel;
                else if (disabled)
                    //a disabled row is dimmed whole, chrome included, so it reads as inert rather than as an option that happens to be grey
                    row = theme.Paint(caret + number + dot + box + label, Theme.Dim);
                else
                    row = caret + number + dot + box + styledLabel;

                //the suffix is appended outside the row paint, since a nested run would drop the accent instead of stacking with it. the word is the option's own when it has one
                if (i == recommendedRow)
                    row += theme.Paint(SuffixFor(options[recommended]), Theme.Dim);
                //the tail runs are appended outside the row paint too, so the cursor keeps its accent and each mark keeps its own ink
                foreach (var run in tailRuns)
                    row += run.Ink is { } runInk ? theme.Paint(run.Text, runInk) : run.Text;
                rows.Add(Fit(row, w));

                //the draft's continuation rows hang under the label column, so the block reads as one option continuing rather than as extra options
                if (draftLines.Count > 1)
                {
                    var draftHang = new string(' ', UnicodeWidth.Of(chrome));
                    for (var s = 1; s < draftLines.Count; s++)
                        rows.Add(Fit(i == atCursor
                            ? theme.Paint(draftHang + draftLines[s], Theme.Accent)
                            : draftHang + draftLines[s], w));
                }

                //the rest of a multi-line label hangs under the label column, blank through the caret and number cells. one label keeps one digit and one cursor stop
                if (passLines is { Length: > 1 })
                {
                    var hang = new string(' ', UnicodeWidth.Of(chrome));
                    for (var s = 1; s < passLines.Length; s++) rows.Add(Fit(hang + passLines[s], w));
                }

                //every option with a description renders it, whichever row the cursor is on
                if (opt?.Description is { } description)
                {
                    //the pad indents past the number and dot column, so the text reads as a child of its option. a fixed 2-cell gutter would leave these rows too wide
                    var pad = new string(' ', Indent.Length + number.Length + dot.Length + box.Length);
                    var budget = w - pad.Length;

                    //split the description on its newlines before it is sanitized, since Sanitize would eat them. a blank line stays a blank row so a pane can breathe
                    foreach (var line in description.Split('\n'))
                    {
                        var segs = SoftWrap.Wrap(TermText.Sanitize(line), budget, budget);
                        if (segs.Count == 0) { rows.Add(""); continue; }
                        foreach (var seg in segs)
                            rows.Add(Fit(pad + theme.Paint(seg.Text, Theme.Dim), w));
                    }
                }

                //one row says the options past the last digit are unnumbered and the arrow keys reach them. it comes after that option is fully rendered
                if (spec.Numbered && i == NumberedRows - 1 && count > NumberedRows)
                    rows.Add(Fit(Indent + theme.Paint(
                        "the rest have no number — use the arrow keys", Theme.Dim), w));
            }

            if (multi)
            {
                //the Next row is chrome rather than an option, unnumbered with no checkbox or description. the blank row above keeps it from reading as an option missing its box
                rows.Add("");
                var caret = atCursor == count ? CursorGlyph : Indent;
                //the pad repeats the number, dot and box columns, so the label sits where an option's label sits
                var pad = new string(' ', (spec.Numbered ? NumberWidth + 2 : 0)
                    + (anyMarked ? MarkPad.Length : 0) + BoxOff.Length);
                var row = caret + pad + NextLabel;
                rows.Add(Fit(atCursor == count ? theme.Paint(row, Theme.Accent) : row, w));
            }

            //the watch row sits above the footer and below the options, since it says what gatto is doing. the caller composes its text and the widget only sanitizes it
            if (pollRow is { Length: > 0 } watching)
            {
                rows.Add("");
                rows.Add(Fit(Indent + theme.Paint(TermText.Sanitize(watching), Theme.Dim), w));
            }

            //a null hint and a null legend mean no footer row at all. the legend shares the hint's row, right-aligned, and takes its own row when it can't fit
            if (spec.FooterHint is { } hint || spec.FooterLegend is not null)
            {
                var hintText = spec.FooterHint is { } h ? TermText.Sanitize(h) : null;
                var legendText = spec.FooterLegend is { } l ? TermText.Sanitize(l) : null;
                rows.Add("");

                //measure the sanitized text before painting it, since the pad comes from display cells and paint adds escapes
                var shared = hintText is not null && legendText is not null && w > 0
                    && Indent.Length + UnicodeWidth.Of(hintText) + 1 + UnicodeWidth.Of(legendText) <= w;

                if (shared)
                {
                    var pad = w - Indent.Length - UnicodeWidth.Of(hintText!) - UnicodeWidth.Of(legendText!);
                    rows.Add(Indent + theme.Paint(hintText!, Theme.Dim) + new string(' ', pad)
                        + theme.Paint(legendText!, Theme.Dim));
                }
                else
                {
                    if (hintText is not null) rows.Add(Fit(Indent + theme.Paint(hintText, Theme.Dim), w));
                    if (legendText is not null) foreach (var r in LegendRows(legendText, w)) rows.Add(r);
                }
            }

            return rows;
        }

        //the legend keeps its right edge while one row holds it. when it doesn't fit one row it wraps rather than cutting, so nothing is lost without a sign
        List<string> LegendRows(string text, int width)
        {
            var cells = UnicodeWidth.Of(text);
            if (width <= 0) return [Indent + theme.Paint(text, Theme.Dim)];
            if (cells + Indent.Length <= width)
                return [new string(' ', width - cells) + theme.Paint(text, Theme.Dim)];

            var budget = Math.Max(1, width - Indent.Length);
            return [.. SoftWrap.Wrap(text, budget, budget)
                .Select(s => Indent + theme.Paint(s.Text, Theme.Dim))];
        }

        //the detail separators are full-width '-' dashes, lighter than the frame's rule so they read as an inner seam
        string Rule(int w)
        {
            var dashes = w <= 0 ? 40 : w;
            return Fit(theme.Paint(new string('-', dashes), Theme.Rule), w);
        }

        //width 0 means unknown, so leave the row alone and let the terminal wrap instead of blanking the prompt
        string Fit(string row, int w) => w <= 0 ? row : TermText.TruncateCells(row, w, _glyphs);

        //the part of the row inside the inline-code span is painted as a chip, and at maps the row's own index back to the question
        string Chipped(string row, int at, int codeAt, int codeLen)
        {
            if (codeLen == 0) return row;
            var from = Math.Max(at, codeAt);
            var to = Math.Min(at + row.Length, codeAt + codeLen);
            if (to <= from) return row;
            return row[..(from - at)] + theme.Chip(row[(from - at)..(to - at)]) + row[(to - at)..];
        }

        //erase the line at column 0 before writing the row. a full-width row leaves the cursor pending, so erasing after it eats the last character
        static string ClearThenRow(string row) => "\r" + Ansi.ClearToEol + row + "\n";
    }

    //the caller owns the frame, so this reader takes head, wrap and paint delegates. copy head's list before appending the input line
    internal static PanelLine ReadLinePanel(
        IKeySource keys, Func<List<string>> head, string prefix,
        Func<string, List<string>> wrap, Action<List<string>> paint,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            //copy head's list rather than appending to it, so a caller that returns a held list doesn't grow a stale input line
            var live = new List<string>(head());
            //the input line is always the last row, since the panel parks the cursor at the end of it
            live.AddRange(wrap(prefix + TermText.Sanitize(sb.ToString())));
            paint(live);

            ConsoleKeyInfo k;
            try { k = keys.ReadKey(ct); }
            catch (InvalidOperationException) { return PanelLine.Dead; }

            switch (k.Key)
            {
                case ConsoleKey.Enter: return PanelLine.Submitted(sb.ToString());
                case ConsoleKey.Escape: return PanelLine.BackedOut;
                case ConsoleKey.Backspace:
                    if (sb.Length > 0) sb.Length--;
                    continue;
            }
            //the DEL key sits above the printable range, so the >= ' ' test admits it. it is built from its code point to keep raw bytes out of this file
            if ((k.KeyChar >= ' ' && k.KeyChar != Del) || k.KeyChar == '\t') sb.Append(k.KeyChar);
        }
    }
}
