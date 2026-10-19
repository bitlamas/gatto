
namespace Gatto.Repl.Commands;

//the gate's verdict for one submitted line. the escape flag keeps the line out of the command chain, and a refusal means nothing is sent
internal readonly record struct SlashGate(string Text, bool Literal, string? Refusal)
{
    internal bool Refused => Refusal is not null;

    //matches a trimmed line against a bare slash command or that command plus an argument, both loops parse every command through here
    internal static bool TryMatchCommand(string trimmed, string name, out string arg)
    {
        if (trimmed == name)
        {
            arg = "";
            return true;
        }
        if (trimmed.Length > name.Length && trimmed[name.Length] == ' '
            && trimmed.StartsWith(name, StringComparison.Ordinal))
        {
            arg = trimmed[name.Length..].Trim();
            return true;
        }
        arg = "";
        return false;
    }


    //single-sourced from the known rows with the matcher the dispatch chain uses, so this and the chain cannot disagree about what is a command
    internal static bool IsSlashCommand(string input, IReadOnlyList<SlashCommands.Cmd> known)
    {
        var trimmed = input.Trim();
        foreach (var cmd in known)
            if (TryMatchCommand(trimmed, cmd.Name, out _)) return true;
        return false;
    }

    //the gate every submitted line passes, a // escape loses one slash and the refusal reads the first token against the declared names
    internal static SlashGate GateSlash(string input, IReadOnlyList<SlashCommands.Cmd> known)
    {
        var trimmed = input.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return new SlashGate(trimmed[1..], Literal: true, null);

        var token = FirstToken(trimmed);
        return Gatto.Core.Commands.CommandNames.IsShaped(token) && !IsDeclaredCommand(token, known)
            ? new SlashGate(input, false, UnknownCommandLine(token))
            : new SlashGate(input, false, null);
    }

    //a colon in this message, a census pins the exact em dash count so a new one would hide behind the bump
    internal static string UnknownCommandLine(string token) =>
        $"{token} is not a command: /help lists them, or start the line with // to send it as text";

    internal static string FirstToken(string trimmed)
    {
        for (var i = 0; i < trimmed.Length; i++)
            if (char.IsWhiteSpace(trimmed[i])) return trimmed[..i];
        return trimmed;
    }

    internal static bool IsDeclaredCommand(string token, IReadOnlyList<SlashCommands.Cmd> known)
    {
        foreach (var cmd in known)
            if (string.Equals(cmd.Name, token, StringComparison.Ordinal)) return true;
        return false;
    }
}
