using Gatto.Repl.Commands;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Tests;

//the help panel's rows with no loop: the commands as the table lists them, and each extension state with its word, its names and its sentence
public class HelpPanelTests
{
    private static ExtensionView View(string name, bool on = true, bool loaded = true, bool offAtLaunch = false, string? error = null,
        bool endpoint = false, string[]? tools = null, string[]? commands = null, string[]? endpoints = null) =>
        new(name, on, loaded, offAtLaunch, error, endpoint, tools ?? [], commands ?? [], endpoints ?? []);

    [Fact]
    public void A_command_row_is_its_name_and_args_its_gloss_and_a_registered_row_s_file()
    {
        var rows = HelpPanel.Commands([.. SlashCommands.All, new SlashCommands.Cmd("/card", "[name]", "draw a card", Source: "cards.csx")]);

        Assert.Equal("/help", rows[0].Label);
        Assert.Equal("this screen", rows[0].Description);
        Assert.Equal("/help", rows[0].Key);
        Assert.Null(rows[0].Trail);
        Assert.Equal("/card [name]", rows[^1].Label);
        Assert.Equal("/card", rows[^1].Key);
        Assert.Equal("cards.csx", Assert.Single(rows[^1].Trail!).Text);
        Assert.Equal(Theme.Dim, rows[^1].Trail![0].Ink);
    }

    [Fact]
    public void An_on_source_wears_the_tick_and_names_its_groups_by_count()
    {
        var row = Assert.Single(HelpPanel.Extensions([View("web_search", tools: ["web_search", "web_fetch"], commands: ["/card"], endpoints: ["lumo"])], GlyphSet.Unicode));

        Assert.Equal("web_search", row.Label);
        Assert.Equal("tools web_search, web_fetch · command /card · endpoint lumo", row.Description);
        Assert.Equal([" ", "✓"], row.Tail!.Select(t => t.Text));
        Assert.Equal(Theme.Ok, row.Tail![1].Ink);
        Assert.Null(row.Mark);
    }

    [Fact]
    public void An_off_source_says_off_dim_before_its_names_and_carries_no_tick()
    {
        var row = Assert.Single(HelpPanel.Extensions([View("web_search", on: false, tools: ["web_search"])], GlyphSet.Ascii));

        Assert.Equal("off", row.Mark!.Text);
        Assert.Equal(Theme.Dim, row.Mark.Ink);
        Assert.Equal("tool web_search", row.Description);
        Assert.Equal([" ", " "], row.Tail!.Select(t => t.Text));   //the tick's cells kept blank, so the column after the names never moves
    }

    [Fact]
    public void A_failed_source_says_failed_in_the_warn_ink_and_its_sentence_is_the_error()
    {
        var view = View("broken", loaded: false, error: "line 1: ; expected");
        var row = Assert.Single(HelpPanel.Extensions([view], GlyphSet.Unicode));

        Assert.Equal("failed", row.Mark!.Text);
        Assert.Equal(Theme.Warn, row.Mark.Ink);
        Assert.Null(row.Description);
        Assert.Equal([" ", " "], row.Tail!.Select(t => t.Text));
        Assert.Equal("line 1: ; expected", HelpPanel.Sentence(view));
    }

    [Fact]
    public void The_endpoint_s_file_is_on_and_says_it_serves_the_session()
    {
        var view = View("lumo", endpoint: true, endpoints: ["lumo"]);
        var row = Assert.Single(HelpPanel.Extensions([view], GlyphSet.Unicode));

        Assert.NotNull(row.Tail);
        Assert.Null(row.Mark);
        Assert.Equal(HelpPanel.ServesEndpoint, HelpPanel.Sentence(view));
    }

    //a source off at launch was never compiled, so on means the next launch and the row claims no tick and no failure
    [Fact]
    public void A_source_off_at_launch_turned_on_waits_for_the_next_launch()
    {
        var off = View("cards", on: false, loaded: false, offAtLaunch: true);
        var on = off with { On = true };

        Assert.Equal("off", HelpPanel.Extensions([off], GlyphSet.Unicode)[0].Mark!.Text);
        Assert.Null(HelpPanel.Sentence(off));
        var row = HelpPanel.Extensions([on], GlyphSet.Unicode)[0];
        Assert.Null(row.Mark);
        Assert.Equal([" ", " "], row.Tail!.Select(t => t.Text));
        Assert.Equal(HelpPanel.OnNextLaunch, HelpPanel.Sentence(on));
    }

    [Fact]
    public void A_source_with_nothing_registered_shows_no_names()
    {
        Assert.Null(HelpPanel.Extensions([View("undo")], GlyphSet.Unicode)[0].Description);
    }
}
