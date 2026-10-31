using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

//the status line's fill pass: the ladder finds the base, presence comes back before length, then the hint, and the row never outgrows the width
public class StatusFillTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly string E = U.Ellipsis;

    private static string Line(StatusInfo s, int width, GlyphSet? g = null) =>
        StripSgr(InputFrame.BuildStatusLine(s, width, T, glyphs: g ?? U));

    private static string StripSgr(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", "");

    //the text between two cell columns, since the cloud mark is one cell and two chars
    private static string Cells(string row, int start, int end)
    {
        var sb = new System.Text.StringBuilder();
        var col = 0;
        foreach (var r in row.EnumerateRunes())
        {
            if (col >= start && col < end) sb.Append(r.ToString());
            col += UnicodeWidth.OfRune(r);
        }
        return sb.ToString();
    }

    private static CtxState Ctx(int used, int window)
    {
        var ctx = new CtxState { ContextWindow = window };
        ctx.RecordUsedTokens(used);
        return ctx;
    }

    private static readonly StatusHint Paste = new(HintKind.Paste, ["Alt+V to paste the image on your clipboard", "Alt+V pastes the image"]);
    private static readonly StatusHint Focus = new(HintKind.Focus, [FocusKeys.HintOf(U), FocusKeys.ShortHintOf(U)]);
    private static readonly StatusHint Teaser = new(HintKind.Teaser, [FocusKeys.HintOf(U), FocusKeys.ShortHintOf(U)]);
    private static readonly StatusHint Fourth = new(HintKind.Background, ["Down selects what runs in the background", "Down selects the background"]);

    //a deep folder and a long branch, the paste hint on the clipboard
    private static StatusInfo InputA() => new(
        @"C:\Users\user\work\repos\my-service\src\controllers\auth", "qwen3.8-flash-next", "coder", Ctx(132_200, 264_400),
        @"C:\Users\user", Branch: "feature/oauth-pkce-fl", TokensUp: 12_400, TokensDown: 3_100, Thinking: "medium",
        Hints: [Paste]);

    //a folder that is its own last segment, no hint
    private static StatusInfo InputB() => new(
        @"C:\Users\user\gatto-walk-footer-default-20261027", "qwen3.8-flash-next", "coder", Ctx(100_000, 263_000),
        @"C:\Users\user", Branch: "main", TokensUp: 120_000, TokensDown: 45_000, Thinking: "high");

    //every optional section set, the sweep's input
    private static StatusInfo Everything(int hints) => new(
        @"C:\Users\user\projects\cc-usage-monitor-long", "qwen3.6-35b-a3b-instruct", "coder", Ctx(340, 1000),
        @"C:\Users\user", Branch: "feature-branch-long", TokensUp: 1200, TokensDown: 3400, Thinking: "high",
        Hints: new[] { Paste, Focus, Teaser, Fourth }.Take(hints).ToList(), Cloud: true,
        Quota: new QuotaReading(18, "max"), Cost: 0.42m);

    [Fact]
    public void Input_A_at_90_keeps_its_two_folder_segments_from_the_rung_that_shed_the_ctx_count()
    {
        var row = Line(InputA(), 90);
        Assert.Equal($"  {E}\\controllers\\auth · {E}ce-fl · qwen3.8-flash-next (medium) · ↑ 12k ↓ 3.1k · ctx 50%", row);
        Assert.Equal(84, UnicodeWidth.Of(row));
    }

    [Fact]
    public void Input_B_at_90_keeps_its_tokens_and_leaves_the_branch_out_and_the_model_short()
    {
        var row = Line(InputB(), 90);
        Assert.Equal($"  ~\\gatto-walk-footer-default-20261027 · qwen3.8{E}sh-next (high) · ↑ 120k ↓ 45k · ctx 38%", row);
        Assert.Equal(88, UnicodeWidth.Of(row));
    }

    [Fact]
    public void Where_rung_1_fits_with_room_the_short_hint_comes_back_flush_right()
    {
        var s = new StatusInfo(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", Ctx(50_000, 100_000), @"C:\Users\user",
            Hints: [Paste]);
        var whole = UnicodeWidth.Of(Line(s with { Hints = null }, 200));
        //wide enough for the core and the short hint, too narrow for the long one beside it
        var width = whole + 2 + UnicodeWidth.Of("Alt+V pastes the image") + 3;
        var row = Line(s, width);
        Assert.EndsWith("Alt+V pastes the image", row, StringComparison.Ordinal);
        Assert.Equal(width, UnicodeWidth.Of(row));
        Assert.Contains("ctx 50% (50k)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_long_hint_beside_rung_0_is_kept_as_at_rc()
    {
        var s = new StatusInfo(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", Ctx(50_000, 100_000), @"C:\Users\user",
            Hints: [Paste]);
        Assert.EndsWith("Alt+V to paste the image on your clipboard", Line(s, 200), StringComparison.Ordinal);
    }

    //the arithmetic that holds the row to one physical row, and the floor that keeps the tail and the model's mark and effort
    [Fact]
    public void The_sweep_never_outgrows_the_width_and_the_floor_keeps_the_tail()
    {
        var widths = Enumerable.Range(40, 161).Concat([90, 120, 188]);
        foreach (var hints in Enumerable.Range(0, 5))
        {
            var s = Everything(hints);
            foreach (var w in widths)
            {
                var row = Line(s, w);
                Assert.True(UnicodeWidth.Of(row) <= w, $"{hints} hints at {w}: {UnicodeWidth.Of(row)} cells: {row}");
                Assert.Contains(U.Cloud + " ", row);
                Assert.Contains("(high)", row);
                //the tail alone, the bare model, ctx, the quota and the cost, is 42 cells, and only below that the cut takes the tail's end
                if (w >= 42) Assert.Contains("· $0.42", row, StringComparison.Ordinal);
                else Assert.Equal(w, UnicodeWidth.Of(row));
            }
        }
    }

    [Fact]
    public void Two_composes_of_one_tuple_give_one_row()
    {
        foreach (var w in new[] { 60, 90, 120, 188 })
            Assert.Equal(InputFrame.BuildStatusLine(Everything(3), w, T, U), InputFrame.BuildStatusLine(Everything(3), w, T, U));
    }

    //nothing the ladder dropped or shortened is left out while its shorter or longer form would still fit
    [Fact]
    public void The_fill_leaves_no_room_a_dropped_or_shortened_piece_could_use()
    {
        var s = Everything(0);
        const string tokens = "↑ 1.2k ↓ 3.4k";
        for (var w = 50; w <= 160; w++)
        {
            var row = Line(s, w);
            var have = UnicodeWidth.Of(row);
            //below the last rung the floor drops the folder and the fill does not run
            if (!row.Contains("cc-usage", StringComparison.Ordinal)) continue;
            if (!row.Contains(tokens, StringComparison.Ordinal))
                Assert.True(have + 3 + UnicodeWidth.Of(tokens) > w, $"tokens fit at {w}: {row}");
            if (!row.Contains("ctx 34% (", StringComparison.Ordinal))
                Assert.True(have + UnicodeWidth.Of(" (340)") > w, $"the ctx count fits at {w}: {row}");
            if (!row.Contains("feature-branch-long ·", StringComparison.Ordinal) && !row.Contains(E + "-long ·", StringComparison.Ordinal))
                Assert.True(have + 3 + UnicodeWidth.Of(E + "-long") > w, $"the branch fits at {w}: {row}");
            if (row.Contains("qwen3.6" + E + "nstruct", StringComparison.Ordinal))
                Assert.True(have + UnicodeWidth.Of("-35b-a3b-i") - UnicodeWidth.Of(E) > w, $"the model's name fits at {w}: {row}");
        }
    }

    [Fact]
    public void The_triangle_comes_with_a_second_live_hint_and_the_map_names_its_columns()
    {
        var one = Everything(1);
        Assert.DoesNotContain(U.Triangle, Line(one, 200));
        Assert.DoesNotContain(InputFrame.StatusCellsOf(one, 200, T, U), c => c.Kind is StatusCell.HintNext);

        var two = Everything(2);
        var row = Line(two, 200);
        Assert.EndsWith($"Alt+V to paste the image on your clipboard {U.Triangle}", row, StringComparison.Ordinal);
        var cells = InputFrame.StatusCellsOf(two, 200, T, U);
        var next = cells.Single(c => c.Kind == StatusCell.HintNext);
        Assert.Equal((198, 200), (next.Start, next.End));
        Assert.DoesNotContain(cells, c => c.Kind == StatusCell.Hint && c.End != 198);
        //the chosen index picks the hint, and the map follows it
        Assert.Contains(FocusKeys.HintOf(U), Line(two with { HintIndex = 1 }, 200));
        //an index past the list is clamped to the last hint
        Assert.Contains(FocusKeys.HintOf(U), Line(two with { HintIndex = 7 }, 200));
    }

    [Fact]
    public void The_ascii_set_paints_the_triangle_as_an_angle_bracket()
    {
        var row = Line(Everything(2), 200, GlyphSet.Ascii);
        Assert.EndsWith("Alt+V to paste the image on your clipboard >", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cell_map_names_each_painted_piece()
    {
        var s = Everything(0);
        var row = Line(s, 200);
        foreach (var c in InputFrame.StatusCellsOf(s, 200, T, U))
        {
            var text = Cells(row, c.Start, c.End);
            switch (c.Kind)
            {
                case StatusCell.Folder: Assert.Equal(@"~\projects\cc-usage-monitor-long", text); break;
                case StatusCell.Branch: Assert.Equal("feature-branch-long", text); break;
                case StatusCell.Ctx: Assert.Equal("ctx 34% (340)", text); break;
                case StatusCell.Cost: Assert.Equal("$0.42", text); break;
            }
        }
    }
}
