using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//the /tools panel's rows and detail, pure in the tool list so a test reads them without a session
internal static class ToolsPanel
{
    //gatto's one line per built-in, shipped text and not the model's description
    private static readonly Dictionary<string, string> Glosses = new(StringComparer.Ordinal)
    {
        ["read_file"] = "read a file",
        ["write_file"] = "write a file whole",
        ["edit_file"] = "replace one exact string in a file",
        ["glob"] = "find files by pattern",
        ["grep"] = "search file contents",
        ["shell"] = "run a PowerShell command",
        ["memory_write"] = "save or retire a project fact",
        ["recall_memory"] = "search past sessions and memory",
        ["run_agent"] = "run a role on a task",
    };

    //one row per tool in the order the plain render lists them, an extension's file dim after the gloss
    public static IReadOnlyList<SelectOption> Rows(IReadOnlyList<ToolInfo> tools) =>
        [.. CommandText.Ordered(tools).Select(t => new SelectOption(t.Name, Description: Gloss(t), Key: t.Name,
            Trail: t.Origin is { } origin ? [new SelectRun(origin + ".csx", Theme.Dim)] : null))];

    //a built-in the table does not name reads its own first sentence, so no row draws an empty gloss
    public static string Gloss(ToolInfo t) =>
        t.Origin is null && Glosses.TryGetValue(t.Name, out var line) ? line : FirstSentence(t.Description);

    //the first line's first sentence: a period ends it only at the line's end or before whitespace, and a trailing colon goes
    public static string FirstSentence(string description)
    {
        var text = description.Replace("\r\n", "\n").Trim();
        var nl = text.IndexOf('\n');
        var line = TermText.Sanitize(nl < 0 ? text : text[..nl]).Trim();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '.' || (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1]))) continue;
            line = line[..(i + 1)];
            break;
        }
        return line.EndsWith(':') ? line[..^1].TrimEnd() : line;
    }

    //the model's text line for line, wrapped at the width, then the policy lines of the tool's own extension dim beneath
    public static IReadOnlyList<DetailRow> Detail(ToolInfo t, IReadOnlyList<(string Extension, string Line)> policy)
    {
        var rows = new List<DetailRow>();
        foreach (var line in t.Description.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            rows.Add(line.Length == 0 ? new DetailRow("") : new DetailRow(line, Wrap: true));
        if (t.Origin is not { } origin) return rows;
        var own = policy.Where(p => string.Equals(p.Extension, origin, StringComparison.Ordinal)).ToList();
        if (own.Count == 0) return rows;
        rows.Add(new DetailRow(""));
        foreach (var (_, line) in own) rows.Add(new DetailRow(line, Dim: true, Wrap: true));
        return rows;
    }
}
