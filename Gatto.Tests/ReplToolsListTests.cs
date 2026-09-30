using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

//the listing shows the session's tools, grouped by origin, so an extension's policy line sits beneath its own tools
public class ReplToolsListTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    private static IReadOnlyList<(string Extension, string Line)> NoPolicy =>
        Array.Empty<(string, string)>();

    [Fact]
    public void The_policy_line_renders_beneath_its_own_extensions_tools()
    {
        var tools = new[]
        {
            new ToolInfo("read_file", "read a file", null),
            new ToolInfo("ask_user", "ask the user", "ask_user"),
            new ToolInfo("web_search", "search", "web_search"),
        };

        var text = Gatto.Repl.Repl.RenderToolsListFor(tools, new[] { ("ask_user", "stop and ask") }, Plain, width: 100, glyphs: GlyphSet.Unicode);

        var rows = text.Split('\n');
        var askRow = Array.FindIndex(rows, r => r.Contains("ask_user", StringComparison.Ordinal));
        var polRow = Array.FindIndex(rows, r => r.Contains("Policy: stop and ask", StringComparison.Ordinal));
        var webRow = Array.FindIndex(rows, r => r.Contains("web_search", StringComparison.Ordinal));

        Assert.True(askRow >= 0 && polRow >= 0 && webRow >= 0);
        Assert.True(askRow < polRow, "the line follows the tools it belongs to");
        Assert.True(polRow < webRow, "and precedes the next extension");
    }

    [Fact]
    public void A_two_tool_extension_shows_its_line_once_after_its_LAST_tool()
    {
        //two tools from one extension get one policy line, placed after the last of them
        var tools = new[]
        {
            new ToolInfo("web_fetch", "fetch a page", "web_search"),
            new ToolInfo("web_search", "search", "web_search"),
        };

        var text = Gatto.Repl.Repl.RenderToolsListFor(tools, new[] { ("web_search", "cite what you fetch") }, Plain, 100, glyphs: GlyphSet.Unicode);

        var rows = text.Split('\n');
        Assert.Equal(1, rows.Count(r => r.Contains("Policy:", StringComparison.Ordinal)));
        var last = Array.FindLastIndex(rows, r => r.Contains("web_", StringComparison.Ordinal)
                                              && !r.Contains("Policy:", StringComparison.Ordinal));
        var pol = Array.FindIndex(rows, r => r.Contains("Policy:", StringComparison.Ordinal));
        Assert.True(last < pol, "the line is the group's footer, not a header");
    }

    //a first line ending in a colon promises a list, fold the keys inline rather than cut at the newline

    [Fact]
    public void A_description_promising_a_list_folds_the_keys_inline()
    {
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("run_agent",
                "Run a named subagent on a task. Available agents:\n"
                + "- code-review: reviews a diff\n- spec-review: reviews a spec", null) },
            NoPolicy, Plain, 200, glyphs: GlyphSet.Unicode);

        Assert.Contains("Available agents: code-review, spec-review.", text, StringComparison.Ordinal);
        //only the keys are folded inline. the values are for the model and must stay off the listing.
        Assert.DoesNotContain("reviews a diff", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colon_with_nothing_under_it_loses_the_colon_rather_than_dangling()
    {
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("odd", "Does a thing. Available agents:\nnot a bulleted list", null) },
            NoPolicy, Plain, 200, glyphs: GlyphSet.Unicode);

        Assert.Contains("Does a thing. Available agents", text, StringComparison.Ordinal);
        Assert.DoesNotContain("agents:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_multiline_description_still_shows_its_first_line_only()
    {
        //an ordinary multi-line description must not fold, otherwise a fold on every multi-line description would pass
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("plain", "The first line.\n- second: line", null) },
            NoPolicy, Plain, 200, glyphs: GlyphSet.Unicode);

        Assert.Contains("The first line.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_builtin_never_gets_a_policy_row()
    {
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("read_file", "read a file", null) }, NoPolicy, Plain, 100, glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain("Policy:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_line_naming_a_builtin_is_not_rendered_under_it()
    {
        //a policy line is keyed by extension name, and a built-in has no origin. a colliding key must find no group and render as its own row.
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("read_file", "read a file", null) },
            new[] { ("read_file", "this line owns no tool") }, Plain, 100, glyphs: GlyphSet.Unicode);

        var rows = text.Split('\n');
        var builtin = Array.FindIndex(rows, r => r.Contains("read a file", StringComparison.Ordinal));
        var pol = Array.FindIndex(rows, r => r.Contains("Policy:", StringComparison.Ordinal));
        Assert.True(builtin < pol, "a built-in keeps its own row and the orphan line follows the group block");
    }

    [Fact]
    public void A_tool_less_extension_renders_one_row_carrying_its_own_origin()
    {
        //an extension with a policy line and no tools would otherwise get no row. its row must name its own origin.
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("read_file", "read a file", null) },
            new[] { ("skillish", "always restate the task first") }, Plain, 100, glyphs: GlyphSet.Unicode);

        Assert.Contains("Policy: always restate the task first", text, StringComparison.Ordinal);
        Assert.Contains("(skillish)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headline_counts_tools_only()
    {
        //a policy row is not a tool. the headline counts tools only, or it overstates what was armed.
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[]
            {
                new ToolInfo("read_file", "read a file", null),
                new ToolInfo("ask_user", "ask the user", "ask_user"),
            },
            new[] { ("ask_user", "stop and ask") }, Plain, 100, glyphs: GlyphSet.Unicode);

        Assert.StartsWith("2 tools armed this session", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_tools_and_no_lines_is_the_only_case_that_says_nothing_is_armed()
    {
        Assert.Equal("no tools are armed this session",
            Gatto.Repl.Repl.RenderToolsListFor(Array.Empty<ToolInfo>(), NoPolicy, Plain, 100, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void A_line_with_no_tools_at_all_still_renders_rather_than_claiming_nothing_is_armed()
    {
        //when only policy rows exist, rendering must continue and not claim nothing is armed.
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            Array.Empty<ToolInfo>(), new[] { ("skillish", "always restate the task first") }, Plain, 100, glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain("no tools are armed", text, StringComparison.Ordinal);
        Assert.Contains("Policy: always restate the task first", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_builtin_block_is_byte_identical_to_the_render_without_any_policy()
    {
        //the built-in block stays identical only because the table trims trailing spaces when a policy row widens the column
        var tools = new[]
        {
            new ToolInfo("shell", "run a command", null),
            new ToolInfo("read_file", "read a file", null),
            new ToolInfo("glob", "find by name", null),
        };

        Assert.Equal(
            Gatto.Repl.Repl.RenderToolsListFor(tools, NoPolicy, Plain, 100, glyphs: GlyphSet.Unicode),
            Gatto.Repl.Repl.RenderToolsListFor(tools, new[] { ("skillish", "x") }, Plain, 100, glyphs: GlyphSet.Unicode)
                .Split('\n').Where(r => !r.Contains("Policy:", StringComparison.Ordinal))
                .Aggregate((a, b) => a + "\n" + b));
    }

    [Fact]
    public void Extensions_group_in_ordinal_origin_order_not_by_tool_name()
    {
        //every origin's rows stay together, ordered by origin name
        var tools = new[]
        {
            new ToolInfo("zebra_tool", "z", "ask_user"),
            new ToolInfo("aardvark_tool", "a", "web_search"),
            new ToolInfo("ask_user", "ask", "ask_user"),
            new ToolInfo("web_fetch", "fetch", "web_search"),
        };

        var rows = Gatto.Repl.Repl.RenderToolsListFor(tools, NoPolicy, Plain, 100, glyphs: GlyphSet.Unicode).Split('\n');
        var lastAskUser = Array.FindLastIndex(rows, r => r.Contains("(ask_user)", StringComparison.Ordinal));
        var firstWeb = Array.FindIndex(rows, r => r.Contains("(web_search)", StringComparison.Ordinal));

        Assert.True(lastAskUser < firstWeb, "ask_user's group comes first and is contiguous");
    }

    //sweep every width, the overflow shows only at the last column. the oracle counts characters, so it can't see a wide character's second cell
    public static IEnumerable<object[]> EveryWidth() =>
        Enumerable.Range(30, 91).Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(EveryWidth))]
    public void The_row_survives_a_width_sweep_without_overflowing(int width)
    {
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("ask_user", "ask the user a question", "ask_user") },
            new[] { ("ask_user", "stop and ask when a choice is the user's") }, Plain, width, glyphs: GlyphSet.Unicode);

        Assert.Contains("Policy:", text, StringComparison.Ordinal);
        Assert.All(text.Split('\n'), r =>
        {
            //the prepended two-space indent is not in the layout's budget. measure the row after removing the indent.
            var body = r.StartsWith("  ", StringComparison.Ordinal) ? r[2..] : r;
            Assert.True(TermText.StripAnsiForWidth(body).Length <= width,
                $"row overflows at {width}: {r}");
        });
    }

    [Fact]
    public void The_plain_render_indents_by_two_and_the_product_reserves_four()
    {
        //every row after the headline has a two-space indent, and the product reserves four columns so the composed line fits a terminal
        var text = Gatto.Repl.Repl.RenderToolsListFor(
            new[] { new ToolInfo("ask_user", "ask the user", "ask_user") },
            new[] { ("ask_user", "stop and ask") }, Plain, 100, glyphs: GlyphSet.Unicode);

        var rows = text.Split('\n');
        Assert.False(rows[0].StartsWith(' '), rows[0]);   //the headline row is flush
        Assert.All(rows.Skip(1), r => Assert.StartsWith("  ", r, StringComparison.Ordinal));
    }
}
