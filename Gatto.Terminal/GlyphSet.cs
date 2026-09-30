namespace Gatto.Terminal;

//which glyph vocabulary to speak. auto asks the host, the explicit values override it for a misjudged one
public enum GlyphMode
{
    Auto,
    Unicode,
    Ascii,
}

//a whole frame of box glyphs, taken as one group since nobody draws a corner alone. under ASCII every joint is + and the vertical is |
public sealed record BoxSet(
    string TopLeft, string TopMid, string TopRight,
    string MidLeft, string Cross, string MidRight,
    string BottomLeft, string BottomMid, string BottomRight,
    string Vertical);

//one table, two sets, resolved once at launch and handed down. a render path reading the environment would let two surfaces disagree on the vocabulary
public sealed class GlyphSet
{
    //don't retype it by eye, copy it byte for byte (a miscopied glyph is exactly what a byte census catches late)
    private static readonly string[] AsciiCat =
    [
        @"/l,",
        @"(^,^7",
        @" l  ~\",
        @" UUf_,)/"
    ];

    //the banner cat, the one this table keeps an ASCII twin for. role cats stay in Cats
    private static readonly string[] UnicodeCat =
    [
        "  /l\u3001",
        "\uff08\uff3e\uff64\uff3e\uff17",
        "  l  ~\u30fd",
        "  \u3058\u3057f_,)\u30ce",
    ];

    //ascii purr face, kept as ruled even though the Unicode side moved on (w mouth where Unicode has a space)
    internal const string AsciiFace = "(=^.w.^)>";

    //the header face, ASCII side: AsciiEmpty's eyes with AsciiFace's tail. the Unicode side doesn't mirror this
    internal const string AsciiHeader = "(^. .^)>";

    //ascii wild face: the brackets stand for ears, the mouth is the ASCII w. the Unicode side doesn't match it
    internal const string AsciiWild = ">^.w.^<";

    //the Unicode tail beside this is an integral sign now, the slash doesn't transliterate it
    internal const string AsciiWaiting = "(^._.^)/";

    //the Unicode empty cat has a katakana arm and a closed mouth this one can't show
    internal const string AsciiEmpty = "(^. .^)/";

    //every face character except the tail must be single-width in all five terminal fonts, or fallback draws it unevenly. a browser can't check this
    public static GlyphSet Unicode { get; } = new()
    {
        Ok = "\u2713", Bad = "\u2717", Warn = "\u26a0", Vision = "\u25c8", Prompt = "\u276f",
        Loaded = "\u25cf", Rule = "\u2500", Dot = "\u00b7", OtherBuild = "\u25b3", NotRun = "\u2013",
        Cat = UnicodeCat, Face = "(^\u00b7 \u00b7^)\u27c6",
        Waiting = "(^\u00b7 \u00b7^)\u222b", Empty = "(^\u00b7_\u00b7^)\uff89",
        Header = "(^\u00b7 \u00b7^)\u2cca",
        Ellipsis = "\u2026", MidEllipsis = "\u22ef",
        Down = "\u2193", Up = "\u2191", Right = "\u2192", Left = "\u2190", Continue = "\u21b3",
        DownKey = "\u2193", UpKey = "\u2191", ArrowsKey = "\u2191\u2193",
        Bar = "\u258f", Sharp = "\u266f", Angle = "\u203a", Approx = "\u2248", Elbow = "\u23bf",
        HeavyRule = "\u2501", Caret = "\u25be", Triangle = "\u25b8", Bullet = "\u2022",
        AtMost = "\u2264", Sum = "\u03a3", Ring = "\u25cb", Times = "\u00d7", Range = "\u2013",
        Box = new("\u250c", "\u252c", "\u2510", "\u251c", "\u253c", "\u2524",
                  "\u2514", "\u2534", "\u2518", "\u2502"),
        Wild = "\u2265^\u2022-\u2022^\u2264",
    };

    //the transliteration for hosts without glyphs. every character a DOS console always had, a fallback needing a font is no fallback
    public static GlyphSet Ascii { get; } = new()
    {
        Ok = "+", Bad = "x", Warn = "!", Vision = "*", Prompt = ">",
        Loaded = "o", Rule = "-", Dot = ".", OtherBuild = "^", NotRun = "-",
        Cat = AsciiCat, Face = AsciiFace, Waiting = AsciiWaiting, Empty = AsciiEmpty,
        Header = AsciiHeader,
        Ellipsis = "...", MidEllipsis = "...",
        Down = "v", Up = "^", Right = "->", Left = "<-", Continue = "->",
        DownKey = "Down", UpKey = "Up", ArrowsKey = "arrows",
        Bar = "|", Sharp = "#", Angle = ">", Approx = "~", Elbow = "`-",
        HeavyRule = "=", Caret = "v", Triangle = ">", Bullet = "*",
        AtMost = "<=", Sum = "sum", Ring = "o", Times = "x", Range = "-",
        Box = new("+", "+", "+", "+", "+", "+", "+", "+", "+", "|"),
        Wild = AsciiWild,
    };

    //satisfied, verified, done
    public required string Ok { get; init; }

    //something gatto tried didn't work
    public required string Bad { get; init; }

    //gatto couldn't do it and measured nothing
    public required string Warn { get; init; }

    //the model's repo has an mmproj
    public required string Vision { get; init; }

    //the cursor mark, doubles as the REPL prompt
    public required string Prompt { get; init; }

    //the weights the session holds
    public required string Loaded { get; init; }

    //a horizontal rule
    public required string Rule { get; init; }

    //the middle dot joining facts on one row
    public required string Dot { get; init; }

    //the pinned llama.cpp build can't load this architecture
    public required string OtherBuild { get; init; }

    //a task nobody asked for. this is an EN dash, em dashes must never appear on screen
    public required string NotRun { get; init; }

    //the banner cat, one string per line
    public required IReadOnlyList<string> Cat { get; init; }

    //the one-line purr face, the tail is what tells it from the other faces. the body is shared by the whole cat family
    public required string Face { get; init; }

    //the face the ticker shows while a prompt is up
    public required string Waiting { get; init; }

    //the cat a screen shows when it has nothing to show, the only one that also closes its mouth
    public required string Empty { get; init; }

    //the face that opens a command and shows in the wizard title while nothing runs. it shares the purr face's body, only the last cell differs
    public required string Header { get; init; }

    //the face wild mode shows, the one face that doesn't share the body. the OFF arm shows no face
    public required string Wild { get; init; }

    //everything here is a required init member, one style. members stay separate even when their twins overlap, folding them would change the Unicode drawing

    //the ellipsis that ends a truncated sentence. its ascii twin is three cells, so rows using it are width-checked
    public required string Ellipsis { get; init; }

    //the ellipsis marking omitted lines, not truncated text. same twin, different Unicode glyph, hence its own member
    public required string MidEllipsis { get; init; }

    //tokens coming down from the model
    public required string Down { get; init; }

    //tokens going up, and the history key
    public required string Up { get; init; }

    //the down arrow as a key the user presses. in key hints the ascii twin is a letter the user will type, so key members spell words
    public required string DownKey { get; init; }

    //the up arrow key, same reason as DownKey. the caret is worse, no keyboard has that character
    public required string UpKey { get; init; }

    //both arrows as one key hint. the ascii twin is several cells wider, so that hint sheds first on narrow consoles
    public required string ArrowsKey { get; init; }

    //a move to the right, or a change from one value to another
    public required string Right { get; init; }

    //a move back to the left
    public required string Left { get; init; }

    //the continuation arrow that hangs a follow-on row under its parent
    public required string Continue { get; init; }

    //the thin bar beside a quoted block
    public required string Bar { get; init; }

    //gatto's own transcript rows, the ones gatto writes rather than the model
    public required string Sharp { get; init; }

    //the angle that points at a choice on a row
    public required string Angle { get; init; }

    //a figure that is estimated rather than measured
    public required string Approx { get; init; }

    //the corner under a tool call, where its result hangs
    public required string Elbow { get; init; }

    //the filled half of a progress bar, Rule is the empty half. the two must stay distinguishable at one cell
    public required string HeavyRule { get; init; }

    //an open block that a key would collapse
    public required string Caret { get; init; }

    //a closed block that a key would open
    public required string Triangle { get; init; }

    //a list item in rendered markdown
    public required string Bullet { get; init; }

    //at most, in a count the user is being asked to approve
    public required string AtMost { get; init; }

    //the running total of a turn's work
    public required string Sum { get; init; }

    //a tool call, Loaded's filled circle is for prose
    public required string Ring { get; init; }

    //a close mark, on something the user can dismiss
    public required string Times { get; init; }

    //the separator inside a range. its own member even though it draws identically to NotRun, typography and status are different things
    public required string Range { get; init; }

    //the table frame, see BoxSet
    public required BoxSet Box { get; init; }


    //on auto, Unicode if WT_SESSION or TERM_PROGRAM is set, ascii for legacy conhost which sets neither. it's a heuristic, the override serves both directions
    public static GlyphSet Resolve(GlyphMode mode, IReadOnlyDictionary<string, string?> env) =>
        mode switch
        {
            GlyphMode.Unicode => Unicode,
            GlyphMode.Ascii => Ascii,
            _ => IsLegacyConsole(env) ? Ascii : Unicode,
        };

    //one detection for the resolver and the install nudge to share. it answers about the host even when unicode was forced
    public static bool IsLegacyConsole(IReadOnlyDictionary<string, string?> env) =>
        !Set(env, "WT_SESSION") && !Set(env, "TERM_PROGRAM");

    //the host's environment read one way, the dictionary is case-insensitive on purpose. a differently-spelled variable mustn't read as unset
    public static IReadOnlyDictionary<string, string?> HostEnv() =>
        System.Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string, StringComparer.OrdinalIgnoreCase);

    private static bool Set(IReadOnlyDictionary<string, string?> env, string key) =>
        env.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);
}
