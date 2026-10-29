using Gatto.Repl.Commands;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Tests;

//the /tools rows and detail as pure functions of the tool list: the glosses, the first sentence, the trail and the policy beneath
public class ToolsPanelTests
{
    private static readonly IReadOnlyList<(string Extension, string Line)> NoPolicy = [];

    private static readonly string[] BuiltIns =
        ["read_file", "write_file", "edit_file", "glob", "grep", "shell", "memory_write", "recall_memory", "run_agent"];

    private static List<ToolInfo> NineBuiltIns() => [.. BuiltIns.Select(n => new ToolInfo(n, "The model's own text. More.", null))];

    [Fact]
    public void The_nine_built_ins_read_their_glosses_in_the_plain_renders_order()
    {
        var rows = ToolsPanel.Rows(NineBuiltIns());

        Assert.Equal(["edit_file", "glob", "grep", "memory_write", "read_file", "recall_memory", "run_agent", "shell", "write_file"],
            rows.Select(r => r.Label));
        Assert.Equal("replace one exact string in a file", rows[0].Description);
        Assert.Equal("run a role on a task", rows[6].Description);
        Assert.Equal("run a PowerShell command", rows[7].Description);
        Assert.All(rows, r => Assert.Null(r.Trail));
        Assert.All(rows, r => Assert.Equal(r.Label, r.Key));
    }

    [Fact]
    public void An_extensions_tool_reads_its_first_sentence_and_its_file_in_the_trail_after_the_built_ins()
    {
        var rows = ToolsPanel.Rows([
            new ToolInfo("web_search", "Search the web. Returns titles.", "web_search"),
            new ToolInfo("read_file", "Read a file.", null),
            new ToolInfo("ask_user", "Ask the user a question.", "ask_user"),
        ]);

        Assert.Equal(["read_file", "ask_user", "web_search"], rows.Select(r => r.Label));
        Assert.Equal("Search the web.", rows[2].Description);
        Assert.Equal("web_search.csx", Assert.Single(rows[2].Trail!).Text);
        Assert.Equal(Theme.Dim, rows[2].Trail![0].Ink);
    }

    [Fact]
    public void A_period_inside_a_word_is_not_a_sentences_end()
    {
        Assert.Equal("Fetch from api.example.com and return it.", ToolsPanel.FirstSentence("Fetch from api.example.com and return it. Then more."));
    }

    [Fact]
    public void A_colon_led_first_line_over_bullets_yields_the_line_without_its_colon_and_no_bullet()
    {
        Assert.Equal("Run a named role on a task.", ToolsPanel.FirstSentence("Run a named role on a task. Available roles:\n- coder: writes code."));
        Assert.Equal("Pick one of these", ToolsPanel.FirstSentence("Pick one of these:\n- alpha: first.\n- beta"));
    }

    [Fact]
    public void A_first_line_of_two_sentences_yields_the_first()
    {
        Assert.Equal("Search the web.", ToolsPanel.FirstSentence("Search the web. Returns up to ten results.\nMore."));
        Assert.Equal("No period here", ToolsPanel.FirstSentence("  No period here  "));
    }

    [Fact]
    public void A_built_ins_detail_is_its_text_and_no_policy_row()
    {
        var detail = ToolsPanel.Detail(new ToolInfo("read_file", "Read a file.\nThe path is relative.", null), [("read_file", "never shown")]);

        Assert.Equal(["Read a file.", "The path is relative."], detail.Select(d => d.Text));
        Assert.All(detail, d => Assert.True(d.Wrap));
        Assert.All(detail, d => Assert.False(d.Dim));
    }

    [Fact]
    public void An_extensions_detail_ends_with_its_own_policy_lines_dim_and_no_other_extensions()
    {
        var detail = ToolsPanel.Detail(new ToolInfo("ask_user", "Ask the user.", "ask_user"),
            [("ask_user", "stop and ask before guessing"), ("web_search", "cite every source"), ("ask_user", "one question at a time")]);

        Assert.Equal(["Ask the user.", "", "stop and ask before guessing", "one question at a time"], detail.Select(d => d.Text));
        Assert.True(detail[2].Dim);
        Assert.True(detail[3].Dim);
        Assert.True(detail[3].Wrap);
        Assert.DoesNotContain(detail, d => d.Text.Contains("Policy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_blank_line_in_the_text_is_a_blank_row()
    {
        var detail = ToolsPanel.Detail(new ToolInfo("odd", "First.\r\n\r\nThird.\n", "kit"), NoPolicy);

        Assert.Equal(["First.", "", "Third."], detail.Select(d => d.Text));
        Assert.False(detail[1].Wrap);
    }

    [Fact]
    public void A_policy_only_extension_yields_no_row()
    {
        var rows = ToolsPanel.Rows([new ToolInfo("read_file", "Read a file.", null)]);

        Assert.Equal(["read_file"], rows.Select(r => r.Label));
    }

    [Fact]
    public void A_tenth_built_in_the_table_does_not_name_reads_its_first_sentence()
    {
        Assert.Equal("Do a new thing.", ToolsPanel.Gloss(new ToolInfo("new_thing", "Do a new thing. Carefully.", null)));
    }
}
