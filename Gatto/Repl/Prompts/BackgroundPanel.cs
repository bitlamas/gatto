using System.Globalization;
using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//the watch as the background panel shows it, read once per view
public sealed record WatchView(string Shown, bool Paused, int Count, int Limit, IReadOnlyList<string> Seen);

//the background panel's words and rows, pure over the handles and the watch one view froze, so a label can never land on a neighbour's row
public static class BackgroundPanel
{
    //the line the detail shows for a task that has written nothing
    internal const string NoOutputYet = "no output yet";

    //the watch row's key, beside the shells keyed on their task numbers
    internal const string WatchKey = "watch";

    //the first line of a command, as a row shows it
    internal static string Head(string command) => TermText.Sanitize(command.Split('\n')[0].Trim());

    //one row per handle then the watch row, the state as the label and the command as the column the table cuts
    public static IReadOnlyList<SelectOption> Rows(IReadOnlyList<BackgroundTasks.BackgroundLive> live, WatchView? watch, GlyphSet g)
    {
        var rows = new List<SelectOption>();
        foreach (var t in live)
        {
            var state = t.Gloss is null
                ? $"running {ElapsedText.Of(t.Elapsed)}"
                : $"ended {g.Dot} {t.Gloss}, waiting for the model";
            rows.Add(new SelectOption($"{t.Number}  {state}", Key: t.Number.ToString(CultureInfo.InvariantCulture), Columns: [Head(t.Command)]));
        }
        if (watch is { } w)
            rows.Add(new SelectOption("watch", Key: WatchKey,
                Columns: [w.Paused ? $"paused {g.Dot} {Plural.Of(w.Count, "letter")} since your last message" : w.Shown]));
        return rows;
    }

    //the words after the title's dot: the shells the list shows, then the watch, or the watch alone
    public static string Count(int shells, bool watch, GlyphSet g) =>
        shells == 0 ? "watch" : Plural.Of(shells, "shell") + (watch ? $" {g.Dot} watch" : "");

    //what moves while the panel is open, every runtime, gloss and output length, so the poll repaints only when a view would read differently
    public static string StateKey(IReadOnlyList<BackgroundTasks.BackgroundLive> live) =>
        string.Join("|", live.Select(t => t.Number + " " + ElapsedText.Of(t.Elapsed) + " " + t.Gloss + " " + t.Written));

    //a shell's detail: the whole command, then all it wrote so far dressed as a shell result, which the panel's window scrolls
    public static IReadOnlyList<DetailRow> ShellDetail(BackgroundTasks.BackgroundLive t, GlyphSet g)
    {
        var (stdout, stderr) = t.OutputSoFar();
        return ShellRows(t.Command, stdout, stderr, g);
    }

    //the command wraps so it reads whole, the output is cut at the width as a shell result is
    internal static IReadOnlyList<DetailRow> ShellRows(string command, string stdout, string stderr, GlyphSet g)
    {
        var rows = new List<DetailRow>();
        foreach (var line in command.Split('\n')) rows.Add(new DetailRow(TermText.Sanitize(line.TrimEnd('\r')), Wrap: true));
        var text = ShellTool.Compose(stdout, stderr, 0, pipeHeld: false, null, null);
        var output = text.Length == 0 ? [NoOutputYet] : text.Split('\n').Select(l => TermText.Sanitize(l.TrimEnd('\r'))).ToList();
        for (var i = 0; i < output.Count; i++)
            rows.Add(new DetailRow(output[i], Gutter: i == 0 ? g.Elbow + " " : "  ", Dim: true));
        return rows;
    }

    //the watch's detail: its folder, the count against the limit, and the letters it has handed over by name
    public static IReadOnlyList<DetailRow> WatchDetail(WatchView w, GlyphSet g)
    {
        var rows = new List<DetailRow> { new(w.Shown), new($"{w.Count} of {w.Limit} letters since your last message", Dim: true) };
        foreach (var name in w.Seen) rows.Add(new DetailRow(TermText.Sanitize(name), Gutter: g.Elbow + " ", Dim: true));
        return rows;
    }
}
