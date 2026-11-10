using System.Globalization;
using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class FooterLadderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly string M = GlyphSet.Unicode.Cloud;

    private static string Line(StatusInfo s, int width, GlyphSet? g = null) =>
        StripSgr(InputFrame.BuildStatusLine(s, width, T, glyphs: g ?? GlyphSet.Unicode));

    private static string StripSgr(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", "");

    private static CtxState Ctx(int used, int window)
    {
        var ctx = new CtxState { ContextWindow = window };
        ctx.RecordUsedTokens(used);
        return ctx;
    }

    private static StatusInfo Acme() => new(
        @"C:\Users\user\sandbox", "acme-max", "generalist", Ctx(11_800, 131_072), @"C:\Users\user",
        TokensUp: 12_400, TokensDown: 972, Thinking: "high", Cloud: true, Allowance: new Allowance("max", AllowanceShape.Remaining, Remaining: 18));

    [Fact]
    public void A_cloud_row_fits_whole_at_81()
    {
        //a cloud row with a quota, which measures 74 cells with the mark as one
        var row = Line(Acme(), 81);
        Assert.Equal($"  ~\\sandbox · {M} acme-max (high) · ↑ 12k ↓ 972 · ctx 9% (12k) · max 18 left", row);
        Assert.Equal(74, UnicodeWidth.Of(row));
    }

    [Fact]
    public void A_cloud_row_one_cell_short_gives_up_the_ctx_count_first()
    {
        //one cell short of the whole row, the count is the first thing the core gives up
        var row = Line(Acme(), 73);
        Assert.Equal($"  ~\\sandbox · {M} acme-max (high) · ↑ 12k ↓ 972 · ctx 9% · max 18 left", row);
        Assert.Equal(68, UnicodeWidth.Of(row));
    }

    [Fact]
    public void A_cloud_row_at_81_shows_the_session_cost()
    {
        //a cloud row with a cost, where the money stands in the quota's place
        var s = Acme() with
        {
            Model = "nex-agi/nex-n2.5-pro", TokensUp = 839_500, TokensDown = 197_200, Allowance = null, Cost = 0.4231m,
        };
        Assert.Equal($"  ~\\sandbox · {M} nex-agi/nex-n2.5-pro (high) · ↑ 840k ↓ 197k · ctx 9% · $0.42", Line(s, 81));
    }

    [Fact]
    public void Quota_turns_warn_at_10_left_or_at_20_percent_of_a_reported_total()
    {
        //ten requests go fast in a turn with tool calls, so the count alone is enough, and a total moves the line up
        var ten = InputFrame.BuildStatusLine(Acme() with { Allowance = new Allowance("max", AllowanceShape.Remaining, Remaining: 10) }, 120, T, GlyphSet.Unicode);
        var eleven = InputFrame.BuildStatusLine(Acme() with { Allowance = new Allowance("max", AllowanceShape.Remaining, Remaining: 11) }, 120, T, GlyphSet.Unicode);
        var fifth = InputFrame.BuildStatusLine(Acme() with { Allowance = new Allowance("max", AllowanceShape.Remaining, Remaining: 40, Total: 200) }, 120, T, GlyphSet.Unicode);
        var above = InputFrame.BuildStatusLine(Acme() with { Allowance = new Allowance("max", AllowanceShape.Remaining, Remaining: 41, Total: 200) }, 120, T, GlyphSet.Unicode);

        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 10 left", ten, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Fg(Theme.Warn, true) + "max 11 left", eleven, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 40 left", fifth, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Fg(Theme.Warn, true) + "max 41 left", above, StringComparison.Ordinal);
    }

    [Fact]
    public void Quota_at_zero_is_warn_with_or_without_a_total()
    {
        //nothing left is the one reading that needs no total to be alarming
        var noTotal = InputFrame.BuildStatusLine(Acme() with { Allowance = new Allowance("max", AllowanceShape.Remaining, Remaining: 0) }, 120, T, GlyphSet.Unicode);
        var withTotal = InputFrame.BuildStatusLine(Acme() with { Allowance = new Allowance("max", AllowanceShape.Remaining, Remaining: 0, Total: 20) }, 120, T, GlyphSet.Unicode);

        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 0 left", noTotal, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 0 left", withTotal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_never_shed_parts_stay_until_the_row_is_cut()
    {
        //the mark, the effort, the ctx percent and the quota survive every rung, and only the final cut can take them
        var s = Acme() with { Branch = "main", Cost = 0.42m };
        for (var w = 140; w >= 20; w--)
        {
            var row = Line(s, w);
            Assert.True(UnicodeWidth.Of(row) <= w, $"row of {UnicodeWidth.Of(row)} cells at {w}");
            var cut = !row.EndsWith("max 18 left", StringComparison.Ordinal) && !row.EndsWith("max 18 left · $0.42", StringComparison.Ordinal);
            if (cut)
            {
                Assert.Equal(w, UnicodeWidth.Of(row));
                continue;
            }
            Assert.Contains(M + " ", row);
            Assert.Contains("(high)", row);
            Assert.Contains("ctx 9%", row);
        }
    }

    [Theory]
    [InlineData("thinking on", true, "qwen (reasoning)")]
    [InlineData("thinking off", true, "qwen (non-reasoning)")]
    [InlineData("none", false, "qwen (non-reasoning)")]
    [InlineData("high", false, "qwen (high)")]
    public void Effort_follows_the_model_in_parentheses(string thinking, bool toggle, string expected)
    {
        var s = new StatusInfo(@"C:\Users\user", "qwen", "coder", new CtxState(), @"C:\Users\user",
            Thinking: thinking, ThinkingToggle: toggle);
        Assert.Equal("  ~ · " + expected, Line(s, 120));
    }

    [Fact]
    public void A_model_with_no_reasoning_switch_shows_its_name_alone()
    {
        //with no switch gatto cannot say whether the model reasons in its text, so it says nothing
        var s = new StatusInfo(@"C:\Users\user", "qwen", "coder", new CtxState(), @"C:\Users\user");
        Assert.Equal("  ~ · qwen", Line(s, 120));
    }

    [Fact]
    public void The_cloud_mark_shows_only_on_a_cloud_endpoint_and_has_an_ascii_twin()
    {
        var local = Acme() with { Cloud = false };
        Assert.DoesNotContain(M, Line(local, 120));
        Assert.Contains(GlyphSet.Ascii.Cloud + " acme-max", Line(Acme(), 120, GlyphSet.Ascii));
        Assert.True(GlyphSet.Ascii.Cloud.All(c => c < 0x80));
    }

    [Fact]
    public void The_cloud_mark_counts_as_one_cell()
    {
        //the mark sits outside the emoji ranges the width table doubles, and the live gate checks conhost agrees
        Assert.Equal(1, UnicodeWidth.Of(GlyphSet.Unicode.Cloud));
    }

    [Fact]
    public void Cost_uses_the_invariant_decimal_point()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.EndsWith("$1.50", Line(Acme() with { Allowance = null, Cost = 1.5m }, 120));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Quota_and_cost_both_show_quota_first()
    {
        //a provider that reports both gets both parts, the quota before the money
        Assert.EndsWith("max 18 left · $0.10", Line(Acme() with { Cost = 0.1m }, 120));
    }

    private static Allowance Week(int? used) => new("week", AllowanceShape.Percent, UsedPercent: used);

    private static bool Warned(Allowance a) =>
        InputFrame.BuildStatusLine(Acme() with { Allowance = a }, 120, T, GlyphSet.Unicode)
            .Contains(Ansi.Fg(Theme.Warn, true) + InputFrame.AllowanceText(a), StringComparison.Ordinal);

    [Fact]
    public void The_three_forms_draw_their_grammar()
    {
        //the provider sends the numbers, core writes the one grammar for each form
        Assert.Equal("week 42% used", InputFrame.AllowanceText(Week(42)));
        Assert.Equal("max 18 left", InputFrame.AllowanceText(new Allowance("max", AllowanceShape.Remaining, Remaining: 18)));
        Assert.Equal("week 420 of 1000", InputFrame.AllowanceText(new Allowance("week", AllowanceShape.UsedOfTotal, Used: 420, Total: 1000)));
        Assert.EndsWith("week 42% used", Line(Acme() with { Allowance = Week(42) }, 120));
    }

    [Fact]
    public void A_null_number_draws_a_dash_in_its_place_and_never_warns()
    {
        //zero means nothing spent, a dash means the provider sent no number
        var percent = Week(null);
        var left = new Allowance("max", AllowanceShape.Remaining);
        var ofTotal = new Allowance("week", AllowanceShape.UsedOfTotal, Total: 1000);
        Assert.Equal("week -% used", InputFrame.AllowanceText(percent));
        Assert.Equal("max - left", InputFrame.AllowanceText(left));
        Assert.Equal("week - of 1000", InputFrame.AllowanceText(ofTotal));
        Assert.False(InputFrame.AllowanceLow(percent));
        Assert.False(InputFrame.AllowanceLow(left));
        Assert.False(InputFrame.AllowanceLow(ofTotal));
        Assert.False(Warned(percent));
    }

    [Fact]
    public void Percent_warns_at_80_used()
    {
        Assert.True(Warned(Week(80)));
        Assert.False(Warned(Week(79)));
    }

    [Fact]
    public void Used_of_total_warns_at_80_percent_used()
    {
        Assert.True(Warned(new Allowance("week", AllowanceShape.UsedOfTotal, Used: 950, Total: 1000)));
        Assert.True(Warned(new Allowance("week", AllowanceShape.UsedOfTotal, Used: 800, Total: 1000)));
        Assert.False(Warned(new Allowance("week", AllowanceShape.UsedOfTotal, Used: 799, Total: 1000)));
    }

    [Fact]
    public void A_label_row_draws_its_label_alone()
    {
        //a row with no number is its label, and a label never warns
        var tier = new Allowance("tier one", AllowanceShape.Label, Used: 990, Total: 1000);
        Assert.Equal("tier one", InputFrame.AllowanceText(tier));
        Assert.False(InputFrame.AllowanceLow(tier));
        Assert.EndsWith("ctx 9% (12k) · tier one", Line(Acme() with { Allowance = tier }, 120));
    }

    [Fact]
    public void The_cost_follows_the_allowance_after_the_cells()
    {
        //the order is the folder to ctx, the cells, the allowance, then the money
        var row = StripSgr(InputFrame.BuildStatusLine(Acme() with { Allowance = Week(42), Cost = 0.42m }, 140, T, GlyphSet.Unicode,
            new BackgroundFacts(1, false, false)));
        Assert.Contains("ctx 9% (12k) · 1 shell · week 42% used · $0.42 ", row);
    }

    [Fact]
    public void The_cost_sheds_at_rung_nine_and_the_allowance_never()
    {
        //the money is the last thing the ladder gives up, before the folder goes, and the floor never brings it back
        var s = Acme() with { Branch = "main", Cost = 0.42m };
        var gone = -1;
        for (var w = 140; w >= 20; w--)
        {
            var row = Line(s, w);
            if (gone < 0 && !row.Contains("$0.42", StringComparison.Ordinal)) gone = w;
            if (gone >= 0) Assert.DoesNotContain("$", row);
        }
        Assert.True(gone > 0);
        var first = Line(s, gone);
        Assert.EndsWith("max 18 left", first);
        Assert.Contains("sandbox", first);
        Assert.Contains("$0.42", Line(s, gone + 1));
    }

    [Fact]
    public void A_long_label_is_cut_at_twelve_cells_on_the_footer_only()
    {
        //the footer spends at most this many cells on a label, the reading keeps it whole
        var a = new Allowance(new string('a', 40), AllowanceShape.Remaining, Remaining: 3);
        Assert.Equal(new string('a', UsageMeter.MaxLabel) + " 3 left", InputFrame.AllowanceText(a));
        Assert.Equal(40, a.Label.Length);
    }
}
