using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl;

//the keys a user presses and what each does, the rows of the help panel's shortcuts tab
public static class Shortcuts
{
    //the Keys field is the keystroke as the keymap binds it, and What reads lower case with no full stop, like the command table
    public sealed record Shortcut(string Keys, string What);

    //a method rather than a field, since one row's key name depends on the glyph set. the keymap and the footer hint still name the same keys
    public static IReadOnlyList<Shortcut> AllOf(GlyphSet? glyphs) =>
    [
        //the key names come from FocusKeys, so /help and the footer hint can't disagree about them. the rows are ordered by how likely a user wants them
        new("Alt+V", "attach an image from the clipboard"),
        new(FocusKeys.ToggleKeyName, "expand / collapse thoughts and tools"),
        new(FocusKeys.MoveKeyNameOf(glyphs ?? GlyphSet.Unicode), "move focus between blocks"),
        new("PgUp/PgDn/Home/End", "scroll the conversation"),
    ];
}
