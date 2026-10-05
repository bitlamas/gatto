using Gatto.Core.Loop;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

//the /context screen against prototype A's rows, with the prototype's own sample figures
public class ContextReportTests
{
    private static readonly Theme Plain = new(new TermCaps(false, false));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static ContextFigures Sample(bool exact = true) => new(65_536, 41_410, exact, false, false, 0.8,
    [
        new("system prompt", ContextGroup.Prefix, 1_180), new("role", ContextGroup.Prefix, 210, "coder"),
        new("model folder", ContextGroup.Prefix, 340), new("context files", ContextGroup.Prefix, 1_920, "2"),
        new("memory index", ContextGroup.Prefix, 460), new("tools", ContextGroup.Prefix, 4_870, "11 built-in"),
        new("tools", ContextGroup.Prefix, 1_240, "2 extensions"), new(ContextCount.TemplateMarkup, ContextGroup.Prefix, 610),
        new("user", ContextGroup.Messages, 1_350), new("assistant", ContextGroup.Messages, 3_880),
        new("reasoning", ContextGroup.Reasoning, 6_410), new("tool results", ContextGroup.ToolResults, 18_940, "64"),
    ], new ContextCache(40_880, 222));

    private static List<string> Visible(ContextFigures f, int width) => [.. ContextReport.Rows(f, width, Plain, U).Select(r => r.Visible)];

    //the prototype's table cells: a swatch, the label to 22, the number to 8, the percent to 8, and a header row with no swatch
    private static string Item(string swatch, string label, string n, string pct) => swatch + " " + label.PadRight(22) + n.PadLeft(8) + pct.PadLeft(8);
    private static string Head(string label, string n, string pct) => label.PadRight(24) + n.PadLeft(8) + pct.PadLeft(8);

    private static string Bar(params (string Glyph, int Count)[] runs) => "  " + string.Concat(runs.Select(r => string.Concat(Enumerable.Repeat(r.Glyph, r.Count))));

    [Fact]
    public void At_120_columns_the_rows_follow_the_prototype()
    {
        var rows = Visible(Sample(), 120);

        Assert.Equal("  context · 41,410 of 65,536 tokens · 63%", rows[0]);
        Assert.Equal("  counted by llama-server's tokenizer · the window is 65,536 · as of the last request", rows[1]);
        Assert.Equal("", rows[2]);
        //116 cells: the groups by largest remainder, then free up to the 80% line and past it
        Assert.Equal(Bar(("█", 19), ("█", 9), ("█", 11), ("█", 34), ("░", 20), ("░", 23)), rows[3]);
        Assert.Equal(new string(' ', 95) + "↑ auto-compacts at 80%", rows[4]);
        Assert.Equal("", rows[5]);
        Assert.Equal("  " + Head("prefix", "10,830", "16.5%") + "      " + Head("conversation", "30,580", "46.7%"), rows[6]);
        Assert.Equal("  " + Item("■", "system prompt", "1,180", "1.8%") + "      " + Item("■", "user", "1,350", "2.1%"), rows[7]);
        Assert.StartsWith("  " + Item("■", "role · coder", "210", "0.3%"), rows[8]);
        Assert.EndsWith(Item("■", "tool results · 64", "18,940", "28.9%"), rows[10]);
        Assert.Contains(rows, r => r.EndsWith(Item("■", "free", "24,126", "36.8%")));
        Assert.Contains(rows, r => r.EndsWith("■ past 80%, auto-compaction runs first"));
        Assert.Contains(rows, r => r.Contains(Item("■", "template markup", "610", "0.9%")));
        Assert.Equal("  cache  last request 40,880 of 41,102 tokens from cache, 222 read fresh", rows[^1]);
        Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= 120, r));
    }

    [Fact]
    public void At_80_columns_the_table_is_one_column_and_the_mark_turns_back()
    {
        var rows = Visible(Sample(), 80);

        Assert.Equal("  counted by the server's tokenizer · as of the last request", rows[1]);
        Assert.Equal(Bar(("█", 13), ("█", 6), ("█", 7), ("█", 22), ("░", 13), ("░", 15)), rows[3]);
        Assert.Equal(new string(' ', 42) + "auto-compacts at 80% ↑", rows[4]);
        Assert.Equal("  " + Head("prefix", "10,830", "16.5%"), rows[6]);
        var conversation = rows.FindIndex(r => r.StartsWith("  conversation", StringComparison.Ordinal));
        Assert.True(conversation > 6 + 8, "the conversation table follows the prefix table");
        Assert.Equal("", rows[conversation - 1]);
        Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= 80, r));
    }

    [Fact]
    public void An_estimate_marks_its_numbers_and_says_where_the_total_came_from()
    {
        var rows = Visible(Sample(exact: false) with { Cache = null }, 120);

        Assert.Equal("  the total is the server's count, the parts are estimates (chars/4 scaled to it) · as of the last request", rows[1]);
        Assert.Contains(rows, r => r.Contains(Item("■", "system prompt", "≈1,180", "1.8%")));
        Assert.Equal("  cache  not reported by this endpoint", rows[^1]);
    }

    [Fact]
    public void Without_auto_compaction_there_is_no_mark_and_no_line_past_it()
    {
        var rows = Visible(Sample() with { AutoCompactAt = null }, 120);

        Assert.DoesNotContain(rows, r => r.Contains("auto-compact"));
        Assert.Equal(Bar(("█", 19), ("█", 9), ("█", 11), ("█", 34), ("░", 43)), rows[3]);
    }

    [Fact]
    public void Before_the_first_request_the_screen_says_so_and_has_no_conversation()
    {
        var f = Sample() with { BeforeFirstRequest = true, Parts = Sample().Parts.Where(p => p.Group == ContextGroup.Prefix).ToList(), Total = 10_830 };
        var rows = Visible(f, 120);

        Assert.EndsWith("· before the first request", rows[1]);
        Assert.DoesNotContain(rows, r => r.Contains("conversation"));
    }

    //no request has run, so no endpoint was asked for cache figures yet. the not-reported line is for a request that carried none
    [Fact]
    public void Before_the_first_request_the_cache_line_says_no_request_yet()
    {
        var before = Sample() with { BeforeFirstRequest = true, Cache = null };
        var after = Sample() with { Cache = null };

        Assert.Equal("  cache  no request yet", Visible(before, 120)[^1]);
        Assert.Equal("  cache  not reported by this endpoint", Visible(after, 120)[^1]);
    }

    //prefix slate, reasoning violet and the cells past the line rust, each with its light pair and its 256 slot
    [Theory]
    [InlineData(true, ThemeMode.Dark, "38;2;94;115;137m", "38;2;154;132;224m", "38;2;138;90;42m")]
    [InlineData(true, ThemeMode.Light, "38;2;62;82;104m", "38;2;138;112;208m", "38;2;196;146;94m")]
    [InlineData(false, ThemeMode.Dark, "38;5;60m", "38;5;104m", "38;5;94m")]
    [InlineData(false, ThemeMode.Light, "38;5;60m", "38;5;98m", "38;5;173m")]
    public void The_bar_paints_prefix_reasoning_and_past_the_line_in_their_own_colours(bool trueColor, ThemeMode mode, string prefix, string reasoning, string past)
    {
        var bar = ContextReport.Rows(Sample(), 120, new Theme(new TermCaps(true, trueColor), mode), U)[3].Rendered;

        Assert.Contains("\x1b[" + prefix + "███", bar);
        Assert.Contains("\x1b[" + reasoning + "███", bar);
        Assert.Contains("\x1b[" + past + "░░░", bar);
    }

    //a table row's swatch is a square the terminal draws with space around it, so stacked swatches stay apart, and it takes one cell
    [Fact]
    public void The_table_swatch_is_a_square_of_one_cell_and_the_bar_stays_solid()
    {
        Assert.Equal("\u25a0", U.Swatch);
        Assert.Equal(1, UnicodeWidth.Of(U.Swatch));
        Assert.Equal("#", GlyphSet.Ascii.Swatch);
        var rows = Visible(Sample(), 120);

        Assert.DoesNotContain(rows.Skip(6), r => r.Contains('\u2588') || r.Contains('\u2591'));
        Assert.Equal(Bar(("\u2588", 73), ("\u2591", 43)), rows[3]);
    }

    [Fact]
    public void Under_the_ascii_set_the_bar_uses_the_ascii_glyphs()
    {
        var rows = ContextReport.Rows(Sample(), 120, Plain, GlyphSet.Ascii).Select(r => r.Visible).ToList();

        Assert.StartsWith("  ###", rows[3]);
        Assert.EndsWith("...", rows[3]);
        Assert.Equal(new string(' ', 95) + "^ auto-compacts at 80%", rows[4]);
    }
}
