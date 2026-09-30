namespace Gatto.Terminal;

//routed to a global sink before the focus stack, so it works at any moment. a toggle doesn't answer a pending prompt
public static class WildKeys
{
    //one name for /help, the /wild description and the keymap, keep them from drifting
    public const string KeyName = "Shift+Tab";

    //shift+Tab with no other modifier, so the terminal's own Ctrl+Shift+Tab and an AltGr composition fall through to the composer
    public static bool Of(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.Tab
        && k.Modifiers.HasFlag(ConsoleModifiers.Shift)
        && !k.Modifiers.HasFlag(ConsoleModifiers.Control)
        && !k.Modifiers.HasFlag(ConsoleModifiers.Alt);
}
