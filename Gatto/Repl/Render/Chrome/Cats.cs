using Gatto.Terminal;
namespace Gatto.Repl.Render.Chrome;

public static class Cats
{
    //the launch banner face: the role file's avatar rows when it carries one, else the default cat, and the glyph set's own cat outside Unicode
    public static string For(IReadOnlyList<string>? avatar, GlyphSet? glyphs) =>
        (glyphs ?? GlyphSet.Unicode) is { } g && !ReferenceEquals(g, GlyphSet.Unicode)
            ? string.Join('\n', g.Cat)
            : avatar is not null ? string.Join('\n', avatar) : Default;

    //the caret face has no space before the ７ where a visor face does, deliberate, don't tidy it into symmetry
    private const string Default = """
          /l、
        （＾､＾７
          l  ~ヽ
          じしf_,)ノ
        """;

    //the one-line face for chrome rows, For is a multi-line banner, and one face serves every role in both sets
    public static string Face(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Face;

    //the face shown while a prompt is up (permission or ask_user), role-agnostic and one line, a method so it can read the glyph set
    public static string WaitingOf(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Waiting;

    //the cat a screen shows when it has nothing to show (empty search, empty shelf, unreachable Hub), transcribed out of the golden
    public static string EmptyOf(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Empty;
}
