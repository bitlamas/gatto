namespace Gatto.Core.Tools;

public enum DiffOp { Same, Removed, Added }

//the line numbers count from 1 inside the diffed text, the caller adds where the text starts in its file
public sealed record DiffRow(DiffOp Op, string Text, int? OldLine, int? NewLine);

//a change is a run of rows that are not the same, with no same row inside it
public sealed record EditDiff(IReadOnlyList<DiffRow> Rows, int Added, int Removed, int Changes, bool Bounded);

//a line diff by longest common subsequence, for display only, removed rows of a change before its added rows
public static class LineDiff
{
    //past this many line pairs the table would cost more memory than a display is worth, so every remaining line is changed
    public const long MaxPairs = 4_000_000;

    public static EditDiff Of(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var head = 0;
        while (head < oldLines.Count && head < newLines.Count && oldLines[head] == newLines[head]) head++;
        var tail = 0;
        while (tail < oldLines.Count - head && tail < newLines.Count - head
            && oldLines[oldLines.Count - 1 - tail] == newLines[newLines.Count - 1 - tail]) tail++;
        int n = oldLines.Count - head - tail, m = newLines.Count - head - tail;
        var bounded = (long)n * m > MaxPairs;

        var ops = new List<DiffOp>(n + m);
        if (bounded)
        {
            ops.AddRange(Enumerable.Repeat(DiffOp.Removed, n));
            ops.AddRange(Enumerable.Repeat(DiffOp.Added, m));
        }
        else
            ops.AddRange(Middle(oldLines, newLines, head, n, m));

        var rows = new List<DiffRow>(head + ops.Count + tail);
        int o = 0, w = 0;
        void Add(DiffOp op)
        {
            rows.Add(op switch
            {
                DiffOp.Same => new DiffRow(op, oldLines[o], o + 1, w + 1),
                DiffOp.Removed => new DiffRow(op, oldLines[o], o + 1, null),
                _ => new DiffRow(op, newLines[w], null, w + 1),
            });
            if (op != DiffOp.Added) o++;
            if (op != DiffOp.Removed) w++;
        }
        for (var i = 0; i < head; i++) Add(DiffOp.Same);
        foreach (var op in ops) Add(op);
        for (var i = 0; i < tail; i++) Add(DiffOp.Same);

        int added = 0, removed = 0, changes = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Op == DiffOp.Added) added++;
            if (rows[i].Op == DiffOp.Removed) removed++;
            if (rows[i].Op != DiffOp.Same && (i == 0 || rows[i - 1].Op == DiffOp.Same)) changes++;
        }
        return new EditDiff(rows, added, removed, changes, bounded);
    }

    //the table and its backtrack over the lines between the common head and tail, each run of changes then ordered removals first
    private static List<DiffOp> Middle(IReadOnlyList<string> a, IReadOnlyList<string> b, int off, int n, int m)
    {
        var t = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                t[i, j] = a[off + i] == b[off + j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        var ops = new List<DiffOp>(n + m);
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[off + x] == b[off + y]) { ops.Add(DiffOp.Same); x++; y++; }
            else if (y >= m || (x < n && t[x + 1, y] >= t[x, y + 1])) { ops.Add(DiffOp.Removed); x++; }
            else { ops.Add(DiffOp.Added); y++; }
        }
        var ordered = new List<DiffOp>(ops.Count);
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i] == DiffOp.Same) { ordered.Add(DiffOp.Same); i++; continue; }
            var j = i;
            while (j < ops.Count && ops[j] != DiffOp.Same) j++;
            var run = ops.GetRange(i, j - i);
            ordered.AddRange(run.Where(op => op == DiffOp.Removed));
            ordered.AddRange(run.Where(op => op == DiffOp.Added));
            i = j;
        }
        return ordered;
    }
}
