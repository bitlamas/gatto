using Gatto.Core.Commands;

namespace Gatto.Tests;

public class CommandRegistryTests
{
    private static CommandRow Row(string name, string file = "undo.csx") =>
        new(name, "", "x", (_, _) => Task.FromResult(""), file);

    [Theory]
    [InlineData("/undo", true)]
    [InlineData("/Undo", true)]      //any case is command-shaped, the prose boundary does not move
    [InlineData("/", false)]
    [InlineData("/a1", false)]
    [InlineData("/usr/bin", false)]
    [InlineData("undo", false)]
    [InlineData("/un-do", false)]
    [InlineData("/\u00e9t", false)]   //a non-ASCII letter is not a command letter
    public void IS_SHAPED_IS_A_SLASH_AND_ASCII_LETTERS(string token, bool shaped) =>
        Assert.Equal(shaped, CommandNames.IsShaped(token));

    [Theory]
    [InlineData("/undo", true)]
    [InlineData("/Undo", false)]     //registration adds lowercase
    [InlineData("/uNdo", false)]
    [InlineData("/undo2", false)]
    [InlineData("/", false)]
    public void IS_REGISTRABLE_ADDS_LOWERCASE(string name, bool ok) =>
        Assert.Equal(ok, CommandNames.IsRegistrable(name));

    [Fact]
    public void ADD_THEN_TRY_GET_RETURNS_THE_ROW_AND_KEEPS_ORDER()
    {
        var reg = new CommandRegistry();
        reg.Add(Row("/undo"));
        reg.Add(Row("/redo", "other.csx"));
        Assert.True(reg.TryGet("/undo", out var row));
        Assert.Equal("undo.csx", row.SourceFile);
        Assert.False(reg.TryGet("/nope", out _));
        Assert.Equal(["/undo", "/redo"], reg.Rows.Select(r => r.Name));
    }

    [Fact]
    public void ADD_OF_A_TAKEN_NAME_THROWS()
    {
        var reg = new CommandRegistry();
        reg.Add(Row("/undo"));
        Assert.Throws<InvalidOperationException>(() => reg.Add(Row("/undo", "b.csx")));
    }
}
