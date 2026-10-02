using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//the modal panel draws inside the composer frame, keeping the rules and status while the editor rows go. every check reads the bytes the painter wrote
public sealed class PanelPlacementTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private const string Role = "coder";

    private static StatusInfo Status() => new(@"C:\proj", "qwen", Role, new CtxState(), @"C:\Users\x");

    //sets up the alt screen and paints one frame, so a test can read the composed block or the rows actually written.
    private static (ChromePainter P, VtScreenSurface S) Wire(int w, int h, params string[] composer)
    {
        var s = new VtScreenSurface(w, h);
        var gate = new object();
        var painter = new ChromePainter(s, T, gate)
        {
            Frame = new InputFrame(s, T, Role, Status(), glyphs: GlyphSet.Unicode),
            RoleForTint = Role,
        };
        var lines = composer.Length > 0 ? composer.ToList() : new List<string> { "" };
        painter.State.Composer = new EditorView(lines, lines.Count - 1, lines[^1].Length);
        painter.AltScreen.Enter();
        painter.Repaint();
        return (painter, s);
    }

    //panel rows arrive indented and fitted, with numbers, so a top-drop clamp is provable by which rows survive.
    private static string[] Panel(int n) =>
        Enumerable.Range(0, n).Select(i => $"  p{i:00}").ToArray();

    private static List<ChromeRow> Rows(ChromePainter p, int w, int h, ChromeRegion region) =>
        p.ComposeChromeBlock(w, h).Rows.Where(r => r.Region == region).ToList();

    //the newest queued message must stay on screen in a short window while a turn runs, with and without the panel
    [Fact]
    public void THE_NEWEST_QUEUED_MESSAGE_SURVIVES_A_SHORT_WINDOW()
    {
        foreach (var h in new[] { 12, 14, 18, 24, 30 })
            foreach (var panelUp in new[] { false, true })
            {
                var (p, _) = Wire(60, h);
                p.State.PurrText = "purr 3s";
                p.State.Tool = ("shell", "dir");
                p.State.QueueTexts = new[] { "first queued message", "second queued message" };
                if (panelUp) p.SetPanel(Panel(p.PanelRowsAvailable()));
                p.Repaint();

                var block = p.ComposeChromeBlock(60, h);
                var dump = $"h={h} panel={panelUp}:\n" + string.Join("\n", block.Rows.Select(r => $"{r.Region}: {r.Visible}"));

                Assert.True(block.Rows.Any(r => r.Region == ChromeRegion.Queue
                                                && r.Visible.Contains("second queued message", StringComparison.Ordinal)),
                    $"the newest queued message is hidden, {dump}");
                Assert.True(block.Rows.Count <= h - 3, $"the block is taller than its budget, {dump}");
                Assert.True(block.Rows.Any(r => r.Region is ChromeRegion.Composer or ChromeRegion.Prompt),
                    $"the input row is gone, {dump}");
                var rows = block.Rows.ToList();
                var queueStart = rows.FindIndex(r => r.Region == ChromeRegion.Queue);
                Assert.True(rows[queueStart].Visible.Trim().Length == 0,
                    $"the queue block lost the blank above it, {dump}");
            }

        //at 8 rows with the purr the budget is five, so even the newest queued message drops when the block is over budget.
        var (tight, _) = Wire(60, 8);
        tight.State.PurrText = "purr 3s";
        tight.State.QueueTexts = new[] { "only queued message" };
        tight.Repaint();
        var tightBlock = tight.ComposeChromeBlock(60, 8);
        Assert.True(tightBlock.Rows.Count <= 5,
            "the newest queued message pushed the block past its budget:\n"
            + string.Join("\n", tightBlock.Rows.Select(r => $"{r.Region}: {r.Visible}")));
    }

    [Fact]
    public void Panel_ReplacesEditorRows_KeepsRulesAndStatus()
    {
        var (p, s) = Wire(60, 20, "typing here");

        var before = p.ComposeChromeBlock(60, 20);
        var topBefore = before.Rows.Single(r => r.Visible.Contains("gatto · coder", StringComparison.Ordinal));
        var ruleBefore = before.Rows[^2];
        var statusBefore = before.Rows[^1];
        Assert.Contains(before.Rows, r => r.Region == ChromeRegion.Composer);

        p.SetPanel(Panel(4));
        var after = p.ComposeChromeBlock(60, 20);

        //the frame's own rules and status must not change.
        var topAfter = after.Rows.Single(r => r.Visible.Contains("gatto · coder", StringComparison.Ordinal));
        Assert.Equal(topBefore.Rendered, topAfter.Rendered);
        Assert.Equal(ruleBefore.Rendered, after.Rows[^2].Rendered);
        Assert.Equal(statusBefore.Rendered, after.Rows[^1].Rendered);

        //the editor rows leave the block, glyph and text alike.
        Assert.DoesNotContain(after.Rows, r => r.Region == ChromeRegion.Composer);
        Assert.DoesNotContain(after.Rows, r => r.Visible.Contains("typing here", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Rows, r => r.Visible.Contains("❯ ", StringComparison.Ordinal));

        //the panel rows are prompt-region chrome, in order, and no hit-test may read them as composer text.
        Assert.Equal(Panel(4), after.Rows.Where(r => r.Region == ChromeRegion.Prompt).Select(r => r.Visible));

        //check the same claims against the bytes actually written.
        p.Repaint();
        Assert.Contains(s.Viewport, r => r.Contains("p03", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Viewport, r => r.Contains("typing here", StringComparison.Ordinal));
        Assert.Equal(new string('─', 60), s.Viewport[^2]);
        //a garbled row passes a non-empty check, so assert a known status field instead.
        Assert.Contains("qwen", s.Viewport[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_ComposerTextSurvives()
    {
        //the caret sits inside the first line on purpose. the default parks it at the end, which would make the round-trip pass for the wrong reason.
        var (p, _) = Wire(60, 20, "abc", "def");
        p.State.Composer = new EditorView(new List<string> { "abc", "def" }, 0, 1);
        var before = Rows(p, 60, 20, ChromeRegion.Composer);
        var caretBefore = p.ComposeChromeBlock(60, 20);
        Assert.Equal(2, before.Count);

        p.SetPanel(Panel(3));
        Assert.Empty(Rows(p, 60, 20, ChromeRegion.Composer));

        p.SetPanel(null);
        var after = Rows(p, 60, 20, ChromeRegion.Composer);
        var caretAfter = p.ComposeChromeBlock(60, 20);

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Rendered, after[i].Rendered);   //the restored row must match byte for byte, escapes included.
            Assert.Equal(before[i].Visible, after[i].Visible);
            Assert.Equal(before[i].RegionRow, after[i].RegionRow);
        }
        Assert.Equal(caretBefore.CaretRow, caretAfter.CaretRow);
        Assert.Equal(caretBefore.CaretCol, caretAfter.CaretCol);
    }

    [Fact]
    public void EmptyPanel_IsNotAPanel_EditorKeepsRendering()
    {
        //an empty panel is not a panel, so it must not blank the composer or park the caret on nothing.
        var (p, _) = Wire(60, 20, "typing here");
        p.SetPanel(Array.Empty<string>());
        Assert.NotEmpty(Rows(p, 60, 20, ChromeRegion.Composer));
        Assert.Contains(p.ComposeChromeBlock(60, 20).Rows,
            r => r.Visible.Contains("typing here", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyPanel_AtTheFrameApi_ComposesTheEditorNotABodylessFrame()
    {
        //the frame guards an empty panel, Compose is public api and an unguarded one throws. the painter never passes one, so only a direct call checks it
        var s = new VtScreenSurface(15, 20);   //the width is below the framed minimum, the case that throws.
        var f = new InputFrame(s, T, Role, Status(), glyphs: GlyphSet.Unicode);
        var view = new EditorView(new List<string> { "abc" }, 0, 3);

        var layout = f.Compose(view, panelRows: Array.Empty<string>());

        Assert.Equal(0, layout.PanelRowCount);
        Assert.NotEmpty(layout.EditorRowTags);
        Assert.Contains(layout.VisibleRows, r => r.Contains("abc", StringComparison.Ordinal));
    }

    [Fact]
    public void Panel_CursorParksOnLastPanelRow()
    {
        var (p, _) = Wire(60, 20, "typing here");
        var rows = new[] { "  question?", "  1. yes", "  > typed answer" };
        p.SetPanel(rows);

        var block = p.ComposeChromeBlock(60, 20);
        var lastPanel = block.Rows.Select((r, i) => (r, i)).Last(x => x.r.Region == ChromeRegion.Prompt);

        Assert.Equal("  > typed answer", lastPanel.r.Visible);   //the free-text input line is always the last panel row.
        Assert.Equal(lastPanel.i, block.CaretRow);
        Assert.Equal(UnicodeWidth.Of("  > typed answer"), block.CaretCol);
        //two frame rows still sit below the caret.
        Assert.Equal(block.Rows.Count - 3, block.CaretRow);
    }

    [Fact]
    public void Panel_TallPanel_ClampsFromTop()
    {
        var (p, _) = Wire(60, 10);
        p.SetPanel(Panel(20));

        var block = p.ComposeChromeBlock(60, 10);
        var panel = block.Rows.Where(r => r.Region == ChromeRegion.Prompt).Select(r => r.Visible).ToList();

        Assert.True(block.Rows.Count <= 10, $"block outgrew the viewport: {block.Rows.Count} rows");
        Assert.Contains("  p19", panel);                 //the actionable tail of the panel survives.
        Assert.DoesNotContain("  p00", panel);           //the head rows are what drop.
        Assert.DoesNotContain("  p01", panel);
        //with a top-drop clamp, what survives must be a contiguous suffix of the panel.
        Assert.Equal(Panel(20)[^panel.Count..], panel);
        //the frame rules and status must always survive the clamp.
        Assert.Contains(block.Rows, r => r.Visible.Contains("gatto · coder", StringComparison.Ordinal));
        Assert.Equal(new string('─', 60), block.Rows[^2].Visible);
        Assert.Contains("qwen", block.Rows[^1].Visible, StringComparison.Ordinal);   //assert the status is intact, a non-empty row could still be garbled
    }

    //a panel over a live call may take that call's rows. the prompt gets the room it would have with no call running
    [Fact]
    public void A_panel_takes_the_rows_of_the_live_call_it_asks_about()
    {
        const int Height = 20;
        var (p, _) = Wire(60, Height);
        p.SetPanel(Panel(1));
        var bare = p.PanelRowsAvailable();

        p.State.Tool = ("shell", "Write-Output 1");
        p.State.ToolInput = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"Write-Output {i}"));
        p.State.ToolWait = "waiting for your input";
        var withCall = p.PanelRowsAvailable();
        Assert.Equal(bare, withCall);

        p.SetPanel(Panel(withCall));
        var block = p.ComposeChromeBlock(60, Height);
        Assert.Equal(Panel(withCall), block.Rows.Where(r => r.Region == ChromeRegion.Prompt).Select(r => r.Visible).ToList());
        Assert.True(block.Rows.Count <= Height, $"{block.Rows.Count} rows in a {Height}-row viewport");
    }

    //exactly what PanelRowsAvailable offers must fit whole, and one row more must lose exactly one. compare against the literal viewport
    [Fact]
    public void THE_PAINTER_NEVER_DROPS_A_ROW_IT_SAID_A_PANEL_COULD_HAVE()
    {
        const int Height = 30;
        var (p, _) = Wire(60, Height);

        var allowed = p.PanelRowsAvailable();
        p.SetPanel(Panel(allowed));
        var block = p.ComposeChromeBlock(60, Height);
        var fits = block.Rows
            .Where(r => r.Region == ChromeRegion.Prompt).Select(r => r.Visible).ToList();

        Assert.Equal(Panel(allowed), fits);
        Assert.True(block.Rows.Count <= Height,
            $"the painter offered {allowed} panel rows and then composed {block.Rows.Count} rows "
            + $"into a {Height}-row viewport");

        //one more row than offered must lose exactly one, so the offer is exact
        p.SetPanel(Panel(allowed + 1));
        var overBlock = p.ComposeChromeBlock(60, Height);
        var over = overBlock.Rows
            .Where(r => r.Region == ChromeRegion.Prompt).Select(r => r.Visible).ToList();

        Assert.Equal(allowed, over.Count);
        Assert.Equal(Panel(allowed + 1)[1..], over);   //the first panel row is the row that dropped.
        Assert.True(overBlock.Rows.Count <= Height, $"an over-tall panel still overflowed: {overBlock.Rows.Count}");
    }

    [Fact]
    public void OverTallPanel_ComposerScroll_RevealsRowsFromTheTop()
    {
        var (p, s) = Wire(60, 20);
        p.SetPanel(Panel(30));

        var tailOnly = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        Assert.DoesNotContain("  p00", tailOnly);

        p.ComposerScroll.ScrollBy(5);
        var scrolled = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        Assert.NotEqual(tailOnly, scrolled);
        var shift = int.Parse(tailOnly[0].Trim()[1..]) - int.Parse(scrolled[0].Trim()[1..]);
        Assert.Equal(5, shift);
    }

    [Fact]
    public void OverTallPanel_ContentThatFits_NeverClamped_ScrollIsANoOp()
    {
        var (p, s) = Wire(60, 20);
        p.SetPanel(Panel(3));
        var before = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        p.ComposerScroll.ScrollBy(50);
        var after = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void OverTallPanel_ScrolledFullyUp_StillWindowedToPanelAllowed()
    {
        //offset 0 happens both when the content fits and when a tall panel is scrolled to the top. a guard that skips the clamp at zero misses that case
        var (p, s) = Wire(60, 20);
        var full = Panel(30);
        p.SetPanel(full);
        var allowed = p.PanelRowsAvailable();
        p.ComposerScroll.ScrollBy(9999);
        var atTop = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        Assert.Equal(allowed, atTop.Count);                 //the panel is windowed here, a raw count would mean the clamp never ran
        Assert.Equal(full.Take(allowed).ToList(), atTop);   //at the top the first allowed rows are the visible ones.
    }

    [Fact]
    public void ScrollingPastTheCaretsRow_HidesTheCaret_ForThatPaintOnly()
    {
        var (p, s) = Wire(60, 20);
        var panel = Panel(30);
        p.SetPanelFactory((w, h) => new PanelContent(panel, Caret: (panel.Length - 1, 2)));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);
        Assert.True(block.CaretVisible);

        p.ComposerScroll.ScrollBy(20);
        var scrolled = p.ComposeChromeBlock(60, 20);
        Assert.False(scrolled.CaretVisible);

        p.ComposerScroll.Reset();
        var back = p.ComposeChromeBlock(60, 20);
        Assert.True(back.CaretVisible);
    }

    [Fact]
    public void WheelOverThePromptPanel_ScrollsIt_ThroughTheRealMouseController()
    {
        var (p, s) = Wire(60, 20);
        p.SetPanel(Panel(30));
        p.Repaint();

        var promptY = Enumerable.Range(0, 20).First(y => s.Viewport[y].TrimStart().StartsWith("p", StringComparison.Ordinal));

        var before = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        p.Mouse.Handle(new MouseEvent(4, promptY, MouseKind.Wheel, MouseButton.None, 120, 0), 60, p.ViewportRows());
        var after = Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible).ToList();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ScrollingUp_ThenTheSamePanelAdvancesAStep_SnapsBackToTheNewTail()
    {
        var (p, s) = Wire(60, 20);
        var page = 0;
        p.SetPanelFactory((w, h) => new PanelContent(Panel(30 + page)));
        p.Repaint();
        p.ComposerScroll.ScrollBy(10);
        Assert.False(p.ComposerScroll.Following);

        page = 1;
        p.Repaint();
        Assert.True(p.ComposerScroll.Following);
    }

    [Theory]
    [InlineData(4, 0)]   //at height four the floor and the frame fill the screen, so painted rows are zero.
    [InlineData(5, 1)]   //at height five the frame takes four rows, so one row is painted.
    [InlineData(6, 2)]   //at height six the frame takes four rows, so two rows are painted.
    public void AtSmallHeights_TheMathMaxThreeFloorPins_ThisExactPaintedCount(int h, int expectedPainted)
    {
        //below height seven the budget floor of three rows decides the count, and it cannot fit the frame rows plus a panel row
        var (p, s) = Wire(60, h);
        p.SetPanel(Panel(80));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, h);
        Assert.Equal(expectedPainted, h - block.Rows.Count);
    }

    [Theory]
    [InlineData(7)] [InlineData(10)] [InlineData(15)] [InlineData(20)] [InlineData(40)]
    public void AtHeightSevenAndAbove_TranscriptPaintsExactlyThreeRows(int h)
    {
        //assert the exact three rows, a reservation one row too big would pass a lower bound and steal a row from the panel
        var (p, s) = Wire(60, h);
        p.SetPanel(Panel(80));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, h);
        Assert.Equal(3, h - block.Rows.Count);
    }

    [Fact]
    public void FullAllowancePanel_WithLivePurrAndTool_BothSurvive()
    {
        var (p, s) = Wire(60, 20);
        p.State.PurrText = "purr 3s";
        p.State.Tool = ("ask_user", "");
        var allowed = p.PanelRowsAvailable();
        p.SetPanel(Panel(allowed));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);

        Assert.Contains(block.Rows, r => r.Region == ChromeRegion.Purr);
        Assert.Contains(block.Rows, r => r.Region == ChromeRegion.Tool);
        Assert.Equal(allowed, Rows(p, 60, 20, ChromeRegion.Prompt).Count);
        Assert.True(block.Rows.Count <= 20 - 3);
    }

    [Fact]
    public void FullAllowancePanel_TheProductsActualPanelUpState_ToolWaitSurvivesNoSeparatePurrRow()
    {
        //with a panel up there's no purr row, the wait text sits on the tool row as a suffix. this fixture builds that state
        var (p, s) = Wire(60, 20);
        p.State.Tool = ("ask_user", "");
        p.State.ToolWait = ChromeTicker.WaitingRow(12_000, 1_900, 0, null);
        var allowed = p.PanelRowsAvailable();
        p.SetPanel(Panel(allowed));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);
        var toolRows = block.Rows.Where(r => r.Region == ChromeRegion.Tool).ToList();

        Assert.DoesNotContain(block.Rows, r => r.Region == ChromeRegion.Purr);
        Assert.Contains(toolRows, r => r.Visible.Contains("waiting for your input", StringComparison.Ordinal));
        Assert.Equal(allowed, Rows(p, 60, 20, ChromeRegion.Prompt).Count);
        Assert.True(block.Rows.Count <= 20 - 3);
    }

    //a panel that needs the live call's rows takes them and is drawn whole, the purr survives, and one row more than offered loses exactly one
    [Fact]
    public void OneRowOverAllowance_TheLiveCallGivesWay_PurrSurvives_PanelLosesOne()
    {
        var (p, s) = Wire(60, 20);
        p.State.PurrText = "purr 3s";
        p.State.Tool = ("ask_user", "");
        p.SetPanel(Panel(1));
        var allowed = p.PanelRowsAvailable();

        p.SetPanel(Panel(allowed));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);
        Assert.Contains(block.Rows, r => r.Region == ChromeRegion.Purr);
        Assert.DoesNotContain(block.Rows, r => r.Region == ChromeRegion.Tool);
        Assert.Equal(allowed, Rows(p, 60, 20, ChromeRegion.Prompt).Count);

        p.SetPanel(Panel(allowed + 1));
        Assert.Equal(allowed, Rows(p, 60, 20, ChromeRegion.Prompt).Count);
    }

    [Theory]
    [InlineData(1, false)]   //an older message of one row is shown, with no roll-up.
    [InlineData(2, true)]    //an older message of two rows is replaced by the more-count line.
    public void QueuesNewestMessage_SurvivesUnderPanelPressure_OlderRollsUpAtTheExactBoundary(int olderRows, bool expectRollup)
    {
        //build a message that wraps to exactly the stated rows at width 58, and assert that first
        var older = string.Join(" ", Enumerable.Repeat("word", olderRows == 1 ? 6 : 20));

        var (probe, _) = Wire(60, 40);
        probe.State.QueueTexts = new[] { older };
        probe.Repaint();
        Assert.Equal(olderRows + 1, Rows(probe, 60, 40, ChromeRegion.Queue).Count);   //the count includes the blank row above the queue.

        var (p, s) = Wire(60, 20);
        p.State.PurrText = "purr 3s";
        p.State.Tool = ("ask_user", "");
        p.State.QueueTexts = new[] { older, "newest queued message" };
        var allowed = p.PanelRowsAvailable();
        p.SetPanel(Panel(allowed));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);
        var queueRows = block.Rows.Where(r => r.Region == ChromeRegion.Queue).ToList();

        Assert.Contains(queueRows, r => r.Visible.Contains("newest queued message", StringComparison.Ordinal));
        Assert.Equal(expectRollup, queueRows.Any(r => r.Visible.Contains("more)", StringComparison.Ordinal)));
        Assert.Equal(!expectRollup, queueRows.Any(r => r.Visible.Contains("word", StringComparison.Ordinal)));
        Assert.True(block.Rows.Count <= 17);
        Assert.Equal(allowed, Rows(p, 60, 20, ChromeRegion.Prompt).Count);   //assert the panel keeps its full offered row count while the queue squeezes its budget.
    }

    [Fact]
    public void QueuesNewestMessage_SurvivesUnderPanelPressure_OlderFarOverBudgetAlsoRollsUp()
    {
        var (p, s) = Wire(60, 20);
        p.State.PurrText = "purr 3s";
        p.State.Tool = ("ask_user", "");
        p.State.QueueTexts = new[] { string.Join(" ", Enumerable.Repeat("older", 40)), "newest queued message" };
        var allowed = p.PanelRowsAvailable();
        p.SetPanel(Panel(allowed));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);
        var queueRows = block.Rows.Where(r => r.Region == ChromeRegion.Queue).ToList();

        Assert.Contains(queueRows, r => r.Visible.Contains("newest queued message", StringComparison.Ordinal));
        Assert.Contains(queueRows, r => r.Visible.Contains("more)", StringComparison.Ordinal));
        Assert.DoesNotContain(queueRows, r => r.Visible.Contains("older", StringComparison.Ordinal));
        Assert.True(block.Rows.Count <= 17);
    }

    [Fact]
    public void QueuesNewestMessage_TwoOneRowOldersNeedsTheRefillPass_OrTheWholeCategoryDrops()
    {
        //the only fixture that reaches the queue fill's refill branch, where the whole category would otherwise drop and take the newest message with it
        var (p, s) = Wire(60, 20);
        p.State.PurrText = "purr 3s";
        p.State.Tool = ("ask_user", "");
        p.State.QueueTexts = new[] { "olderA one row", "olderB one row", "newest queued message" };
        var allowed = p.PanelRowsAvailable();
        p.SetPanel(Panel(allowed));
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 20);
        var queueRows = block.Rows.Where(r => r.Region == ChromeRegion.Queue).ToList();

        Assert.Contains(queueRows, r => r.Visible.Contains("newest queued message", StringComparison.Ordinal));
        Assert.Contains(queueRows, r => r.Visible.Contains("(+2 more)", StringComparison.Ordinal));
        Assert.DoesNotContain(queueRows, r => r.Visible.Contains("olderA", StringComparison.Ordinal));
        Assert.DoesNotContain(queueRows, r => r.Visible.Contains("olderB", StringComparison.Ordinal));
        Assert.True(block.Rows.Count <= 17);
        Assert.Equal(allowed, Rows(p, 60, 20, ChromeRegion.Prompt).Count);   //assert the panel keeps its full offered row count while the queue refill runs.
    }

    [Fact]
    public void AtNineRows_NoPanel_TheTranscriptMinimumCostsTheOlderQueueNotTheNewest()
    {
        //at height nine the queue is one row over budget, so the clamp drops the older message and its roll-up and keeps the newest
        var (p, s) = Wire(60, 9);
        p.State.QueueTexts = new[] { "older queued message", "newest queued message" };
        p.Repaint();
        var block = p.ComposeChromeBlock(60, 9);
        Assert.Contains(block.Rows, r => r.Region == ChromeRegion.Queue
                                         && r.Visible.Contains("newest queued message", StringComparison.Ordinal));
        Assert.DoesNotContain(block.Rows, r => r.Visible.Contains("more)", StringComparison.Ordinal));
        Assert.True(block.Rows.Count <= 6);
    }

    //height zero means the terminal would not say, so the offer must be zero, or a sizing caller builds rows nobody can see
    [Fact]
    public void AN_UNKNOWN_HEIGHT_OFFERS_NOTHING_RATHER_THAN_EVERYTHING()
    {
        var s = new VtScreenSurface(60, 0);   //built by hand, Wire repaints and a zero-row surface can't be painted on
        var p = new ChromePainter(s, T, new object())
        {
            Frame = new InputFrame(s, T, Role, Status(), glyphs: GlyphSet.Unicode),
            RoleForTint = Role,
        };

        Assert.Equal(0, p.PanelRowsAvailable());
        Assert.NotEqual(int.MaxValue, p.PanelRowsAvailable());
    }

    //the offer tracks height, so the tall-minus-small difference proves the scaling rather than a tuned constant
    [Fact]
    public void A_TALLER_TERMINAL_OFFERS_A_PANEL_MORE_ROWS()
    {
        var (tall, _) = Wire(60, 40);
        var (small, _) = Wire(60, 20);

        Assert.Equal(20, tall.PanelRowsAvailable() - small.PanelRowsAvailable());
    }

    [Fact]
    public void Panel_ShorterThanTheBudget_IsNotClamped()
    {
        //the clamp is a last resort. a panel that already fits must pass through whole.
        var (p, _) = Wire(60, 20);
        p.SetPanel(Panel(6));
        Assert.Equal(Panel(6), Rows(p, 60, 20, ChromeRegion.Prompt).Select(r => r.Visible));
    }

    [Fact]
    public void Panel_Resize_RepaintsWithoutDesync()
    {
        var (p, s) = Wire(100, 20, "typing here");
        var wide = new[]
        {
            "  " + new string('q', 90),
            "  1. " + new string('y', 85),
            "  Esc to cancel",
        };
        p.SetPanel(wide);
        Assert.All(p.ComposeChromeBlock(100, 20).Rows, r => Assert.True(UnicodeWidth.Of(r.Visible) <= 100));

        s.Resize(60, 20);
        p.Repaint();

        //a pure resize repaints the rows it last built, so a stale wide row must be cut to the new width. a wrapped one desyncs the caret park and the row count
        var block = p.ComposeChromeBlock(60, 20);
        Assert.All(block.Rows, r => Assert.True(UnicodeWidth.Of(r.Visible) <= 60,
            $"row overflows the resized surface ({UnicodeWidth.Of(r.Visible)} cells): '{r.Visible}'"));
        Assert.All(s.Viewport, r => Assert.True(UnicodeWidth.Of(r) <= 60));

        //rules and status must re-rule to the new width.
        Assert.Equal(new string('─', 60), block.Rows[^2].Visible);
        Assert.Equal(60, UnicodeWidth.Of(block.Rows.Single(
            r => r.Visible.Contains("gatto · coder", StringComparison.Ordinal)).Visible));
        Assert.Equal(new string('─', 60), s.Viewport[^2]);
        Assert.Contains("qwen", s.Viewport[^1], StringComparison.Ordinal);   //the status line must stay intact at the new width, so assert its content

        //the caret must stay parked on the panel's last row, inside the narrowed frame.
        Assert.Equal(block.Rows.Count - 3, block.CaretRow);
        Assert.True(block.CaretCol < 60);
        Assert.Equal(0, s.Scrolled);   //the compositor must not scroll during the reflow.
    }

    [Fact]
    public void Panel_BelowMinFramedWidth_HasNoRulesAndStillTagsEveryRow()
    {
        //below MinFramedWidth the frame emits no rules and no status, so panel rows start at row zero. the caret math counts only the rows actually emitted
        var (p, _) = Wire(15, 20, "typing here");
        p.SetPanel(new[] { "a", "b", "c" });

        var block = p.ComposeChromeBlock(15, 20);
        var panel = block.Rows.Where(r => r.Region == ChromeRegion.Prompt).ToList();

        Assert.Equal(new[] { "a", "b", "c" }, panel.Select(r => r.Visible));
        Assert.Equal(new[] { 0, 1, 2 }, panel.Select(r => r.RegionRow));
        Assert.DoesNotContain(block.Rows, r => r.Region == ChromeRegion.Rule);
        Assert.DoesNotContain(block.Rows, r => r.Region == ChromeRegion.Composer);
        Assert.Equal("c", block.Rows[block.CaretRow].Visible);   //the caret must sit on the last panel row.
    }

    //only a visible panel may produce prompt rows, or drag-select and hit-testing would read them as composer text
    [Fact]
    public void NoPanel_NoRowCarriesThePromptRegion()
    {
        var (p, _) = Wire(60, 20, "typing here");

        var bare = p.ComposeChromeBlock(60, 20);
        Assert.False(p.State.PanelUp);
        Assert.DoesNotContain(bare.Rows, r => r.Region == ChromeRegion.Prompt);
        Assert.Contains(bare.Rows, r => r.Visible.Contains("typing here", StringComparison.Ordinal));

        //the panel replaces the composer text, it does not dim it above the panel.
        p.SetPanel(new[] { "  question?", "❯ 1. yes" });
        var panelled = p.ComposeChromeBlock(60, 20);
        Assert.Equal(2, panelled.Rows.Count(r => r.Region == ChromeRegion.Prompt));
        Assert.DoesNotContain(panelled.Rows, r => r.Visible.Contains("typing here", StringComparison.Ordinal));
    }
}
