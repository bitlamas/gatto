using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//a footer word's key string is the key a press on it acts as, and the arrows answer nothing
public class FooterKeysTests
{
    [Theory]
    [InlineData("Esc", ConsoleKey.Escape)]
    [InlineData("Enter", ConsoleKey.Enter)]
    [InlineData("Tab", ConsoleKey.Tab)]
    [InlineData("Space", ConsoleKey.Spacebar)]
    public void A_NAMED_KEY_MAPS_TO_ITS_CONSOLE_KEY(string word, ConsoleKey key) =>
        Assert.Equal(key, FooterKeys.KeyOf(word)!.Value.Key);

    [Theory]
    [InlineData("m")]
    [InlineData("a")]
    [InlineData("?")]
    [InlineData("p")]
    public void A_ONE_CHARACTER_KEY_MAPS_TO_THAT_CHARACTER(string word) =>
        Assert.Equal(word[0], FooterKeys.KeyOf(word)!.Value.KeyChar);

    [Fact]
    public void THE_ARROWS_MAP_TO_NO_KEY()
    {
        Assert.Null(FooterKeys.KeyOf(GlyphSet.Unicode.ArrowsKey));
        Assert.Null(FooterKeys.KeyOf(GlyphSet.Ascii.ArrowsKey));
    }

    [Fact]
    public void A_WORD_NO_KEY_SPELLS_MAPS_TO_NONE() => Assert.Null(FooterKeys.KeyOf("search"));
}
