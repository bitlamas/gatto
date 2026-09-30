using System.Globalization;
using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl;

//the rows an edit's permission prompt shows, at most the cap, in the file's order, every change keeping its first added row
internal static class ChangeRows
{
    public const int Cap = 14;

    //the rows at any cap from one diff, each cap built once, the prompt asks again on every paint of a short window
    public static Func<int, List<DetailRow>> At(string oldS, string newS, EditView? view, GlyphSet g)
    {
        IReadOnlyList<ToolBodyRow>? list = null;
        var byCap = new Dictionary<int, List<DetailRow>>();
        return cap =>
        {
            lock (byCap)
            {
                if (byCap.TryGetValue(cap, out var built)) return built;
                if (list is null)
                {
                    var (o, n) = EditLocate.Lines(oldS, newS, view);
                    list = ToolBody.Edit(LineDiff.Of(o, n), view);
                }
                return byCap[cap] = Rows(list, cap, g);
            }
        };
    }

    private static List<DetailRow> Rows(IReadOnlyList<ToolBodyRow> list, int cap, GlyphSet g)
    {
        var chosen = Choose(list, cap, out var hiddenAdded, out var hiddenRemoved);
        var w = chosen.Where(i => i is int k && list[k].Number is not null).Select(i => list[i!.Value].Number!.Value.ToString(CultureInfo.InvariantCulture).Length).DefaultIfEmpty(0).Max();
        var rows = new List<DetailRow>(cap);
        foreach (var i in chosen)
        {
            if (i is not int k) { rows.Add(new DetailRow(g.MidEllipsis, Gutter: "", Dim: true)); continue; }
            var r = list[k];
            var gutter = w > 0 ? (r.Number is int num ? num.ToString(CultureInfo.InvariantCulture) : "").PadLeft(w) + " " : "";
            var kind = r.Kind switch { BodyKind.Added => DetailKind.Added, BodyKind.Removed => DetailKind.Removed, _ => DetailKind.Same };
            rows.Add(new DetailRow(r.Text, Gutter: gutter, Kind: kind));
        }
        var hidden = new List<string>();
        if (hiddenAdded > 0) hidden.Add($"+{hiddenAdded} added");
        if (hiddenRemoved > 0) hidden.Add($"+{hiddenRemoved} removed");
        if (hidden.Count > 0) rows.Add(new DetailRow($"{g.Ellipsis} {string.Join(", ", hidden)}", Gutter: "", Dim: true));
        return rows;
    }

    //the kept rows by index in the list's order, null where a gap row parts two runs. a run grows only by a row adjacent to it, and every addition is costed with its gap and count rows
    public static List<int?> Choose(IReadOnlyList<ToolBodyRow> list, int cap, out int hiddenAdded, out int hiddenRemoved)
    {
        bool Changed(int i) => list[i].Kind is BodyKind.Added or BodyKind.Removed;
        var kept = new SortedSet<int>();
        if (list.Count <= cap)
            kept.UnionWith(Enumerable.Range(0, list.Count));
        else
        {
            var changes = new List<(int Start, int End)>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!Changed(i) || (i > 0 && Changed(i - 1))) continue;
                var end = i;
                while (end + 1 < list.Count && Changed(end + 1)) end++;
                changes.Add((i, end));
            }
            int Cost()
            {
                var runs = kept.Count == 0 ? 0 : 1 + kept.Zip(kept.Skip(1), (a, b) => b - a > 1 ? 1 : 0).Sum();
                var left = Enumerable.Range(0, list.Count).Any(i => Changed(i) && !kept.Contains(i));
                return kept.Count + Math.Max(0, runs - 1) + (left ? 1 : 0);
            }
            bool TryAdd(int i)
            {
                if (i < 0 || i >= list.Count || !kept.Add(i)) return false;
                if (Cost() <= cap) return true;
                kept.Remove(i);
                return false;
            }
            foreach (var (s, e) in changes)
                TryAdd(Enumerable.Range(s, e - s + 1).FirstOrDefault(k => list[k].Kind == BodyKind.Added, s));
            //grow each change's kept run by its own changed rows, a pass to the front then a pass to the back, until neither adds a row
            bool grew;
            do
            {
                grew = false;
                foreach (var (s, e) in changes)
                    if (kept.GetViewBetween(s, e) is { Count: > 0 } run && run.Min - 1 >= s) grew |= TryAdd(run.Min - 1);
                foreach (var (s, e) in changes)
                    if (kept.GetViewBetween(s, e) is { Count: > 0 } run && run.Max + 1 <= e) grew |= TryAdd(run.Max + 1);
            } while (grew);
            //then the rows that stay, nearest to a change first, by the same passes over every kept run
            do
            {
                grew = false;
                foreach (var start in Runs(kept).Select(r => r.Start).ToList())
                    if (start - 1 >= 0 && !Changed(start - 1)) grew |= TryAdd(start - 1);
                foreach (var end in Runs(kept).Select(r => r.End).ToList())
                    if (end + 1 < list.Count && !Changed(end + 1)) grew |= TryAdd(end + 1);
            } while (grew);
        }
        hiddenAdded = Enumerable.Range(0, list.Count).Count(i => list[i].Kind == BodyKind.Added && !kept.Contains(i));
        hiddenRemoved = Enumerable.Range(0, list.Count).Count(i => list[i].Kind == BodyKind.Removed && !kept.Contains(i));
        var result = new List<int?>(cap);
        int? prev = null;
        foreach (var i in kept)
        {
            if (prev is int p && i - p > 1) result.Add(null);
            result.Add(i);
            prev = i;
        }
        return result;
    }

    private static IEnumerable<(int Start, int End)> Runs(SortedSet<int> kept)
    {
        int? start = null, last = null;
        foreach (var i in kept)
        {
            if (last is int l && i - l > 1) { yield return (start!.Value, l); start = i; }
            start ??= i;
            last = i;
        }
        if (start is int s && last is int e) yield return (s, e);
    }
}
