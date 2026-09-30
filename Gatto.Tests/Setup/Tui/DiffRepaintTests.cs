using Gatto.Cli.Setup.Tui;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the frame repaints by writing only the rows that changed. the test must measure the writes, since a clear-and-rewrite and a diff draw the same picture.
public class DiffRepaintTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    //a surface that keeps each Write separately, RecordingSurface appends into one buffer and can't say how many rows were rewritten
    private sealed class WriteLog : ITermSurface
    {
        public List<string> Writes { get; } = [];
        public int Width { get; set; } = 100;
        public int Height { get; set; }
        public void Write(string s) => Writes.Add(s);
        public string All => string.Concat(Writes);
    }

    private sealed class NoKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("no key expected");
    }

    private static Screen Body(params string[] rows) =>
        new([], Region.List, "Pick a model",
            [.. rows.Select(r => PaintedRow.Of("  " + r))], null,
            [new FooterKey("Enter", "choose")]);

    private static Screen Rows(int n) =>
        Body([.. Enumerable.Range(0, n).Select(i => $"row{i:00}")]);

    //the emulator's screen as one string, so the byte-bearing separator is spelled once in this file
    private static string Lit(VtScreenSurface s) => string.Join(Environment.NewLine, s.Viewport);

    private static TuiWizardSurface Face(WriteLog s, FakePulse? pulse = null) =>
        new(s, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0,
            pulse: pulse is null ? null : () => pulse);

    //the diff

    //one row changed, one row written, the unchanged rows are not rewritten and nothing is erased wholesale
    [Fact]
    public void A_REPAINT_THAT_CHANGES_ONE_ROW_WRITES_ONE_ROW()
    {
        var s = new WriteLog();
        var f = Face(s);

        f.Paint((_, _) => Body("ALPHA", "BRAVO", "CHARLIE"));
        s.Writes.Clear();
        f.Paint((_, _) => Body("ALPHA", "DELTA", "CHARLIE"));

        Assert.DoesNotContain(Ansi.ClearBelow, s.All, StringComparison.Ordinal);
        Assert.Contains("DELTA", s.All, StringComparison.Ordinal);
        //the rows that did not change are the real assertion, a diff that rewrote everything would satisfy the two above
        Assert.DoesNotContain("ALPHA", s.All, StringComparison.Ordinal);
        Assert.DoesNotContain("CHARLIE", s.All, StringComparison.Ordinal);
        Assert.DoesNotContain("BRAVO", s.All, StringComparison.Ordinal);
    }

    //the other half: a repaint that changes nothing writes no row at all. without it, a diff that always writes exactly one row would satisfy the test above.
    [Fact]
    public void A_REPAINT_THAT_CHANGES_NOTHING_WRITES_NO_ROWS()
    {
        var s = new WriteLog();
        var f = Face(s);

        f.Paint((_, _) => Rows(6));
        s.Writes.Clear();
        f.Paint((_, _) => Rows(6));

        Assert.DoesNotContain("row0", s.All, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.ClearBelow, s.All, StringComparison.Ordinal);
    }

    //a frame that shrank must erase what it no longer covers, or the taller frame's tail sits under it looking like part of it
    [Fact]
    public void A_FRAME_THAT_SHRANK_CLEARS_BELOW_THE_LAST_ROW_IT_KEPT()
    {
        var s = new WriteLog();
        var f = Face(s);

        f.Paint((_, _) => Rows(20));
        s.Writes.Clear();
        f.Paint((_, _) => Rows(14));

        Assert.Contains(Ansi.ClearBelow, s.All, StringComparison.Ordinal);
    }

    //a resize invalidates every row, since a row composed at one width says nothing about another. the diff would keep rows that only look unchanged.
    [Fact]
    public void A_RESIZE_REPAINTS_THE_WHOLE_FRAME()
    {
        var s = new WriteLog();
        var f = Face(s);

        f.Paint((_, _) => Rows(8));
        s.Width = 80;
        s.Writes.Clear();
        f.Paint((_, _) => Rows(8));

        //every row of the frame is on screen again, including the ones whose text did not change
        Assert.Contains("row00", s.All, StringComparison.Ordinal);
        Assert.Contains("row07", s.All, StringComparison.Ordinal);
        Assert.Contains("Pick a model", s.All, StringComparison.Ordinal);
    }

    //what the diff stopped clearing by accident

    //there is no purr line, the pulse puts the purr in the title row, guarded by PurrPlaceTests. the cursor park stays, it keeps clear-below on the right row.

    //the other two edges absolute addressing rests on

    //a height-only resize invalidates the diff too, since a shorter terminal drops the top rows and slides the whole frame up
    [Fact]
    public void A_HEIGHT_ONLY_RESIZE_REPAINTS_THE_WHOLE_FRAME()
    {
        var screen = new VtScreenSurface(100, 40);
        var f = new TuiWizardSurface(screen, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0);

        f.Paint((_, _) => Body("ALPHA", "BRAVO"));
        screen.Resize(100, 30);                     //same width, the fake drops rows from the top
        f.Paint((_, _) => Body("ALPHA", "DELTA"));

        var live = screen.Viewport.Where(r => r.Length > 0).ToList();
        //the frame appears once and whole, a diff over stale addresses leaves two copies of the rows it rewrote
        Assert.Single(live, r => r.Contains("ALPHA", StringComparison.Ordinal));
        Assert.Single(live, r => r.Contains("DELTA", StringComparison.Ordinal));
        Assert.Contains(live, r => r.Contains("Pick a model", StringComparison.Ordinal));
        Assert.DoesNotContain(live, r => r.Contains("BRAVO", StringComparison.Ordinal));
    }

    //a frame exactly as tall as the screen must not scroll it, assert Scrolled since the picture looks almost right
    [Fact]
    public void A_FRAME_THAT_FILLS_THE_TERMINAL_DOES_NOT_SCROLL_IT()
    {
        //grow the body until the painted frame is exactly as tall as the screen
        var probe = new VtScreenSurface(100, 40);
        var pf = new TuiWizardSurface(probe, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0);
        pf.Paint((_, _) => Rows(10));
        var chrome = pf.LastPainted.Count - 10;

        var screen = new VtScreenSurface(100, 40);
        var f = new TuiWizardSurface(screen, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0);

        f.Paint((_, _) => Rows(40 - chrome));
        Assert.Equal(40, f.LastPainted.Count);      //the fixture really fills the screen
        Assert.Equal(0, screen.Scrolled);

        //the diff that follows addresses rows the terminal has not moved
        f.Paint((_, _) => Body([.. Enumerable.Range(0, 40 - chrome)
            .Select(i => i == 0 ? "CHANGED" : $"row{i:00}")]));

        Assert.Equal(0, screen.Scrolled);
        Assert.Contains(screen.Viewport, r => r.Contains("CHANGED", StringComparison.Ordinal));
        Assert.Contains(screen.Viewport, r => r.Contains("gatto setup", StringComparison.Ordinal));
    }

    //the width edge

    //the erase goes before the row's content, an ESC[K after a full-width row eats its last character. sweep the widths, a single width hides the defect.
    [Theory]
    [InlineData(78)]
    [InlineData(79)]
    [InlineData(80)]
    [InlineData(81)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(119)]
    [InlineData(120)]
    public void A_ROW_THAT_EXACTLY_FILLS_THE_WIDTH_KEEPS_ITS_LAST_CHARACTER(int width)
    {
        var screen = new VtScreenSurface(width, 40);
        var f = new TuiWizardSurface(screen, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0);

        //the filler is width - 2 cells, so the row ends on the last column. make the full-width row the one that changes, or the erase never touches it.
        var filler = new string('x', width - 3);
        f.Paint((_, _) => Body(filler + "A", "second"));
        f.Paint((_, _) => Body(filler + "Z", "second"));

        var line = screen.Viewport.First(r => r.Contains('x', StringComparison.Ordinal));
        Assert.EndsWith("Z", line.TrimEnd(), StringComparison.Ordinal);
    }
}
