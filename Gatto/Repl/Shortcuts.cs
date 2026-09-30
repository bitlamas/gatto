using System.Text;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl;

//the rows are padded to their own column and never wrapped, since the transcript re-renders from what it stored. dim, unlike the command table's names
public static class Shortcuts
{
    //the Keys field is the keystroke as the keymap binds it, and What reads lower case with no full stop, like the command table
    public sealed record Shortcut(string Keys, string What);

    //the key names come from FocusKeys, so /help and the footer hint can't disagree about them. the rows are ordered by how likely a user wants them

    //a method rather than a field, since one row's key name depends on the glyph set. the keymap and the footer hint still name the same keys
    public static IReadOnlyList<Shortcut> AllOf(GlyphSet? glyphs) =>
    [
        new("Alt+V", "attach an image from the clipboard"),
        new(FocusKeys.ToggleKeyName, "expand / collapse thoughts and tools"),
        new(FocusKeys.MoveKeyNameOf(glyphs ?? GlyphSet.Unicode), "move focus between blocks"),
        new("PgUp/PgDn/Home/End", "scroll the conversation"),
    ];

    //the key column is at least as wide as the command table's, so /help reads as one grid. it's measured in cells, since a key name may hold arrows
    private static int ColumnWidth(GlyphSet g) =>
        Math.Max(SlashCommands.ColumnWidth(), AllOf(g).Max(s => UnicodeWidth.Of(s.Keys)) + 2);

    //all dim, one aligned row per shortcut under a heading, with no trailing newline since the caller joins it onto the command table
    public static string RenderRich(Theme theme, GlyphSet? glyphs)
    {
        //resolve the glyph set once, so the column and the rows measured against it come from the same set
        var g = glyphs ?? GlyphSet.Unicode;
        var col = ColumnWidth(g);
        var sb = new StringBuilder();
        sb.Append(theme.Paint("shortcuts", Theme.Dim)).Append('\n');
        foreach (var s in AllOf(g))
        {
            var pad = new string(' ', col - UnicodeWidth.Of(s.Keys));
            sb.Append('\n').Append(theme.Paint(s.Keys + pad + s.What, Theme.Dim));
        }
        return sb.ToString();
    }
}
