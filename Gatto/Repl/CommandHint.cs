using System;
using System.Collections.Generic;

namespace Gatto.Repl;

//the candidates, the dim text and what Tab inserts, one answer for both the ghost and the key so they cannot disagree
public readonly record struct CommandCompletion(string Token, IReadOnlyList<SlashCommands.Cmd> Candidates)
{
    public bool Any => Candidates.Count > 0;

    //fold an unbounded cycle counter onto the candidate list, negative values too so a reverse key stays in range
    public int Index(int cycle) =>
        Candidates.Count == 0 ? 0 : ((cycle % Candidates.Count) + Candidates.Count) % Candidates.Count;

    //the dim remainder for this candidate, the rest of the name then its Args, taken from the table so help and the ghost agree
    public string Hint(int cycle)
    {
        if (!Any) return "";
        var cmd = Candidates[Index(cycle)];
        var rest = cmd.Name[Token.Length..];
        return cmd.Args.Length == 0 ? rest : rest + " " + cmd.Args;
    }

    //what Tab inserts, the name alone (parameters are shown, not typed). it answers empty when nothing completes, and callers ask Any first
    public string Completion(int cycle) => Any ? Candidates[Index(cycle)].Name : "";
}

//the pure half of slash-command completion, it answers from the line and the table and holds nothing
public static class CommandHint
{
    private static readonly CommandCompletion None = new("", Array.Empty<SlashCommands.Cmd>());

    //what completes the buffer, only while the composer is typing the command name, and a bare / offers the whole table
    public static CommandCompletion For(IReadOnlyList<string>? lines, int cursorLine, int cursorCol)
    {
        if (lines is not { Count: 1 }) return None;
        var line = lines[0];
        if (cursorLine != 0 || cursorCol != line.Length) return None;
        if (line.Length == 0 || line[0] != '/') return None;

        //there is deliberately no "past the name" check, no command name contains whitespace so the loop already answers nothing
        var hits = new List<SlashCommands.Cmd>();
        foreach (var cmd in SlashCommands.All)
            if (cmd.Name.StartsWith(line, StringComparison.Ordinal)) hits.Add(cmd);
        return hits.Count == 0 ? None : new CommandCompletion(line, hits);
    }
}
