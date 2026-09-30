namespace Gatto.Terminal;

//the focus actions the alt-screen viewport understands, sent to a global sink before the focus stack so a modal prompt still passes them
public enum FocusKey { Prev, Next, Toggle }

public static class FocusKeys
{
    //the toggle key name, in one place so the footer hint and /help never drift from the keymap
    public const string ToggleKeyName = "Ctrl+R";

    //the focus-move key name, in one place so the hint and /help's table name the same two keys

    //a method, because a const cannot read the glyph set the two readers hold

    //the name comes from the glyph set, so the ASCII set spells it Ctrl+Up/Down and the Unicode set shows the arrows
    public static string MoveKeyNameOf(GlyphSet g) => $"Ctrl+{g.UpKey}/{g.DownKey}";

    //the footer hint, from the one keymap so a change never leaves it out of step with /help. a method, because a const cannot read the glyph set
    public static string HintOf(GlyphSet? glyphs) =>
        HintCore(glyphs ?? GlyphSet.Unicode);

    private static string HintCore(GlyphSet g) =>
        MoveKeyNameOf(g) + " move " + g.Dot + " " + ToggleKeyName + " expand/collapse";

    //maps a keystroke to a focus action, or null. the Alt guard lets an AltGr character reach the composer, and Shift is allowed through on purpose
    public static FocusKey? Of(ConsoleKeyInfo k)
    {
        var ctrl = k.Modifiers.HasFlag(ConsoleModifiers.Control) && !k.Modifiers.HasFlag(ConsoleModifiers.Alt);
        return (k.Key, ctrl) switch
        {
            (ConsoleKey.UpArrow, true) => FocusKey.Prev,
            (ConsoleKey.DownArrow, true) => FocusKey.Next,
            (ConsoleKey.R, true) => FocusKey.Toggle,
            _ => null,
        };
    }
}
