using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//the one place a command's name, args and summary are declared, with the dispatch owning the behaviour and a guard pinning the two
public static class SlashCommands
{
    //the flag says whether a command can reach the model, and the purr hangs off it. it is read from the raw input before /init and /remember rewrite themselves. source names the file that registered a row, null for a built-in
    public sealed record Cmd(string Name, string Args, string Summary, bool ReachesModel = false, bool BypassesQueue = false, string? Source = null);

    //a non-command reaches the model, a command only when its row says so, and a registered row never does
    public static bool ReachesModel(string input, IReadOnlyList<Cmd> known)
    {
        var trimmed = input.Trim();
        foreach (var cmd in known)
            if (SlashGate.TryMatchCommand(trimmed, cmd.Name, out _)) return cmd.ReachesModel;
        //default true so ordinary text purrs, and a command missing its row shows a spinner rather than going silent
        return true;
    }

    //a command that reads state and changes nothing the running turn uses answers mid-turn instead of waiting in the queue. /permissions and /tasks only in their list views
    public static bool BypassesQueue(string input)
    {
        var trimmed = input.Trim();
        foreach (var cmd in All)
            if (SlashGate.TryMatchCommand(trimmed, cmd.Name, out var arg))
                return cmd.Name switch
                {
                    "/permissions" => arg.Trim() is "" or "all",
                    "/tasks" => arg.Trim() == "",
                    _ => cmd.BypassesQueue,
                };
        return false;
    }

    //dispatch branches with no /help row. the set is empty, and it exists so the next exemption is written down and the guard can see it
    public static readonly IReadOnlySet<string> ExemptCommands = new HashSet<string>(StringComparer.Ordinal);

    public static readonly IReadOnlyList<Cmd> All =
    [
        new("/help", "", "this screen", BypassesQueue: true),
        new("/new", "", "reset the conversation"),
        new("/role", "[name]", "show or switch role"),
        new("/name", "[text]", "name this session, on the composer and the tab"),
        //quant is named here so a verb the dispatch accepts is findable in /help. add stays out, since its only outcome is a sentence pointing at gatto model
        new("/model", "[name|quant]", "show or switch the model, or manage its files"),
        //a toggle-only model gets the same verb, with a picker offering just none and on
        new("/effort", "[level]", "show or set thinking effort"),
        //the summary is written by the model, so this command reaches it
        new("/compact", "", "summarize + shrink context", ReachesModel: true),
        //the input becomes an ordinary prompt, so the command falls through to a real turn and reaches the model
        new("/remember", "<text>", "bank a note to project memory, in the model's own words",
            ReachesModel: true),
        //the key name comes from WildKeys, so /help can't drift from the real keymap
        new("/wild", "[always|never]", "no prompts (" + Gatto.Terminal.WildKeys.KeyName + " toggles)"),
        new("/safe", "", "prompts back on"),
        new("/sandbox", "[on]", "show the sandbox state, or turn it on for this process"),
        new("/permissions", "[all|revoke <n>]", "review + revoke standing permission grants"),
        new("/tools", "", "list the tools armed this session", BypassesQueue: true),
        new("/context", "", "show what fills the context window", BypassesQueue: true),
        //the kill acts at once on its own arm, during a turn too, and the list answers in the panel then
        new("/tasks", "[kill <n>]", "list the background tasks, or end one", BypassesQueue: true),
        //acts at once on the composer like a kill, at rest and during a turn, so no bypass flag: that path answers in a panel, this one in a line
        new("/watch", "[folder|off]", "send each new letter in a folder to the model"),
        //like /remember, the input becomes InitPrompt and is dispatched as if typed
        new("/init", "", "survey the project, write GATTO.md", ReachesModel: true),
        new("/quit", "", "exit"),
    ];

    internal static string Left(Cmd c) => (c.Name + " " + c.Args).TrimEnd();

    //the widest name-and-args column plus two spaces, shared with the shortcuts table so /help prints as one grid
    internal static int ColumnWidth(IReadOnlyList<Cmd> rows) => rows.Max(c => UnicodeWidth.Of(Left(c))) + 2;

    //no ANSI, one row per line, padded by cell width and with no trailing newline. a registered row ends with its file after two spaces
    public static string RenderPlain(IReadOnlyList<Cmd> rows)
    {
        var col = ColumnWidth(rows);
        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            var left = Left(rows[i]);
            sb.Append(left).Append(' ', col - UnicodeWidth.Of(left)).Append(rows[i].Summary);
            if (rows[i].Source is { } src) sb.Append("  ").Append(src);
            if (i < rows.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }
}
