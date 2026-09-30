using System.Globalization;
using System.Text.RegularExpressions;

namespace Gatto.Core.Tools;

//the shell tool's own result text read back into parts, for display only, so the model's text never changes
public sealed record ShellOutput(IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr, IReadOnlyList<string> Harness)
{
    public static readonly ShellOutput Empty = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    private static readonly Regex ExitLine = new(@"^\(exit code -?\d+\)$", RegexOptions.CultureInvariant);
    private static readonly Regex ExitGloss = new(@"^exit (-?\d+)$", RegexOptions.CultureInvariant);

    public bool StdoutBlank => Stdout.All(l => l.Trim().Length == 0);

    public string? LastStderr => Stderr.LastOrDefault(l => l.Trim().Length > 0);

    //the gloss holds the exit code even when the cap cut the exit line from the text
    public static int? ExitCodeOf(string? gloss) =>
        gloss is not null && ExitGloss.Match(gloss) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            ? code : null;

    public static ShellOutput Parse(string text)
    {
        if (text.Length == 0) return Empty;
        //the tool writes CRLF, so each line loses one trailing CR before anything compares it
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var harness = new List<string>();
        while (lines.Count > 0)
        {
            var last = lines[^1];
            if (ExitLine.IsMatch(last)) { lines.RemoveAt(lines.Count - 1); continue; }
            if (last == ShellTool.PipeHeldNote || last == ToolArgs.TruncatedMarker) { harness.Insert(0, last); lines.RemoveAt(lines.Count - 1); continue; }
            break;
        }
        var at = lines.IndexOf(ShellTool.StderrMarker);
        var stdout = at < 0 ? lines : lines.Take(at).ToList();
        var stderr = at < 0 ? new List<string>() : lines.Skip(at + 1).ToList();
        return new ShellOutput(stdout.Select(ResolveCr).ToList(), stderr.Select(ResolveCr).ToList(), harness);
    }

    //the last piece wins if no earlier one is longer (a redraw), else the CR was stray and the pieces join
    public static string ResolveCr(string line)
    {
        if (line.IndexOf('\r') < 0) return line;
        var pieces = line.Split('\r');
        var last = pieces[^1];
        for (var i = 0; i < pieces.Length - 1; i++)
            if (pieces[i].Length > last.Length) return string.Concat(pieces);
        return last;
    }
}
