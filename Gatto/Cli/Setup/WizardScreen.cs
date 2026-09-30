using Gatto.Core.Acquire;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//one screen, as data rather than paint, so tests assert on it. the private constructor keeps the hierarchy closed, so a new kind fails at compile time
internal abstract record WizardScreen
{
    private WizardScreen() { }

    //whether the screen offers go back, stamped by the flow and rendered by the face. one flag for both askable kinds, so the faces cannot drift
    public bool AllowBack { get; init; }

    //where the screen sits on the strip, stamped by the flow at the moment it is shown. an empty list is real, and renders the row with no cursor
    public IReadOnlyList<StripSection> Strip { get; init; } = [];

    //the rows above the title, which here is the cat on the welcome screen, stamped by the flow. it cannot be body rows, which render below the title
    public IReadOnlyList<string>? Hero { get; init; }


    //pick one of N, with a key that is never rendered so copy can change freely. watching is a bool rather than a predicate, since a screen stays pure data
    internal sealed record Choice(
        string Key,
        string? Question,
        IReadOnlyList<ChoiceOption> Options,
        IReadOnlyList<WizardRow>? BodyRows = null,
        ShelfView? Shelf = null,
        bool Watching = false,
        //the sentence the transcript commits when a watching screen resolves itself, declared by the screen so the flow stays pure
        string? ArrivedLabel = null,
        //no cursor until the user moves one, and Enter before a choice does nothing. the flag is about the cursor, which a recommended option says nothing about
        bool NoDefault = false,
        //the standing typed row's placeholder, null on a screen that takes no typed text, and both faces render it
        string? Door = null,
        //what is already typed in the door, kept so a search's own sentence stays verifiable. it is view data, so the face renders it without keeping a buffer
        string? Draft = null,
        //the answers are keys rather than a numbered list, and the footer is composed from the options. exactly two options, since a third key does not exist
        bool KeysOnly = false,
        //the machine's facts as data, null elsewhere, laid out by the face since a flow has no width
        MachineView? Machine = null,
        //the fetch's facts as data, null elsewhere, laid out by the face. the folder is one value, since the consent and the offline arm name the same path
        EngineView? Engine = null,
        //the model fetch's facts, a field of its own since the two blocks differ and a screen is never both
        ModelView? Model = null,
        //how long the fetch took, passed on the screen since an arrival screen has no tick, so the settled line keeps that figure
        long? PurredMs = null,
        //what a stopped fetch left on disk, as the whole tick so both rows read one value. a screen with kept bytes never shows the nothing-written default
        FetchTick? Kept = null,
        //the fetch is paused on the user's word, stated by the flow. the bar tail reads paused and Esc prices as CostOfKept, since the fetch already stopped
        bool Paused = false,
        //a flag rather than a figure, since the flow has no clock, and the face measured the time it just ran
        bool PurredSinceWatch = false,
        //the check's live rows, composed by the face from the tick, since the facts change. the block's closing blank is drawn even before a first task starts
        bool Checking = false,
        //the REPL's sixteen-frame purr rather than the download watch's six, since a check is short. default false keeps every other watching screen as it was
        bool FullPurr = false,
        //the words right of the footer keys, a string since the folding kind belongs to the face
        string? Legend = null,
        //what Esc costs here, which wins over the face's computed price. the fetch screens leave it null, since their price is the live figure off the tick
        string? ArmedCost = null,
        //no digits on the rows, since a digit promises a key that picks them. the same flag puts ↑↓ move in the footer, so the two cannot disagree
        bool Unnumbered = false,
        //this screen offers the source switch, so m answers it and the footer names it. it stays positional, since the door census reads that closing argument
        bool SwitchesSource = false) : WizardScreen
    {
        //nothing moves this screen on except the watch, computed from the screen itself. the back flag is left out, since a back row is another escape
        public bool OnlyTheWatchAdvances =>
            Watching && Door is null && !Options.Any(o => o.Advances && !o.Disabled);
    }

    //rows the user reads and acknowledges, each a WizardRow so a span can be accented like a body row
    internal sealed record Info(string Key, IReadOnlyList<WizardRow> Rows) : WizardScreen;

    //something is happening and the wait is long enough to say so, bound onto TickLine by the face
    internal sealed record Progress(string Key, string Label) : WizardScreen;

    //free text with validation, the rule kept on the screen. the placeholder is an instruction, since an example reads as an option to pick
    internal sealed record Ask(
        string Key,
        string? Label,
        Func<string, string?> Validate,
        string? Placeholder = null,
        AskOffer? Offer = null,
        IReadOnlyList<WizardRow>? BodyRows = null,
        //what Enter does here, in the footer's words, next to the rule it names. the Esc verb comes from AllowBack instead, since the flow owns that fact
        string EnterVerb = "continue") : WizardScreen;

    //the end of a setup, with the next step the user does now. null means there is no closing sentence, since a blank row renders a bare gutter mark
    internal sealed record Terminal(
        string Key,
        IReadOnlyList<WizardRow> Rows,
        WizardRow? NextStep,
        bool Success) : WizardScreen;
}

//how a body row reads, as a meaning the widget turns into paint, since a screen knows nothing of themes or terminals
internal enum RowTone
{
    //ordinary body text, which is white, so a row has to ask for anything dimmer
    Body,
    //the thing to look for, a file name in a download list, in the accent ink
    Subject,
    //the only dim tone, for a row that must not compete with the real link, such as the all builds fallback
    Aside,
}

//a status mark that occupies the gutter, two cells wide. a meaning rather than a character, with exhaustive switches for the glyph and the ink
internal enum RowGlyph
{
    //gatto checked this and it is well, green with a ✓
    Good,
    //something gatto tried did not work, in amber, since every screen with this mark also says what to do next
    Bad,

    //gatto could not do what it offered and the fault is not the model's, so it takes the warning mark rather than a cross
    Warn,

    //work that never happened, shown as a dim dash, since it is the absence of a result
    NotRun,
}

//one row of a screen's body, with its tone, spans and shape as data. three span kinds at most, and the indent comes off the row
internal sealed record WizardRow(string Text, RowTone Tone = RowTone.Body,
    IReadOnlyList<string>? Highlight = null, int Indent = 0, int Hang = 0, RowGlyph? Glyph = null,
    IReadOnlyList<string>? Lift = null, IReadOnlyList<string>? Keys = null, string? Tail = null,
    IReadOnlyList<string>? Items = null,
    bool Wide = false, bool Optional = false)
{
    public static implicit operator WizardRow(string text) => new(text);

    //the row's text, so it still reads as a string in a test that joins a body
    public override string ToString() => Text;
}

//an ask's default as a pickable row, since blank Enter on a free-text row clears it. the value is the same string a user typing it would have sent
internal sealed record AskOffer(string Label, string Value);

//one option, with the key as the contract and the label as copy. the Esc verb field is both the footer's word and Esc's answer, so they cannot disagree

//the word beside the mark, replacing recommended where that word is wrong. both faces read the same word, which is lowercase
internal sealed record ChoiceOption(string Key, string Label, string? Description = null,
    bool Disabled = false, bool Recommended = false, bool Advances = true, string? EscVerb = null,
    string? Press = null, string? MarkWord = null);
