using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

public class SpanWrapTests
{
    private const char Nbsp = ' ';

    //the three pinned cases

    [Fact]
    public void SplitsUnstyledTextAcrossRows()
    {
        var rows = SpanWrap.Wrap(new[] { new StyledSpan("aaaa bbbb", SpanFlags.None) }, 4, 4);
        Assert.Equal(2, rows.Count);
        Assert.Equal("aaaa", rows[0].Single().Text);
        Assert.Equal("bbbb", rows[1].Single().Text);
    }

    [Fact]
    public void BoldSpanStraddlingRowEdge_SplitsKeepingFlags()
    {
        var rows = SpanWrap.Wrap(new[]
        {
            new StyledSpan("plain ", SpanFlags.None),
            new StyledSpan("bold words here", SpanFlags.Bold),
        }, 10, 10);
        Assert.True(rows.Count >= 2);
        Assert.All(rows.SelectMany(r => r).Where(s => s.Text.Contains("bold") || s.Text.Contains("words")),
            s => Assert.Equal(SpanFlags.Bold, s.Flags));
    }

    [Fact]
    public void ChipIsUnbreakable_MovesWholeToNextRow()
    {
        var rows = SpanWrap.Wrap(new[]
        {
            new StyledSpan("xxxxxx ", SpanFlags.None),
            new StyledSpan("two words", SpanFlags.Chip),   //9 cells, and a chip must not split at its inner space
        }, 12, 12);
        var chipRow = rows.First(r => r.Any(s => s.Flags.HasFlag(SpanFlags.Chip)));
        Assert.Equal("two words", chipRow.First(s => s.Flags.HasFlag(SpanFlags.Chip)).Text);
    }

    //a chip is painted with no padding, so its rendered width is its text cells and the wrap budget charges nothing extra
    [Fact]
    public void ChipNoPadding_RenderedWidthIsTextCells_FitsToWidth()
    {
        const int W = 12;
        //5 a's plus a trailing space plus a 6-cell chip is exactly the 12-cell budget, so one row
        var rows = SpanWrap.Wrap(new[]
        {
            new StyledSpan("aaaaa ", SpanFlags.None),          //5 letters and a trailing space
            new StyledSpan("bbbbbb", SpanFlags.Chip),          //6 cells of text and 6 rendered, since a chip adds no padding
        }, W, W);
        Assert.Single(rows);
        foreach (var row in rows)
        {
            var rendered = row.Sum(sp => UnicodeWidth.Of(sp.Text));   //a chip adds nothing extra to its rendered width
            Assert.True(rendered <= W, $"row rendered width {rendered} exceeds {W}");
        }
    }

    //edge cases

    [Fact]
    public void EmptySpanList_YieldsOneEmptyRow()
    {
        var rows = SpanWrap.Wrap(Array.Empty<StyledSpan>(), 10, 10);
        Assert.Single(rows);
        Assert.Empty(rows[0]);
    }

    [Fact]
    public void BudgetZero_DisablesWrapping_SingleRowAllSpans()
    {
        var input = new[]
        {
            new StyledSpan("alpha beta gamma delta epsilon", SpanFlags.None),
            new StyledSpan("bold", SpanFlags.Bold),
        };
        var rows = SpanWrap.Wrap(input, 0, 0);
        Assert.Single(rows);
        Assert.Equal("alpha beta gamma delta epsilonbold", string.Concat(rows[0].Select(s => s.Text)));
        Assert.Equal(SpanFlags.Bold, rows[0][1].Flags);
    }

    [Fact]
    public void SingleWordLongerThanRow_HardBreaksKeepingFlags()
    {
        var rows = SpanWrap.Wrap(new[] { new StyledSpan("Supercalifragilistic", SpanFlags.Bold) }, 8, 8);
        Assert.True(rows.Count >= 3);
        Assert.All(rows.SelectMany(r => r), s => Assert.Equal(SpanFlags.Bold, s.Flags));
        Assert.Equal("Supercalifragilistic", string.Concat(rows.SelectMany(r => r).Select(s => s.Text)));
    }

    [Fact]
    public void LinkStraddlingRowEdge_BothPiecesCarrySameUrl()
    {
        var rows = SpanWrap.Wrap(new[]
        {
            new StyledSpan("see the docs here", SpanFlags.Link, "https://example.com"),
        }, 8, 8);
        Assert.True(rows.Count >= 2);
        Assert.All(rows.SelectMany(r => r), s =>
        {
            Assert.Equal(SpanFlags.Link, s.Flags);
            Assert.Equal("https://example.com", s.LinkUrl);
        });
    }

    [Fact]
    public void ChipWiderThanRow_StaysWhole_OnItsOwnRow()
    {
        //a chip wider than the row is hard-broken whole, and its inner space is never a break point
        var rows = SpanWrap.Wrap(new[]
        {
            new StyledSpan("aa bb cc dd", SpanFlags.Chip),
        }, 6, 6);
        var chipTexts = rows.SelectMany(r => r).Where(s => s.Flags.HasFlag(SpanFlags.Chip)).ToArray();
        //joining the chip slices gives the original text back, and every inner space is still a real space
        Assert.Equal("aa bb cc dd", string.Concat(chipTexts.Select(s => s.Text)));
        Assert.All(chipTexts, s => Assert.DoesNotContain(Nbsp, s.Text));
    }

    [Fact]
    public void ChipAtStartAndEndOfLine_RoundTrips()
    {
        var input = new[]
        {
            new StyledSpan("start", SpanFlags.Chip),
            new StyledSpan(" middle text here ", SpanFlags.None),
            new StyledSpan("end", SpanFlags.Chip),
        };
        var rows = SpanWrap.Wrap(input, 9, 9);
        AssertRowsReconstruct(input, rows, 9, 9);
    }

    [Fact]
    public void SpanOfOnlySpaces_HandledWithoutCorruption()
    {
        var input = new[]
        {
            new StyledSpan("aaa", SpanFlags.None),
            new StyledSpan("   ", SpanFlags.None),
            new StyledSpan("bbb", SpanFlags.None),
        };
        var rows = SpanWrap.Wrap(input, 4, 4);
        AssertRowsReconstruct(input, rows, 4, 4);
    }

    //every row's slices reconstruct SoftWrap's row text

    [Fact]
    public void RowSlices_Reconstruct_SoftWrapRowText_ForManyInputs()
    {
        var cases = new (StyledSpan[] Spans, int First, int Cont)[]
        {
            (new[] { new StyledSpan("aaaa bbbb", SpanFlags.None) }, 4, 4),
            (new[] { new StyledSpan("plain ", SpanFlags.None), new StyledSpan("bold words here", SpanFlags.Bold) }, 10, 10),
            (new[] { new StyledSpan("xxxxxx ", SpanFlags.None), new StyledSpan("two words", SpanFlags.Chip) }, 12, 12),
            (new[] { new StyledSpan("alpha beta gamma delta", SpanFlags.None) }, 12, 10),     //first and continuation budgets differ here
            (new[] { new StyledSpan("  leading indent kept", SpanFlags.None) }, 8, 8),        //the first row keeps its indent
            (new[] { new StyledSpan("aa  bb  cc", SpanFlags.None) }, 4, 4),                   //consecutive spaces
            (new[] { new StyledSpan("aaa  bbb", SpanFlags.None) }, 3, 3),                     //a wrap at the boundary plus a dropped leading space
            (new[] { new StyledSpan("Supercalifragilistic", SpanFlags.Bold) }, 7, 5),         //one long word, so a hard break
            (new[] { new StyledSpan("see the docs here", SpanFlags.Link, "https://x.io") }, 8, 8),
            (new[] { new StyledSpan("a", SpanFlags.None), new StyledSpan("b c d e f g", SpanFlags.Italic), new StyledSpan(" tail", SpanFlags.None) }, 5, 5),
            (new[] { new StyledSpan("word", SpanFlags.Chip), new StyledSpan(" and ", SpanFlags.None), new StyledSpan("chip here", SpanFlags.Chip) }, 7, 7),
            (Array.Empty<StyledSpan>(), 6, 6),
        };
        foreach (var c in cases)
            AssertRowsReconstruct(c.Spans, SpanWrap.Wrap(c.Spans, c.First, c.Cont), c.First, c.Cont);
    }

    //the oracle for the slicing: rebuilding SoftWrap's row text from the emitted slices must give the same string
    private static void AssertRowsReconstruct(
        IReadOnlyList<StyledSpan> spans, IReadOnlyList<IReadOnlyList<StyledSpan>> rows, int first, int cont)
    {
        var concat = string.Concat(spans.Select(s =>
            s.Flags.HasFlag(SpanFlags.Chip) ? s.Text.Replace(' ', Nbsp) : s.Text));
        var segs = SoftWrap.Wrap(concat, first, cont);

        Assert.Equal(segs.Count, rows.Count);
        for (var i = 0; i < segs.Count; i++)
        {
            //swap the chip spaces for NBSP again, so the rebuilt text is comparable to SoftWrap's row text
            var rebuilt = string.Concat(rows[i].Select(s =>
                s.Flags.HasFlag(SpanFlags.Chip) ? s.Text.Replace(' ', Nbsp) : s.Text));
            Assert.Equal(segs[i].Text, rebuilt);
        }
    }
}
