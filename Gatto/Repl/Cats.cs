using Gatto.Terminal;
namespace Gatto.Repl;

public static class Cats
{
    //the launch banner cat, one body for all roles (the coder's eyes, the oracle's hat, six lines) and one ASCII cat
    public static string For(string role, GlyphSet? glyphs) =>
        (glyphs ?? GlyphSet.Unicode) is { } g && !ReferenceEquals(g, GlyphSet.Unicode)
            ? string.Join('\n', g.Cat)
            : ForUnicode(role);

    private static string ForUnicode(string role) => role switch
    {
        //visor eyes, the coder is the one with shades at the terminal
        "coder" => """
              /l、
            （▀､▀ ７
              l  ~ヽ
              じしf_,)ノ
            """,
        //the pointed hat replaces the ear line, so the face sits on the same row as the other roles
        "oracle" => """
                ▲
               ╱☆╲
              ⌒‾‾‾⌒
            （＾､＾７
              l  ~ヽ
              じしf_,)ノ
            """,
        //the caret face has no space before the ７ and the coder's does, deliberate, don't tidy it into symmetry
        _ => """
              /l、
            （＾､＾７
              l  ~ヽ
              じしf_,)ノ
            """,
    };

    //the one-line face for chrome rows, For is a multi-line banner, and one face serves every role in both sets
    public static string Face(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Face;

    //the face shown while a prompt is up (permission or ask_user), role-agnostic and one line, a method so it can read the glyph set
    public static string WaitingOf(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Waiting;

    //the cat a screen shows when it has nothing to show (empty search, empty shelf, unreachable Hub), transcribed out of the golden
    public static string EmptyOf(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Empty;
}
