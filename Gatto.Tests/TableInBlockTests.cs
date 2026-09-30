using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

public class TableInBlockTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    //the rows are painted even under plain caps, so the assertions compare stripped text
    private static List<string> Run(IReadOnlyList<string> lines, int width) =>
        ItemRender.ProseRun(lines, Plain, "coder", width, glyphs: GlyphSet.Unicode)
            .Select(TermText.StripAnsiForWidth).ToList();

    private static readonly string[] Message =
    {
        "Here are the models:",
        "| model | status |",
        "| --- | --- |",
        "| kimi-k2 | idle |",
        "That's all.",
    };

    [Fact]
    public void TableRegionRendersAsAGridInsideTheProseRun()
    {
        var rows = Run(Message, 60);
        Assert.Contains(rows, r => r.Contains('┌'));
        Assert.DoesNotContain(rows, r => r.Contains("| --- |", StringComparison.Ordinal));
    }

    [Fact]
    public void OneBulletPerMessage_ProseAfterATableDoesNotReArmIt()
    {
        Assert.Equal(1, Run(Message, 60).Count(r => r.TrimStart().StartsWith('●')));
    }

    [Fact]
    public void ATableFirstMessageCarriesExactlyOneBullet()
    {
        //a table-first message takes the one bullet, and prose after it hangs instead of opening a second run
        string[] tableFirst = { "| a | b |", "| --- | --- |", "| 1 | 2 |", "after the table" };
        Assert.Equal(1, Run(tableFirst, 60).Count(r => r.TrimStart().StartsWith('●')));
    }

    [Fact]
    public void AHeaderOnlyTableStillRenders_TheStreamingStateEveryTablePassesThrough()
    {
        //header and delimiter with no body rows still render, which is both a streaming state and a stream aborted there
        string[] partial = { "| a | b |", "| --- | --- |" };
        Assert.Contains(Run(partial, 40), r => r.Contains('┌'));

        //at a width that forces the records fallback the header alone still has to draw something, or the lines vanish
        var narrow = Run(new[] { "| averyveryverylongheaderword | b |", "| --- | --- |" }, 14);
        Assert.Contains(narrow, r => r.Trim().Length > 0);
    }

    [Fact]
    public void TwoPipedLinesWithoutAValidDelimiterStayProse()
    {
        //two piped lines need the delimiter shape, or any pair of piped lines would read as a table
        string[] notATable = { "| a | b |", "| x | y |", "| 1 | 2 |" };
        Assert.DoesNotContain(Run(notATable, 40), r => r.Contains('┌'));
    }

    [Fact]
    public void APipeLineInsideAFenceIsNotATable()
    {
        string[] fenced = { "```", "| a | b |", "| --- | --- |", "```" };
        Assert.DoesNotContain(Run(fenced, 40), r => r.Contains('┌'));
    }

    [Fact]
    public void EveryIntermediateAccumulationRendersTheTable()
    {
        //every prefix from the delimiter row on renders a table, which is what streaming shows
        for (var n = 3; n <= Message.Length; n++)
        {
            var prefix = Message.Take(n).ToList();
            Assert.Contains(Run(prefix, 60), r => r.Contains('┌'));
        }
        Assert.Equal(Run(Message, 60), Run(Message.ToList(), 60));
    }

    [Fact]
    public void AFenceOpeningInsideAPreScannedRegionIsNotSwallowed()
    {
        //the row-shape check has to exclude fence lines, or the region eats the opener and parity inverts for the rest of the message
        string[] lines = { "| lang | snippet |", "| --- | --- |", "```sh | x", "print('hi')", "```" };
        var rows = Run(lines, 60);
        Assert.Contains(rows, r => r.Contains("print('hi')", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("```sh", StringComparison.Ordinal) && r.Contains('│'));
    }

    [Fact]
    public void TableReLaysOutAcrossWidths()
    {
        //the table plus chrome and the hang needs 22 cells, so 20 is a width that forces a re-layout
        var wide = Run(Message, 100);
        var narrow = Run(Message, 20);
        Assert.NotEqual(wide.First(r => r.Contains('┌')), narrow.First(r => r.Contains('┌')));
    }

    [Fact]
    public void ALateWideRowFlipsAGridToRecordsMidStream()
    {
        //a wide row arriving late flips the grid to the records form mid-stream
        var lines = new List<string> { "| k | v |", "| --- | --- |", "| x | short |" };
        Assert.Contains(Run(lines, 22), r => r.Contains('┌'));

        lines.Add("| y | " + string.Join(' ', Enumerable.Repeat("wwww", 12)) + " |");
        var after = Run(lines, 22);
        Assert.DoesNotContain(after, r => r.Contains('┌'));
        Assert.Contains(after, r => r.Contains("k: ", StringComparison.Ordinal));
    }
}
