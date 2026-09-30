using Gatto.Tests.Fakes;
using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;

namespace Gatto.Tests;

public class TableLinearModeTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    //the linear surface has no transcript model, so rows are append-only and the list collects what it commits
    private static (StreamRenderer R, List<string> Committed) Linear()
    {
        var surface = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var renderer = new StreamRenderer(painter, surface, T, "coder", new ChromeTicker(painter, gate), gate);
        var committed = new List<string>();
        renderer.CommitTap = rows => committed.AddRange(rows.Select(TermText.StripAnsiForWidth));
        return (renderer, committed);
    }

    private static List<string> Feed(params string[] lines)
    {
        var (r, committed) = Linear();
        foreach (var line in lines) r.OnTextDelta(line + "\n");
        r.EndTurn();
        return committed;
    }

    [Fact]
    public void LinearMode_CommitsOneRenderedGridAtBlockClose()
    {
        var committed = Feed("| a | b |", "| --- | --- |", "| 1 | 2 |", "");
        Assert.Contains(committed, r => r.Contains('┌'));
        Assert.DoesNotContain(committed, r => r.Contains("| --- |", StringComparison.Ordinal));
    }

    [Fact]
    public void LinearMode_PipeLinesInsideAFenceAreNotBuffered()
    {
        var committed = Feed("```", "| a | b |", "| --- | --- |", "```");
        Assert.DoesNotContain(committed, r => r.Contains('┌'));
    }

    [Fact]
    public void LinearMode_ANotATableFlushKeepsProseStyling()
    {
        //a pipe line that is not a table keeps the normal styling, at the prose gutter
        var committed = Feed("a | b is not a table", "");
        Assert.Contains(committed, r => r.Contains("a | b is not a table", StringComparison.Ordinal));
        Assert.DoesNotContain(committed, r => r.Contains('┌'));
    }

    [Fact]
    public void LinearMode_TableIsIndentedIntoTheProseGutter()
    {
        //the table starts at column 2, the same gutter the rich path uses
        var committed = Feed("| a | b |", "| --- | --- |", "| 1 | 2 |", "");
        var top = committed.First(r => r.Contains('┌'));
        Assert.StartsWith("  ┌", top, StringComparison.Ordinal);
    }

    [Fact]
    public void LinearMode_AnUnclosedTableStillCommitsAtEndOfTurn()
    {
        var committed = Feed("| a | b |", "| --- | --- |", "| 1 | 2 |");
        Assert.Contains(committed, r => r.Contains('┌'));
    }
}
