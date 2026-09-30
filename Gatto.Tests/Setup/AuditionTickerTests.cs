using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Roles.Audition;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the audition shows one progress line, neither zero nor five. an objection to many lines is not an objection to progress.
public class AuditionTickerTests
{
    private static AuditionProgress Started(int i) =>
        new(AuditionStage.Started, TaskId: $"B{i}", Label: $"task {i}", Index: i, Total: 5);

    private static AuditionProgress Done(int i, bool pass = true) =>
        new(AuditionStage.Done, TaskId: $"B{i}", Label: $"task {i}", Index: i, Total: 5, Pass: pass);

    //five task boundaries leave exactly one row on screen, so no committed newline reaches the scrollback
    [Fact]
    public void FIVE_TASKS_LEAVE_ONE_LIVE_ROW_and_commit_nothing()
    {
        var w = new StringWriter();
        //fix the width so the row clamps the same on every machine. a test whose result moves with the host is a test about the host.
        using var ticker = new AuditionTicker(w, rich: true, windowWidth: () => 120, glyphs: GlyphSet.Unicode);

        for (var i = 1; i <= 5; i++) { ticker.Report(Started(i)); ticker.Report(Done(i)); }

        var written = w.ToString();
        Assert.DoesNotContain('\n', written);          //no committed newline reaches the scrollback at all.
        Assert.Contains("purr", written, StringComparison.Ordinal);
        Assert.Contains("task 5 of 5", written, StringComparison.Ordinal);

        //the row comes down at Finish, so later output cannot print over it
        ticker.Finish();
        Assert.EndsWith("\r", w.ToString(), StringComparison.Ordinal);
    }

    //redirected output is a log, so each task commits one line (rewriting in place would leave carriage returns in the file).
    [Fact]
    public void A_LOG_GETS_ONE_COMMITTED_LINE_PER_TASK_and_no_motion()
    {
        var w = new StringWriter();
        using var ticker = new AuditionTicker(w, rich: false, glyphs: GlyphSet.Unicode);

        for (var i = 1; i <= 3; i++) { ticker.Report(Started(i)); ticker.Report(Done(i, pass: i != 2)); }

        var lines = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("✓ B1", lines[0], StringComparison.Ordinal);
        Assert.Contains("✗ B2", lines[1], StringComparison.Ordinal);   //a failed task still commits its line.
        Assert.DoesNotContain("purr", w.ToString(), StringComparison.Ordinal);

        //strip line terminators before checking for carriage returns (a bare check sees the windows terminator instead of the rewrite)
        Assert.DoesNotContain('\r', w.ToString().Replace("\r\n", "\n"));
    }

    //the wizard drops Reusing, it started that server itself moments before. the cli keeps it, there it explains why nothing loaded.
    [Fact]
    public void A_NOTE_IS_KEPT_and_REUSING_is_still_swallowed()
    {
        var w = new StringWriter();
        using var ticker = new AuditionTicker(w, rich: false, glyphs: GlyphSet.Unicode);

        ticker.Report(AuditionProgress.Note("starting qwen…"));
        ticker.Report(AuditionProgress.Reusing("using the server already running for qwen"));

        Assert.Contains("starting qwen…", w.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("already running", w.ToString(), StringComparison.Ordinal);
    }

    //the row is short and watched, so it takes the full purr frame set. assert against PurrHead itself, so the audition and the repl can't drift apart.
    [Theory]
    [InlineData(0)]
    [InlineData(1120)]
    [InlineData(72_000)]
    public void THE_ROW_IS_THE_FULL_PURR_with_the_task_as_its_tail(long ms)
    {
        var row = AuditionTicker.Row(ms, "task 3 of 5", Gatto.Terminal.GlyphSet.Unicode);

        Assert.StartsWith(ChromeTicker.PurrHead(Cats.Face(GlyphSet.Unicode), ms, PurrFrames.Full), row,
            StringComparison.Ordinal);
        Assert.EndsWith(" · task 3 of 5", row, StringComparison.Ordinal);
        //the short frame set belongs to the watch, the audition row takes the full one.
        Assert.NotEqual(ChromeTicker.PurrHead(Cats.Face(GlyphSet.Unicode), ms, PurrFrames.Short) + " · task 3 of 5", row);
    }

    //before the first task the row drops the tail, a count nobody measured is an invention.
    [Fact]
    public void BEFORE_THE_FIRST_TASK_the_row_carries_no_count()
    {
        var row = AuditionTicker.Row(4_000, "", Gatto.Terminal.GlyphSet.Unicode);

        Assert.DoesNotContain("task", row, StringComparison.Ordinal);
        Assert.DoesNotContain(" · ", row, StringComparison.Ordinal);
        Assert.Contains("purr", row, StringComparison.Ordinal);
    }
}
