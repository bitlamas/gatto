using System.Globalization;
using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Repl.Term;
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
        TokensUp: 12_400, TokensDown: 972, Thinking: "high", Cloud: true, Quota: new QuotaReading(18, "max"));

    [Fact]
    public void A_cloud_row_fits_whole_at_81()
    {
        //a cloud row with a quota, which measures 78 cells with the mark as one
        var row = Line(Acme(), 81);
        Assert.Equal($"  ~\\sandbox · {M} acme-max (high) · ↑ 12.4k ↓ 972 · ctx 9% (11.8k) · max 18 left", row);
        Assert.Equal(78, UnicodeWidth.Of(row));
    }

    [Fact]
    public void A_cloud_row_one_cell_short_gives_up_the_ctx_count_first()
    {
        //one cell short of the whole row, the count is the first thing the core gives up
        var row = Line(Acme(), 77);
        Assert.Equal($"  ~\\sandbox · {M} acme-max (high) · ↑ 12.4k ↓ 972 · ctx 9% · max 18 left", row);
        Assert.Equal(70, UnicodeWidth.Of(row));
    }

    [Fact]
    public void A_cloud_row_at_81_shows_the_session_cost()
    {
        //a cloud row with a cost, where the money stands in the quota's place
        var s = Acme() with
        {
            Model = "nex-agi/nex-n2.5-pro", TokensUp = 839_500, TokensDown = 197_200, Quota = null, Cost = 0.4231m,
        };
        Assert.Equal($"  ~\\sandbox · {M} nex-agi/nex-n2.5-pro (high) · ↑ 839.5k ↓ 197.2k · ctx 9% · $0.42", Line(s, 81));
    }

    [Fact]
    public void Quota_turns_warn_at_10_left_or_at_20_percent_of_a_reported_total()
    {
        //ten requests go fast in a turn with tool calls, so the count alone is enough, and a total moves the line up
        var ten = InputFrame.BuildStatusLine(Acme() with { Quota = new QuotaReading(10, "max") }, 120, T, GlyphSet.Unicode);
        var eleven = InputFrame.BuildStatusLine(Acme() with { Quota = new QuotaReading(11, "max") }, 120, T, GlyphSet.Unicode);
        var fifth = InputFrame.BuildStatusLine(Acme() with { Quota = new QuotaReading(40, "max", 200) }, 120, T, GlyphSet.Unicode);
        var above = InputFrame.BuildStatusLine(Acme() with { Quota = new QuotaReading(41, "max", 200) }, 120, T, GlyphSet.Unicode);

        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 10 left", ten, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Fg(Theme.Warn, true) + "max 11 left", eleven, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 40 left", fifth, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Fg(Theme.Warn, true) + "max 41 left", above, StringComparison.Ordinal);
    }

    [Fact]
    public void Quota_at_zero_is_warn_with_or_without_a_total()
    {
        //nothing left is the one reading that needs no total to be alarming
        var noTotal = InputFrame.BuildStatusLine(Acme() with { Quota = new QuotaReading(0, "max") }, 120, T, GlyphSet.Unicode);
        var withTotal = InputFrame.BuildStatusLine(Acme() with { Quota = new QuotaReading(0, "max", 20) }, 120, T, GlyphSet.Unicode);

        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 0 left", noTotal, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.Warn, true) + "max 0 left", withTotal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rungs_shed_in_the_ruled_order()
    {
        //every part long enough that each rung changes something, swept from wide to narrow
        var s = new StatusInfo(@"C:\Users\user\projects\cc-usage-monitor-long", "qwen3.6-35b-a3b-instruct", "coder",
            Ctx(340, 1000), @"C:\Users\user", Branch: "feature-branch-long", TokensUp: 1200, TokensDown: 3400,
            Thinking: "high", FocusHint: "Ctrl+R", Cloud: true, Quota: new QuotaReading(18, "max"));
        var gone = new Func<string, bool>[]
        {
            r => !r.Contains("Ctrl+R"),
            r => !r.Contains("ctx 34% ("),
            r => !r.Contains("feature-branch-long"),
            r => !r.Contains("feature-branch-long") && !r.Contains(GlyphSet.Unicode.Ellipsis + "-long ·"),
            r => !r.Contains(@"~\projects"),
            r => !r.Contains("qwen3.6-35b-a3b-instruct"),
            r => !r.Contains("↑"),
            r => !r.Contains("cc-usage-monitor-long"),
            r => !r.Contains("nstruct"),
        };
        for (var w = 140; w >= 20; w--)
        {
            var row = Line(s, w);
            var flags = gone.Select(f => f(row)).ToArray();
            for (var i = 1; i < flags.Length; i++)
                Assert.True(!flags[i] || flags[i - 1], $"at {w} rung {i} shed before rung {i - 1}: {row}");
        }
    }

    [Fact]
    public void The_never_shed_parts_stay_until_the_row_is_cut()
    {
        //the mark, the effort, the ctx percent and the quota survive every rung, and only the final cut can take them
        var s = Acme() with { Branch = "main" };
        for (var w = 140; w >= 20; w--)
        {
            var row = Line(s, w);
            Assert.True(UnicodeWidth.Of(row) <= w, $"row of {UnicodeWidth.Of(row)} cells at {w}");
            var cut = !row.EndsWith("max 18 left", StringComparison.Ordinal);
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
            Assert.EndsWith("$1.50", Line(Acme() with { Quota = null, Cost = 1.5m }, 120));
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
}
