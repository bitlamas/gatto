using Gatto.Repl.Commands;

namespace Gatto.Tests;

//an alias is typed and never shown: it rewrites into the declared command before the gate, and no alias row reaches the table or its render
public class SlashAliasesTests
{
    [Theory]
    [InlineData("/exit", "/quit")]
    [InlineData("/clear", "/new")]
    [InlineData("/rename", "/name")]
    public void An_alias_alone_gives_its_command(string alias, string command) =>
        Assert.Equal(command, SlashAliases.Resolve(alias));

    [Fact]
    public void An_argument_rides_through_the_rewrite() =>
        Assert.Equal("/name a b", SlashAliases.Resolve("/rename a b"));

    [Fact]
    public void Surrounding_whitespace_is_trimmed() =>
        Assert.Equal("/quit", SlashAliases.Resolve("  /exit  "));

    [Theory]
    [InlineData("//exit")]
    [InlineData("say /exit")]
    [InlineData("/exi")]
    [InlineData("/exits")]
    [InlineData("/Exit")]
    [InlineData("/quit")]
    [InlineData("hello")]
    [InlineData("")]
    public void Anything_else_comes_back_as_the_same_string(string input) =>
        Assert.Same(input, SlashAliases.Resolve(input));

    [Fact]
    public void The_gate_passes_a_rewritten_alias() =>
        Assert.False(SlashGate.GateSlash(SlashAliases.Resolve("/exit"), SlashCommands.All).Refused);

    [Fact]
    public void An_undeclared_twin_of_an_alias_is_still_refused()
    {
        var g = SlashGate.GateSlash("/exi", SlashCommands.All);
        Assert.True(g.Refused);
        Assert.Equal(SlashGate.UnknownCommandLine("/exi"), g.Refusal);
    }

    [Fact]
    public void No_alias_reaches_the_command_table_or_its_plain_render()
    {
        var plain = SlashCommands.RenderPlain(SlashCommands.All);
        foreach (var (alias, command) in SlashAliases.All)
        {
            Assert.DoesNotContain(SlashCommands.All, c => c.Name == alias);
            Assert.Contains(SlashCommands.All, c => c.Name == command);
            Assert.DoesNotContain(alias, plain, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_names_are_the_aliases_of_the_pairs()
    {
        Assert.Equal(["/exit", "/clear", "/rename"], SlashAliases.Names);
        Assert.Equal(SlashAliases.All.Select(p => p.Alias), SlashAliases.Names);
    }
}
