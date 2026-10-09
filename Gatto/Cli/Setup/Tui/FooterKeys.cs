using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//a footer word's key string as the key a press on it acts as, so every mouse press on the footer reuses a key path. the arrows name a pair and act as neither
internal static class FooterKeys
{
    internal static ConsoleKeyInfo? KeyOf(string footerKey) => footerKey switch
    {
        "Esc" => new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false),
        "Enter" => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        "Tab" => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
        "Space" => new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false),
        { Length: 1 } one => new ConsoleKeyInfo(one[0],
            char.IsAsciiLetter(one[0]) ? ConsoleKey.A + (char.ToLowerInvariant(one[0]) - 'a') : 0, false, false, false),
        _ => null,
    };
}
