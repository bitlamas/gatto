using System.Text.RegularExpressions;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

//a thought renders the model's markdown: the thought italic base, the thought colour as accent, no tables
public sealed class ThoughtMarkdownTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static readonly Regex Sgr = new(((char)27).ToString() + @"\[[0-9;]*m", RegexOptions.Compiled);

    private static string Plain(string row) => Sgr.Replace(row, "");

    private static ReasoningItem OpenThought(params string[] lines) =>
        new(lines) { Collapsed = false, Streaming = false, Elapsed = TimeSpan.FromSeconds(5) };

    private static ReasoningItem Glance(params string[] lines) =>
        new(lines) { Collapsed = true, Streaming = false, Elapsed = TimeSpan.FromSeconds(5), BetweenTools = true };

    //rows 0 and 1 of an open block are the chevron header and the bare rail
    private static string BodyRow(IReadOnlyList<string> rows, int index = 2) => rows[index];

    [Fact]
    public void A_bold_word_loses_its_markers_and_holds_the_bold_sgr_between_the_words()
    {
        var row = BodyRow(OpenThought("the **bold** word").Render(80, T, U));
        Assert.Equal($"{U.Box.Vertical} the bold word", Plain(row));
        Assert.Contains("the " + Ansi.Bold + "bold", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_becomes_a_chip_and_no_backtick_survives()
    {
        var row = BodyRow(OpenThought("`git status` ran").Render(80, T, U));
        var plain = Plain(row);
        Assert.Equal($"{U.Box.Vertical} git status ran", plain);
        Assert.DoesNotContain("`", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_takes_the_chip_of_the_thought_colour()
    {
        var row = BodyRow(OpenThought("`git status` ran").Render(80, T, U));
        Assert.Contains(Ansi.Fg(T.ChipColor(T.Map(Theme.Thought)), true) + "git status", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_open_block_row_keeps_the_rail_out_of_a_copy()
    {
        var rows = ItemRender.ReasoningExpandedTagged(new[] { "one", "**two**" }, TimeSpan.FromSeconds(5), T, 80);
        Assert.All(rows.Skip(2), r => Assert.Equal(GutterWrap.Hang.Length, r.LeadCells));
    }

    [Fact]
    public void Soft_emphasis_loses_its_markers()
    {
        var row = BodyRow(OpenThought("*soft* word").Render(80, T, U));
        Assert.Equal($"{U.Box.Vertical} soft word", Plain(row));
    }

    [Fact]
    public void The_thought_base_returns_after_a_bold_span()
    {
        var row = BodyRow(OpenThought("the **bold** word").Render(80, T, U));
        Assert.Contains(Ansi.Italic + " word", row, StringComparison.Ordinal);   //the italic of the base sits right before the trailing word
    }

    [Fact]
    public void A_bullet_takes_the_glyph_of_the_set_and_not_the_dash()
    {
        var row = BodyRow(OpenThought("- first item").Render(80, T, U));
        Assert.Equal($"{U.Box.Vertical} {U.Bullet} first item", Plain(row));
    }

    [Fact]
    public void A_fence_keeps_its_stars_and_the_line_after_the_fence_renders()
    {
        var rows = OpenThought("```", "**kept**", "```", "**gone**").Render(80, T, U);
        Assert.Contains(rows, r => Plain(r).EndsWith("**kept**", StringComparison.Ordinal));
        Assert.Contains(rows, r => Plain(r) == $"{U.Box.Vertical} gone");
        Assert.All(rows, r => Assert.DoesNotContain("```", Plain(r), StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_bold_line_wraps_under_the_rail_and_the_wrap_rows_are_continuations()
    {
        var item = OpenThought("the **bold** word and many more words that will not fit inside forty cells at all");
        var rows = item.Render(40, T, U);
        var wraps = item.RowWraps(40, T, U);
        Assert.True(wraps.Count(w => w.Continuation) >= 1, "the line did not wrap");
        for (var i = 0; i < rows.Count; i++)
            if (wraps[i].Continuation)
                Assert.StartsWith($"{U.Box.Vertical}", Plain(rows[i]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_capped_preview_renders_the_markdown_of_its_first_rows()
    {
        var rows = new ReasoningItem(new[] { "**a**", "b", "c", "d" }) { Collapsed = true, Streaming = true }
            .Render(80, T, U);
        Assert.Equal(4, rows.Count);   //three rendered rows and the affordance row
        Assert.Equal("  a", Plain(rows[0]));
        Assert.Equal("  b", Plain(rows[1]));
        Assert.Equal("  c", Plain(rows[2]));
    }

    [Fact]
    public void The_glance_row_strips_markers_and_shapes()
    {
        var row = Plain(Assert.Single(Glance("**pi Research (from `pi.ubitools.com`):**", "- **Q1** inject").Render(100, T, U)));
        Assert.Equal($"{U.Triangle} pi Research (from pi.ubitools.com): Q1 inject (thought for 5s)", row);
    }

    [Fact]
    public void The_session_lead_keeps_the_stars_literal()
    {
        var row = Plain(Assert.Single(new SessionLeadItem("**x**", null).Render(80, T, U)));
        Assert.Equal("  **x**", row);
    }

    [Fact]
    public void An_escape_byte_is_sanitized_before_the_parser_reads_it()
    {
        var rows = OpenThought("think" + (char)7 + (char)27 + "[7mred").Render(80, T, U);
        Assert.All(rows, r => Assert.DoesNotContain(((char)27).ToString() + "[7m", r, StringComparison.Ordinal));   //the model's inverse video never reaches the row
        Assert.Contains("[7mred", Plain(BodyRow(rows)), StringComparison.Ordinal);   //the rest of the text stays as text
    }
}
