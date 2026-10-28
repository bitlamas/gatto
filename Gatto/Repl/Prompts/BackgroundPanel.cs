using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//the watch as the background panel shows it, read once per open
public sealed record WatchView(string Shown, bool Paused, int Count, int Limit, IReadOnlyList<string> Seen);

//the background panel's words and rows, pure over the handles and the watch one open froze, so a label can never land on a neighbour's row
public static class BackgroundPanel
{
    //a command head is never cut below this many cells, a shorter head says nothing
    internal const int HeadCells = 8;

    //the indent the widget puts before an unnumbered label, which a head's cut must budget for
    private const int LabelColumn = 2;

    //the line the detail shows for a task that has written nothing
    internal const string NoOutputYet = "no output yet";

    //the first line of a command, as a row shows it
    internal static string Head(string command) => TermText.Sanitize(command.Split('\n')[0].Trim());

    //one row per handle then the watch row, each runtime and gloss read live off its handle
    public static IReadOnlyList<string> Labels(IReadOnlyList<BackgroundTasks.BackgroundLive> live, WatchView? watch, int width, GlyphSet g)
    {
        var rows = new List<string>();
        foreach (var t in live)
        {
            var state = t.Gloss is null
                ? $"running {ElapsedText.Of(t.Elapsed)}"
                : $"ended {g.Dot} {t.Gloss}, waiting for the model";
            var lead = $"{t.Number}  {state}  ";
            var room = width > 0 ? Math.Max(HeadCells, width - LabelColumn - UnicodeWidth.Of(lead)) : int.MaxValue;
            var head = Head(t.Command);
            rows.Add(lead + (UnicodeWidth.Of(head) > room ? TermText.TruncateCells(head, room, glyphs: g) : head));
        }
        if (watch is { } w)
            rows.Add(w.Paused ? $"watch paused {g.Dot} {Plural.Of(w.Count, "letter")} since your last message" : $"watch  {w.Shown}");
        return rows;
    }

    //what moves while the panel is open, every runtime and every gloss, so the poll repaints only when a row would read differently
    public static string StateKey(IReadOnlyList<BackgroundTasks.BackgroundLive> live) =>
        string.Join("|", live.Select(t => t.Number + " " + ElapsedText.Of(t.Elapsed) + " " + t.Gloss));

    //a shell's detail: the whole command, then what it wrote so far dressed as a shell result, the last rows that fit the cap
    public static IReadOnlyList<DetailRow> ShellDetail(BackgroundTasks.BackgroundLive t, int cap, GlyphSet g)
    {
        var rows = new List<DetailRow>();
        foreach (var line in t.Command.Split('\n')) rows.Add(new DetailRow(TermText.Sanitize(line.TrimEnd('\r'))));
        var (stdout, stderr) = t.OutputSoFar();
        var text = ShellTool.Compose(stdout, stderr, 0, pipeHeld: false, null, null);
        var output = text.Length == 0 ? [NoOutputYet] : text.Split('\n').Select(l => TermText.Sanitize(l.TrimEnd('\r'))).ToList();
        var room = Math.Max(1, cap - rows.Count);
        var shown = output.Skip(Math.Max(0, output.Count - room)).ToList();
        for (var i = 0; i < shown.Count; i++)
            rows.Add(new DetailRow(shown[i], Gutter: i == 0 ? g.Elbow + " " : "  ", Dim: true));
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
