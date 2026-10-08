using Gatto.Terminal;
namespace Gatto.Cli.Setup.Tui;

//everything one screen needs to become rows, held in a record so each call site names its fields
internal readonly record struct Screen(
    IReadOnlyList<StripSection> Sections,
    Region Focused,
    string? Title,
    IReadOnlyList<PaintedRow> Body,
    DoorRow? Door,
    IReadOnlyList<FooterKey> Keys,
    Legend? Legend = null,
    string? Armed = null,
    IReadOnlyList<PaintedRow>? Hero = null,
    string? Working = null,
    int? StripAt = null,
    string? Command = null);   //the title row's name when the screen's differs from the face's command

//what the door says on its right, kept beside the keys it names. a hint for a key that does nothing is as wrong as a key nobody is told about
internal static class DoorHints
{
    //the ways in that work on every screen whose door has no key of its own. a function so the glyph set can supply the down key, the record's default is null
    public static string AnywhereOf(GlyphSet g) => $"Tab or {g.DownKey} to type";

    //the shelf's door hint, and only ?, since the wizard face binds no mouse
    public const string Search = "? to search";

    //the local shelf's door says ? to search, the same words as the shelf's
    public const string SearchLocal = "? to search";

    //what a search door says once the keys are in it, the keys rather than how to get there
    public static string FocusedOf(GlyphSet g) => $"Enter search {g.Dot} Esc back";

    //what the folder door says, Enter look, since that door sends gatto to a folder
    public static string FocusedLookOf(GlyphSet g) => $"Enter look {g.Dot} Esc back";
}

//what the door says while the keys are in it, or null when it says nothing. a door knows its own keys, so the painter does not infer this from another string
internal readonly record struct DoorRow(
    string Placeholder, string Draft = "", string? Hint = null,
    string? Focused = null);

//composes one screen into rows, and is the only place that knows their order. every row is width-bounded here, since this is the last step before the terminal
internal static class ScreenPainter
{
    private const string Indent = "  ";

    //five cells between the question and the purr, read off the golden rather than chosen
    private const string PurrGap = "     ";

    //what the keys row says while a probe runs, since a bounded read offers no keys to press
    private static string LookingOf(GlyphSet g) => $"looking{g.Ellipsis}";

    //the header's version line as plain text, for the tests that pin it. the inked form is HeaderRow, composed the same way
    public static string Header(int width, string version, string build, GlyphSet? glyphs,
        string command = DefaultCommand) =>
        HeaderRow(glyphs ?? GlyphSet.Unicode, width, version, build, command).Text;

    //the glyph set is threaded in from the face, so no painter asks the environment twice
    public static IReadOnlyList<PaintedRow> Paint(
        Screen s, int width, string version, string build, GlyphSet? glyphs,
        string command = DefaultCommand)
        => Paint(s, width, version, build, glyphs, out _, command);

    internal const string DefaultCommand = "gatto setup";   //the wizard's own name when no caller names the command, so there is one entry literal

    //the cursor goes on the door's own caret cell, measured off that string. the row is an offset from the end, since the fit drops blanks above the door
    public static IReadOnlyList<PaintedRow> Paint(
        Screen s, int width, string version, string build, GlyphSet? glyphs, out CaretSpot? caret,
        string command = DefaultCommand)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        caret = null;
        int? caretColumn = null;
        var doorAt = -1;
        //the head names the wizard, since the wizard owns the terminal, and there is one shape now
        var rows = new List<PaintedRow>
        {
            HeaderRow(g, width, version, build, command),
            new(Strip.Runs(s.Sections, StripFocus(s), g)),
            PaintedRow.Of(string.Concat(Enumerable.Repeat(g.Rule, Math.Max(1, width))), RunInk.Dim),
        };

        //the hero goes between the rule and the title, and it cannot be a body row, which renders below the title
        if (s.Hero is { Count: > 0 } hero) { rows.AddRange(hero); rows.Add(PaintedRow.Of("", fit: RowFit.Structural)); }

        //while a probe runs the purr sits on the title row and the keys row says looking, one field for one fact
        if (s.Title is { Length: > 0 } t)
        {
            rows.Add(s.Working is { Length: > 0 } w
                ? new PaintedRow([new Run(Indent + t, RunInk.Bright), new Run(PurrGap + w, RunInk.Accent)])
                : PaintedRow.Of(Indent + t, RunInk.Bright));
            rows.Add(PaintedRow.Of("", fit: RowFit.Structural));
        }

        rows.AddRange(s.Body);

        if (s.Door is { } door)
        {
            //the blank before the door is kept, so the fit never takes it
            rows.Add(PaintedRow.Of("", fit: RowFit.Fixed));
            var (doorRow, column) = DoorRowText(door, s.Focused == Region.Search, width, g);
            doorAt = rows.Count;
            caretColumn = column;
            rows.Add(doorRow);
        }

        rows.Add(PaintedRow.Of(string.Concat(Enumerable.Repeat(g.Rule, Math.Max(1, width))), RunInk.Dim));
        rows.Add(
            //the armed row wins over the working one, so a working screen with keys of its own renders them. the probe cannot arm anything, the fetch can
            s.Armed is { Length: > 0 } a ? new PaintedRow(Footer.ArmedRuns(a))
            : s.Working is { Length: > 0 } && s.Keys.Count == 0
                ? new PaintedRow(Footer.ArmedRuns(LookingOf(g)))
            : new PaintedRow(Footer.Runs(width, s.Keys, s.Legend)));

        //the caret column must be inside the width. the rows are clamped to it below, so a caret past the edge would sit on a cell with no mark
        if (doorAt >= 0 && caretColumn is { } c && c < width)
            caret = new CaretSpot(FromEnd: rows.Count - doorAt, Column: c);

        return [.. rows.Select(r => r.Clamp(width))];
    }

    //the header: the command on the left, the version and build dim against the right edge
    private static PaintedRow HeaderRow(GlyphSet g, int width, string version, string build,
        string command)
    {
        var left = Indent + command;   //passed in, since gatto model runs this same flow and a literal would name a command the user didn't type
        var right = $"v{version} {g.Dot} build {build}";
        //right-aligned content stops two cells short of the wall, while the rule rows run the full width as the frame's edge
        var gap = Math.Max(1, Margins.Inside(width)
            - Gatto.Terminal.UnicodeWidth.Of(left) - Gatto.Terminal.UnicodeWidth.Of(right));
        return new PaintedRow([
            new Run(left, RunInk.Bright),
            new Run(new string(' ', gap)),
            new Run(right, RunInk.Dim)]);
    }

    //the strip never has a cursor, and the -1 stays a constant so call sites don't each spell out that there is none
    private static int StripFocus(Screen s) => -1;

    //the typed door's caret, the same one cell mark the shelf's search box draws so the wizard has one caret
    private static string CaretOf(GlyphSet g) => g.Bar;

    private static (PaintedRow Row, int? Caret) DoorRowText(
        DoorRow door, bool focused, int width, GlyphSet g)
    {
        //a null hint means the caller didn't choose, so the painter fills in the standing hint here. the ❯ starts at column zero like an option row's
        var hint = door.Hint ?? DoorHints.AnywhereOf(g);
        var room = Math.Max(0, width - 2 - Gatto.Terminal.UnicodeWidth.Of(hint) - 1);
        var left = $"{g.Prompt} " + (door.Draft.Length > 0
            ? TypedDoor.Window(door.Draft, room)
            : door.Placeholder);
        //the caret and typed text take the accent, the hint is dim. the caret sits after the draft or before the placeholder, and only the search door keeps its hint
        if (focused)
        {
            //compose the text and the caret column together, so the terminal cursor sits on the same mark the row draws
            var before = door.Draft.Length > 0
                ? $"{g.Prompt} " + TypedDoor.Window(door.Draft, room)
                : $"{g.Prompt} ";
            var typed = before + CaretOf(g)
                + (door.Draft.Length > 0 ? "" : " " + door.Placeholder);
            var column = Gatto.Terminal.UnicodeWidth.Of(before);
            if (door.Focused is not { Length: > 0 } says)
                return (new PaintedRow([new Run(typed, RunInk.Accent)]), column);

            //the hint is right-aligned content, so the pad is measured from Margins.Inside rather than the full width
            var pad = Math.Max(1, Margins.Inside(width) - Gatto.Terminal.UnicodeWidth.Of(typed)
                - Gatto.Terminal.UnicodeWidth.Of(says));
            return (new PaintedRow([
                new Run(typed, RunInk.Accent),
                new Run(new string(' ', pad)),
                new Run(says, RunInk.Dim)]), column);
        }

        //an unfocused door draws no caret and takes no cursor. it sits in dim, like every other region the keys are not in
        if (door.Draft.Length > 0)
            return (new PaintedRow([new Run(left, RunInk.Dim)]), null);

        //the hint drops when the row can't hold both, the door's own text is the subject and stays whole
        var leftCells = Gatto.Terminal.UnicodeWidth.Of(left);
        var hintCells = Gatto.Terminal.UnicodeWidth.Of(hint);
        //the fit test uses the same inset width as the gap, otherwise the hint drops at one width and draws at another
        var inside = Margins.Inside(width);
        if (leftCells + 2 + hintCells > inside)
            return (new PaintedRow([new Run(left, RunInk.Dim)]), null);

        return (new PaintedRow([
            new Run(left, RunInk.Dim),
            new Run(new string(' ', inside - leftCells - hintCells)),
            new Run(hint, RunInk.Dim)]), null);
    }

    //left text with right text pushed to the inside of the frame, at least one cell between them, for the wizard's tail rows
    private static string Row(string left, string right, int width)
    {
        if (right.Length == 0) return left;
        var gap = Math.Max(1, Margins.Inside(width)
            - Gatto.Terminal.UnicodeWidth.Of(left) - Gatto.Terminal.UnicodeWidth.Of(right));
        return left + new string(' ', gap) + right;
    }
}
