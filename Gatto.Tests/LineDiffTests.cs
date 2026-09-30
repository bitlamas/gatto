using Gatto.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Gatto.Tests;

public sealed class LineDiffTests(ITestOutputHelper output)
{
    private static string[] L(params string[] lines) => lines;

    private static string Ops(EditDiff d) =>
        string.Join(" ", d.Rows.Select(r => (r.Op switch { DiffOp.Same => "=", DiffOp.Removed => "-", _ => "+" }) + r.Text));

    //the new side rebuilt from the rows, so the diff can be checked without trusting its own counts
    private static List<string> Apply(EditDiff d) =>
        d.Rows.Where(r => r.Op != DiffOp.Removed).Select(r => r.Text).ToList();

    private static List<string> Old(EditDiff d) =>
        d.Rows.Where(r => r.Op != DiffOp.Added).Select(r => r.Text).ToList();

    public static IEnumerable<object[]> Fixtures()
    {
        yield return new object[] { L("a", "b", "c"), L("a", "x", "c") };
        yield return new object[] { L("a", "b"), L("a", "x", "y", "b") };
        yield return new object[] { L("a", "x", "y", "b"), L("a", "b") };
        yield return new object[] { Fourteen(), SixChanged() };
        yield return new object[] { L("a", "b"), L() };
        yield return new object[] { L("a", "b", "c"), L("a", "b", "c") };
        yield return new object[] { L(), L("a") };
        var rnd = new Random(20260928);
        for (var k = 0; k < 20; k++)
        {
            var old = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => "w" + rnd.Next(0, 6)).ToArray();
            var neu = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => "w" + rnd.Next(0, 6)).ToArray();
            yield return new object[] { old, neu };
        }
    }

    private static string[] Fourteen() => Enumerable.Range(1, 14).Select(i => $"line {i}").ToArray();

    //lines 2, 4, 6, 8, 10 and 12 changed, each between two lines that stay
    private static string[] SixChanged() => Fourteen().Select((l, i) => i % 2 == 1 && i < 12 ? l + " changed" : l).ToArray();

    [Fact]
    public void One_changed_line_is_one_removed_and_one_added()
    {
        var d = LineDiff.Of(L("a", "b", "c"), L("a", "x", "c"));
        Assert.Equal("=a -b +x =c", Ops(d));
        Assert.Equal((1, 1, 1, false), (d.Added, d.Removed, d.Changes, d.Bounded));
    }

    [Fact]
    public void Rows_added_between_two_anchors()
    {
        var d = LineDiff.Of(L("a", "b"), L("a", "x", "y", "b"));
        Assert.Equal("=a +x +y =b", Ops(d));
        Assert.Equal((2, 0, 1), (d.Added, d.Removed, d.Changes));
    }

    [Fact]
    public void Rows_removed_only()
    {
        var d = LineDiff.Of(L("a", "x", "y", "b"), L("a", "b"));
        Assert.Equal("=a -x -y =b", Ops(d));
        Assert.Equal((0, 2, 1), (d.Added, d.Removed, d.Changes));
    }

    [Fact]
    public void Six_changes_in_fourteen_lines_are_six_changes()
    {
        var d = LineDiff.Of(Fourteen(), SixChanged());
        Assert.Equal((6, 6, 6), (d.Added, d.Removed, d.Changes));
        Assert.Contains("=line 1 -line 2 +line 2 changed =line 3", Ops(d), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_new_side_removes_every_line()
    {
        var d = LineDiff.Of(L("a", "b"), L());
        Assert.Equal("-a -b", Ops(d));
        Assert.Equal((0, 2, 1), (d.Added, d.Removed, d.Changes));
    }

    [Fact]
    public void Equal_sides_are_all_same_and_no_change()
    {
        var d = LineDiff.Of(L("a", "b", "c"), L("a", "b", "c"));
        Assert.Equal("=a =b =c", Ops(d));
        Assert.Equal((0, 0, 0), (d.Added, d.Removed, d.Changes));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Applying_the_rows_to_the_old_lines_gives_the_new_lines(string[] old, string[] neu)
    {
        var d = LineDiff.Of(old, neu);
        Assert.Equal(neu, Apply(d));
        Assert.Equal(old, Old(d));
        Assert.Equal(neu.Length - d.Added, old.Length - d.Removed);
        var changed = d.Rows.Select(r => r.Op != DiffOp.Same).ToList();
        Assert.Equal(changed.Where((c, i) => c && (i == 0 || !changed[i - 1])).Count(), d.Changes);
    }

    [Fact]
    public void Removed_rows_of_a_change_stand_before_its_added_rows()
    {
        var d = LineDiff.Of(L("a", "p", "q", "b"), L("a", "r", "s", "t", "b"));
        Assert.Equal("=a -p -q +r +s +t =b", Ops(d));
        foreach (var (old, neu) in Fixtures().Select(f => ((string[])f[0], (string[])f[1])))
        {
            var rows = LineDiff.Of(old, neu).Rows;
            for (var i = 1; i < rows.Count; i++)
                Assert.False(rows[i - 1].Op == DiffOp.Added && rows[i].Op == DiffOp.Removed, "an added row stands before a removed row of its change");
        }
    }

    [Fact]
    public void The_numbers_count_each_side_from_1()
    {
        var d = LineDiff.Of(L("a", "b", "c"), L("a", "x", "y", "c"));
        Assert.Equal(new (int?, int?)[] { (1, 1), (2, null), (null, 2), (null, 3), (3, 4) }, d.Rows.Select(r => (r.OldLine, r.NewLine)));
    }

    [Fact]
    public void A_pair_past_the_bound_returns_every_row_removed_and_added()
    {
        var old = Enumerable.Range(0, 2100).Select(i => $"old {i}").ToArray();
        var neu = Enumerable.Range(0, 2100).Select(i => $"new {i}").ToArray();
        var d = LineDiff.Of(L("head").Concat(old).Concat(L("tail")).ToArray(), L("head").Concat(neu).Concat(L("tail")).ToArray());
        Assert.True(d.Bounded);
        Assert.Equal((2100, 2100, 1), (d.Added, d.Removed, d.Changes));
        Assert.Equal(DiffOp.Same, d.Rows[0].Op);
        Assert.Equal(DiffOp.Same, d.Rows[^1].Op);
        Assert.All(d.Rows.Skip(1).Take(2100), r => Assert.Equal(DiffOp.Removed, r.Op));
    }

    [Fact]
    public void A_pair_at_the_bound_is_diffed()
    {
        var rnd = new Random(7);
        var old = Enumerable.Range(0, 2002).Select(i => $"    var value{i} = Compute({rnd.Next(0, 1000)});").ToArray();
        var neu = old.Select((l, i) => i > 0 && i < 2001 && rnd.Next(10) == 0 ? l + " //changed" : l).ToArray();
        neu[1] += " //changed";
        neu[2000] += " //changed";
        var before = GC.GetAllocatedBytesForCurrentThread();
        var d = LineDiff.Of(old, neu);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"2000 x 2000 middle, allocated {bytes / 1024 / 1024} MB");
        Assert.False(d.Bounded);
        Assert.Equal(neu, Apply(d));
    }
}
