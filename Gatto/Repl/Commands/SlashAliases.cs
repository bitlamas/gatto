namespace Gatto.Repl.Commands;

//commands typed from muscle memory that stand in for a declared one, resolved before the gate, so no alias row reaches /help, the ghost or completion
internal static class SlashAliases
{
    //alias then command, each pair resolved to the declared command's own name
    public static readonly (string Alias, string Command)[] All =
    [
        ("/exit", "/quit"),
        ("/clear", "/new"),
        ("/rename", "/name"),
    ];

    //the alias names, so an extension registering one is refused at load as one registering the command is
    public static readonly string[] Names = [.. All.Select(p => p.Alias)];

    //rewrites an alias with or without an argument to its command, anything else comes back as the same string
    public static string Resolve(string input)
    {
        var trimmed = input.Trim();
        foreach (var (alias, command) in All)
            if (SlashGate.TryMatchCommand(trimmed, alias, out var arg))
                return arg.Length == 0 ? command : command + " " + arg;
        return input;
    }
}
