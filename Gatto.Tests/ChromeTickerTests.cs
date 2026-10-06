using System.Diagnostics;
using Gatto.Core.Client;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a fake ISlotsReader that answers from memory, so a test picks the poll answer, makes the read throw, and counts the polls
file sealed class FakeSlotsReader(SlotProgress? result = null, bool throws = false) : ISlotsReader
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public SlotProgress? Result { get; set; } = result;
    public bool Throws { get; set; } = throws;

    public Task<SlotProgress?> ReadAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Throws) throw new HttpRequestException("slots endpoint down");
        return Task.FromResult(Result);
    }
}

//a reader that parks in ReadAsync until the test releases it, and keeps the token it was handed. a test checks turn end doesn't cancel a poll still in flight
file sealed class BlockingSlotsReader : ISlotsReader
{
    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken ObservedToken { get; private set; }

    public async Task<SlotProgress?> ReadAsync(CancellationToken ct)
    {
        ObservedToken = ct;
        Entered.TrySetResult(true);
        await Release.Task.ConfigureAwait(false);
        return null;
    }
}

//one timer owns the purr row and the tool dot's blink for the whole turn, and only StartTurn and StopTurn start and stop it.
public class ChromeTickerTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (RecordingSurface Surface, ChromePainter Painter, object Gate) NewPainter(int width = 80, int height = 20)
    {
        var gate = new object();
        var surface = new RecordingSurface { Width = width, Height = height };
        var status = new StatusInfo(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", new CtxState(), @"C:\Users\user");
        var painter = new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
        };
        return (surface, painter, gate);
    }

    [Fact]
    public void PurrRow_Format_KaomojiFramesElapsedTokens()
    {
        var row = ChromeTicker.PurrRow("（＾ω＾ ７", elapsedMs: 12_400, tokens: 1234, toolTokens: 0);
        Assert.StartsWith("（＾ω＾ ７ purr", row, StringComparison.Ordinal);
        Assert.EndsWith("12s · ↓ 1.2k", row, StringComparison.Ordinal);
    }

    [Fact]
    public void PurrRow_FramesArePadded_TimerColumnNeverShifts()
    {
        //frames pad to one width so the timer's column holds, and the assertion checks that column since the timer's text changes length between ticks
        var shortest = ChromeTicker.PurrRow("(=^･ω･^)ゝ", elapsedMs: 0, tokens: 0, toolTokens: 0);
        var longest = ChromeTicker.PurrRow("(=^･ω･^)ゝ", elapsedMs: 1120, tokens: 0, toolTokens: 0);   //140 ms per frame, so 1120 is frame 8, the accordion's three-dot peak
        var column = shortest.IndexOf("<1s", StringComparison.Ordinal);
        Assert.True(column > 0, shortest);
        Assert.Equal(column, longest.IndexOf("1s", StringComparison.Ordinal));
        Assert.EndsWith("<1s · ↓ 0", shortest, StringComparison.Ordinal);
    }

    //both purrs share one composer and differ only in the frame set, so a second animation for the calmer watch must not appear.
    [Fact]
    public void THE_FRAME_SET_IS_THE_ONLY_THING_THAT_DIFFERS_between_the_two_purrs()
    {
        const string face = "(=^･ω･^)ゝ";

        var full = ChromeTicker.PurrHead(face, 1120, PurrFrames.Full);
        var shortSet = ChromeTicker.PurrHead(face, 1120, PurrFrames.Short);

        Assert.StartsWith(face + " purr", full, StringComparison.Ordinal);
        Assert.StartsWith(face + " purr", shortSet, StringComparison.Ordinal);
        Assert.EndsWith(ChromeTicker.FormatElapsed(1120), full, StringComparison.Ordinal);
        Assert.EndsWith(ChromeTicker.FormatElapsed(1120), shortSet, StringComparison.Ordinal);
        Assert.NotEqual(full, shortSet);

        //calmer means fewer frames, and the frame count is the only thing that proves it.
        Assert.True(PurrFrames.Short.Frames.Count < PurrFrames.Full.Frames.Count,
            "the watch's set is the shorter one — it may run for hours");
        Assert.Equal(6, PurrFrames.Short.Frames.Count);

        //the mock's cell count is approximate, so assert the frame that is drawn and the width it measures.
        Assert.Contains("purr..", PurrFrames.Short.Frames);
        Assert.Equal(7, PurrFrames.Short.Width);
    }

    //every frame in a set pads to that set's width, so the column after the accordion holds. one shared width would leave the short purr trailing dead cells
    [Theory]
    [InlineData(0)]
    [InlineData(1120)]
    [InlineData(3_600_000)]
    public void EVERY_FRAME_IN_A_SET_IS_THE_SAME_WIDTH(long elapsedMs)
    {
        foreach (var set in new[] { PurrFrames.Full, PurrFrames.Short })
        {
            Assert.Equal(set.Width, set.At(elapsedMs).Length);
            Assert.Equal(set.Width, set.At(elapsedMs + 140).Length);
        }
    }

    [Theory]
    [InlineData(0, "<1s")]
    [InlineData(38_000, "38s")]
    [InlineData(59_000, "59s")]           //the last value before the minute tier.
    [InlineData(60_000, "1m 0s")]
    [InlineData(1_015_000, "16m 55s")]
    [InlineData(3_599_000, "59m 59s")]    //the last value before the hour tier.
    [InlineData(3_600_000, "1h 0m 0s")]
    [InlineData(12_012_000, "3h 20m 12s")]
    public void FormatElapsed_HumanTiers(long ms, string expected)
        => Assert.Equal(expected, ChromeTicker.FormatElapsed(ms));

    [Fact]
    public void PurrRow_And_WaitingRow_ShareElapsedFormat_AndSpaceAfterArrow()
    {
        //both rows read the elapsed time through FormatElapsed, so they cannot drift apart. the token count sits one space after the ↓ arrow.
        var purr = ChromeTicker.PurrRow("x", elapsedMs: 1_015_000, tokens: 1300, toolTokens: 0);
        Assert.Contains("16m 55s · ↓ 1.3k", purr, StringComparison.Ordinal);
        var wait = ChromeTicker.WaitingRow(elapsedMs: 1_015_000, tokens: 1300, toolTokens: 0, glyphs: GlyphSet.Unicode);
        Assert.Contains("16m 55s · ↓ 1.3k", wait, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionText_PastTense_MatchesLivePurrShape()
    {
        Assert.Equal("x purred for 16m 55s · ↓ 5.4k", ChromeTicker.CompletionText("x", 1_015_000, 5400, 0, glyphs: GlyphSet.Unicode));
        Assert.Equal("x purred for 38s · ↓ 0", ChromeTicker.CompletionText("x", 38_000, 0, 0, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void The_completion_line_shows_the_time_of_day_after_the_elapsed()
    {
        Assert.Equal("(^· ·^)⟆ purred for 1h 2m 46s (1:32 AM) · ↓ 46.1k · Σ 85.6k",
            ChromeTicker.CompletionText("(^· ·^)⟆", 3_766_000, 46_100, 39_500, glyphs: GlyphSet.Unicode, timeOfDay: "1:32 AM"));
        Assert.Equal("(^· ·^)⟆ purred for 1h 2m 46s (14:21) · ↓ 46.1k · Σ 85.6k",
            ChromeTicker.CompletionText("(^· ·^)⟆", 3_766_000, 46_100, 39_500, glyphs: GlyphSet.Unicode, timeOfDay: "14:21"));
    }

    [Fact]
    public async Task The_completion_row_reads_the_time_of_day_when_the_turn_ends()
    {
        var now = "start";   //the clock flips to end before the read, so only a read at turn end can show (end)
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate, timeOfDay: () => now);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);
        now = "end";
        var row = ticker.CompletionRow();
        ticker.StopTurn();

        Assert.NotNull(row);
        Assert.Matches(@"^x purred for \d+s \(end\) · ↓ ", row!);
    }

    [Fact]
    public async Task A_clock_that_cannot_answer_leaves_the_time_out_of_the_completion_row()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate, timeOfDay: () => null);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);
        var row = ticker.CompletionRow();
        ticker.StopTurn();

        Assert.NotNull(row);
        Assert.Matches(@"^x purred for \d+s · ↓ ", row!);
    }

    [Fact]
    public async Task A_ticker_given_no_clock_reads_the_users_own_clock()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);
        var before = Gatto.Core.LocalClock.Now();
        var row = ticker.CompletionRow();
        var after = Gatto.Core.LocalClock.Now();
        ticker.StopTurn();

        Assert.NotNull(before);
        Assert.NotNull(row);
        Assert.True(row!.Contains($" ({before}) ", StringComparison.Ordinal) || row.Contains($" ({after}) ", StringComparison.Ordinal), row);
    }

    [Fact]
    public void CompletionRow_IsNull_BeforeAnyTurn()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        Assert.Null(ticker.CompletionRow());
    }

    [Fact]
    public async Task CompletionRow_IsNull_AfterStopTurn_EvenThoughATurnRanEarlier()
    {
        //after StopTurn it must answer null. a later non-turn dispatch reads the row too, so a stale clock would commit a growing line.
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);   //wait past the 1-second floor for the completion line.
        Assert.NotNull(ticker.CompletionRow());   //the known match, a turn that ran past the floor must give a row
        ticker.StopTurn();

        Assert.Null(ticker.CompletionRow());
    }

    //time spent waiting on a prompt is not purring. the wait covers the whole 1.5 seconds, so work elapsed stays at microseconds and no line may appear.
    [Fact]
    public async Task A_turn_spent_waiting_on_a_prompt_did_not_purr()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");

        ticker.BeginUserWait();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);    //waits longer than the floor, all of it waiting.
        Assert.Null(ticker.CompletionRow());
        ticker.EndUserWait();

        Assert.Null(ticker.CompletionRow());
    }

    //a wait belongs to its own turn. without the reset in StartTurn the earlier wait is still subtracted, so a full working turn reports nothing.
    [Fact]
    public async Task A_wait_does_not_carry_into_the_next_turn()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        var sw = Stopwatch.StartNew();

        ticker.StartTurn("x");
        ticker.BeginUserWait();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);
        ticker.EndUserWait();
        ticker.StopTurn();

        ticker.StartTurn("x");                                          //the second turn is all work.
        var second = Stopwatch.StartNew();
        while (second.ElapsedMilliseconds < 1500) await Task.Delay(20);

        Assert.NotNull(ticker.CompletionRow());
    }

    //the waiting row counts how long the question has been up. the turn's clock is frozen while it is, so showing the turn would be a timer that never moves.
    [Fact]
    public async Task The_waiting_row_counts_the_questions_age_not_the_turns()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn(Cats.Face(GlyphSet.Unicode));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1600) await Task.Delay(20);
        Assert.Contains("1s", painter.State.PurrText ?? "", StringComparison.Ordinal);

        ticker.BeginUserWait();
        painter.SetPanel(new[] { "which one?" });
        while (painter.State.ToolWait is null && sw.ElapsedMilliseconds < 6000) await Task.Delay(20);

        Assert.NotNull(painter.State.ToolWait);
        Assert.Contains("<1s", painter.State.ToolWait!, StringComparison.Ordinal);
    }

    //work either side of a wait adds up. the wait counts for nothing toward the 1s floor, so only a closed wait lets the second stretch show
    [Fact]
    public async Task Work_either_side_of_a_wait_adds_up_across_it()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000) await Task.Delay(20);    //the displayed work stays under the 1s floor.
        Assert.Null(ticker.CompletionRow());
        ticker.BeginUserWait();
        while (sw.ElapsedMilliseconds < 2200) await Task.Delay(20);    //the waiting time counts for nothing toward the floor.
        ticker.EndUserWait();
        while (sw.ElapsedMilliseconds < 3000) await Task.Delay(20);    //the added work brings the total over the floor.

        Assert.NotNull(ticker.CompletionRow());
    }

    [Fact]
    public void LivePurrRow_IsPaintedInTheRoleTint()
    {
        //the whole live purr row takes the role tint while the turn runs, and the committed line at turn end is grey.
        var (surface, painter, _) = NewPainter();
        painter.RoleForTint = "oracle";
        painter.State.PurrText = "(=^-^) purr 3s · ↓ 12";
        painter.Repaint();
        Assert.Contains(Ansi.Fg(T.RoleTint("oracle"), true), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PurrRow_Accordion_BuildsDotsOneByOne()
    {
        //the frame index is elapsedMs / 140 modulo the frame count, so 840, 980 and 1120 ms pick the one-dot, two-dot and three-dot frames.
        string Purr(long ms) => ChromeTicker.PurrRow("x", elapsedMs: ms, tokens: 0, toolTokens: 0);
        Assert.Contains("purrrrrrr.", Purr(840), StringComparison.Ordinal);
        Assert.DoesNotContain("purrrrrrr..", Purr(840));
        Assert.Contains("purrrrrrr..", Purr(980), StringComparison.Ordinal);
        Assert.DoesNotContain("purrrrrrr...", Purr(980));
        Assert.Contains("purrrrrrr...", Purr(1120), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PanelUp_TickSwapsToWaitingRow_PurrHidden_DotSolid()
    {
        //while a panel is up the purr goes and the tool row shows a static waiting suffix with a solid dot. solid means waiting on you, a blink means executing
        var (_, painter, gate) = NewPainter();
        lock (gate)
        {
            painter.State.Tool = ("write_file", "test\\yahoo2.txt");
            painter.State.PanelRows = new[] { "  Do you want to proceed?", "❯ 1. Yes" };
        }
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn(Cats.Face(GlyphSet.Unicode));

        var sw = Stopwatch.StartNew();
        while (painter.State.ToolWait is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);

        Assert.NotNull(painter.State.ToolWait);
        Assert.StartsWith(Cats.WaitingOf(glyphs: GlyphSet.Unicode), painter.State.ToolWait!, StringComparison.Ordinal);
        Assert.Contains("waiting for your input", painter.State.ToolWait, StringComparison.Ordinal);
        Assert.Null(painter.State.PurrText);
        Assert.True(painter.State.BlinkOn, "the dot must be SOLID while a panel is up");

        ticker.StopTurn();
        Assert.Null(painter.State.ToolWait);
    }

    [Fact]
    public async Task PanelUp_Clears_PurrResumesOnTheNextTick()
    {
        //the purr must come back on the next tick after the panel clears. the turn is not restarted, so the same ticker has to notice the flip mid-turn.
        var (_, painter, gate) = NewPainter();
        lock (gate)
        {
            painter.State.Tool = ("write_file", "test\\yahoo2.txt");
            painter.State.PanelRows = new[] { "  Do you want to proceed?", "❯ 1. Yes" };
        }
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn(Cats.Face(GlyphSet.Unicode));

        var sw = Stopwatch.StartNew();
        while (painter.State.ToolWait is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        Assert.NotNull(painter.State.ToolWait);
        Assert.Null(painter.State.PurrText);

        lock (gate) { painter.State.PanelRows = null; }

        sw.Restart();
        while (painter.State.PurrText is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);

        Assert.NotNull(painter.State.PurrText);
        Assert.Contains("purr", painter.State.PurrText!, StringComparison.Ordinal);
        Assert.Null(painter.State.ToolWait);

        ticker.StopTurn();
    }

    [Fact]
    public void PurrRow_WithRealCatsFace_StaysSingleLine()
    {
        //the row must stay one line, so feed the real Cats.Face (an embedded newline desyncs the erase and smears chrome into scrollback)
        var row = ChromeTicker.PurrRow(Cats.Face(GlyphSet.Unicode), elapsedMs: 12_400, tokens: 1234, toolTokens: 0);
        Assert.DoesNotContain('\n', row);
        Assert.DoesNotContain('\r', row);
    }

    [Fact]
    public async Task StartTurn_TicksPaintPurrAndFlipBlink_StopTurnClears()
    {
        var (surface, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);

        ticker.StartTurn("（＾ω＾）");

        //wait, with a bound, for the first tick past the grace window.
        var sw = Stopwatch.StartNew();
        while (painter.State.PurrText is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        Assert.NotNull(painter.State.PurrText);
        var first = painter.State.PurrText;
        var firstBlink = painter.State.BlinkOn;

        //the blink flips on a 500 ms cadence, so two samples 500 ms apart must differ at least once.
        var sawFlip = false;
        sw.Restart();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            await Task.Delay(500);
            if (painter.State.BlinkOn != firstBlink) { sawFlip = true; break; }
        }
        Assert.True(sawFlip, "BlinkOn never flipped across the sampling window");
        Assert.NotNull(first);

        var writtenBeforeStop = surface.Text.Length;
        ticker.StopTurn();
        Assert.Null(painter.State.PurrText);
        Assert.True(surface.Text.Length > writtenBeforeStop,
            "StopTurn() should perform one final Repaint that writes to the surface");
    }

    [Fact]
    public void PurrRow_Prefill_NamesThePhase_WithoutNeedingTheServer()
    {
        //the phase comes from what gatto knows locally, so the row holds on a cloud endpoint, an old server, or a timed-out /slots poll.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 0, toolTokens: 0, prefilling: true);

        Assert.Contains("reading context", row, StringComparison.Ordinal);
        Assert.Contains("2m 10s", row, StringComparison.Ordinal);
        Assert.DoesNotContain("↓", row);        //nothing has decoded yet, so claiming a count would be a lie.
        Assert.DoesNotContain("prefill", row);  //the row spells the phase out as reading context, so the word prefill must not appear in it
    }

    [Fact]
    public void PurrRow_Prefill_AddsServerNumbersWhenAPollLands()
    {
        //the server value is a truthful numerator. the slot gives no denominator, so gatto pairs it with its own chars÷4 estimate and marks that one ~.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 0, toolTokens: 0,
            slots: new SlotProgress(PromptTotal: 20480, PromptProcessed: 20480, Decoded: 202, Processing: true),
            prefilling: true, promptEstimate: 85_000);

        //the server numbers are appended after the timer, so a poll landing cannot move the elapsed
        Assert.EndsWith("· 20.5k/~85.0k", row, StringComparison.Ordinal);
        Assert.Contains("2m 10s", row, StringComparison.Ordinal);
        Assert.DoesNotContain("202", row);   //the slot's n_decoded still holds another task's count, it must not surface here
    }

    [Fact]
    public void PurrRow_Prefill_OmitsTheDenominatorWhenNoEstimateIsKnown()
    {
        var row = ChromeTicker.PurrRow("x", elapsedMs: 1000, tokens: 0, toolTokens: 0,
            slots: new SlotProgress(20480, 20480, 202, true), prefilling: true);

        Assert.EndsWith("· 20.5k", row, StringComparison.Ordinal);
        Assert.DoesNotContain("~", row);
    }

    [Fact]
    public void PurrRow_Prefill_TimerColumnNeverShifts()
    {
        //the prefill frames pad to the longest one, so the elapsed cannot jitter left and right every cycle. a number the user is reading must not move.
        var cols = new List<int>();
        for (long ms = 0; ms <= 2000; ms += 100)
        {
            var row = ChromeTicker.PurrRow("x", ms, tokens: 0, toolTokens: 0, prefilling: true);
            cols.Add(row.IndexOf(ChromeTicker.FormatElapsed(ms), StringComparison.Ordinal));
        }
        Assert.All(cols, c => Assert.True(c > 0, "the elapsed must be present in every frame"));
        Assert.Single(cols.Distinct());   //every frame puts the elapsed at the same column.
    }

    [Fact]
    public void PurrRow_Prefill_TimerColumnIsUnchangedWhenServerNumbersArrive()
    {
        //a poll that arrives appends its numbers, it must not shove the timer.
        var bare = ChromeTicker.PurrRow("x", 5000, 0, 0, prefilling: true);
        var withNumbers = ChromeTicker.PurrRow("x", 5000, 0, 0,
            slots: new SlotProgress(20480, 20480, 202, true), prefilling: true, promptEstimate: 85_000);

        var elapsed = ChromeTicker.FormatElapsed(5000);
        Assert.Equal(bare.IndexOf(elapsed, StringComparison.Ordinal),
                     withNumbers.IndexOf(elapsed, StringComparison.Ordinal));
    }

    [Fact]
    public void PurrRow_Decode_UsesRealDecodedCount_NeverRendersPrefill100()
    {
        //once the server has processed the whole prompt the row enters decode with the server's own count. that count wins over the local chars÷4 estimate.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 999, toolTokens: 0,
            slots: new SlotProgress(101197, 101197, Decoded: 97, Processing: true));
        Assert.EndsWith("· ↓ 97", row, StringComparison.Ordinal);
        Assert.DoesNotContain("prefill", row);
        Assert.DoesNotContain("999", row);
    }

    [Fact]
    public void PurrRow_WithToolTokens_AppendsAccumulatedSigmaTotal()
    {
        //when tool results contributed, a trailing Σ total shows the whole turn's throughput, decode plus tool tokens, and the live ↓ count stays.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 88, toolTokens: 28800, slots: null);
        Assert.Contains("· ↓ 88", row, StringComparison.Ordinal);
        Assert.EndsWith("· Σ 28.9k", row, StringComparison.Ordinal);
    }

    [Fact]
    public void PurrRow_NoToolTokens_HasNoSigma()
    {
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 88, toolTokens: 0, slots: null);
        Assert.EndsWith("· ↓ 88", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Σ", row);
    }

    [Fact]
    public void PurrRow_Prefill_KeepsSigmaSoBankedToolWorkStaysVisible()
    {
        //during a re-prefill the live count is gone, nothing has decoded yet. the Σ total still shows the work done so far and that keeps the row from reading as idle.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 0, toolTokens: 12500, prefilling: true);

        Assert.Contains("reading context", row, StringComparison.Ordinal);
        Assert.EndsWith("· Σ 12.5k", row, StringComparison.Ordinal);
        Assert.DoesNotContain("↓", row);
    }

    [Fact]
    public async Task AddToolResultChars_AccumulatesIntoLivePurrSigma()
    {
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");
        ticker.AddTokenChars(400);              //four characters per token give a decode estimate of 100.
        ticker.AddToolResultChars(4 * 12500);

        string? seen = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            var t = painter.State.PurrText;
            if (t is not null && t.Contains("Σ", StringComparison.Ordinal)) { seen = t; break; }
            await Task.Delay(20);
        }
        ticker.StopTurn();
        Assert.NotNull(seen);
        Assert.Contains("Σ 12.6k", seen!, StringComparison.Ordinal);   //the total adds the decode estimate and the tool tokens.
    }

    [Fact]
    public void PurrRow_NoSlots_FallsBackToCharsEstimate()
    {
        //remote and OpenAI-compatible servers expose no /slots, so the row keeps the chars÷4 estimate.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 130_000, tokens: 1300, toolTokens: 0, slots: null);
        Assert.EndsWith("· ↓ 1.3k", row, StringComparison.Ordinal);
        Assert.DoesNotContain("prefill", row);
    }

    [Fact]
    public async Task Poller_LandsTheProcessedCountInThePrefillRow()
    {
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(20480, 20480, Decoded: 202, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x", promptTokensEstimate: 85_000);   //no deltas have streamed, so the turn is in prefill.

        string? seen = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            var t = painter.State.PurrText;
            if (t is not null && t.Contains("20.5k", StringComparison.Ordinal)) { seen = t; break; }
            await Task.Delay(20);
        }
        ticker.StopTurn();

        Assert.NotNull(seen);
        Assert.EndsWith("· 20.5k/~85.0k", seen!, StringComparison.Ordinal);
        Assert.True(reader.Calls >= 1, "the poller must have hit the reader while the turn was silent");
    }

    //a turn that already emitted keeps reading as decode, since only a new turn or a progress chunk clears that flag. a poll then shows the new task's n_decoded, 0
    [Fact]
    public async Task A_REPREFILL_AFTER_AN_EMITTING_ROUND_READS_AS_DECODE_ON_THE_SLOTS_PATH()
    {
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(42, 0, Decoded: 0, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x", promptTokensEstimate: 234_000);
        ticker.MarkStreaming();

        var sw = Stopwatch.StartNew();
        while (reader.Calls < 1 && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        await Task.Delay(300);   //wait so that at least one tick follows the poll.
        var seen = painter.State.PurrText;
        ticker.StopTurn();

        Assert.NotNull(seen);
        Assert.DoesNotContain("reading context", seen!, StringComparison.Ordinal);
        Assert.Contains("↓ 0", seen!, StringComparison.Ordinal);
    }

    //a prompt_progress chunk puts the row on the prefill branch and prints its real total with no ~. a later /slots answer must not replace the stream's numbers
    [Fact]
    public async Task A_PROGRESS_CHUNK_SHOWS_READING_CONTEXT_WITH_ITS_REAL_TOTAL()
    {
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(42, 42, Decoded: 0, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x", promptTokensEstimate: 85_000);
        ticker.MarkStreaming();
        ticker.SetPromptProgress(total: 15_063, processed: 4_096);

        var sw = Stopwatch.StartNew();
        while (reader.Calls < 1 && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        await Task.Delay(300);   //wait so that at least one tick follows the poll.
        var seen = painter.State.PurrText;
        ticker.StopTurn();

        Assert.NotNull(seen);
        Assert.Contains("reading context", seen!, StringComparison.Ordinal);
        Assert.EndsWith("· 4.1k/15.1k", seen!, StringComparison.Ordinal);
        Assert.DoesNotContain("~", seen!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poller_DeadReader_NeverFaults_AndThePhaseStillShows()
    {
        //a throwing reader must not fault the ticker or cost the user the explanation. the phase is derived locally, so it holds through failed polls.
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(throws: true);
        var ticker = new ChromeTicker(painter, gate, reader);
        var faults = 0;
        ticker.OnFault = _ => Interlocked.Increment(ref faults);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (painter.State.PurrText is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        var txt = painter.State.PurrText;
        await Task.Delay(200);   //give the poll time to fire and fail before the row is read
        ticker.StopTurn();

        Assert.NotNull(txt);
        Assert.Contains("reading context", txt!, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref faults));
    }

    [Fact]
    public async Task ANullPollAnswer_DoesNotRevertTheCountAlreadyShown()
    {
        //a null poll is no information, so the row keeps the last count it showed. the numerator is monotonic, so a stale one is still a truthful lower bound.
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(20480, 20480, 202, true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x", promptTokensEstimate: 85_000);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            if (painter.State.PurrText?.Contains("20.5k", StringComparison.Ordinal) == true) break;
            await Task.Delay(20);
        }
        Assert.Contains("20.5k", painter.State.PurrText ?? "");

        reader.Result = null;
        var callsAtFlip = reader.Calls;
        sw.Restart();
        while (reader.Calls < callsAtFlip + 2 && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        var after = painter.State.PurrText;
        ticker.StopTurn();

        Assert.Contains("20.5k", after ?? "");
    }

    [Fact]
    public async Task StaleDecodedFromAPreviousTask_NeverBecomesThisTurnsCount()
    {
        //a slot can report the previous task's n_decoded through a whole new prefill, so a latched maximum would show another conversation's count
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(20480, 20480, Decoded: 202, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) await Task.Delay(20);   //wait for a poll to land before the row is read
        var live = painter.State.PurrText;
        var completion = ticker.CompletionRow();
        ticker.StopTurn();

        Assert.DoesNotContain("202", live ?? "");
        Assert.DoesNotContain("202", completion ?? "");
    }

    [Fact]
    public async Task Poller_StartTurn_ClearsStalePrefill_FromThePreviousTurn()
    {
        //the reset in StartTurn and the clear in StopTurn keep one turn's prefill from bleeding into the next, where the server reports nothing processing
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(101197, 34407, 0, true));
        var ticker = new ChromeTicker(painter, gate, reader);

        ticker.StartTurn("x");
        var sw = Stopwatch.StartNew();
        while (!(painter.State.PurrText?.Contains("prefill", StringComparison.Ordinal) ?? false)
               && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        ticker.StopTurn();

        reader.Result = null;
        ticker.StartTurn("x");
        string? second = null;
        sw.Restart();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            var t = painter.State.PurrText;
            if (t is not null) { second = t; break; }
            await Task.Delay(20);
        }
        ticker.StopTurn();

        Assert.NotNull(second);
        Assert.DoesNotContain("prefill", second!);
    }

    [Fact]
    public async Task Decode_PrefersSlotsCount_AndDoesNotFlipBackToTheEstimate()
    {
        //the server's decode count wins over the chars÷4 estimate. falling back to the estimate whenever a delta arrives would flip the shown count back and forth.
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(101197, 101197, Decoded: 170, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x");
        ticker.AddTokenChars(400);   //the estimate is 100, lower than the real 170.

        string? seen = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            var t = painter.State.PurrText;
            if (t is not null && t.Contains("↓ 170", StringComparison.Ordinal)) { seen = t; break; }
            await Task.Delay(20);
        }
        Assert.NotNull(seen);

        for (var i = 0; i < 6; i++)   //the count must never flip back to the estimate.
        {
            await Task.Delay(30);
            var t = painter.State.PurrText;
            if (t is not null) Assert.DoesNotContain("↓ 100", t);
        }
        ticker.StopTurn();
    }

    [Fact]
    public async Task Decode_TransientPollFailure_DoesNotDipTheCount_FlooredAtMaxDown()
    {
        //a failed poll keeps the last answer, and the waiting row, which reads no slot, is floored at the highest decode shown
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(101197, 101197, Decoded: 170, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x");
        ticker.AddTokenChars(400);   //the chars÷4 estimate is 100.

        Assert.True(SpinWait.SpinUntil(() => painter.State.PurrText?.Contains("↓ 170", StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(30)), "the row never showed the server's count");

        reader.Throws = true;
        var polled = reader.Calls;
        Assert.True(SpinWait.SpinUntil(() => reader.Calls > polled, TimeSpan.FromSeconds(30)), "the failing poll never ran");
        lock (gate) painter.State.PurrText = null;
        Assert.True(SpinWait.SpinUntil(() => painter.State.PurrText is not null, TimeSpan.FromSeconds(30)),
            "no tick painted the row after the failed poll");
        Assert.Contains("↓ 170", painter.State.PurrText!, StringComparison.Ordinal);   //the failed poll kept the last answer

        lock (gate)
        {
            painter.State.Tool = ("write_file", "out.txt");
            painter.State.PanelRows = new[] { "  Do you want to proceed?", "❯ 1. Yes" };
        }
        Assert.True(SpinWait.SpinUntil(() => painter.State.ToolWait is not null, TimeSpan.FromSeconds(30)), "the waiting row never came");
        var waiting = painter.State.ToolWait!;
        ticker.StopTurn();

        Assert.Contains("↓ 170", waiting, StringComparison.Ordinal);   //the floor holds the highest decode, not the chars÷4 estimate
        Assert.DoesNotContain("↓ 100", waiting);
    }

    [Fact]
    public async Task CompletionRow_UsesTheSlotsDecodeCount_NotTheEstimate()
    {
        //the committed line reads the highest decode count the turn showed (the slot is gone by StopTurn, so the estimate would be lower)
        var (_, painter, gate) = NewPainter();
        var reader = new FakeSlotsReader(new SlotProgress(101197, 101197, Decoded: 170, Processing: true));
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x");
        ticker.AddTokenChars(400);   //the chars÷4 estimate is 100.
        await Task.Delay(1300);       //wait past the 1-second floor so CompletionRow has a line to give
        var row = ticker.CompletionRow();
        ticker.StopTurn();

        Assert.NotNull(row);
        Assert.Contains("↓ 170", row!, StringComparison.Ordinal);
        Assert.DoesNotContain("↓ 100", row!);
    }

    [Fact]
    public async Task TickerFault_SelfDisarms_ReportsOnce()
    {
        //a painting fault is reported once and the ticker stops. it must not call OnFault on every tick.
        var throwing = new ThrowingWidthSurface();
        var status = new StatusInfo(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", new CtxState(), @"C:\Users\user");
        var gate = new object();
        var painter = new ChromePainter(throwing, T, gate)
        {
            Frame = new InputFrame(throwing.Inner, T, "coder", status, glyphs: GlyphSet.Unicode),
        };
        var ticker = new ChromeTicker(painter, gate);
        var faults = 0;
        Exception? reported = null;
        ticker.OnFault = ex => { Interlocked.Increment(ref faults); Volatile.Write(ref reported, ex); };

        ticker.StartTurn("（＾ω＾）");

        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref reported) is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        Assert.NotNull(reported);
        Assert.Equal(1, Volatile.Read(ref faults));

        await Task.Delay(300);   //three more tick slots. it must not call OnFault again
        Assert.Equal(1, Volatile.Read(ref faults));
    }

    [Fact]
    public void PrefillRow_DropsTheDenominator_OnceTheServerHasProcessedMoreThanItPredicted()
    {
        //once the processed count passes the estimate, the estimate is disproven, so the row drops that denominator and keeps the server's own count
        var row = ChromeTicker.PurrRow("x", elapsedMs: 60_000, tokens: 0, toolTokens: 0,
            slots: new SlotProgress(20480, 20480, 0, true), prefilling: true, promptEstimate: 16_000);

        Assert.Contains("20.5k", row, StringComparison.Ordinal);
        Assert.DoesNotContain("/~", row);
    }

    [Fact]
    public void PrefillRow_KeepsTheDenominator_WhileTheEstimateStillHolds()
    {
        var row = ChromeTicker.PurrRow("x", elapsedMs: 60_000, tokens: 0, toolTokens: 0,
            slots: new SlotProgress(20480, 20480, 0, true), prefilling: true, promptEstimate: 85_000);

        Assert.EndsWith("· 20.5k/~85.0k", row, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefillRow_KeepsTheDenominatorAtExactlyTheEstimate()
    {
        //a value equal to the estimate is still consistent with it. only passing it disproves the number.
        var row = ChromeTicker.PurrRow("x", elapsedMs: 60_000, tokens: 0, toolTokens: 0,
            slots: new SlotProgress(20000, 20000, 0, true), prefilling: true, promptEstimate: 20_000);

        Assert.EndsWith("· 20.0k/~20.0k", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10_000, 1.0, 10_000)]      //a fresh or resumed session has no measured ratio, so the estimate stays unscaled.
    [InlineData(10_000, 1.31, 13_100)]     //one measured turn shows chars÷4 ran 31% light, so the denominator lifts.
    [InlineData(0, 1.4, 0)]                //zero stays zero, and a zero estimate leaves the row without a denominator
    public void ScaledPromptEstimate_AppliesTheMeasuredEstimateToTruthRatio(int estimate, double ratio, long expected)
    {
        //the same server-measured ratio the compactor already uses, so the denominator isn't systematically low (chars÷4 under-counts code, CJK paths, and JSON)
        Assert.Equal(expected, Gatto.Repl.Repl.ScaledPromptEstimate(estimate, ratio));
    }

    [Fact]
    public async Task StopTurn_DoesNotCancelAnInFlightPoll()
    {
        //turn end is no reason to cancel a request to the server
        var (_, painter, gate) = NewPainter();
        var reader = new BlockingSlotsReader();
        var ticker = new ChromeTicker(painter, gate, reader);
        ticker.StartTurn("x");

        Assert.True(await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            "the poller must have reached the reader");
        ticker.StopTurn();
        await Task.Delay(100);   //give a cancellation every chance to propagate.

        Assert.False(reader.ObservedToken.IsCancellationRequested,
            "a turn end must not cancel an in-flight /slots read, because gatto never cancels a request to the local server on an ambient trigger");
        reader.Release.SetResult();
    }

    [Fact]
    public async Task MarkStreaming_EndsThePrefillPhase_ForAToolCallThatStreamsNoText()
    {
        //a streaming tool call delivers argument deltas and no content deltas. without this signal the row keeps claiming prefill while the model composes the call.
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");

        var sw = Stopwatch.StartNew();
        while (painter.State.PurrText is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        Assert.Contains("reading context", painter.State.PurrText!, StringComparison.Ordinal);

        ticker.MarkStreaming();   //what OnToolCallDelta reports, the model has started emitting

        string? after = null;
        sw.Restart();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            var t = painter.State.PurrText;
            if (t is not null && !t.Contains("reading context", StringComparison.Ordinal)) { after = t; break; }
            await Task.Delay(20);
        }
        ticker.StopTurn();

        Assert.NotNull(after);
        Assert.Contains("purr", after!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkStreaming_IsClearedByTheNextStartTurn()
    {
        //the flag is turn state. a turn that streamed a tool call must not stop the next turn from reporting its own prefill.
        var (_, painter, gate) = NewPainter();
        var ticker = new ChromeTicker(painter, gate);
        ticker.StartTurn("x");
        ticker.MarkStreaming();
        ticker.StopTurn();

        ticker.StartTurn("x");
        var sw = Stopwatch.StartNew();
        while (painter.State.PurrText is null && sw.ElapsedMilliseconds < 30_000) await Task.Delay(20);
        var txt = painter.State.PurrText;
        ticker.StopTurn();

        Assert.NotNull(txt);
        Assert.Contains("reading context", txt!, StringComparison.Ordinal);
    }
}
