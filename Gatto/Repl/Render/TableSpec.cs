using System.Text;
using System.Text.RegularExpressions;

namespace Gatto.Repl.Render;

public enum ColumnAlign { Left, Center, Right }

//a parsed table, its body rows already repaired to the header's cell count, short rows padded and extras dropped
public sealed record TableSpec(
    IReadOnlyList<string> Headers,
    IReadOnlyList<ColumnAlign> Alignments,
    IReadOnlyList<IReadOnlyList<string>> Rows);

//markdown table parsing only, no theme, no width, no state
public static partial class TableParse
{
    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex DelimiterCell();

    //split a row on unescaped '|', the leading and trailing pipes are optional and \| counts as text
    public static IReadOnlyList<string> SplitCells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith(@"\|", StringComparison.Ordinal)) t = t[..^1];

        var cells = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append('|'); i++; continue; }
            if (t[i] == '|') { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(t[i]);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    //a line is row shaped when it holds an unescaped '|' and is not a fence marker. the scan cannot see the fence, so a fence opener would invert fence parity
    public static bool IsRowShaped(string line)
    {
        if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) return false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '|') return true;
        }
        return false;
    }

    private static ColumnAlign AlignOf(string cell)
    {
        var left = cell.StartsWith(':');
        var right = cell.EndsWith(':');
        if (left && right) return ColumnAlign.Center;
        return right ? ColumnAlign.Right : ColumnAlign.Left;
    }

    //parse from start, end is exclusive, only when the delimiter row has as many cells as the header. the delimiter identifies the table, so it is never repaired
    public static bool TryParse(IReadOnlyList<string> lines, int start, out TableSpec? spec, out int end)
    {
        spec = null;
        end = start;
        if (start + 1 >= lines.Count) return false;
        if (!IsRowShaped(lines[start]) || !IsRowShaped(lines[start + 1])) return false;

        var headers = SplitCells(lines[start]);
        var delims = SplitCells(lines[start + 1]);
        if (delims.Count != headers.Count) return false;
        foreach (var d in delims) if (!DelimiterCell().IsMatch(d)) return false;

        var aligns = delims.Select(AlignOf).ToList();
        var rows = new List<IReadOnlyList<string>>();
        var i = start + 2;
        for (; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0 || !IsRowShaped(lines[i])) break;
            rows.Add(Repair(SplitCells(lines[i]), headers.Count));
        }

        spec = new TableSpec(headers, aligns, rows);
        end = i;
        return true;
    }

    //pad a short row with empty cells and drop the cells beyond the header count
    private static IReadOnlyList<string> Repair(IReadOnlyList<string> cells, int n)
    {
        if (cells.Count == n) return cells;
        var repaired = new List<string>(n);
        for (var i = 0; i < n; i++) repaired.Add(i < cells.Count ? cells[i] : "");
        return repaired;
    }

    //every table region in the line list, as start and end pairs in order, end exclusive
    public static IReadOnlyList<(int Start, int End)> Regions(IReadOnlyList<string> lines)
    {
        var regions = new List<(int, int)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryParse(lines, i, out _, out var end)) { regions.Add((i, end)); i = end - 1; }
        }
        return regions;
    }
}
