//this class lays out the top rule, the status line and the composed rows with their caret position, the painter draws them
using System.Globalization;
using Gatto.Repl.Input;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class InputFrameTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static StatusInfo Status(int? window = null, int used = 0)
    {
        var ctx = new CtxState { ContextWindow = window };
        if (used > 0) ctx.RecordUsage(new Gatto.Core.Client.Usage(used, 0));
        return new StatusInfo(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", ctx, @"C:\Users\user");
    }

    [Fact]   //the footer shows the hint when it is set and leaves it out when it is not
    public void Footer_shows_the_focus_keymap_hint_when_set()
    {
        var hinted = Status() with { FocusHint = FocusKeys.HintOf(glyphs: GlyphSet.Unicode) };
        Assert.Contains("Ctrl+R", StripSgr(InputFrame.BuildStatusLine(hinted, 200, T, glyphs: GlyphSet.Unicode)), StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+R", StripSgr(InputFrame.BuildStatusLine(Status(), 200, T, glyphs: GlyphSet.Unicode)), StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_keymap_hint_is_flush_right_on_a_wide_footer()
    {
        var hinted = Status() with { FocusHint = FocusKeys.HintOf(glyphs: GlyphSet.Unicode) };
        var visible = StripSgr(InputFrame.BuildStatusLine(hinted, 200, T, glyphs: GlyphSet.Unicode));
        Assert.EndsWith(FocusKeys.HintOf(glyphs: GlyphSet.Unicode), visible, StringComparison.Ordinal);   //the hint is the last text on the row.
        Assert.Equal(200, UnicodeWidth.Of(visible));                          //the row is padded to the full width.
        //the core sits on the left with folder and model unabbreviated
        Assert.StartsWith(@"  ~\projects\gatto · qwen3.6-35b", visible, StringComparison.Ordinal);
    }

    [Fact]   //the hint drops before any of the core is elided
    public void Focus_keymap_hint_drops_first_when_the_footer_is_tight()
    {
        var hinted = Status() with { FocusHint = FocusKeys.HintOf(glyphs: GlyphSet.Unicode) };
        //45 fits the core on its own, and the core plus the hint needs more
        var visible = StripSgr(InputFrame.BuildStatusLine(hinted, 45, T, glyphs: GlyphSet.Unicode));
        Assert.DoesNotContain("Ctrl+R", visible, StringComparison.Ordinal);
        Assert.Contains(@"~\projects\gatto", visible, StringComparison.Ordinal);   //the folder stays unabbreviated.
        Assert.Contains("qwen3.6-35b", visible, StringComparison.Ordinal);         //the model name stays unabbreviated.
    }

    [Fact]
    public void Status_folder_keeps_a_sibling_of_the_profile_whole()
    {
        //a folder whose name only starts with the profile's name is not under the profile, so it must not read as ~
        var sibling = new StatusInfo(@"C:\Users\user2\projects", "qwen3.6-35b", "coder", new CtxState(), @"C:\Users\user");
        var visible = StripSgr(InputFrame.BuildStatusLine(sibling, 200, T, glyphs: GlyphSet.Unicode));
        Assert.StartsWith(@"  C:\Users\user2\projects · qwen3.6-35b", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_folder_is_a_bare_tilde_at_the_profile_itself()
    {
        var home = new StatusInfo(@"C:\Users\user", "qwen3.6-35b", "coder", new CtxState(), @"C:\Users\user");
        var visible = StripSgr(InputFrame.BuildStatusLine(home, 200, T, glyphs: GlyphSet.Unicode));
        Assert.StartsWith("  ~ · qwen3.6-35b", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void TopRule_LabelRightAligned()
    {
        var rule = InputFrame.BuildTopRule("coder", width: 40, T);
        Assert.Contains(T.Paint("gatto", Theme.Accent, bold: true), rule);
        Assert.Contains(T.Paint("coder", Theme.RoleCoder), rule);
        Assert.EndsWith(T.Paint("──", Theme.Rule), rule);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(121)]
    public void TopRule_FillsWidthExactly_SpacedLabel(int width)
    {
        var rule = InputFrame.BuildTopRule("coder", width, T);
        var visible = StripSgr(rule);
        Assert.Equal(width, UnicodeWidth.Of(visible));   //the rule is exactly the width it was given, so it never overflows
        Assert.Contains("gatto · coder", visible);       //the label uses a spaced middle dot, matching the status line.
    }

    [Fact]   //an armed hint takes the label's slot and adds no row
    public void An_armed_hint_replaces_the_role_label_on_the_top_rule()
    {
        var plain = InputFrame.BuildTopRule("coder", 60, T);
        var hinted = InputFrame.BuildTopRule("coder", 60, T, hint: "Esc again to clear");

        Assert.Contains("Esc again to clear", hinted, StringComparison.Ordinal);
        Assert.DoesNotContain("coder", hinted);
        Assert.NotEqual(plain, hinted);
    }

    [Fact]
    public void A_hinted_top_rule_occupies_exactly_one_row_at_the_same_width()
    {
        //a hint must not change the height of the chrome block at any width
        foreach (var w in new[] { 20, 40, 80, 120 })
        {
            var plain = InputFrame.BuildTopRuleVisible("coder", w, T);
            var hinted = InputFrame.BuildTopRuleVisible("coder", w, T, hint: "Ctrl+C again to quit");
            Assert.Equal(1, UnicodeWidth.Rows(plain, w));
            Assert.Equal(1, UnicodeWidth.Rows(hinted, w));
        }
    }

    [Fact]
    public void A_hint_too_wide_for_the_rule_degrades_without_wrapping()
    {
        var v = InputFrame.BuildTopRuleVisible("coder", 12, T, hint: "Ctrl+C again to quit");
        Assert.Equal(1, UnicodeWidth.Rows(v, 12));   //an over-wide hint is truncated to one row at width 12
    }

    [Fact]
    public void A_null_hint_renders_the_rule_exactly_as_before()
    {
        //a null hint renders the rule as the call with no hint does, so the default has to stay null
        var rule = InputFrame.BuildTopRule("coder", 80, T, wild: true, hint: null);
        var visible = StripSgr(rule);
        Assert.Contains("gatto · coder · wild", visible);
        Assert.Contains(T.Paint("gatto", Theme.Accent, bold: true), rule);
        Assert.Contains(T.Paint("coder", T.RoleTint("coder")), rule);
        Assert.Contains(T.Paint("wild", Theme.Err), rule);
        Assert.Equal(InputFrame.BuildTopRule("coder", 80, T, wild: true), rule);
    }

    [Fact]
    public void StatusLine_AbbreviatesHome_OmitsUnknownCtx()
    {
        var line = InputFrame.BuildStatusLine(Status(), width: 80, T, glyphs: GlyphSet.Unicode);
        Assert.Contains(@"~\projects\gatto", line);
        Assert.DoesNotContain("ctx", line);
    }

    [Fact]
    public void StatusLine_ShowsCtx_WarnAt80()
    {
        var cool = InputFrame.BuildStatusLine(Status(window: 1000, used: 340), 80, T, glyphs: GlyphSet.Unicode);
        Assert.Contains("ctx 34%", cool);
        Assert.DoesNotContain(Ansi.Fg(Theme.Warn, true), cool, StringComparison.Ordinal);
        var hot = InputFrame.BuildStatusLine(Status(window: 1000, used: 800), 80, T, glyphs: GlyphSet.Unicode);
        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "ctx 80%", hot, StringComparison.Ordinal);
    }

    [Fact]
    public void TopRule_ShowsWild_OnTheComposerRule_KeepingAccentTint()
    {
        //the wild flag appears on the composer's top rule, and the footer leaves it out
        var on = InputFrame.BuildTopRule("coder", width: 60, T, wild: true);
        Assert.Contains("gatto · coder · wild", StripSgr(on));
        Assert.Contains(T.Paint("wild", Theme.Err), on);

        var off = InputFrame.BuildTopRule("coder", width: 60, T, wild: false);
        Assert.DoesNotContain("wild", StripSgr(off));
    }

    [Fact]
    public void StatusLine_NeverShowsRoleOrWild_TheyLiveOnTheRule()
    {
        var s = Status() with { Role = "oracle", Wild = true };
        var visible = StripSgr(InputFrame.BuildStatusLine(s, 120, T, glyphs: GlyphSet.Unicode));
        Assert.DoesNotContain("wild", visible);
        Assert.DoesNotContain("oracle", visible);
    }

    [Fact]
    public void StatusLine_ShowsEffort_InParentheses_AfterTheModel()
    {
        var s = Status() with { Thinking = "high" };
        Assert.Contains("qwen3.6-35b (high)", StripSgr(InputFrame.BuildStatusLine(s, 120, T, glyphs: GlyphSet.Unicode)));

        var off = StripSgr(InputFrame.BuildStatusLine(Status(), 120, T, glyphs: GlyphSet.Unicode));
        Assert.DoesNotContain("(", off);
    }

    [Fact]
    public void StatusLine_ToggleModel_ReadsReasoningOrNonReasoning_AtEveryWidth()
    {
        //a toggle model's footer names the mode in parentheses, never the old thinking on or off words
        var on = Status() with { Thinking = "thinking on", ThinkingToggle = true };
        var off = Status() with { Thinking = "thinking off", ThinkingToggle = true };
        for (var w = 40; w <= 120; w += 5)
        {
            var onLine = StripSgr(InputFrame.BuildStatusLine(on, w, T, glyphs: GlyphSet.Unicode));
            var offLine = StripSgr(InputFrame.BuildStatusLine(off, w, T, glyphs: GlyphSet.Unicode));
            Assert.Contains("(reasoning)", onLine);
            Assert.Contains("(non-reasoning)", offLine);
            Assert.DoesNotContain("thinking", onLine + offLine);
        }
    }

    [Fact]
    public void StatusLine_WithServingMismatch_RendersTheWarningChip()
    {
        var s = Status() with { Model = "gemma-4-26b", Serving = "qwen3.6-35b" };
        var text = StripSgr(InputFrame.BuildStatusLine(s, 120, T, glyphs: GlyphSet.Unicode));
        Assert.Contains("gemma-4-26b", text);
        Assert.Contains("serving qwen3.6-35b", text);
    }

    [Fact]
    public void StatusLine_WithoutMismatch_RendersNoChip()
    {
        var s = Status() with { Model = "gemma-4-26b", Serving = null };
        Assert.DoesNotContain("serving", StripSgr(InputFrame.BuildStatusLine(s, 120, T, glyphs: GlyphSet.Unicode)));
    }

    [Fact]
    public void StatusLine_ServingMismatch_UsesTheThemesWarnTint()
    {
        var s = Status() with { Model = "gemma-4-26b", Serving = "qwen3.6-35b" };
        var line = InputFrame.BuildStatusLine(s, 120, T, glyphs: GlyphSet.Unicode);
        Assert.Contains(Ansi.Fg(Theme.Warn, true) + " ⚠ serving qwen3.6-35b", line, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusLine_LongCwd_DegradesToLastSegment_OneRow()
    {
        //a cwd too long for the footer keeps the ellipsis, the separator and the last segment, so the row stays one line
        var s = Status() with { Cwd = @"C:\Users\user\" + string.Join(@"\", Enumerable.Repeat("deep", 30)) };
        var visible = StripSgr(InputFrame.BuildStatusLine(s, width: 60, T, glyphs: GlyphSet.Unicode));
        Assert.Contains(GlyphSet.Unicode.Ellipsis + @"\deep", visible);
        Assert.True(UnicodeWidth.Of(visible) <= 60, $"footer {UnicodeWidth.Of(visible)} > 60 — must be one row");
    }

    [Fact]
    public void Compose_ExposesVisibleRows_OnePerRenderedRow()
    {
        var frame = FramedFixture(width: 40);
        var layout = frame.Compose(new EditorView(new List<string> { "hello" }, 0, 5));
        Assert.Equal(layout.Rows.Count, layout.VisibleRows.Count);
        Assert.Contains("hello", layout.VisibleRows[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", layout.VisibleRows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void StatusLine_Full_RendersBranchModelTokensCtx_NoRole()
    {
        var s = Status(window: 1000, used: 340) with { Branch = "main", TokensUp = 1200, TokensDown = 3400 };
        var visible = StripSgr(InputFrame.BuildStatusLine(s, width: 100, T, glyphs: GlyphSet.Unicode));
        Assert.Contains(@"~\projects\gatto", visible);
        Assert.Contains("main", visible);
        Assert.Contains("qwen3.6-35b", visible);
        Assert.Contains("↑ 1.2k ↓ 3.4k", visible);
        Assert.Contains("ctx 34%", visible);
        Assert.DoesNotContain("coder", visible);      //the role is drawn on the top rule, so the footer leaves it out
    }

    [Fact]
    public void StatusLine_NullBranch_OmittedNoDanglingSeparator()
    {
        var visible = StripSgr(InputFrame.BuildStatusLine(Status() with { Branch = null }, width: 100, T, glyphs: GlyphSet.Unicode));
        Assert.DoesNotContain(" ·  · ", visible);   //an omitted branch must not leave a doubled separator.
        Assert.DoesNotContain("· ·", visible);
    }

    [Fact]
    public void StatusLine_ZeroTokens_SegmentOmitted()
    {
        var visible = StripSgr(InputFrame.BuildStatusLine(Status() with { TokensUp = 0, TokensDown = 0 }, 100, T, glyphs: GlyphSet.Unicode));
        Assert.DoesNotContain("↑", visible);
        Assert.DoesNotContain("↓", visible);
    }

    [Fact]
    public void StatusLine_NonZeroTokens_SegmentShown()
    {
        var visible = StripSgr(InputFrame.BuildStatusLine(Status() with { TokensUp = 999, TokensDown = 5 }, 100, T, glyphs: GlyphSet.Unicode));
        Assert.Contains("↑ 999 ↓ 5", visible);
    }

    [Fact]
    public void StatusLine_Tight_DropsBranch_KeepsCtx_OneRow()
    {
        //a tight row gives up the branch long before the ctx percent, and stays one line
        var s = Status(window: 1000, used: 340) with
        {
            Cwd = @"C:\Users\user\" + string.Join(@"\", Enumerable.Repeat("deep", 30)),
            Branch = "a-very-long-feature-branch-name-that-is-quite-long",
            TokensUp = 3_800_000,
            TokensDown = 20_000,
        };
        var visible = StripSgr(InputFrame.BuildStatusLine(s, width: 52, T, glyphs: GlyphSet.Unicode));
        Assert.True(UnicodeWidth.Of(visible) <= 52, $"footer {UnicodeWidth.Of(visible)} > 52, must be one row");
        Assert.DoesNotContain("a-very-long", visible);
        Assert.Contains("ctx 34%", visible);
    }

    [Fact]
    public void StatusLine_Ladder_FirstRungThatFitsWins_GlyphWidthAware()
    {
        //the cwd holds two wide glyphs (4 cells), so the fit measures cells rather than characters
        var s = Status(window: 1000, used: 260) with
        {
            Cwd = @"C:\Users\user\terra 月球\projects\cc-usage-monitor",
            Branch = "main", Thinking = "medium", TokensUp = 2300, TokensDown = 222,
        };

        //at full width the footer shows the full path, the effort, tokens and ctx with its count
        var full = StripSgr(InputFrame.BuildStatusLine(s, width: 120, T, glyphs: GlyphSet.Unicode));
        Assert.Contains(@"terra 月球\projects\cc-usage-monitor", full);
        Assert.Contains("qwen3.6-35b (medium)", full);
        Assert.Contains("↑ 2.3k ↓ 222", full);
        Assert.Contains("ctx 26% (260)", full);

        //at 52 the branch and the tokens are gone, the effort and the percent stay, the folder shows the ruled short form, and the row fits exactly
        var narrow = StripSgr(InputFrame.BuildStatusLine(s, width: 52, T, glyphs: GlyphSet.Unicode));
        Assert.Equal("  " + GlyphSet.Unicode.Ellipsis + @"\cc-usage-… · qwen3.6-35b (medium) · ctx 26%", narrow);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(1200, "1.2k")]
    [InlineData(34600, "34.6k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_234_567, "1.2M")]
    public void KFormat_Pins(long n, string expected) => Assert.Equal(expected, InputFrame.KFormat(n));

    [Fact]
    public void KFormat_UsesInvariantCulture_EvenUnderCommaDecimalCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");   //force a culture that uses comma decimal separators.
            Assert.Equal("1.2k", InputFrame.KFormat(1200));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void NarrowTerminal_SkipsRulesAndStatus()
    {
        var layout = FramedFixture(width: 15).Compose(new EditorView(new[] { "x" }, 0, 1));
        Assert.All(layout.VisibleRows, r => Assert.DoesNotContain("─", r));
        Assert.Single(layout.Rows);
    }

    [Fact]
    public void Compose_LongLogicalLine_SoftWrapsWithHang_NoMidWordBreak()
    {
        var layout = FramedFixture(width: 20).Compose(
            new EditorView(new[] { "lorem ipsum dolor sit amet consectetur" }, 0, 0));
        var editorRows = EditorRows(layout);
        Assert.True(editorRows.Length >= 2);
        Assert.StartsWith("❯ lorem", editorRows[0]);
        Assert.All(editorRows, r => Assert.True(UnicodeWidth.Of(r) <= 20));
        //every word must stay whole inside one visual row.
        foreach (var w in new[] { "lorem", "ipsum", "dolor", "sit", "amet", "consectetur" })
            Assert.Contains(editorRows, r => r.Contains(w));
    }

    [Fact]
    public void Compose_ContinuationMark_OnlyAtLogicalJoins()
    {
        var layout = FramedFixture(width: 20).Compose(
            new EditorView(new[] { "first logical line that wraps", "second" }, 1, 0));
        var rows = EditorRows(layout);
        Assert.Equal(1, rows.Count(r => r.TrimEnd().EndsWith("\\")));
        Assert.False(rows[0].TrimEnd().EndsWith("\\"));
    }

    [Fact]
    public void Compose_CursorMapsThroughWrap()
    {
        var line = "aaaa bbbb cc";                    //width 12 wraps this line into two editor rows.
        var layout = FramedFixture(width: 12).Compose(
            new EditorView(new[] { line }, 0, line.IndexOf("cc", StringComparison.Ordinal)));
        AssertCursorOnEditorRow(layout, expectedVisualRow: 1, expectedCol0Based: 2);
    }

    [Fact]
    public void Compose_WideGlyphStraddlingWrapColumn_LandsOnNewRow_PreBoundaryCellEmpty()
    {
        var layout = FramedFixture(width: 10).Compose(new EditorView(new[] { "aaaaaa字" }, 0, 6));
        AssertCursorOnEditorRow(layout, expectedVisualRow: 1, expectedCol0Based: 2);
    }

    [Fact]
    public void Compose_NarrowTerminal_OverflowingContinuationMark_CursorLandsOnRealLastRow_NotPhantomMarkRow()
    {
        //one wrap segment can occupy two physical rows when the mark overflows, so the caret offset must sum screenRows
        var layout = FramedFixture(width: 4).Compose(new EditorView(new[] { "字", "" }, 1, 0));

        Assert.Equal(layout.TotalRows - 1, layout.CursorRowOffset);
        Assert.Equal(2, layout.CursorCol);
        Assert.True(layout.ScreenRows[0] > 1);                        //the first line occupies two screen rows, proving the overflow happened.
    }

    //selection highlights are computed from the one composer layout, so they cover exactly each row's sub-range

    [Fact]
    public void Highlight_paints_only_the_selected_subrange_on_a_single_row()
    {
        var frame = FramedFixture(width: 40);
        var lines = new List<string> { "abcde" };
        var plain = frame.Compose(new EditorView(lines, 0, 5));
        var selected = frame.Compose(new EditorView(lines, 0, 5, SelStart: (0, 1), SelEnd: (0, 3)));

        //only cells 1 to 3 of the run are highlighted
        var prompt = T.Paint("❯", Theme.Accent, bold: true) + " ";
        var expected = prompt + TermText.HighlightCells(T.Paint("abcde", Theme.Bright), 1, 3, T.SelectionBgOn);
        Assert.Equal(expected, selected.Rows[1]);

        //the highlight must not change the visible text or row structure.
        Assert.Equal(plain.VisibleRows, selected.VisibleRows);
        Assert.Equal(plain.TotalRows, selected.TotalRows);
        Assert.Equal(plain.ScreenRows, selected.ScreenRows);
    }

    [Fact]
    public void Multirow_selection_highlights_each_wrapped_rows_own_subrange()
    {
        //the range spans two wrapped rows, so row 0 highlights to its own end and row 1 to the caret.
        var frame = FramedFixture(width: 12);
        const string line = "aaaa bbbb cc";
        var lines = new List<string> { line };
        var plain = frame.Compose(new EditorView(lines, 0, line.Length));
        var selected = frame.Compose(new EditorView(lines, 0, line.Length, SelStart: (0, 2), SelEnd: (0, 11)));

        //the wrap row that begins a logical line still shows the prompt.
        var prompt = T.Paint("❯", Theme.Accent, bold: true) + " ";
        var expectedRow0 = prompt + TermText.HighlightCells(T.Paint("aaaa bbbb", Theme.Bright), 2, 9, T.SelectionBgOn);
        Assert.Equal(expectedRow0, selected.Rows[0]);   //width 12 is below the framed minimum, so row 0 is the editor row.

        //row 1 highlights from its start to the selection end.
        var expectedRow1 = "  " + TermText.HighlightCells(T.Paint("cc", Theme.Bright), 0, 1, T.SelectionBgOn);
        Assert.Equal(expectedRow1, selected.Rows[1]);

        Assert.Equal(plain.VisibleRows, selected.VisibleRows);
        Assert.Equal(plain.TotalRows, selected.TotalRows);
        Assert.Equal(plain.ScreenRows, selected.ScreenRows);
    }

    [Fact]
    public void Partial_highlight_does_not_change_wrap_or_row_count_at_any_width()
    {
        const string text = "lorem ipsum dolor sit amet consectetur adipiscing elit";
        foreach (var w in new[] { 20, 40, 80 })
        {
            var frame = FramedFixture(width: w);
            var a = frame.Compose(new EditorView(new[] { text }, 0, 0));
            var b = frame.Compose(new EditorView(new[] { text }, 0, 0, SelStart: (0, 5), SelEnd: (0, 15)));
            Assert.Equal(a.TotalRows, b.TotalRows);
            Assert.Equal(a.ScreenRows, b.ScreenRows);
            Assert.Equal(a.VisibleRows, b.VisibleRows);
            Assert.NotEqual(a.Rows, b.Rows);   //the rendered rows must still differ, proving the highlight is present.
        }
    }

    private static InputFrame FramedFixture(int width) =>
        new(new RecordingSurface { Width = width }, T, "coder", Status(), glyphs: GlyphSet.Unicode);

    //removes SGR sequences and keeps the visible glyphs.
    private static string StripSgr(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", "");

    //returns only editor rows, dropping the top rule and the bottom rule and status when framed.
    private static string[] EditorRows(FrameLayout layout)
    {
        var rows = layout.VisibleRows;
        return rows.Count >= 4 && rows[0].Contains('─')
            ? rows.Skip(1).Take(rows.Count - 3).ToArray()
            : rows.ToArray();
    }

    //the caret target is a physical row offset from the frame top and a 0-based column.
    private static void AssertCursorOnEditorRow(FrameLayout layout, int expectedVisualRow, int expectedCol0Based)
    {
        Assert.Equal(expectedCol0Based, layout.CursorCol);

        var firstEditor = 0;
        var acc = 0;
        for (var i = 0; i < layout.VisibleRows.Count; i++)
        {
            if (layout.VisibleRows[i].StartsWith("❯ ", StringComparison.Ordinal)) { firstEditor = acc; break; }
            acc += layout.ScreenRows[i];
        }
        Assert.Equal(firstEditor + expectedVisualRow, layout.CursorRowOffset);
    }

    //the ghost shows only when typed text plus ghost fits width minus 3, so write the fixture width as that sum

    private const string Ghosted = "/m";                       //the typed prefix occupies two cells.
    private const string GhostText = "odel [name|quant]";  //the ghost offer occupies seventeen cells.
    private const string NoGhost = "/z";                       //the same-length input with no candidates.

    private static FrameLayout ComposeOne(int width, string line) =>
        FramedFixture(width).Compose(new EditorView(new List<string> { line }, 0, line.Length));

    [Fact]
    public void The_ghost_renders_dim_after_the_typed_text()
    {
        var layout = ComposeOne(60, Ghosted);
        var row = EditorRows(layout)[0];

        Assert.Equal("❯ " + Ghosted + GhostText, row);
        //the ghost is painted as one dim theme span, so its brackets and pipes survive any fragmenting of the row
        Assert.Contains(T.Paint(GhostText, Theme.Dim), layout.Rows[1], StringComparison.Ordinal);
        //the painted row and the visible row must hold identical text, or the painter under-erases and the ghost stays behind
        Assert.Equal(row, StripSgr(layout.Rows[1]));
    }

    //the baseline input has the same length and no candidates, so the comparison is about the ghost alone
    [Fact]
    public void The_ghost_moves_neither_the_caret_nor_the_row_structure()
    {
        var with = ComposeOne(60, Ghosted);
        var without = ComposeOne(60, NoGhost);

        Assert.Equal(without.CursorCol, with.CursorCol);
        Assert.Equal(without.CursorRowOffset, with.CursorRowOffset);
        Assert.Equal(without.ScreenRows, with.ScreenRows);
        Assert.Equal(without.TotalRows, with.TotalRows);
    }

    [Fact]
    public void The_ghost_is_shown_whole_at_the_width_where_it_exactly_fits()
    {
        Assert.Equal("❯ " + Ghosted + GhostText, EditorRows(ComposeOne(22, Ghosted))[0]);
    }

    //the ghost is dropped whole, half an offer is worse than none
    [Fact]
    public void The_ghost_is_dropped_whole_one_cell_narrower()
    {
        Assert.Equal("❯ " + Ghosted, EditorRows(ComposeOne(21, Ghosted))[0]);
    }

    //sweep the widths across the boundary (one width passes at almost every size). rows must stay a cell short of the edge, or pending wrap eats a character
    [Theory]
    [InlineData(20)] [InlineData(21)] [InlineData(22)] [InlineData(23)] [InlineData(24)]
    [InlineData(25)] [InlineData(26)] [InlineData(27)] [InlineData(28)] [InlineData(30)]
    [InlineData(40)] [InlineData(60)] [InlineData(80)]
    public void The_ghost_never_reaches_the_terminal_edge_at_any_width(int width)
    {
        var with = ComposeOne(width, Ghosted);
        var without = ComposeOne(width, NoGhost);

        Assert.Equal(without.ScreenRows, with.ScreenRows);
        //only editor rows are checked, since the rules span the full width by design and hold no ghost.
        foreach (var row in EditorRows(with))
            Assert.True(UnicodeWidth.Of(row) <= width - 1,
                $"row reaches the edge at width {width}: {UnicodeWidth.Of(row)} cells — {row}");
    }

    //an escaped slash is literal text the user means to send, so it never gets an offer.
    [Fact]
    public void The_escape_hatch_draws_no_ghost()
    {
        Assert.Equal("❯ //m", EditorRows(ComposeOne(60, "//m"))[0]);
    }

    //only an ambiguous prefix proves the first candidate is shown, since a single-candidate prefix hides an off-by-one in the cycle index.
    [Fact]
    public void The_ghost_offers_the_first_candidate_of_an_ambiguous_prefix()
    {
        Assert.Equal("❯ /role [name]", EditorRows(ComposeOne(60, "/r"))[0]);
    }

    //no ghost unless the caret is at the end, it would promise a completion Tab does not perform
    [Fact]
    public void No_ghost_when_the_cursor_is_not_at_the_end()
    {
        var layout = FramedFixture(60).Compose(new EditorView(new List<string> { Ghosted }, 0, 1));
        Assert.Equal("❯ " + Ghosted, EditorRows(layout)[0]);
    }

    //the ghost is appended after the buffer, so selection covers the same cells of buffer text.
    [Fact]
    public void A_selection_highlights_the_same_cells_with_a_ghost_present()
    {
        var frame = FramedFixture(width: 60);
        var lines = new List<string> { Ghosted };
        var selected = frame.Compose(new EditorView(lines, 0, 2, SelStart: (0, 0), SelEnd: (0, 2)));

        var prompt = T.Paint("❯", Theme.Accent, bold: true) + " ";
        var highlighted = TermText.HighlightCells(T.Paint(Ghosted, Theme.Bright), 0, 2, T.SelectionBgOn);
        Assert.Equal(prompt + highlighted + T.Paint(GhostText, Theme.Dim), selected.Rows[1]);
    }
}
