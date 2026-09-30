using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gatto.Core.Tools;

public enum BodyKind { Plain, Numbered, Heading, Note, Added, Removed }

public enum BodyInk { Text, Heading, Note, Err }

//one line of a tool's result as the open block shows it, with the index of the path row a match sits under or -1
public sealed record ToolBodyRow(string Text, BodyKind Kind, BodyInk Ink, int? Number = null, int Heading = -1);

//a tool's own text read back into the rows of its open block, for display only, so the model's text never changes
public static class ToolBody
{
    private static readonly Regex ReadGloss = new(@"^(\d+) lines?( \(truncated\))?$", RegexOptions.CultureInvariant);
    private static readonly Regex CapNote = new(@"^\[capped at .+\]$", RegexOptions.CultureInvariant);

    //the first line a read shows, since the tool skips offset minus 1 lines and an offset of 0 or below reads from line 1
    public static int StartOf(string toolName, string argsJson)
    {
        if (toolName != "read_file") return 1;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("offset", out var o)
                && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var n) ? Math.Max(1, n) : 1;
        }
        catch (JsonException) { return 1; }
    }

    //one row per line, a last cap line is a note
    public static IReadOnlyList<ToolBodyRow> Plain(string text, bool error = false)
    {
        if (text.Length == 0) return Array.Empty<ToolBodyRow>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var ink = error ? BodyInk.Err : BodyInk.Text;
        var rows = lines.Select(l => new ToolBodyRow(l, BodyKind.Plain, ink)).ToList();
        if (CapNote.IsMatch(lines[^1])) rows[^1] = new ToolBodyRow(lines[^1], BodyKind.Note, BodyInk.Note);
        return rows;
    }

    //null when the text does not have the line count the gloss says, so a hook's text is never labelled with file line numbers
    public static IReadOnlyList<ToolBodyRow>? ReadFile(string text, string? gloss, int start)
    {
        if (gloss is null || ReadGloss.Match(gloss) is not { Success: true } m
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return null;
        if (n == 0) return text.Length == 0 ? Array.Empty<ToolBodyRow>() : null;
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var truncated = m.Groups[2].Success;
        if (truncated && (lines.Count != n + 1 || lines[^1] != ToolArgs.TruncatedMarker)) return null;
        if (!truncated && lines.Count != n) return null;
        var body = lines.Take(n).ToList();
        if (!truncated && body[^1].Length == 0) body.RemoveAt(body.Count - 1);   //the empty line after a final newline
        var rows = body.Select((l, i) => new ToolBodyRow(l, BodyKind.Numbered, BodyInk.Text, start + i)).ToList();
        if (truncated) rows.Add(new ToolBodyRow(lines[^1], BodyKind.Note, BodyInk.Note));
        return rows;
    }

    //the neighbours before, the diff's rows, the neighbours after, numbered by the file when a view gives where the edit starts
    public static IReadOnlyList<ToolBodyRow> Edit(EditDiff diff, EditView? view)
    {
        var rows = new List<ToolBodyRow>();
        var at = view?.Start - 1;
        if (view is not null)
            for (var i = 0; i < view.Before.Count; i++)
                rows.Add(new ToolBodyRow(view.Before[i], BodyKind.Numbered, BodyInk.Text, view.Start - view.Before.Count + i));
        foreach (var r in diff.Rows)
            rows.Add(r.Op switch
            {
                DiffOp.Removed => new ToolBodyRow(r.Text, BodyKind.Removed, BodyInk.Text, at + r.OldLine),
                DiffOp.Added => new ToolBodyRow(r.Text, BodyKind.Added, BodyInk.Text, at + r.NewLine),
                _ => new ToolBodyRow(r.Text, BodyKind.Numbered, BodyInk.Text, at + r.NewLine),
            });
        if (view is not null)
        {
            var next = view.Start + diff.Rows.Count(r => r.Op != DiffOp.Removed);
            for (var i = 0; i < view.After.Count; i++)
                rows.Add(new ToolBodyRow(view.After[i], BodyKind.Numbered, BodyInk.Text, next + i));
        }
        return rows;
    }

    //a numbered row per line of the content, a final newline ending its line rather than starting an empty one
    public static IReadOnlyList<ToolBodyRow> Write(string content)
    {
        if (content.Length == 0) return Array.Empty<ToolBodyRow>();
        var lines = content.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        if (content.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines.Select((l, i) => new ToolBodyRow(l, BodyKind.Numbered, BodyInk.Text, i + 1)).ToList();
    }

    //where the window rests before the user moves it, the first change that adds a row centred in the height, clamped to the last position
    public static int RestTop(IReadOnlyList<ToolBodyRow> body, int height)
    {
        static bool Changed(ToolBodyRow r) => r.Kind is BodyKind.Added or BodyKind.Removed;
        int? top = null;
        int? firstRemoved = null;
        for (var i = 0; i < body.Count && top is null; i++)
        {
            if (!Changed(body[i]) || (i > 0 && Changed(body[i - 1]))) continue;
            var end = i;
            while (end + 1 < body.Count && Changed(body[end + 1])) end++;
            firstRemoved ??= i;
            var firstAdded = Enumerable.Range(i, end - i + 1).FirstOrDefault(k => body[k].Kind == BodyKind.Added, -1);
            if (firstAdded < 0) continue;
            var size = end - i + 1;
            top = size >= height ? firstAdded : i - (height - size) / 2;
        }
        return Math.Clamp(top ?? firstRemoved ?? 0, 0, Math.Max(0, body.Count - height));
    }

    //a heading per run of one path, a numbered row per match under it, then the cap notes
    public static IReadOnlyList<ToolBodyRow> Grep(GrepParse parse)
    {
        var rows = new List<ToolBodyRow>();
        string? path = null;
        var heading = -1;
        foreach (var match in parse.Matches)
        {
            if (match.Path != path)
            {
                path = match.Path;
                heading = rows.Count;
                rows.Add(new ToolBodyRow(path, BodyKind.Heading, BodyInk.Heading));
            }
            rows.Add(new ToolBodyRow(match.Text.TrimStart(), BodyKind.Numbered, BodyInk.Text, match.Line, heading));
        }
        if (rows.Count == 0) return rows;
        foreach (var note in parse.Notes) rows.Add(new ToolBodyRow(note, BodyKind.Note, BodyInk.Note));
        return rows;
    }
}
